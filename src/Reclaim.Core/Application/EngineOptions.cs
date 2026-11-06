using System;

namespace Reclaim.Core.Application
{
    /// <summary>Các mốc thời gian của ReclaimEngine. Mặc định dùng cho dịch vụ thật; unit test truyền giá trị ngắn.</summary>
    public sealed class EngineOptions
    {
        // nextdns khởi động nguội có thể mất ~11-16 giây để chọn được endpoint DoH (xem nhật ký), nên chờ rộng rãi.
        public TimeSpan HealthTimeout = TimeSpan.FromSeconds(30);
        public int ProbeTimeoutMs = 7000;
        public int PollSleepMs = 300;

        // Lúc khởi động dịch vụ: chờ tối đa chừng này cho nextdns trả lời rồi mới chuyển DNS (quá hạn vẫn chuyển: fail-closed).
        public TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

        // Watchdog trong Tick: khi khỏe thì kiểm tra thưa, khi lỗi thì kiểm tra dày hơn.
        // Mỗi lần thăm dò là MỘT truy vấn thật tới NextDNS (tên ngẫu nhiên reclaim-xxxx.example.com để không trúng cache), và nó được tính
        // vào hạn mức truy vấn của gói NextDNS (gói miễn phí 300.000/tháng): 30 giây/lần tốn ~86.000/tháng, 5 phút/lần chỉ ~8.600.
        // nextdns treo là hiếm nên 5 phút là đủ; khi đã thấy bất thường thì dày lại theo WatchdogRetryInterval.
        public TimeSpan WatchdogInterval = TimeSpan.FromMinutes(5);
        public TimeSpan WatchdogRetryInterval = TimeSpan.FromSeconds(5);
        public int WatchdogProbeTimeoutMs = 8000;

        // Chỉ báo lỗi lên giao diện khi bất thường kéo dài (rớt mạng thoáng qua 12-20 giây là chuyện bình thường).
        public TimeSpan IssueAfter = TimeSpan.FromSeconds(45);

        // Khởi động lại nextdns chỉ khi nó KHÔNG trả lời gì (treo), mạng đang có, đủ lâu và không dồn dập.
        public int NoResponseChecksBeforeRestart = 3;
        public TimeSpan UnhealthyRestartAfter = TimeSpan.FromSeconds(45);
        public TimeSpan MinRestartGap = TimeSpan.FromMinutes(3);

        // Kiểm tra lại quy tắc tường lửa/chính sách registry (phòng bị xóa tay).
        public TimeSpan GuardCheckInterval = TimeSpan.FromSeconds(60);

        // Chu kỳ ghi dòng tóm tắt chẩn đoán vào nhật ký.
        public TimeSpan SummaryInterval = TimeSpan.FromMinutes(10);

        // Thời gian tối đa chờ nextdns tạm khi kiểm tra ID mới.
        public TimeSpan VerifyTimeout = TimeSpan.FromSeconds(40);
    }
}
