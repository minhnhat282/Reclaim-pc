using System;
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
