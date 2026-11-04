using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Reclaim.Core.Application;
using Reclaim.Core.Domain;

namespace Reclaim.Core.Infrastructure
{
    /// <summary>
    /// Các lớp tự bảo vệ (đều idempotent, chạy dưới tài khoản SYSTEM của dịch vụ):
    ///  1. DACL của dịch vụ: Administrators chỉ còn quyền xem, không dừng/xóa/đổi cấu hình.
    ///  2. ACL khóa registry của dịch vụ (chặn sửa Start=4 hoặc xóa khóa).
    ///  3. ACL thư mục cài đặt (chặn thay/xóa file) và thư mục dữ liệu (chặn sửa cài đặt, hash mật khẩu).
    /// Đây là rào cản đáng kể với thao tác thông thường nhưng KHÔNG thể chặn hoàn toàn quản trị viên
    /// quyết tâm (Safe Mode, công cụ chạy quyền SYSTEM, khởi động từ USB...).
    /// </summary>
    public sealed class WindowsProtectionHardener : IProtectionHardener
    {
        private const string ServiceRegistryKey = @"SYSTEM\CurrentControlSet\Services\" + AppPaths.ServiceName;

        private static readonly SecurityIdentifier SystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        private static readonly SecurityIdentifier AdminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier UsersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        private const int ErrorServiceDoesNotExist = 1060;

        private readonly ILog _log;

        public WindowsProtectionHardener(ILog log)
        {
            _log = log;
        }

        /// <summary>Áp dụng mọi lớp; nếu có lớp lỗi thì ném ReclaimException liệt kê các lớp đó (không báo thành công giả).</summary>
        public void Apply(ProtectionOptions options)
        {
            bool any = options.Any;
            var failures = new List<string>();
            Step(failures, "DACL dịch vụ", delegate { SetServiceSecurity(BuildServiceSddl(options), any); });
            Step(failures, "ACL registry", delegate { SetRegistryAcl(any); });
            Step(failures, "ACL thư mục dữ liệu", delegate { SetDataDirAcl(any); });
            Step(failures, "ACL thư mục cài đặt", delegate { SetInstallDirAcl(any); });
            if (failures.Count > 0)
            {
                throw new ReclaimException(ErrorCodes.ApplyFailed, string.Join("; ", failures));
            }
        }

        private void Step(List<string> failures, string name, Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                _log.Warn("Tự bảo vệ - " + name + ": " + ex.Message);
                failures.Add(name + " (" + ex.Message + ")");
            }
        }

        // ------------------------------------------------------------------ dịch vụ

        /// <summary>
        /// Quyền dịch vụ (SDDL): CC=query config, DC=change config, LC=query status, SW=enum dependents,
        /// RP=start, WP=stop, DT=pause/continue, LO=interrogate, CR=user control, SD=delete,
        /// RC=read control, WD=write DAC, WO=write owner.
        /// </summary>
        internal static string BuildServiceSddl(ProtectionOptions p)
        {
            string admins;
            if (!p.Any)
            {
                admins = "CCDCLCSWRPWPDTLOCRSDRCWDWO"; // mặc định của Windows
            }
            else
            {
                var sb = new StringBuilder("CCLCSWLOCRRC");   // chỉ xem
                if (!p.Service) sb.Append("RPWPDTDC");         // start, stop, pause, đổi cấu hình
                if (!p.Uninstall) sb.Append("SD");             // xóa dịch vụ
                admins = sb.ToString();
            }
            // SYSTEM giữ WD để dịch vụ có thể tự nới/siết lại DACL của chính nó.
            return "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWD;;;SY)(A;;" + admins + ";;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";
        }

        private static void SetServiceSecurity(string sddl, bool locked)
        {
            IntPtr scm = Native.OpenSCManager(null, null, Native.SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                IntPtr service = Native.OpenService(scm, AppPaths.ServiceName, Native.WRITE_DAC);
                if (service == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (!locked && error == ErrorServiceDoesNotExist) return; // không có gì để nới
                    throw new Win32Exception(error);
                }
                try
                {
                    IntPtr descriptor;
                    uint size;
                    if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, Native.SDDL_REVISION_1, out descriptor, out size))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    try
                    {
                        if (!Native.SetServiceObjectSecurity(service, Native.DACL_SECURITY_INFORMATION, descriptor))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    finally
                    {
                        Native.LocalFree(descriptor);
                    }
                }
                finally
                {
                    Native.CloseServiceHandle(service);
                }
            }
            finally
            {
                Native.CloseServiceHandle(scm);
            }
        }

        // ------------------------------------------------------------------ registry

        private static void SetRegistryAcl(bool locked)
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                ServiceRegistryKey, RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ChangePermissions | RegistryRights.ReadKey))
            {
                if (key == null)
                {
                    if (locked) throw new InvalidOperationException("không tìm thấy khóa registry của dịch vụ");
                    return;
                }
                RegistrySecurity security = key.GetAccessControl();
                foreach (RegistryAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                {
                    security.RemoveAccessRuleSpecific(rule);
                }
                if (locked)
                {
                    security.SetAccessRuleProtection(true, false);
                    security.AddAccessRule(new RegistryAccessRule(SystemSid, RegistryRights.FullControl,
                        InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
                    security.AddAccessRule(new RegistryAccessRule(AdminsSid, RegistryRights.ReadKey,
                        InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
                    security.AddAccessRule(new RegistryAccessRule(UsersSid, RegistryRights.ReadKey,
                        InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
                }
                else
                {
                    security.SetAccessRuleProtection(false, false); // trả về quyền kế thừa mặc định
                }
                key.SetAccessControl(security);
            }
        }

        // ------------------------------------------------------------------ thư mục

        private static void SetDataDirAcl(bool locked)
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var dir = new DirectoryInfo(AppPaths.DataDir);
            DirectorySecurity security = dir.GetAccessControl();
            ClearExplicit(security);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(Allow(SystemSid, FileSystemRights.FullControl));
            security.AddAccessRule(Allow(AdminsSid, locked ? FileSystemRights.ReadAndExecute : FileSystemRights.FullControl));
            dir.SetAccessControl(security);
        }

        private static void SetInstallDirAcl(bool locked)
        {
            if (!Directory.Exists(AppPaths.InstallDir))
            {
                if (locked) throw new DirectoryNotFoundException("chưa cài đặt vào " + AppPaths.InstallDir);
                return;
            }
            var dir = new DirectoryInfo(AppPaths.InstallDir);
            DirectorySecurity security = dir.GetAccessControl();
            ClearExplicit(security);
            if (locked)
            {
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(Allow(SystemSid, FileSystemRights.FullControl));
                security.AddAccessRule(Allow(AdminsSid, FileSystemRights.ReadAndExecute));
                security.AddAccessRule(Allow(UsersSid, FileSystemRights.ReadAndExecute));
            }
            else
            {
                security.SetAccessRuleProtection(false, false); // kế thừa lại từ Program Files
            }
            dir.SetAccessControl(security);
        }

        private static FileSystemAccessRule Allow(SecurityIdentifier sid, FileSystemRights rights)
        {
            return new FileSystemAccessRule(sid, rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow);
        }

        private static void ClearExplicit(DirectorySecurity security)
        {
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                security.RemoveAccessRuleSpecific(rule);
            }
        }
    }
}
