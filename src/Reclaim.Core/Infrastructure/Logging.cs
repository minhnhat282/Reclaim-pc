using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Reclaim.Core.Application;

namespace Reclaim.Core.Infrastructure
{
    /// <summary>Nhật ký file có xoay vòng (512 KB x 2). Không bao giờ ghi mật khẩu hay truy vấn DNS.</summary>
    public sealed class FileLog : ILog
    {
        private const long MaxBytes = 512 * 1024;
        private readonly string _path;
        private readonly bool _echo;
        private readonly object _sync = new object();

        public FileLog(string path, bool echoToConsole)
        {
            _path = path;
            _echo = echoToConsole;
        }

        /// <summary>N dòng cuối của nhật ký (đọc cả file xoay vòng .1 nếu file hiện tại ngắn).</summary>
        public IList<string> Tail(int count)
        {
            var lines = new List<string>();
            lock (_sync)
            {
                foreach (string path in new[] { _path + ".1", _path })
                {
                    try
                    {
                        if (!File.Exists(path)) continue;
                        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null) lines.Add(line);
                        }
                    }
                    catch (Exception ex)
                    {
                        lines.Add("(không đọc được " + Path.GetFileName(path) + ": " + ex.Message + ")");
                    }
                }
            }
            if (lines.Count > count) lines.RemoveRange(0, lines.Count - count);
            return lines;
        }

        public void Info(string message) { Write("INFO", message); }
        public void Warn(string message) { Write("WARN", message); }
        public void Error(string message) { Write("ERROR", message); }

        private void Write(string level, string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "] " + message;
            if (_echo) Console.WriteLine(line);
            lock (_sync)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    var info = new FileInfo(_path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        string old = _path + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(_path, old);
                    }
                    File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // Nhật ký không được làm hỏng dịch vụ.
                }
            }
        }
    }

    public sealed class NullLog : ILog
    {
        public static readonly NullLog Instance = new NullLog();
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }
}
