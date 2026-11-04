using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace Reclaim.Core.Security
{
    /// <summary>Token mở khóa ngắn hạn, trượt theo hoạt động. Chỉ giữ trong bộ nhớ của dịch vụ.</summary>
    public sealed class SessionStore
    {
        private readonly Dictionary<string, DateTime> _tokens = new Dictionary<string, DateTime>();
        private readonly object _sync = new object();
        private readonly TimeSpan _idle;
        private readonly int _max;

        public SessionStore(TimeSpan idleTimeout, int maxTokens)
        {
            _idle = idleTimeout;
            _max = maxTokens;
        }

        public string Issue()
        {
            byte[] raw = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(raw);
            }
            string token = Convert.ToBase64String(raw);
            lock (_sync)
            {
                Purge();
                while (_tokens.Count >= _max)
                {
                    string oldest = _tokens.OrderBy(kv => kv.Value).First().Key;
                    _tokens.Remove(oldest);
                }
                _tokens[token] = DateTime.UtcNow + _idle;
            }
            return token;
        }

        public bool Validate(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            lock (_sync)
            {
                Purge();
                if (!_tokens.ContainsKey(token)) return false;
                _tokens[token] = DateTime.UtcNow + _idle;
                return true;
            }
        }

        public void Revoke(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            lock (_sync) { _tokens.Remove(token); }
        }

        public void Clear()
        {
            lock (_sync) { _tokens.Clear(); }
        }

        private void Purge()
        {
            DateTime now = DateTime.UtcNow;
            foreach (string k in _tokens.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
            {
                _tokens.Remove(k);
            }
        }
    }
}
