using System.Diagnostics;
using System.Text;

namespace Reclaim.Core.Infrastructure
{
    public sealed class ProcessResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public bool Success { get { return ExitCode == 0; } }
    }

    public static class ProcessUtil
    {
        /// <summary>Chạy tiến trình ẩn, thu stdout+stderr. Đối số được truyền thẳng, không qua shell.</summary>
        public static ProcessResult Run(string fileName, string arguments, int timeoutMs)
        {
            var info = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = new Process { StartInfo = info })
            {
                var sb = new StringBuilder();
                process.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) lock (sb) { sb.AppendLine(e.Data); }
                };
                process.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) lock (sb) { sb.AppendLine(e.Data); }
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { }
                    return new ProcessResult { ExitCode = -1, Output = "timeout" };
                }
                process.WaitForExit(); // đảm bảo đã đọc hết đầu ra
                lock (sb)
                {
                    return new ProcessResult { ExitCode = process.ExitCode, Output = sb.ToString().Trim() };
                }
            }
        }
    }
}
