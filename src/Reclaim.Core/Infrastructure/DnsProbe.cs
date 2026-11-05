using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Reclaim.Core.Application;

namespace Reclaim.Core.Infrastructure
{
    /// <summary>
    /// Gửi 1 truy vấn DNS (UDP) tới 127.0.0.1:53 cho một tên ngẫu nhiên (tránh bộ nhớ đệm) và xem phản hồi.
    /// </summary>
    public sealed class DnsProbe : IHealthProbe
    {
        private static readonly Random Rng = new Random();

        /// <summary>NOERROR hoặc NXDOMAIN: chuỗi nextdns -> NextDNS hoạt động. SERVFAIL/không trả lời: lỗi (ví dụ ID sai, mất mạng).</summary>
        public bool IsResolverHealthy()
        {
            int rcode = Query(new IPEndPoint(IPAddress.Loopback, 53), 2500);
            return rcode == 0 || rcode == 3;
        }

        /// <summary>Có BẤT KỲ bộ phân giải nào trả lời ở 127.0.0.1:53 (kể cả trả lỗi) hay không.</summary>
        public bool IsResolverAnswering()
        {
            // Cả hai họ IP: một bộ phân giải riêng chỉ nghe ở [::1]:53 cũng phải được coi là "đang có người trả lời".
            return Query(new IPEndPoint(IPAddress.Loopback, 53), 2500) >= 0
                || Query(new IPEndPoint(IPAddress.IPv6Loopback, 53), 2500) >= 0;
        }

        /// <summary>
        /// Chờ tới timeoutMs cho MỘT truy vấn. nextdns tự chờ upstream tối đa ~5 giây mỗi truy vấn khi mới khởi động,
        /// nên timeout phải lớn hơn mức đó, nếu không ta bỏ truy vấn đúng lúc nextdns sắp trả lời.
        /// </summary>
        public int QueryRcode(int timeoutMs)
        {
            return Query(new IPEndPoint(IPAddress.Loopback, 53), timeoutMs);
        }

        /// <summary>Như QueryRcode nhưng gửi tới một cổng khác (dùng để thử nextdns tạm mà không đụng tới nextdns đang chạy).</summary>
        public int QueryRcode(IPEndPoint endpoint, int timeoutMs)
        {
            return Query(endpoint, timeoutMs);
        }

        public bool IsNetworkAvailable()
        {
            try { return NetworkInterface.GetIsNetworkAvailable(); }
            catch { return true; } // không biết thì coi như có mạng (không được lấy cớ này để bỏ qua lỗi)
        }

        /// <returns>RCODE của phản hồi, hoặc -1 nếu không có phản hồi hợp lệ.</returns>
        private static int Query(IPEndPoint endpoint, int timeoutMs)
        {
            try
            {
                byte[] id = new byte[2];
                byte[] suffix = new byte[4];
                lock (Rng) { Rng.NextBytes(id); Rng.NextBytes(suffix); }

                byte[] query = BuildQuery(id, "reclaim-" + BitConverter.ToString(suffix).Replace("-", "").ToLowerInvariant() + ".example.com");

                using (var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.ReceiveTimeout = timeoutMs;
                    socket.SendTimeout = 2500;
                    socket.Connect(endpoint);
                    socket.Send(query);
                    byte[] buffer = new byte[512];
                    int n = socket.Receive(buffer);
                    if (n < 12) return -1;
                    if (buffer[0] != id[0] || buffer[1] != id[1]) return -1;
                    return buffer[3] & 0x0F;
                }
            }
            catch (SocketException)
            {
                return -1;
            }
        }

        private static byte[] BuildQuery(byte[] id, string name)
        {
            var packet = new System.Collections.Generic.List<byte>();
            packet.Add(id[0]); packet.Add(id[1]);
            packet.Add(0x01); packet.Add(0x00);          // cờ: RD
            packet.Add(0x00); packet.Add(0x01);          // QDCOUNT = 1
            packet.Add(0); packet.Add(0);                // ANCOUNT
            packet.Add(0); packet.Add(0);                // NSCOUNT
            packet.Add(0); packet.Add(0);                // ARCOUNT
            foreach (string label in name.Split('.'))
            {
                byte[] bytes = Encoding.ASCII.GetBytes(label);
                packet.Add((byte)bytes.Length);
                packet.AddRange(bytes);
            }
            packet.Add(0);
            packet.Add(0x00); packet.Add(0x01);          // QTYPE = A
            packet.Add(0x00); packet.Add(0x01);          // QCLASS = IN
            return packet.ToArray();
        }
    }
}
