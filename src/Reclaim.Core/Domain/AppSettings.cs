using System.Runtime.Serialization;

namespace Reclaim.Core.Domain
{
    [DataContract]
    public sealed class ProtectionOptions
    {
        /// <summary>Chống gỡ cài đặt ứng dụng.</summary>
        [DataMember(Name = "uninstall")] public bool Uninstall { get; set; }

        /// <summary>Chống thay đổi cài đặt (ID, bật/tắt, các tùy chọn bảo vệ).</summary>
        [DataMember(Name = "settings")] public bool Settings { get; set; }

        /// <summary>Chống vô hiệu hóa dịch vụ Windows.</summary>
        [DataMember(Name = "service")] public bool Service { get; set; }

        public bool Any
        {
            get { return Uninstall || Settings || Service; }
        }

        public ProtectionOptions Clone()
        {
            return new ProtectionOptions { Uninstall = Uninstall, Settings = Settings, Service = Service };
        }
    }

    [DataContract]
    public sealed class PasswordRecord
    {
        [DataMember(Name = "salt")] public string Salt { get; set; }
        [DataMember(Name = "hash")] public string Hash { get; set; }
        [DataMember(Name = "iterations")] public int Iterations { get; set; }
    }

    /// <summary>Cài đặt lưu bền vững. Thêm trường mới = tăng Schema và xử lý trong Normalize().</summary>
    [DataContract]
    public sealed class AppSettings
    {
        public const int CurrentSchema = 1;

        [DataMember(Name = "schema")] public int Schema { get; set; }
        [DataMember(Name = "enabled")] public bool Enabled { get; set; }
        [DataMember(Name = "profileId")] public string ProfileId { get; set; }
        [DataMember(Name = "protection")] public ProtectionOptions Protection { get; set; }
        [DataMember(Name = "password")] public PasswordRecord Password { get; set; }
        [DataMember(Name = "failedAttempts")] public int FailedAttempts { get; set; }
        [DataMember(Name = "lockedUntilTicks")] public long LockedUntilTicks { get; set; }
        [DataMember(Name = "nextDnsSha256")] public string NextDnsSha256 { get; set; }

        public bool HasPassword
        {
            get { return Password != null && !string.IsNullOrEmpty(Password.Hash); }
        }

        public static AppSettings CreateDefault()
        {
            return new AppSettings().Normalize();
        }

        /// <summary>Đưa về trạng thái hợp lệ sau khi đọc từ đĩa (DataContract không gọi constructor).</summary>
        public AppSettings Normalize()
        {
            Schema = CurrentSchema;
            if (ProfileId == null) ProfileId = string.Empty;
            if (NextDnsSha256 == null) NextDnsSha256 = string.Empty;
            if (Protection == null || !HasPassword) Protection = new ProtectionOptions();
            return this;
        }

        public AppSettings Clone()
        {
            var c = new AppSettings
            {
                Schema = Schema,
                Enabled = Enabled,
                ProfileId = ProfileId,
                Protection = Protection == null ? new ProtectionOptions() : Protection.Clone(),
                FailedAttempts = FailedAttempts,
                LockedUntilTicks = LockedUntilTicks,
                NextDnsSha256 = NextDnsSha256
            };
            if (Password != null)
            {
                c.Password = new PasswordRecord { Salt = Password.Salt, Hash = Password.Hash, Iterations = Password.Iterations };
            }
            return c;
        }
    }
}
