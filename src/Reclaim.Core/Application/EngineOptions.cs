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

        // Watchdog trong Tick: khi khỏe thì kiểm tra thưa, khi lỗi thì kiểm tra dày hơn.
        public TimeSpan WatchdogInterval = TimeSpan.FromSeconds(30);
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
