using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Reclaim.Core.Domain;

namespace Reclaim.Core.Infrastructure
{
    /// <summary>
    /// Điểm duy nhất ràng buộc Reclaim với định dạng cấu hình của nextdns CLI
    /// (mỗi dòng "tên giá trị", xem host/service/config.go của nextdns).
    /// Mọi giá trị quan trọng được ghi tường minh để nâng cấp nextdns không âm thầm đổi hành vi.
    /// </summary>
    public static class NextDnsConfig
    {
        public const string DefaultControl = "reclaim-nextdns";

        /// <summary>
        /// Địa chỉ nextdns lắng nghe: IPv4 loopback, và IPv6 loopback nếu máy bind được ::1.
        /// Cú pháp "listen" lặp nhiều dòng đã được kiểm chứng trên nextdns 1.49.0 (nghe đủ UDP và TCP ở cả hai địa chỉ).
        /// </summary>
        public static IList<string> DefaultListens()
        {
            var listens = new List<string> { "127.0.0.1:53" };
            if (Ipv6LoopbackAvailable()) listens.Add("[::1]:53");
            return listens;
        }

        /// <summary>
        /// Máy có IPv6 loopback dùng được không. Nếu nextdns không bind được địa chỉ nào trong danh sách "listen"
        /// nó có thể thoát hẳn (khi đó mất DNS do fail-closed), nên chỉ thêm ::1 khi chắc chắn bind được.
        /// </summary>
        public static bool Ipv6LoopbackAvailable()
        {
            try
            {
                if (!Socket.OSSupportsIPv6) return false;
                using (var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static string Render(string profileId)
        {
            return Render(profileId, DefaultListens(), DefaultControl);
        }

        public static string Render(string profileId, IList<string> listens, string control)
        {
            if (!ProfileId.IsValid(profileId))
            {
                throw new ReclaimException(ErrorCodes.InvalidProfile, "ID NextDNS không hợp lệ.");
            }
            if (listens == null || listens.Count == 0)
            {
                throw new ReclaimException(ErrorCodes.Internal, "Thiếu địa chỉ lắng nghe cho nextdns.");
            }
            var sb = new StringBuilder();
            sb.Append("# Do Reclaim tạo tự động. Mọi chỉnh sửa tay sẽ bị ghi đè.\n");
            sb.Append("profile ").Append(profileId).Append('\n');
            foreach (string listen in listens)
            {
                sb.Append("listen ").Append(listen).Append('\n');
            }
            sb.Append("control ").Append(control).Append('\n');
            sb.Append("cache-size 10MB\n");
            // Giới hạn TTL gửi cho client để thay đổi profile NextDNS có hiệu lực nhanh hơn (cache của nextdns vẫn giữ TTL thật).
            sb.Append("max-ttl 5m\n");
            // Reclaim không cần mDNS của nextdns (mặc định "all" = nghe trên mọi adapter).
            sb.Append("mdns disabled\n");
            sb.Append("log-queries false\n");
            sb.Append("report-client-info false\n");
            sb.Append("detect-captive-portals false\n");
            sb.Append("auto-activate false\n");
            sb.Append("setup-router false\n");
            return sb.ToString();
        }

        public static void Write(string path, string profileId)
        {
            WriteContent(path, Render(profileId));
        }

        public static void WriteContent(string path, string content)
        {
            // UTF-8 KHÔNG BOM: BOM sẽ làm hỏng tên khóa đầu tiên khi nextdns đọc file.
            AtomicFile.Write(path, new UTF8Encoding(false).GetBytes(content));
        }
    }
}
