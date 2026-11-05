using System;

namespace Reclaim.Core.Domain
{
    public static class ErrorCodes
    {
        public const string Unavailable = "unavailable";
        public const string BadRequest = "bad_request";
        public const string Internal = "internal";
        public const string InvalidProfile = "invalid_profile";
        public const string WeakPassword = "weak_password";
        public const string BadPassword = "bad_password";
        public const string Throttled = "throttled";
        public const string NeedUnlock = "need_unlock";
        public const string NoPassword = "no_password";
        public const string PasswordExists = "password_exists";
        public const string ApplyFailed = "apply_failed";
        public const string Denied = "denied";
        public const string MissingBinary = "missing_binary";
        public const string BinaryChanged = "binary_changed";
        public const string Cancelled = "cancelled";
        public const string Timeout = "timeout";
        public const string BadServer = "bad_server";
        public const string Protocol = "protocol";
        public const string ConnectionLost = "connection_lost";
    }

    /// <summary>Lỗi nghiệp vụ có mã lỗi; thông điệp an toàn để hiển thị cho người dùng.</summary>
    public sealed class ReclaimException : Exception
    {
        public string Code { get; private set; }

        public ReclaimException(string code, string message) : base(message)
        {
            Code = code;
        }
    }
}
