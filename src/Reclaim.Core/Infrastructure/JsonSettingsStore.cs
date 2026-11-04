using System;
using System.IO;
using Reclaim.Core.Application;
using Reclaim.Core.Domain;
using Reclaim.Core.Ipc;

namespace Reclaim.Core.Infrastructure
{
    /// <summary>settings.json, ghi nguyên tử (file tạm + thay thế) và giữ một bản .bak.</summary>
    public sealed class JsonSettingsStore : ISettingsStore
    {
        private readonly string _path;
        private readonly ILog _log;
        private readonly object _sync = new object();

        public JsonSettingsStore(string path, ILog log)
        {
            _path = path;
            _log = log;
        }

        public AppSettings Load()
        {
            lock (_sync)
            {
                AppSettings s = TryRead(_path);
                if (s == null) s = TryRead(_path + ".bak");
                if (s == null)
                {
                    if (File.Exists(_path))
                    {
                        _log.Error("settings.json hỏng, dùng cài đặt mặc định.");
                        try { File.Move(_path, _path + ".corrupt"); } catch { }
                    }
                    s = AppSettings.CreateDefault();
                }
                return s.Normalize();
            }
        }

        public void Save(AppSettings settings)
        {
            lock (_sync)
            {
                AtomicFile.Write(_path, Json.Serialize(settings));
            }
        }

        private static AppSettings TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return Json.Deserialize<AppSettings>(File.ReadAllBytes(path));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
