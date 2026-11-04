using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Serialization;
using Microsoft.Win32;
using Reclaim.Core.Application;
using Reclaim.Core.Ipc;

namespace Reclaim.Core.Infrastructure
{
    [DataContract]
    internal sealed class DnsBackupEntry
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        // Danh sách DNS tĩnh ban đầu của từng họ IP: "" = DHCP, null = Reclaim chưa từng đổi họ này (không được đụng khi khôi phục).
        [DataMember(Name = "v4")] public string V4 { get; set; }
        [DataMember(Name = "v6")] public string V6 { get; set; }
    }

    [DataContract]
    internal sealed class DnsBackupFile
    {
        [DataMember(Name = "entries")] public List<DnsBackupEntry> Entries { get; set; }
    }

    /// <summary>
    /// Đặt DNS của mọi adapter đang hoạt động về 127.0.0.1 / ::1 bằng netsh (theo chỉ số adapter nên không phụ thuộc ngôn ngữ).
    /// Lưu DNS tĩnh ban đầu của từng adapter để khôi phục chính xác khi tắt/gỡ cài đặt.
    /// IPv6 trỏ về ::1 giống hành vi "nextdns activate" của upstream (nextdns chỉ lắng nghe IPv4;
    /// truy vấn tới ::1 thất bại ngay và Windows chuyển sang IPv4).
    /// </summary>
    public sealed class NetshDnsConfigurator : IDnsConfigurator
    {
        private const string Loop4 = "127.0.0.1";
        private const string Loop6 = "::1";
        private const string RegV4 = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";
        private const string RegV6 = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\";
        private const string RegBackupRoot = @"SOFTWARE\Reclaim\DnsBackup";
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(60);

        private readonly ILog _log;
        private readonly object _sync = new object();
        private readonly Dictionary<string, DateTime> _retryAfter = new Dictionary<string, DateTime>();
        private List<DnsBackupEntry> _backup;

        public NetshDnsConfigurator(ILog log)
        {
            _log = log;
        }

        public void RouteToLoopback()
        {
            lock (_sync)
            {
                bool changed = false;
                foreach (NetworkInterface nic in Candidates())
                {
                    try { changed |= EnforceOne(nic); }
                    catch (Exception ex) { _log.Warn("DNS " + nic.Name + ": " + ex.Message); }
                }
                if (changed) FlushCache();
            }
        }

        /// <returns>true nếu mọi adapter đã được khôi phục. Bản sao lưu của adapter chưa khôi phục được GIỮ LẠI để thử lại.</returns>
        public bool ResetAll()
        {
            lock (_sync)
            {
                LoadBackup();
                bool allOk = true;
                bool any = false;
                var restored = new HashSet<string>();
                var present = new HashSet<string>();
                foreach (NetworkInterface nic in Candidates())
                {
                    present.Add(nic.Id);
                    try
                    {
                        DnsBackupEntry entry = _backup.Find(e => e.Id == nic.Id);
                        // Chỉ khôi phục khi có bản sao lưu: không bao giờ đoán và ghi đè cấu hình (kể cả loopback) của người dùng.
                        if (entry == null) continue;
                        IPInterfaceProperties props = nic.GetIPProperties();
                        int i4 = Index(props, false);
                        int i6 = Index(props, true);
                        bool ok = true;
                        if (entry.V4 != null) ok &= RestoreFamily(nic.Id, false, i4, entry.V4);
                        if (entry.V6 != null) ok &= RestoreFamily(nic.Id, true, i6, entry.V6);
                        any = true;
                        if (ok) restored.Add(nic.Id);
                        else
                        {
                            allOk = false;
                            _log.Warn("Chưa khôi phục được DNS của \"" + nic.Name + "\"; giữ lại bản sao lưu để thử lại.");
                        }
                    }
                    catch (Exception ex)
                    {
                        allOk = false;
                        _log.Warn("Reset DNS " + nic.Name + ": " + ex.Message);
                    }
                }
                // Adapter có trong bản sao lưu nhưng hiện không có (rút card USB, đã tắt...): khôi phục thẳng vào registry
                // để khi adapter xuất hiện lại không còn sót DNS loopback.
                foreach (DnsBackupEntry entry in _backup.ToArray())
                {
                    if (present.Contains(entry.Id) || restored.Contains(entry.Id)) continue;
                    if (RestoreViaRegistry(entry)) restored.Add(entry.Id);
                    else allOk = false;
                }

                // Chỉ xóa bản sao lưu của adapter đã khôi phục thành công.
                _backup.RemoveAll(e => restored.Contains(e.Id));
                if (!SaveBackup()) allOk = false;
                _retryAfter.Clear();
                if (any) FlushCache();
                if (allOk) _log.Info("Đã khôi phục DNS ban đầu.");
                return allOk;
            }
        }

        public bool RecoverOrphans(Func<bool> resolverAnswers)
        {
            lock (_sync)
            {
                LoadBackup();
                var orphans = new List<KeyValuePair<NetworkInterface, bool[]>>();
                foreach (NetworkInterface nic in Candidates())
                {
                    if (_backup.Exists(e => string.Equals(e.Id, nic.Id, StringComparison.OrdinalIgnoreCase))) continue; // có bản ghi: ResetAll lo
                    bool o4 = IsOurLoopback(ReadNameServer(nic.Id, false), false);
                    bool o6 = IsOurLoopback(ReadNameServer(nic.Id, true), true);
                    if (o4 || o6) orphans.Add(new KeyValuePair<NetworkInterface, bool[]>(nic, new[] { o4, o6 }));
                }
                if (orphans.Count == 0) return true;

                // Có bộ phân giải đang trả lời ở loopback -> có thể là của người dùng, không đụng vào.
                if (resolverAnswers()) return true;

                bool allOk = true;
                foreach (KeyValuePair<NetworkInterface, bool[]> item in orphans)
                {
                    try
                    {
                        IPInterfaceProperties props = item.Key.GetIPProperties();
                        bool ok = true;
                        if (item.Value[0]) ok &= RestoreFamily(item.Key.Id, false, Index(props, false), string.Empty);
                        if (item.Value[1]) ok &= RestoreFamily(item.Key.Id, true, Index(props, true), string.Empty);
                        if (ok) _log.Warn("Đã trả về DHCP DNS loopback mồ côi của \"" + item.Key.Name + "\" (không có bản sao lưu và không có bộ phân giải).");
                        else allOk = false;
                    }
                    catch (Exception ex)
                    {
                        allOk = false;
                        _log.Warn("Dọn DNS mồ côi " + item.Key.Name + ": " + ex.Message);
                    }
                }
                FlushCache();
                return allOk;
            }
        }

        // ------------------------------------------------------------------

        private static IEnumerable<NetworkInterface> Candidates()
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                yield return nic;
            }
        }

        private bool EnforceOne(NetworkInterface nic)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) return false;
            IPInterfaceProperties props = nic.GetIPProperties();
            int i4 = Index(props, false);
            int i6 = Index(props, true);
            bool changed = false;

            if (i4 >= 0 && !IsCompliant(props, AddressFamily.InterNetwork))
            {
                if (!BackupFamily(nic, false)) return changed; // không có bản sao lưu thì KHÔNG đổi DNS
                changed |= Apply(nic, false, i4);
            }
            if (i6 >= 0 && !IsCompliant(props, AddressFamily.InterNetworkV6))
            {
                if (!BackupFamily(nic, true)) return changed;
                changed |= Apply(nic, true, i6);
            }
            return changed;
        }

        private static bool IsCompliant(IPInterfaceProperties props, AddressFamily family)
        {
            int count = 0;
            foreach (IPAddress a in props.DnsAddresses)
            {
                if (a.AddressFamily != family) continue;
                count++;
                if (!IPAddress.IsLoopback(a)) return false;
            }
            return count == 1;
        }

        private static int Index(IPInterfaceProperties props, bool v6)
        {
            try
            {
                return v6 ? props.GetIPv6Properties().Index : props.GetIPv4Properties().Index;
            }
            catch (NetworkInformationException)
            {
                return -1; // adapter không bật họ giao thức này
            }
        }

        private bool Apply(NetworkInterface nic, bool v6, int index)
        {
            string key = nic.Id + (v6 ? "/6" : "/4");
            DateTime next;
            if (_retryAfter.TryGetValue(key, out next) && DateTime.UtcNow < next) return false;

            string family = v6 ? "ipv6" : "ipv4";
            ProcessResult r = ProcessUtil.Run("netsh.exe",
                "interface " + family + " set dnsservers name=" + index + " source=static address=" + (v6 ? Loop6 : Loop4) + " validate=no",
                15000);
            if (r.Success)
            {
                _retryAfter.Remove(key);
                _log.Info("Đã đặt DNS " + family + " của \"" + nic.Name + "\" về loopback.");
                return true;
            }
            _retryAfter[key] = DateTime.UtcNow + RetryDelay;
            _log.Warn("netsh " + family + " \"" + nic.Name + "\" thất bại: " + r.Output);
            return false;
        }

        /// <summary>
        /// Khôi phục một họ IP. Nếu họ đó hiện không có index (đang tắt trên adapter) thì khôi phục thẳng trong registry
        /// khi còn DNS loopback, để khi họ IP được bật lại không còn sót cấu hình của Reclaim.
        /// </summary>
        private bool RestoreFamily(string nicId, bool v6, int index, string originalList)
        {
            if (index >= 0) return Restore(v6 ? "ipv6" : "ipv4", index, originalList);
            if (!IsLoopback(ReadNameServer(nicId, v6))) return true; // không còn dấu vết của Reclaim
            return WriteNameServer((v6 ? RegV6 : RegV4) + nicId, originalList);
        }

        /// <returns>true nếu khôi phục thành công. KHÔNG tự chuyển sang DHCP khi khôi phục DNS tĩnh thất bại (sẽ làm mất cấu hình gốc).</returns>
        private bool Restore(string family, int index, string originalList)
        {
            string[] servers = Split(originalList);
            if (servers.Length == 0)
            {
                return ProcessUtil.Run("netsh.exe", "interface " + family + " set dnsservers name=" + index + " source=dhcp", 15000).Success;
            }

            ProcessResult first = ProcessUtil.Run("netsh.exe",
                "interface " + family + " set dnsservers name=" + index + " source=static address=" + servers[0] + " validate=no", 15000);
            if (!first.Success)
            {
                _log.Warn("netsh " + family + " khôi phục DNS tĩnh thất bại: " + first.Output);
                return false;
            }
            bool ok = true;
            for (int i = 1; i < servers.Length; i++)
            {
                ProcessResult add = ProcessUtil.Run("netsh.exe",
                    "interface " + family + " add dnsservers name=" + index + " address=" + servers[i] + " index=" + (i + 1) + " validate=no", 15000);
                if (!add.Success) ok = false;
            }
            return ok;
        }

        private bool RestoreViaRegistry(DnsBackupEntry entry)
        {
            bool ok = true;
            if (entry.V4 != null) ok &= WriteNameServer(RegV4 + entry.Id, entry.V4);
            if (entry.V6 != null) ok &= WriteNameServer(RegV6 + entry.Id, entry.V6);
            return ok;
        }

        private bool WriteNameServer(string keyPath, string servers)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(keyPath, true))
                {
                    if (key == null) return true; // adapter đã bị gỡ hẳn: không còn gì để khôi phục
                    key.SetValue("NameServer", string.Join(",", Split(servers)), RegistryValueKind.String);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Khôi phục DNS qua registry thất bại (" + keyPath + "): " + ex.Message);
                return false;
            }
        }

        private static void FlushCache()
        {
            ProcessUtil.Run("ipconfig.exe", "/flushdns", 5000);
        }

        // ------------------------------------------------------------------ sao lưu DNS gốc

        /// <summary>
        /// Sao lưu DNS ban đầu của MỘT họ IP trên adapter, ngay trước khi Reclaim đổi họ đó.
        /// </summary>
        /// <returns>true nếu bản sao lưu ĐÃ ĐƯỢC GHI XUỐNG ĐĨA (chỉ khi đó mới được đổi DNS).</returns>
        private bool BackupFamily(NetworkInterface nic, bool v6)
        {
            LoadBackup();
            DnsBackupEntry entry = _backup.Find(e => e.Id == nic.Id);
            if (entry != null && (v6 ? entry.V6 : entry.V4) != null) return true; // đã sao lưu họ này

            string retryKey = nic.Id + (v6 ? "/bk6" : "/bk4");
            DateTime next;
            if (_retryAfter.TryGetValue(retryKey, out next) && DateTime.UtcNow < next) return false;

            bool isNew = entry == null;
            if (isNew)
            {
                entry = new DnsBackupEntry { Id = nic.Id };
                _backup.Add(entry);
            }
            string original = OriginalOrEmpty(ReadNameServer(nic.Id, v6), v6);
            if (v6) entry.V6 = original; else entry.V4 = original;

            if (!SaveBackup())
            {
                // hoàn nguyên bộ nhớ để lần sau thử lại
                if (isNew) _backup.Remove(entry);
                else if (v6) entry.V6 = null; else entry.V4 = null;
                _retryAfter[retryKey] = DateTime.UtcNow + RetryDelay;
                _log.Error("Không lưu được bản sao DNS; bỏ qua \"" + nic.Name + "\" để không mất DNS gốc.");
                return false;
            }
            _retryAfter.Remove(retryKey);
            return true;
        }

        private static string ReadNameServer(string nicId, bool v6)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey((v6 ? RegV6 : RegV4) + nicId))
                {
                    object value = key == null ? null : key.GetValue("NameServer");
                    return value == null ? string.Empty : value.ToString().Trim();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Reclaim chỉ bao giờ đặt ĐÚNG MỘT địa chỉ (127.0.0.1 cho IPv4, ::1 cho IPv6). Danh sách y hệt như vậy khi ta chuẩn bị
        /// sao lưu nghĩa là Reclaim đã đặt nó trước đây (bản sao lưu cũ đã mất) -> coi như DHCP.
        /// Mọi danh sách khác, kể cả nhiều loopback do người dùng chủ ý cấu hình, được giữ NGUYÊN VẸN.
        /// </summary>
        private static string OriginalOrEmpty(string list, bool v6)
        {
            return IsOurLoopback(list, v6) ? string.Empty : (list ?? string.Empty);
        }

        /// <summary>true nếu danh sách là ĐÚNG MỘT địa chỉ loopback mà Reclaim đặt (127.0.0.1 / ::1).</summary>
        private static bool IsOurLoopback(string list, bool v6)
        {
            string[] items = Split(list);
            if (items.Length != 1) return false;
            IPAddress ip;
            return IPAddress.TryParse(items[0], out ip) && ip.Equals(v6 ? IPAddress.IPv6Loopback : IPAddress.Loopback);
        }

        private static bool IsLoopback(string list)
        {
            foreach (string s in Split(list))
            {
                IPAddress ip;
                if (IPAddress.TryParse(s, out ip) && IPAddress.IsLoopback(ip)) return true;
            }
            return false;
        }

        private static string[] Split(string list)
        {
            if (string.IsNullOrWhiteSpace(list)) return new string[0];
            return list.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private void LoadBackup()
        {
            if (_backup != null) return;
            _backup = TryReadBackup(AppPaths.DnsBackupFile)
                      ?? TryReadBackup(AppPaths.DnsBackupFile + ".bak")
                      ?? new List<DnsBackupEntry>();
            MergeRegistryBackup(); // file mất/hỏng thì vẫn còn bản sao thứ hai trong registry
        }

        private List<DnsBackupEntry> TryReadBackup(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                DnsBackupFile file = Json.Deserialize<DnsBackupFile>(File.ReadAllBytes(path));
                return file != null && file.Entries != null ? file.Entries : new List<DnsBackupEntry>();
            }
            catch (Exception ex)
            {
                _log.Warn("Không đọc được " + Path.GetFileName(path) + ": " + ex.Message);
                return null;
            }
        }

        /// <returns>true nếu bản sao lưu đã được ghi bền vững ở ÍT NHẤT MỘT nơi (file hoặc registry).</returns>
        private bool SaveBackup()
        {
            bool fileOk;
            try
            {
                AtomicFile.Write(AppPaths.DnsBackupFile, Json.Serialize(new DnsBackupFile { Entries = _backup }));
                fileOk = true;
            }
            catch (Exception ex)
            {
                _log.Warn("Không ghi được dns-backup.json: " + ex.Message);
                fileOk = false;
            }
            bool registryOk = SyncRegistryBackup();
            return fileOk || registryOk;
        }

        /// <summary>Bản sao thứ hai: HKLM\SOFTWARE\Reclaim\DnsBackup\{adapter}\V4|V6.</summary>
        private bool SyncRegistryBackup()
        {
            try
            {
                using (RegistryKey root = Registry.LocalMachine.CreateSubKey(RegBackupRoot))
                {
                    var keep = new HashSet<string>(_backup.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
                    foreach (string name in root.GetSubKeyNames())
                    {
                        if (!keep.Contains(name)) root.DeleteSubKeyTree(name, false);
                    }
                    foreach (DnsBackupEntry entry in _backup)
                    {
                        using (RegistryKey key = root.CreateSubKey(entry.Id))
                        {
                            SetOrDelete(key, "V4", entry.V4);
                            SetOrDelete(key, "V6", entry.V6);
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                _log.Warn("Không ghi được bản sao DNS trong registry: " + ex.Message);
                return false;
            }
        }

        private static void SetOrDelete(RegistryKey key, string name, string value)
        {
            if (value == null) key.DeleteValue(name, false);
            else key.SetValue(name, value, RegistryValueKind.String);
        }

        private void MergeRegistryBackup()
        {
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(RegBackupRoot))
                {
                    if (root == null) return;
                    foreach (string id in root.GetSubKeyNames())
                    {
                        using (RegistryKey key = root.OpenSubKey(id))
                        {
                            if (key == null) continue;
                            string v4 = key.GetValue("V4") as string;
                            string v6 = key.GetValue("V6") as string;
                            if (v4 == null && v6 == null) continue;
                            DnsBackupEntry existing = _backup.Find(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
                            if (existing == null)
                            {
                                _backup.Add(new DnsBackupEntry { Id = id, V4 = v4, V6 = v6 });
                            }
                            else
                            {
                                if (existing.V4 == null) existing.V4 = v4;
                                if (existing.V6 == null) existing.V6 = v6;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Không đọc được bản sao DNS trong registry: " + ex.Message);
            }
        }
    }
}
