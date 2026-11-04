using System.IO;
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
        public static string Render(string profileId)
        {
            if (!ProfileId.IsValid(profileId))
            {
                throw new ReclaimException(ErrorCodes.InvalidProfile, "ID NextDNS không hợp lệ.");
            }
            var sb = new StringBuilder();
            sb.Append("# Do Reclaim tạo tự động. Mọi chỉnh sửa tay sẽ bị ghi đè.\n");
            sb.Append("profile ").Append(profileId).Append('\n');
            sb.Append("listen 127.0.0.1:53\n");
            sb.Append("control reclaim-nextdns\n");
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
            string content = Render(profileId);
            // UTF-8 KHÔNG BOM: BOM sẽ làm hỏng tên khóa đầu tiên khi nextdns đọc file.
            AtomicFile.Write(path, new UTF8Encoding(false).GetBytes(content));
        }
    }
}
