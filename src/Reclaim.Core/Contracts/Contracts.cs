using System.Runtime.Serialization;
using Reclaim.Core.Domain;

namespace Reclaim.Core.Contracts
{
    public static class Ops
    {
        public const string GetState = "get_state";
        public const string Unlock = "unlock";
        public const string Lock = "lock";
        public const string SetPassword = "set_password";
        public const string ChangePassword = "change_password";
        public const string RemovePassword = "remove_password";
        public const string SetProtection = "set_protection";
        public const string Save = "save";
        public const string Maintenance = "maintenance";
        public const string GetLog = "get_log";
    }

    [DataContract]
    public sealed class Request
    {
        [DataMember(Name = "op")] public string Op { get; set; }
        [DataMember(Name = "token")] public string Token { get; set; }
        [DataMember(Name = "password")] public string Password { get; set; }
        [DataMember(Name = "newPassword")] public string NewPassword { get; set; }
        [DataMember(Name = "profileId")] public string ProfileId { get; set; }
        [DataMember(Name = "enabled")] public bool Enabled { get; set; }
        [DataMember(Name = "protection")] public ProtectionOptions Protection { get; set; }
    }

    [DataContract]
    public sealed class StateDto
    {
        [DataMember(Name = "enabled")] public bool Enabled { get; set; }
        [DataMember(Name = "profileId")] public string ProfileId { get; set; }
        [DataMember(Name = "nextDnsRunning")] public bool NextDnsRunning { get; set; }
        [DataMember(Name = "enforcing")] public bool Enforcing { get; set; }
        [DataMember(Name = "passwordSet")] public bool PasswordSet { get; set; }
        [DataMember(Name = "unlocked")] public bool Unlocked { get; set; }
        [DataMember(Name = "protection")] public ProtectionOptions Protection { get; set; }
        [DataMember(Name = "throttleSeconds")] public int ThrottleSeconds { get; set; }
        [DataMember(Name = "maintenanceSeconds")] public int MaintenanceSeconds { get; set; }
        [DataMember(Name = "lastError")] public string LastError { get; set; }

        /// <summary>Cảnh báo không làm gián đoạn bảo vệ (ví dụ chưa tạo được quy tắc chặn đường vòng). Rỗng nếu không có.</summary>
        [DataMember(Name = "warning")] public string Warning { get; set; }
        [DataMember(Name = "restartCount")] public int RestartCount { get; set; }
    }

    [DataContract]
    public sealed class Response
    {
        [DataMember(Name = "ok")] public bool Ok { get; set; }
        [DataMember(Name = "code")] public string Code { get; set; }
        [DataMember(Name = "message")] public string Message { get; set; }
        [DataMember(Name = "token")] public string Token { get; set; }
        [DataMember(Name = "state")] public StateDto State { get; set; }
        [DataMember(Name = "log")] public string[] Log { get; set; }
    }
}
