using System.IO;
using System.Runtime.Serialization.Json;

namespace Reclaim.Core.Ipc
{
    internal static class Json
    {
        public static byte[] Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream())
            {
                serializer.WriteObject(ms, value);
                return ms.ToArray();
            }
        }

        public static T Deserialize<T>(byte[] data)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream(data))
            {
                return (T)serializer.ReadObject(ms);
            }
        }
    }
}
