using System.IO;

namespace Reclaim.Core.Infrastructure
{
    /// <summary>Ghi file nguyên tử: ghi file tạm (flush xuống đĩa) rồi thay thế, giữ một bản .bak.</summary>
    internal static class AtomicFile
    {
        public static void Write(string path, byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(true);
            }
            if (File.Exists(path))
            {
                File.Replace(temp, path, path + ".bak");
            }
            else
            {
                File.Move(temp, path);
            }
        }
    }
}
