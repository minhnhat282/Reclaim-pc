using System.Text.RegularExpressions;

namespace Reclaim.Core.Domain
{
    /// <summary>ID cấu hình NextDNS (ví dụ "abc123"). Kiểm tra chặt vì giá trị này đi vào file cấu hình của nextdns.</summary>
    public static class ProfileId
    {
        private static readonly Regex Pattern =
            new Regex("^[0-9a-z]{6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string Normalize(string input)
        {
            return (input ?? string.Empty).Trim().ToLowerInvariant();
        }

        public static bool IsValid(string normalized)
        {
            return normalized != null && Pattern.IsMatch(normalized);
        }
    }
}
