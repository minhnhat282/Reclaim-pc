using System;
using System.Collections.Generic;
using Reclaim.Core.Domain;

namespace Reclaim.Core.Application
{
    public interface ILog
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }

    public interface ISettingsStore
    {
        AppSettings Load();
        void Save(AppSettings settings);
    }

    /// <summary>Kết quả của một lần ép DNS các adapter về loopback.</summary>
    public sealed class DnsRouteResult
    {
        private readonly List<string> _failures = new List<string>();

        /// <summary>Số adapter đang hoạt động đã trỏ đúng loopback (sẵn có hoặc vừa đặt xong).</summary>
        public int Compliant { get; set; }

        /// <summary>true nếu lần này có đổi DNS của ít nhất một adapter.</summary>
        public bool Changed { get; set; }

        /// <summary>Tên các adapter đang hoạt động mà Reclaim không đặt được DNS.</summary>
        public IList<string> Failures { get { return _failures; } }

        public bool AllOk { get { return _failures.Count == 0; } }

        /// <summary>
        /// Đủ dùng để coi là đã áp dụng: không có adapter nào lỗi, hoặc ít nhất một adapter đã đúng
        /// (adapter ảo/VPN lỗi lẻ không được chặn việc bật bảo vệ; Tick sẽ thử lại và báo lỗi).
        /// </summary>
        public bool Usable { get { return _failures.Count == 0 || Compliant > 0; } }

        public string Describe()
        {
            return string.Join(", ", _failures);
        }
    }

    /// <summary>Điều hướng DNS hệ thống về nextdns cục bộ (127.0.0.1 / ::1).</summary>
    public interface IDnsConfigurator
    {
        /// <summary>Đảm bảo mọi adapter đang hoạt động trỏ về loopback. Idempotent, rẻ khi đã đúng.</summary>
        DnsRouteResult RouteToLoopback();

        /// <summary>Một dòng mô tả DNS hiện tại của từng adapter (để ghi nhật ký chẩn đoán).</summary>
        string DescribeAdapters();

        /// <summary>Khôi phục DNS ban đầu của các adapter đã bị Reclaim thay đổi.</summary>
        /// <returns>true nếu mọi adapter đã được khôi phục; bản sao lưu của adapter lỗi được giữ lại để thử lại.</returns>
        bool ResetAll();

        /// <summary>
        /// Lưới an toàn khi bản sao lưu bị mất: adapter chỉ trỏ đúng giá trị Reclaim đặt (127.0.0.1 / ::1), KHÔNG có bản ghi,
        /// và KHÔNG có bộ phân giải nào trả lời ở loopback -> trả về DHCP. Nếu có bộ phân giải đang trả lời (có thể là của
        /// người dùng) thì không đụng vào.
        /// </summary>
        bool RecoverOrphans(Func<bool> resolverAnswers);
    }

    /// <summary>Quản lý tiến trình nextdns.exe (nguyên bản, không sửa đổi).</summary>
    public interface INextDnsRunner : IDisposable
    {
        bool IsRunning { get; }
        int RestartCount { get; }

        /// <summary>Số lần nextdns thoát liên tiếp mà chưa chạy ổn định được 60 giây (0 khi đang ổn).</summary>
        int ConsecutiveFailures { get; }

        string ComputeBinaryHash();
        void Start(string profileId);
        void Stop();
        IList<string> RecentLog();

        /// <summary>Dòng lỗi gần nhất mà nextdns ghi ra (null nếu chưa có). Dùng để báo nguyên nhân thật cho người dùng.</summary>
        string LastProblemLine();
    }

    public interface IHealthProbe
    {
        /// <summary>true nếu nextdns cục bộ trả lời truy vấn DNS bằng kết quả hợp lệ.</summary>
        bool IsResolverHealthy();

        /// <summary>true nếu có bất kỳ bộ phân giải nào trả lời ở 127.0.0.1:53 (kể cả trả lỗi).</summary>
        bool IsResolverAnswering();

        /// <summary>Gửi một truy vấn thử và chờ tối đa timeoutMs. Trả về RCODE, hoặc -1 nếu không có phản hồi hợp lệ.</summary>
        int QueryRcode(int timeoutMs);

        /// <summary>true nếu máy có ít nhất một kết nối mạng đang hoạt động (dùng để không khởi động lại nextdns oan lúc rớt mạng).</summary>
        bool IsNetworkAvailable();
    }

    /// <summary>Áp dụng các lớp tự bảo vệ (ACL, DACL dịch vụ...) theo tùy chọn.</summary>
    public interface IProtectionHardener
    {
        /// <exception cref="ReclaimException">Nếu BẤT KỲ lớp nào không áp dụng được (mô tả các lớp lỗi trong thông điệp).</exception>
        void Apply(ProtectionOptions options);
    }

    /// <summary>
    /// Chặn các đường vòng qua bộ lọc: DNS/DoH của chương trình khác (tường lửa), DoH của trình duyệt và
    /// Smart Multi-Homed Name Resolution (chính sách registry). Mọi thao tác idempotent.
    /// </summary>
    public interface IBypassGuard
    {
        /// <summary>Áp dụng/kiểm tra lại. Trả về mô tả từng phần lỗi (danh sách rỗng nếu tất cả đã đúng).</summary>
        IList<string> Ensure();

        /// <summary>
        /// Gỡ mọi thứ Ensure đã áp dụng (khôi phục giá trị registry cũ). Phải gọi TRƯỚC khi trả DNS về ban đầu,
        /// vì quy tắc chặn DNS ngoài còn lại sẽ làm mất DNS khi Reclaim không còn chạy.
        /// </summary>
        /// <returns>true nếu đã gỡ sạch.</returns>
        bool Remove();
    }

    /// <summary>Kiểm tra một ID NextDNS bằng nextdns tạm trên cổng khác, không đụng tới nextdns/DNS đang chạy.</summary>
    public interface IProfileVerifier
    {
        /// <returns>null nếu ID dùng được; ngược lại là thông báo lỗi.</returns>
        string Verify(string profileId, int timeoutMs);
    }
}
