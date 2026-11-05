using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Reclaim.Core.Application;

namespace Reclaim.Core.Infrastructure
{
    /// <summary>
    /// Thử một ID NextDNS bằng một tiến trình nextdns tạm nghe ở cổng khác (127.0.0.1:cổng-tự-do), tách hẳn khỏi
    /// nextdns đang phục vụ ở cổng 53: nhập sai ID khi đang bảo vệ không làm mất DNS của máy.
    /// </summary>
    public sealed class NextDnsProfileVerifier : IProfileVerifier
    {
        private const string TempControl = "reclaim-nextdns-verify";

        private readonly string _exePath;
        private readonly string _confPath;
        private readonly ILog _log;

        public NextDnsProfileVerifier(string exePath, string confPath, ILog log)
        {
            _exePath = exePath;
            _confPath = confPath;
            _log = log;
        }

        public string Verify(string profileId, int timeoutMs)
        {
            if (!File.Exists(_exePath)) return "Không tìm thấy nextdns.exe để kiểm tra ID.";
            int port = FreePort();
            if (port <= 0)
            {
                _log.Warn("Không tìm được cổng tạm để kiểm tra ID; bỏ qua bước kiểm tra.");
                return null;
            }

            var recent = new Queue<string>();
            Process process = null;
            try
            {
                var listens = new List<string> { "127.0.0.1:" + port };
                NextDnsConfig.WriteContent(_confPath, NextDnsConfig.Render(profileId, listens, TempControl));

                var info = new ProcessStartInfo(_exePath, "run -config-file \"" + _confPath + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(_exePath)
                };
                process = new Process { StartInfo = info };
                DataReceivedEventHandler collect = delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (recent)
                    {
                        recent.Enqueue(e.Data);
                        while (recent.Count > 30) recent.Dequeue();
                    }
                };
                process.OutputDataReceived += collect;
                process.ErrorDataReceived += collect;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var probe = new DnsProbe();
                var endpoint = new IPEndPoint(IPAddress.Loopback, port);
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                bool answered = false;
                int last = -1;
                while (DateTime.UtcNow < deadline)
                {
                    if (process.HasExited)
                        return "nextdns tạm thoát ngay khi khởi động" + Detail(recent) + ".";
                    last = probe.QueryRcode(endpoint, 7000);
                    if (last == 0 || last == 3)
                    {
                        _log.Info("ID NextDNS đã được kiểm tra trên cổng tạm " + port + ".");
                        return null;
                    }
                    if (last >= 0) answered = true;
                    Thread.Sleep(300);
                }

                _log.Warn("Kiểm tra ID thất bại (mã DNS cuối: " + last + ")." + Detail(recent));
                return answered
                    ? "NextDNS không chấp nhận ID này (mã DNS " + last + "). Hãy kiểm tra lại ID; bảo vệ hiện tại vẫn giữ nguyên."
                    : "Không kết nối được tới NextDNS bằng ID này. Hãy kiểm tra ID và kết nối mạng; bảo vệ hiện tại vẫn giữ nguyên.";
            }
            finally
            {
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    try { process.WaitForExit(3000); } catch { }
                    process.Dispose();
                }
                try { File.Delete(_confPath); } catch { }
            }
        }

        private static string Detail(Queue<string> recent)
        {
            lock (recent)
            {
                string found = null;
                foreach (string line in recent)
                {
                    string l = line.ToLowerInvariant();
                    if (l.Contains(".error(") || l.Contains(".crit(") || l.Contains("panic") || l.Contains("fatal")) found = line;
                }
                return found == null ? string.Empty : " (" + found + ")";
            }
        }

        private static int FreePort()
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
            catch
            {
                return 0;
            }
        }
    }
}
