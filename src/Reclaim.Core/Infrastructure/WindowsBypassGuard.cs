using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Reclaim.Core.Application;

namespace Reclaim.Core.Infrastructure
{
    /// <summary>
    /// Chặn các đường vòng qua bộ lọc DNS (idempotent, chạy dưới SYSTEM):
    ///  1. Windows Firewall (COM INetFwPolicy2, không phụ thuộc ngôn ngữ): nextdns.exe không được gửi DNS không mã hóa ra
    ///     cổng 53 (nextdns tự rơi về UDP/53 tới NextDNS với profile "none" = KHÔNG lọc khi DoH bị chặn); mọi chương trình
    ///     khác không được gửi DNS/DoT (53, 853) ra ngoài loopback; chặn DoH/QUIC tới vài nhà cung cấp DNS công cộng nổi tiếng.
    ///  2. Chính sách registry: tắt DNS-over-HTTPS của Chrome, Edge, Brave, Firefox và tắt Smart Multi-Homed Name Resolution.
    /// Giá trị registry cũ được lưu và khôi phục khi gỡ. Lưu ý: trình duyệt Chromium sẽ hiện "Managed by your organization".
    /// </summary>
    public sealed class WindowsBypassGuard : IBypassGuard
    {
        private const int DirectionOut = 2;      // NET_FW_RULE_DIR_OUT
        private const int ActionBlock = 0;       // NET_FW_ACTION_BLOCK
        private const int ProfilesAll = 0x7FFFFFFF;
        private const int ProtoTcp = 6;
        private const int ProtoUdp = 17;
        private const string RuleVersion = "Reclaim v1";
        private const string PolicyBackupRoot = @"SOFTWARE\Reclaim\PolicyBackup";

        // Mọi địa chỉ trừ loopback (127.0.0.0/8 và ::1): truy vấn tới nextdns cục bộ không bao giờ bị chặn.
        private const string NonLoopback =
            "0.0.0.0-126.255.255.255,128.0.0.0-255.255.255.255,::,::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff";

        // Máy chủ DoH/DoT công cộng nổi tiếng (Google, Cloudflare, Quad9, OpenDNS, AdGuard).
        private const string PublicDnsProviders =
            "8.8.8.8,8.8.4.4,2001:4860:4860::8888,2001:4860:4860::8844," +
            "1.1.1.1,1.0.0.1,2606:4700:4700::1111,2606:4700:4700::1001," +
            "9.9.9.9,149.112.112.112,2620:fe::fe,2620:fe::9," +
            "208.67.222.222,208.67.220.220," +
            "94.140.14.14,94.140.15.15";

        private sealed class RuleSpec
        {
            public string Name;
            public int Protocol;
            public string RemotePorts;
            public string RemoteAddresses;
            public string Application;

            public string Description
            {
                get { return RuleVersion + " | " + (Application ?? "-") + " | " + (RemoteAddresses ?? "-"); }
            }
        }

        private sealed class PolicySpec
        {
            public string Id;
            public string KeyPath;
            public string ValueName;
            public RegistryValueKind Kind;
            public object Value;
        }

        // Tên các quy tắc cố định (không phụ thuộc đường dẫn) để Remove tìm được dù nextdns.exe đã đổi chỗ.
        private static readonly string[] RuleNames =
        {
            "Reclaim - nextdns block DNS UDP 53",
            "Reclaim - nextdns block DNS TCP 53",
            "Reclaim - block other apps DNS UDP",
            "Reclaim - block other apps DNS TCP",
            "Reclaim - block public DoH TCP",
            "Reclaim - block public DoH QUIC"
        };

        private static readonly PolicySpec[] Policies =
        {
            new PolicySpec { Id = "chrome-doh", KeyPath = @"SOFTWARE\Policies\Google\Chrome", ValueName = "DnsOverHttpsMode", Kind = RegistryValueKind.String, Value = "off" },
            new PolicySpec { Id = "edge-doh", KeyPath = @"SOFTWARE\Policies\Microsoft\Edge", ValueName = "DnsOverHttpsMode", Kind = RegistryValueKind.String, Value = "off" },
            new PolicySpec { Id = "brave-doh", KeyPath = @"SOFTWARE\Policies\BraveSoftware\Brave", ValueName = "DnsOverHttpsMode", Kind = RegistryValueKind.String, Value = "off" },
            new PolicySpec { Id = "firefox-doh", KeyPath = @"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", ValueName = "Enabled", Kind = RegistryValueKind.DWord, Value = 0 },
            new PolicySpec { Id = "firefox-doh-locked", KeyPath = @"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", ValueName = "Locked", Kind = RegistryValueKind.DWord, Value = 1 },
            new PolicySpec { Id = "smart-name-resolution", KeyPath = @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient", ValueName = "DisableSmartNameResolution", Kind = RegistryValueKind.DWord, Value = 1 }
        };

        private readonly ILog _log;
        private readonly HashSet<string> _reported = new HashSet<string>(); // lỗi đã ghi (Ensure chạy mỗi phút, không ghi lặp)

        public WindowsBypassGuard(ILog log)
        {
            _log = log;
        }

        // ------------------------------------------------------------------ Ensure / Remove

        public IList<string> Ensure()
        {
            var problems = new List<string>();
            EnsureFirewall(problems);
            if (problems.Count == 0) _reported.Clear(); // đã ổn: nếu lỗi quay lại thì ghi lại
            foreach (PolicySpec policy in Policies)
            {
                try { SetPolicy(policy); }
                catch (Exception ex)
                {
                    WarnOnce("policy:" + policy.Id, "Chính sách " + policy.Id + ": " + ex.Message);
                    problems.Add("chính sách " + policy.Id + " (" + ex.Message + ")");
                }
            }
            return problems;
        }

        public bool Remove()
        {
            bool ok = RemoveFirewall();
            foreach (PolicySpec policy in Policies)
            {
                try { RestorePolicy(policy); }
                catch (Exception ex)
                {
                    _log.Warn("Khôi phục chính sách " + policy.Id + ": " + ex.Message);
                    ok = false;
                }
            }
            return ok;
        }

        // ------------------------------------------------------------------ tường lửa

        private void WarnOnce(string key, string message)
        {
            if (_reported.Add(key + "|" + message)) _log.Warn(message);
        }

        private static IList<RuleSpec> BuildRules()
        {
            string exe = AppPaths.NextDnsExe;
            return new List<RuleSpec>
            {
                new RuleSpec { Name = RuleNames[0], Protocol = ProtoUdp, RemotePorts = "53", Application = exe },
                new RuleSpec { Name = RuleNames[1], Protocol = ProtoTcp, RemotePorts = "53", Application = exe },
                new RuleSpec { Name = RuleNames[2], Protocol = ProtoUdp, RemotePorts = "53,853", RemoteAddresses = NonLoopback },
                new RuleSpec { Name = RuleNames[3], Protocol = ProtoTcp, RemotePorts = "53,853", RemoteAddresses = NonLoopback },
                new RuleSpec { Name = RuleNames[4], Protocol = ProtoTcp, RemotePorts = "443", RemoteAddresses = PublicDnsProviders },
                new RuleSpec { Name = RuleNames[5], Protocol = ProtoUdp, RemotePorts = "443", RemoteAddresses = PublicDnsProviders }
            };
        }

        private void EnsureFirewall(List<string> problems)
        {
            object rules;
            try
            {
                rules = OpenRules();
            }
            catch (Exception ex)
            {
                string message = Describe(ex);
                WarnOnce("open", "Không mở được Windows Firewall: " + message);
                problems.Add("tường lửa (" + message + ")");
                return;
            }

            foreach (RuleSpec spec in BuildRules())
            {
                try { EnsureRule(rules, spec); }
                catch (Exception ex)
                {
                    string message = Describe(ex);
                    WarnOnce("rule:" + spec.Name, "Quy tắc tường lửa \"" + spec.Name + "\": " + message);
                    problems.Add("quy tắc tường lửa \"" + spec.Name + "\" (" + message + ")");
                }
            }
        }

        private void EnsureRule(object rules, RuleSpec spec)
        {
            object existing = FindRule(rules, spec.Name);
            if (existing != null)
            {
                if (RuleMatches(existing, spec)) return;
                Call(rules, "Remove", spec.Name);
            }

            object rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true));
            Set(rule, "Name", spec.Name);
            Set(rule, "Description", spec.Description);
            Set(rule, "Grouping", "Reclaim");
            if (spec.Application != null) Set(rule, "ApplicationName", spec.Application);
            Set(rule, "Protocol", spec.Protocol);               // phải đặt giao thức trước khi đặt cổng
            Set(rule, "RemotePorts", spec.RemotePorts);
            if (spec.RemoteAddresses != null) Set(rule, "RemoteAddresses", spec.RemoteAddresses);
            Set(rule, "Direction", DirectionOut);
            Set(rule, "Action", ActionBlock);
            Set(rule, "Profiles", ProfilesAll);
            Set(rule, "Enabled", true);
            Call(rules, "Add", rule);
            _log.Info("Đã tạo quy tắc tường lửa \"" + spec.Name + "\".");
        }

        private static bool RuleMatches(object rule, RuleSpec spec)
        {
            if (!Convert.ToBoolean(Get(rule, "Enabled"))) return false;
            if (Convert.ToInt32(Get(rule, "Direction")) != DirectionOut) return false;
            if (Convert.ToInt32(Get(rule, "Action")) != ActionBlock) return false;
            if (Convert.ToInt32(Get(rule, "Protocol")) != spec.Protocol) return false;
            if (!string.Equals(Convert.ToString(Get(rule, "RemotePorts")), spec.RemotePorts, StringComparison.OrdinalIgnoreCase)) return false;
            int profiles = Convert.ToInt32(Get(rule, "Profiles"));
            if (profiles != ProfilesAll && profiles != 7) return false; // "tất cả" có thể được trả về là 0x7FFFFFFF hoặc 7 (domain|private|public)
            if (spec.Application != null &&
                !string.Equals(Convert.ToString(Get(rule, "ApplicationName")), spec.Application, StringComparison.OrdinalIgnoreCase)) return false;
            // Mô tả mang phiên bản + đường dẫn + danh sách địa chỉ: đổi một trong ba thì quy tắc được tạo lại.
            // (Không so sánh trực tiếp RemoteAddresses vì Windows chuẩn hóa lại chuỗi này.)
            if (!string.Equals(Convert.ToString(Get(rule, "Description")), spec.Description, StringComparison.Ordinal)) return false;
            return true;
        }

        private bool RemoveFirewall()
        {
            bool ok = true;
            object rules = null;
            try { rules = OpenRules(); }
            catch (Exception ex)
            {
                _log.Warn("Không mở được Windows Firewall qua COM: " + Describe(ex));
            }

            foreach (string name in RuleNames)
            {
                bool removed = false;
                if (rules != null)
                {
                    try
                    {
                        if (FindRule(rules, name) != null) Call(rules, "Remove", name);
                        removed = true;
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("Gỡ quy tắc \"" + name + "\" qua COM: " + Describe(ex));
                    }
                }
                if (!removed)
                {
                    // Dự phòng bằng netsh. "show rule" trả mã khác 0 khi không có quy tắc nào tên đó (không phụ thuộc ngôn ngữ):
                    // khi đó coi như đã gỡ, nếu không thì máy không có quy tắc nào vẫn bị báo "gỡ thất bại" và kẹt khi tắt/gỡ cài đặt.
                    ProcessResult exists = ProcessUtil.Run("netsh.exe", "advfirewall firewall show rule name=\"" + name + "\"", 15000);
                    if (exists.Success)
                    {
                        ProcessResult r = ProcessUtil.Run("netsh.exe", "advfirewall firewall delete rule name=\"" + name + "\"", 15000);
                        if (!r.Success)
                        {
                            _log.Warn("netsh không xóa được quy tắc \"" + name + "\": " + r.Output);
                            ok = false;
                        }
                    }
                }
            }
            return ok;
        }

        private static object OpenRules()
        {
            object policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true));
            return Get(policy, "Rules");
        }

        private const int HResultFileNotFound = unchecked((int)0x80070002);

        /// <returns>
        /// null nếu không có quy tắc tên đó. Windows báo "không có" bằng HRESULT 0x80070002, và .NET đổi HRESULT này thành
        /// FileNotFoundException (không phải COMException), có thể bị bọc trong TargetInvocationException. Mọi lỗi khác được
        /// ném tiếp: nếu coi lỗi lạ là "không có" thì mỗi lần kiểm tra lại tạo thêm một quy tắc trùng.
        /// </returns>
        private static object FindRule(object rules, string name)
        {
            try { return Call(rules, "Item", name); }
            catch (Exception ex)
            {
                if (IsNotFound(ex)) return null;
                throw;
            }
        }

        private static bool IsNotFound(Exception ex)
        {
            Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            if (inner is FileNotFoundException) return true;
            COMException com = inner as COMException;
            return com != null && com.ErrorCode == HResultFileNotFound;
        }

        // ------------------------------------------------------------------ chính sách registry

        private void SetPolicy(PolicySpec spec)
        {
            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(spec.KeyPath))
            {
                object current = key.GetValue(spec.ValueName, null);
                bool has = current != null;
                if (has)
                {
                    RegistryValueKind currentKind = key.GetValueKind(spec.ValueName);
                    if (currentKind != RegistryValueKind.String && currentKind != RegistryValueKind.DWord)
                    {
                        // Không ghi đè cấu hình lạ của quản trị viên, nhưng phải báo: phạm vi bảo vệ đang bị giảm.
                        throw new InvalidOperationException("giá trị hiện có thuộc kiểu registry " + currentKind + " nên không được ghi đè");
                    }
                    if (currentKind == spec.Kind && ValueEquals(current, spec)) return; // đã đúng
                    SaveBackup(spec, true, currentKind, current);
                }
                else
                {
                    SaveBackup(spec, false, spec.Kind, null);
                }
                key.SetValue(spec.ValueName, spec.Value, spec.Kind);
                _log.Info("Đã đặt chính sách " + spec.Id + ".");
            }
        }

        private void RestorePolicy(PolicySpec spec)
        {
            using (RegistryKey backup = Registry.LocalMachine.OpenSubKey(PolicyBackupRoot + @"\" + spec.Id))
            {
                if (backup == null) return; // Reclaim chưa từng đổi giá trị này
                bool existed = Convert.ToInt32(backup.GetValue("existed", 0)) == 1;
                string kindText = backup.GetValue("kind", "String") as string;
                string valueText = backup.GetValue("value", string.Empty) as string;

                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(spec.KeyPath, true))
                {
                    if (key != null)
                    {
                        object current = key.GetValue(spec.ValueName, null);
                        // Chỉ động vào khi giá trị vẫn là của Reclaim; quản trị viên đã sửa sang giá trị khác thì để nguyên.
                        if (current != null && ValueEquals(current, spec))
                        {
                            if (existed)
                            {
                                var kind = (RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), kindText);
                                if (kind == RegistryValueKind.DWord) key.SetValue(spec.ValueName, Convert.ToInt32(valueText), kind);
                                else key.SetValue(spec.ValueName, valueText, RegistryValueKind.String);
                            }
                            else
                            {
                                key.DeleteValue(spec.ValueName, false);
                            }
                        }
                    }
                }
            }
            Registry.LocalMachine.DeleteSubKeyTree(PolicyBackupRoot + @"\" + spec.Id, false);
        }

        /// <summary>Chỉ ghi bản sao lưu lần đầu (giá trị gốc); các lần Ensure sau không ghi đè bằng giá trị của chính Reclaim.</summary>
        private static void SaveBackup(PolicySpec spec, bool existed, RegistryValueKind kind, object current)
        {
            using (RegistryKey existing = Registry.LocalMachine.OpenSubKey(PolicyBackupRoot + @"\" + spec.Id))
            {
                if (existing != null) return;
            }
            using (RegistryKey backup = Registry.LocalMachine.CreateSubKey(PolicyBackupRoot + @"\" + spec.Id))
            {
                backup.SetValue("existed", existed ? 1 : 0, RegistryValueKind.DWord);
                backup.SetValue("kind", kind.ToString(), RegistryValueKind.String);
                backup.SetValue("value", current == null ? string.Empty : Convert.ToString(current), RegistryValueKind.String);
            }
        }

        private static bool ValueEquals(object current, PolicySpec spec)
        {
            if (spec.Kind == RegistryValueKind.DWord)
            {
                return current is int && (int)current == Convert.ToInt32(spec.Value);
            }
            return string.Equals(current as string, Convert.ToString(spec.Value), StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ COM qua reflection (không cần tham chiếu thêm)

        private static object Get(object target, string name)
        {
            try { return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null); }
            catch (Exception ex) { throw Step("đọc " + name, ex); }
        }

        private static void Set(object target, string name, object value)
        {
            try { target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new object[] { value }); }
            catch (Exception ex) { throw Step("đặt " + name + "=" + value, ex); }
        }

        private static object Call(object target, string name, params object[] args)
        {
            try { return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args); }
            catch (Exception ex)
            {
                if (name == "Item" && IsNotFound(ex)) throw; // trường hợp bình thường: để FindRule xử lý
                throw Step("gọi " + name, ex);
            }
        }

        private static InvalidOperationException Step(string step, Exception ex)
        {
            return new InvalidOperationException("bước \"" + step + "\": " + Describe(ex), ex);
        }

        private static string Describe(Exception ex)
        {
            TargetInvocationException wrapped = ex as TargetInvocationException;
            return wrapped != null && wrapped.InnerException != null ? wrapped.InnerException.Message : ex.Message;
        }
    }
}
