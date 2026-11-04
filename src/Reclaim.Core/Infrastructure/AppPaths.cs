using System;
using System.IO;

namespace Reclaim.Core.Infrastructure
{
    public static class AppPaths
    {
        public const string ProductName = "Reclaim";
        public const string ServiceName = "ReclaimSvc";
        public const string PipeName = "reclaim-ipc";
        public const string ServiceExeName = "Reclaim.Service.exe";
        public const string GuiExeName = "Reclaim.exe";

        public static readonly string InstallDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName);

        public static readonly string DataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductName);

        /// <summary>Thư mục chứa file thực thi đang chạy (khi đã cài đặt thì trùng InstallDir).</summary>
        public static string BaseDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }
        }

        public static string SettingsFile { get { return Path.Combine(DataDir, "settings.json"); } }
        public static string NextDnsConfFile { get { return Path.Combine(DataDir, "nextdns.conf"); } }
        public static string DnsBackupFile { get { return Path.Combine(DataDir, "dns-backup.json"); } }
        public static string LogFile { get { return Path.Combine(DataDir, "reclaim.log"); } }

        public static string NextDnsExe
        {
            get { return Path.Combine(BaseDir, "nextdns", "nextdns.exe"); }
        }

        /// <summary>Ưu tiên bản đã cài đặt; nếu chưa cài thì dùng bản cạnh ứng dụng.</summary>
        public static string FindServiceExe()
        {
            string installed = Path.Combine(InstallDir, ServiceExeName);
            return File.Exists(installed) ? installed : Path.Combine(BaseDir, ServiceExeName);
        }
    }
}
