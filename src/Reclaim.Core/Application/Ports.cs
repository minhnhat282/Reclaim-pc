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

    /// <summary>Điều hướng DNS hệ thống về nextdns cục bộ (127.0.0.1 / ::1).</summary>
    public interface IDnsConfigurator
    {
        /// <summary>Đảm bảo mọi adapter đang hoạt động trỏ về loopback. Idempotent, rẻ khi đã đúng.</summary>
        void RouteToLoopback();

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
        string ComputeBinaryHash();
        void Start(string profileId);
        void Stop();
        IList<string> RecentLog();
    }

    public interface IHealthProbe
    {
        /// <summary>true nếu nextdns cục bộ trả lời truy vấn DNS bằng kết quả hợp lệ.</summary>
        bool IsResolverHealthy();

        /// <summary>true nếu có bất kỳ bộ phân giải nào trả lời ở 127.0.0.1:53 (kể cả trả lỗi).</summary>
        bool IsResolverAnswering();

        /// <summary>Gửi một truy vấn thử và chờ tối đa timeoutMs. Trả về RCODE, hoặc -1 nếu không có phản hồi hợp lệ.</summary>
        int QueryRcode(int timeoutMs);
    }

    /// <summary>Áp dụng các lớp tự bảo vệ (ACL, DACL dịch vụ...) theo tùy chọn.</summary>
    public interface IProtectionHardener
    {
        /// <exception cref="ReclaimException">Nếu BẤT KỲ lớp nào không áp dụng được (mô tả các lớp lỗi trong thông điệp).</exception>
        void Apply(ProtectionOptions options);
    }
}
