using System;
using System.IO;

namespace Reclaim.Core.Ipc
{
    /// <summary>Khung thông điệp: 4 byte độ dài (little-endian) + dữ liệu.</summary>
    internal static class FrameIO
    {
        public const int MaxFrame = 64 * 1024;

        public static void Write(Stream stream, byte[] data)
        {
            byte[] len = BitConverter.GetBytes(data.Length);
            stream.Write(len, 0, 4);
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }

        public static byte[] Read(Stream stream)
        {
            int len = BitConverter.ToInt32(ReadExact(stream, 4), 0);
            if (len <= 0 || len > MaxFrame) throw new InvalidDataException("Khung dữ liệu không hợp lệ.");
            return ReadExact(stream, len);
        }

        private static byte[] ReadExact(Stream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int n = stream.Read(buffer, offset, count - offset);
                if (n <= 0) throw new EndOfStreamException();
                offset += n;
            }
            return buffer;
        }
    }
}
