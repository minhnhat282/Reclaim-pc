using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Reclaim.Core.Application;

namespace Reclaim.Core.Infrastructure
{
    /// <summary>
    /// Chạy "nextdns.exe run -config-file ..." như tiến trình con và tự khởi động lại khi chết
    /// (lùi dần 0.5s ... 10s). nextdns chạy chế độ foreground vì tiến trình cha không phải services.exe.
    /// </summary>
    public sealed class NextDnsProcessRunner : INextDnsRunner
    {
        private const int MaxLogLines = 100;

        private readonly string _exe;
        private readonly string _conf;
        private readonly ILog _log;
        private readonly object _sync = new object();
        private readonly Queue<string> _lines = new Queue<string>();
        private readonly object _linesSync = new object();

        private Process _process;
        private Timer _restartTimer;
        private bool _wantRunning;
        private int _failures;
        private int _restarts;
        private DateTime _startedAtUtc;

        public NextDnsProcessRunner(string exePath, string confPath, ILog log)
        {
            _exe = exePath;
            _conf = confPath;
            _log = log;
        }

        public bool IsRunning
        {
            get
            {
                lock (_sync)
                {
                    return _process != null && !HasExited(_process);
                }
            }
        }

        public int RestartCount
        {
            get { lock (_sync) { return _restarts; } }
        }

        public int ConsecutiveFailures
        {
            get { lock (_sync) { return _failures; } }
        }

        /// <summary>
        /// Dòng lỗi (error/crit/panic/fatal) gần nhất trong nhật ký của nextdns, ví dụ lỗi không bind được cổng 53.
        /// </summary>
        public string LastProblemLine()
        {
            lock (_linesSync)
            {
                string found = null;
                foreach (string line in _lines)
                {
                    string l = line.ToLowerInvariant();
                    if (l.Contains(".error(") || l.Contains(".crit(") || l.Contains("panic") || l.Contains("fatal")) found = line;
                }
                return found;
            }
        }

        public string ComputeBinaryHash()
        {
            if (!File.Exists(_exe)) return null;
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(_exe))
            {
                byte[] hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public void Start(string profileId)
        {
            lock (_sync)
            {
                _wantRunning = false;
                CancelTimerLocked();
                KillLocked();

                NextDnsConfig.Write(_conf, profileId);
                _wantRunning = true;
                _failures = 0;
                SpawnLocked();
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                _wantRunning = false;
                CancelTimerLocked();
                KillLocked();
            }
        }

        public IList<string> RecentLog()
        {
            lock (_linesSync)
            {
                return new List<string>(_lines);
            }
        }

        public void Dispose()
        {
            Stop();
        }

        // ------------------------------------------------------------------

        private void SpawnLocked()
        {
            if (!File.Exists(_exe)) throw new FileNotFoundException("Không tìm thấy nextdns.exe", _exe);
            KillStrays();

            var info = new ProcessStartInfo(_exe, "run -config-file \"" + _conf + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_exe)
            };
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { Capture(e.Data); };
            process.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { Capture(e.Data); };
            process.Exited += OnExited;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _process = process;
            _startedAtUtc = DateTime.UtcNow;
            _log.Info("Đã chạy nextdns (pid " + process.Id + ").");
        }

        private void OnExited(object sender, EventArgs e)
        {
            lock (_sync)
            {
                if (!ReferenceEquals(sender, _process)) return; // tiến trình cũ đã bị thay thế
                int code = -1;
                try { code = _process.ExitCode; } catch { }
                _log.Warn("nextdns đã thoát (mã " + code + ").");
                if (!_wantRunning) return;

                if (DateTime.UtcNow - _startedAtUtc > TimeSpan.FromSeconds(60)) _failures = 0;
                ScheduleRestartLocked();
            }
        }

        private void ScheduleRestartLocked()
        {
            int delay = Math.Min(10000, 500 * (1 << Math.Min(_failures, 5)));
            _failures++;
            CancelTimerLocked();
            _restartTimer = new Timer(OnRestartTimer, null, delay, Timeout.Infinite);
        }

        private void OnRestartTimer(object state)
        {
            lock (_sync)
            {
                if (!_wantRunning) return;
                if (_process != null && !HasExited(_process)) return;
                try
                {
                    _restarts++;
                    SpawnLocked();
                }
                catch (Exception ex)
                {
                    _log.Error("Không chạy lại được nextdns: " + ex.Message);
                    ScheduleRestartLocked();
                }
            }
        }

        private void CancelTimerLocked()
        {
            if (_restartTimer != null)
            {
                _restartTimer.Dispose();
                _restartTimer = null;
            }
        }

        private void KillLocked()
        {
            Process p = _process;
            _process = null; // để OnExited của tiến trình này bị bỏ qua
            if (p == null) return;
            try
            {
                if (!p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
            }
            catch { }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }

        /// <summary>Diệt nextdns.exe "mồ côi" của chính Reclaim (ví dụ sau khi dịch vụ bị kill) để giải phóng cổng 53.</summary>
        private void KillStrays()
        {
            foreach (Process p in Process.GetProcessesByName("nextdns"))
            {
                try
                {
                    string path = p.MainModule.FileName;
                    if (string.Equals(path, _exe, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
        }

        private static bool HasExited(Process p)
        {
            try { return p.HasExited; }
            catch { return true; }
        }

        private void Capture(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.Length > 300) line = line.Substring(0, 300);
            lock (_linesSync)
            {
                _lines.Enqueue(line);
                while (_lines.Count > MaxLogLines) _lines.Dequeue();
            }
            _log.Info("nextdns: " + line);
        }
    }
}
