using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32;
using Reclaim.Core.Domain;

namespace Reclaim.Core.Infrastructure
{
    /// <summary>
    /// Cài đặt / gỡ cài đặt không cần trình cài đặt ngoài: sao chép file, đăng ký dịch vụ Windows,
    /// mục "Apps &amp; features" và lối tắt Start Menu. Phải chạy với quyền quản trị.
    /// </summary>
    public static class AppInstaller
    {
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + AppPaths.ProductName;
        private const string SafeBootKey = @"SYSTEM\CurrentControlSet\Control\SafeBoot\Network\" + AppPaths.ServiceName;

        public static bool IsAdmin()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public static bool IsServiceInstalled()
        {
            try
            {
                return ServiceController.GetServices().Any(s => s.ServiceName == AppPaths.ServiceName);
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ cài đặt

        public static void Install(string sourceDir)
        {
            RequireAdmin();
            sourceDir = sourceDir.TrimEnd('\\');

            if (!File.Exists(Path.Combine(sourceDir, "nextdns", "nextdns.exe")))
            {
                throw new ReclaimException(ErrorCodes.MissingBinary,
                    "Không tìm thấy nextdns\\nextdns.exe cạnh ứng dụng. Xem third_party\\nextdns\\README.txt.");
            }
            if (!File.Exists(Path.Combine(sourceDir, AppPaths.ServiceExeName)) || !File.Exists(Path.Combine(sourceDir, AppPaths.GuiExeName)))
            {
                throw new ReclaimException(ErrorCodes.BadRequest, "Thiếu Reclaim.exe hoặc Reclaim.Service.exe trong thư mục build.");
            }

            // Lần gỡ cài đặt trước có thể còn một batch dọn dẹp chạy nền (tối đa ~5 phút). Nếu không hủy nó,
            // nó sẽ xóa chính các file ta sắp chép (chỉ để sót những file đang bị khóa).
            CancelPendingCleanup();

            bool exists = IsServiceInstalled();
            if (exists) StopService();

            // Nâng cấp có hoàn tác: sao lưu bản đang cài, chép bản mới, kiểm tra; bất kỳ bước nào lỗi (kể cả dịch vụ
            // không khởi động được) thì khôi phục bản cũ rồi bật lại dịch vụ. Cài mới (chưa có bản cũ) thì không có gì để hoàn tác.
            string backupDir = AppPaths.InstallDir + ".backup";
            bool backedUp = false;
            try
            {
                try
                {
                    if (!string.Equals(sourceDir, AppPaths.InstallDir, StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(Path.Combine(AppPaths.InstallDir, AppPaths.ServiceExeName)))
                        {
                            DeleteDirectoryQuietly(backupDir);
                            CopyTree(AppPaths.InstallDir, backupDir);
                            backedUp = true;
                        }
                        CopyTree(sourceDir, AppPaths.InstallDir);
                    }
                    Directory.CreateDirectory(AppPaths.DataDir);

                    // Bản nextdns mới có thể khác: để dịch vụ ghi nhận lại mã băm ở lần chạy đầu.
                    var store = new JsonSettingsStore(AppPaths.SettingsFile, NullLog.Instance);
                    AppSettings settings = store.Load();
                    settings.NextDnsSha256 = string.Empty;
                    store.Save(settings);
                }
                catch (UnauthorizedAccessException)
                {
                    throw new ReclaimException(ErrorCodes.Denied,
                        "Không ghi được vào thư mục cài đặt. Nếu đang bật tự bảo vệ, hãy dùng \"Tạm gỡ bảo vệ\" trong ứng dụng trước.");
                }
                catch (IOException ex)
                {
                    throw new ReclaimException(ErrorCodes.Denied, "Không sao chép được file (đang được sử dụng?): " + ex.Message);
                }

                VerifyInstalledFiles();

                string binPath = "\"" + Path.Combine(AppPaths.InstallDir, AppPaths.ServiceExeName) + "\" --service";
                string quoted = "\"" + binPath.Replace("\"", "\\\"") + "\"";

                if (!exists)
                {
                    RunSc("create " + AppPaths.ServiceName + " binPath= " + quoted + " start= auto obj= LocalSystem DisplayName= \"Reclaim\"");
                }
                else
                {
                    RunSc("config " + AppPaths.ServiceName + " binPath= " + quoted + " start= auto");
                }
                RunSc("description " + AppPaths.ServiceName + " \"Reclaim DNS protection service\"");
                RunSc("failure " + AppPaths.ServiceName + " reset= 86400 actions= restart/5000/restart/5000/restart/5000");

                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(SafeBootKey))
                {
                    k.SetValue(string.Empty, "Service");
                }
                WriteUninstallEntry();
                CreateShortcut();

                StartService();

                // Chốt lại lần nữa: không báo "thành công" nếu file đã bị xóa mất.
                VerifyInstalledFiles();
            }
            catch (Exception ex)
            {
                bool restored = false;
                if (backedUp) restored = RollbackInstall(backupDir, exists);
                else DeleteDirectoryQuietly(backupDir);
                if (!exists) CleanupFailedFreshInstall(); // cài mới thất bại: không để lại dịch vụ/registry/file dở dang

                ReclaimException known = ex as ReclaimException;
                if (known != null)
                {
                    throw new ReclaimException(known.Code, known.Message + (restored ? " Đã khôi phục bản cũ." : string.Empty));
                }
                throw;
            }

            DeleteDirectoryQuietly(backupDir);
        }

        /// <summary>
        /// Hoàn tác một lần CÀI MỚI thất bại (chưa từng có dịch vụ): xóa dịch vụ vừa đăng ký, khóa registry, lối tắt và file đã chép.
        /// Dữ liệu người dùng (DataDir) được giữ. Từng bước độc lập và chạy lại được nhiều lần.
        /// </summary>
        private static void CleanupFailedFreshInstall()
        {
            try { if (IsServiceInstalled()) StopService(); } catch { }
            try { if (IsServiceInstalled()) ProcessUtil.Run("sc.exe", "delete " + AppPaths.ServiceName, 15000); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(SafeBootKey, false); } catch { }
            try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { }
            DeleteDirectoryQuietly(AppPaths.InstallDir);
        }

        /// <returns>true nếu đã khôi phục xong bản cũ (khi đó bản sao lưu được xóa; nếu không, giữ lại để khôi phục tay).</returns>
        private static bool RollbackInstall(string backupDir, bool restartService)
        {
            bool restored;
            try
            {
                CopyTree(backupDir, AppPaths.InstallDir); // ghi đè các file mới bằng file cũ
                restored = true;
            }
            catch
            {
                restored = false;
            }
            if (restartService)
            {
                try { StartService(); } catch { }
            }
            if (restored) DeleteDirectoryQuietly(backupDir);
            return restored;
        }

        private static void DeleteDirectoryQuietly(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        private static string CleanupCancelMarker
        {
            get { return Path.Combine(AppPaths.DataDir, "install.marker"); }
        }

        /// <summary>
        /// Tạo file dấu để batch dọn dẹp của lần gỡ trước tự dừng, rồi chờ một vòng của nó (rmdir đang chạy dở) kết thúc.
        /// </summary>
        private static void CancelPendingCleanup()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(CleanupCancelMarker, DateTime.UtcNow.ToString("o"));
            }
            catch
            {
                // Không ghi được dấu thì vẫn tiếp tục; VerifyInstalledFiles sẽ báo lỗi nếu file bị xóa.
            }
            WaitForCleanupBatch();
        }

        /// <summary>
        /// Chờ batch dọn dẹp của lần gỡ trước tự thoát (nó tự xóa file .bat của chính nó ở bước cuối, và kiểm tra file dấu
        /// mỗi ~2 giây). Thay cho việc ngủ cố định: chờ tối thiểu 1 giây, tối đa 12 giây, thoát sớm khi không còn batch nào.
        /// </summary>
        private static void WaitForCleanupBatch()
        {
            DateTime started = DateTime.UtcNow;
            Thread.Sleep(1000);
            while (DateTime.UtcNow - started < TimeSpan.FromSeconds(12))
            {
                bool pending;
                try { pending = Directory.GetFiles(Path.GetTempPath(), "reclaim-cleanup-*.bat").Length > 0; }
                catch { pending = false; }
                if (!pending) return;
                Thread.Sleep(500);
            }
        }

        private static void VerifyInstalledFiles()
        {
            string[] required =
            {
                AppPaths.GuiExeName,
                AppPaths.ServiceExeName,
                "Reclaim.Core.dll",
                Path.Combine("nextdns", "nextdns.exe")
            };
            foreach (string relative in required)
            {
                if (!File.Exists(Path.Combine(AppPaths.InstallDir, relative)))
                {
                    throw new ReclaimException(ErrorCodes.Internal,
                        "Cài đặt chưa đầy đủ: thiếu " + relative + " trong " + AppPaths.InstallDir + ". Hãy chạy lại trình cài đặt.");
                }
            }
        }

        private static void WriteUninstallEntry()
        {
            string gui = Path.Combine(AppPaths.InstallDir, AppPaths.GuiExeName);
            using (RegistryKey k = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "Reclaim");
                k.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version.ToString(3));
                k.SetValue("Publisher", "Reclaim");
                k.SetValue("InstallLocation", AppPaths.InstallDir);
                k.SetValue("DisplayIcon", gui);
                k.SetValue("UninstallString", "\"" + gui + "\" /uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        private static string ShortcutPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Reclaim.lnk");
            }
        }

        private static void CreateShortcut()
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { ShortcutPath });
                Type linkType = link.GetType();
                linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link,
                    new object[] { Path.Combine(AppPaths.InstallDir, AppPaths.GuiExeName) });
                linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { AppPaths.InstallDir });
                linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "Reclaim" });
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            catch
            {
                // Lối tắt chỉ là tiện ích, không làm hỏng cài đặt.
            }
        }

        // ------------------------------------------------------------------ gỡ cài đặt

        public static void Uninstall()
        {
            RequireAdmin();

            if (IsServiceInstalled()) StopService();

            // Gỡ quy tắc chặn DNS ngoài TRƯỚC khi trả DNS: nếu để sót, máy sẽ mất DNS sau khi Reclaim biến mất.
            bool guardRemoved;
            try { guardRemoved = new WindowsBypassGuard(NullLog.Instance).Remove(); }
            catch { guardRemoved = false; }

            // Trả DNS về như cũ khi dịch vụ đã dừng (DataDir còn dns-backup.json).
            bool dnsRestored;
            try
            {
                var dns = new NetshDnsConfigurator(NullLog.Instance);
                // nextdns đã dừng: ngoài khôi phục theo bản sao lưu, dọn cả adapter loopback mất bản sao lưu.
                dnsRestored = dns.ResetAll() & dns.RecoverOrphans(new DnsProbe().IsResolverAnswering);
            }
            catch { dnsRestored = false; }
            if (!dnsRestored || !guardRemoved)
            {
                // Không xóa gì cả: dịch vụ được bật lại (tự áp dụng lại bảo vệ) để máy không mất mạng, giữ nguyên bản sao lưu DNS.
                try { if (IsServiceInstalled()) StartService(); } catch { }
                throw new ReclaimException(ErrorCodes.ApplyFailed,
                    "Không khôi phục được " + (dnsRestored ? "quy tắc tường lửa/chính sách do Reclaim đặt" : "DNS ban đầu của một số adapter") +
                    " nên đã hủy gỡ cài đặt (dịch vụ được bật lại). Hãy thử lại.");
            }

            if (IsServiceInstalled())
            {
                ProcessResult r = ProcessUtil.Run("sc.exe", "delete " + AppPaths.ServiceName, 15000);
                if (!r.Success)
                {
                    throw new ReclaimException(ErrorCodes.Denied, "Không xóa được dịch vụ. Bảo vệ có thể đang bật. " + r.Output);
                }
            }

            try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Reclaim", false); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(SafeBootKey, false); } catch { }
            try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { }
            try { if (Directory.Exists(AppPaths.DataDir)) Directory.Delete(AppPaths.DataDir, true); } catch { }

            ScheduleInstallDirRemoval();
        }

        /// <summary>
        /// File của chính ứng dụng đang bị khóa nên xóa bằng một batch chạy nền, thử lại tới ~5 phút
        /// cho tới khi người dùng đóng cửa sổ Reclaim.
        /// </summary>
        private static void ScheduleInstallDirRemoval()
        {
            if (!Directory.Exists(AppPaths.InstallDir)) return;
            try { if (File.Exists(CleanupCancelMarker)) File.Delete(CleanupCancelMarker); } catch { }
            string bat = Path.Combine(Path.GetTempPath(), "reclaim-cleanup-" + Guid.NewGuid().ToString("N") + ".bat");
            string script =
                "@echo off\r\n" +
                "for /l %%i in (1,1,150) do (\r\n" +
                "  if exist \"" + CleanupCancelMarker + "\" goto cancelled\r\n" +
                "  rmdir /s /q \"" + AppPaths.InstallDir + "\" 2>nul\r\n" +
                "  if not exist \"" + AppPaths.InstallDir + "\" goto done\r\n" +
                "  ping 127.0.0.1 -n 3 >nul\r\n" +
                ")\r\n" +
                ":done\r\n" +
                ":cancelled\r\n" +
                "del \"%~f0\"\r\n";
            File.WriteAllText(bat, script);
            var info = new System.Diagnostics.ProcessStartInfo(bat)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            System.Diagnostics.Process.Start(info);
        }

        // ------------------------------------------------------------------ dịch vụ Windows

        private static void StopService()
        {
            try
            {
                using (var sc = new ServiceController(AppPaths.ServiceName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                throw new ReclaimException(ErrorCodes.Denied,
                    "Không dừng được dịch vụ Reclaim. Bảo vệ có thể đang bật - hãy mở khóa và chọn \"Tạm gỡ bảo vệ\" trong ứng dụng. (" + ex.Message + ")");
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                throw new ReclaimException(ErrorCodes.Denied, "Dịch vụ Reclaim không dừng kịp.");
            }
        }

        private static void StartService()
        {
            using (var sc = new ServiceController(AppPaths.ServiceName))
            {
                if (sc.Status == ServiceControllerStatus.Running) return;
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
        }

        private static void RunSc(string arguments)
        {
            ProcessResult r = ProcessUtil.Run("sc.exe", arguments, 20000);
            if (!r.Success)
            {
                throw new ReclaimException(ErrorCodes.Internal, "sc.exe thất bại: " + r.Output);
            }
        }

        private static void RequireAdmin()
        {
            if (!IsAdmin())
            {
                throw new ReclaimException(ErrorCodes.Denied, "Cần quyền quản trị viên.");
            }
        }

        private static void CopyTree(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".pdb" || ext == ".xml") continue;
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            }
            foreach (string dir in Directory.GetDirectories(source))
            {
                CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)));
            }
        }
    }
}
