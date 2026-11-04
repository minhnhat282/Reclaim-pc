namespace Reclaim.Core.Security
{
    /// <summary>
    /// Mật khẩu: 8-64 ký tự ASCII in được (chữ, số, ký hiệu, khoảng trắng).
    /// Chỉ ASCII để tránh khác biệt Unicode (NFC/NFD) giữa các bộ gõ khiến không mở khóa được.
    /// </summary>
    public static class PasswordPolicy
    {
        public const int MinLength = 8;
        public const int MaxLength = 64;

        /// <returns>null nếu hợp lệ, ngược lại là thông báo lỗi.</returns>
        public static string Validate(string password)
        {
            if (password == null || password.Length < MinLength || password.Length > MaxLength)
            {
                return "Mật khẩu phải dài từ " + MinLength + " đến " + MaxLength + " ký tự.";
            }
            foreach (char c in password)
            {
                if (c < 0x20 || c > 0x7E)
                {
                    return "Mật khẩu chỉ gồm chữ cái không dấu, số và ký tự đặc biệt thông thường.";
                }
            }
            return null;
        }
    }
}
