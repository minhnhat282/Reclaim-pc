using System;
using System.Security.Cryptography;
using System.Text;
using Reclaim.Core.Domain;

namespace Reclaim.Core.Security
{
    /// <summary>PBKDF2-HMAC-SHA256, muối ngẫu nhiên, lưu kèm số vòng lặp để có thể nâng cấp sau.</summary>
    public static class PasswordHasher
    {
        public const int DefaultIterations = 600000;
        private const int SaltSize = 16;
        private const int HashSize = 32;

        public static PasswordRecord Create(string password)
        {
            byte[] salt = new byte[SaltSize];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }
            byte[] hash = Derive(password, salt, DefaultIterations);
            return new PasswordRecord
            {
                Salt = Convert.ToBase64String(salt),
                Hash = Convert.ToBase64String(hash),
                Iterations = DefaultIterations
            };
        }

        public static bool Verify(string password, PasswordRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Hash) || string.IsNullOrEmpty(record.Salt) || record.Iterations < 1000)
            {
                return false;
            }
            try
            {
                byte[] expected = Convert.FromBase64String(record.Hash);
                byte[] actual = Derive(password ?? string.Empty, Convert.FromBase64String(record.Salt), record.Iterations);
                return FixedTimeEquals(expected, actual);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static byte[] Derive(string password, byte[] salt, int iterations)
        {
            using (var kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256))
            {
                return kdf.GetBytes(HashSize);
            }
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
