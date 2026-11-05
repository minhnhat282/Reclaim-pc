using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Reclaim.Desktop.Services
{
    internal sealed class ElevationResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
    }

    internal static class Elevation
    {
        private const int ErrorCancelled = 1223;

        /// <summary>
        /// Chạy "Reclaim.Service.exe &lt;verb&gt;" với quyền quản trị (hiện hộp thoại UAC) và đọc kết quả
        /// qua một file tạm, vì tiến trình nâng quyền không thể chuyển hướng đầu ra.
        /// </summary>
        public static ElevationResult Run(string exePath, string verb)
        {
            return Run(exePath, verb, false);
        }

        /// <param name="visible">true nếu tiến trình nâng quyền có cửa sổ cần hiện ra (hộp thoại); false thì chạy ẩn.</param>
        public static ElevationResult Run(string exePath, string verb, bool visible)
        {
            string resultFile = Path.Combine(Path.GetTempPath(), "reclaim-" + Guid.NewGuid().ToString("N") + ".txt");
            var info = new ProcessStartInfo(exePath, verb + " --result \"" + resultFile + "\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(exePath)
            };

            try
            {
                using (Process p = Process.Start(info))
                {
                    p.WaitForExit();
                }
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == ErrorCancelled)
                    return new ElevationResult { Ok = false, Message = "Bạn đã hủy yêu cầu quyền quản trị." };
                return new ElevationResult { Ok = false, Message = ex.Message };
            }

            string text = string.Empty;
            try
            {
                if (File.Exists(resultFile))
                {
                    text = File.ReadAllText(resultFile).Trim();
                    File.Delete(resultFile);
                }
            }
            catch
            {
            }

            if (text == "OK") return new ElevationResult { Ok = true, Message = "OK" };
            return new ElevationResult { Ok = false, Message = text.Length > 0 ? text : "Thao tác không hoàn tất." };
        }
    }
}
