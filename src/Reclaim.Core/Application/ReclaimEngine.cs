using System;
using System.Collections.Generic;
using System.Threading;
using Reclaim.Core.Contracts;
using Reclaim.Core.Domain;
using Reclaim.Core.Security;

namespace Reclaim.Core.Application
{
    /// <summary>
    /// Toàn bộ nghiệp vụ của Reclaim (không phụ thuộc Windows): áp dụng cấu hình, mật khẩu,
    /// token mở khóa, chế độ bảo trì. Mọi thứ phụ thuộc hệ điều hành đi qua các interface trong Ports.cs.
    /// </summary>
    public sealed class ReclaimEngine
    {
        private const int FreeAttempts = 5;
        private static readonly TimeSpan MaintenanceWindow = TimeSpan.FromMinutes(5);
        // nextdns khởi động nguội có thể mất ~11-13 giây để chọn được endpoint DoH (xem nhật ký), nên chờ rộng rãi.
        private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(30);
        private const int ProbeTimeoutMs = 7000;

        private readonly ISettingsStore _store;
        private readonly IDnsConfigurator _dns;
        private readonly INextDnsRunner _runner;
        private readonly IHealthProbe _probe;
        private readonly IProtectionHardener _hardener;
        private readonly ILog _log;
        private readonly SessionStore _sessions = new SessionStore(TimeSpan.FromMinutes(10), 8);

        private readonly object _gate = new object();      // bảo vệ trạng thái trong bộ nhớ (ngắn)
        private readonly object _authLock = new object();  // tuần tự hóa việc kiểm tra mật khẩu
        private readonly object _applyLock = new object(); // tuần tự hóa việc áp dụng cấu hình (có thể lâu)

        private readonly object _lifecycle = new object(); // tuần tự hóa khởi động nextdns/đặt DNS với Stop
        private volatile bool _stopped;

        private AppSettings _settings;
        private bool _enforce;
        private DateTime _maintenanceUntilUtc = DateTime.MinValue;
        private string _lastError = string.Empty;

        public ReclaimEngine(ISettingsStore store, IDnsConfigurator dns, INextDnsRunner runner,
            IHealthProbe probe, IProtectionHardener hardener, ILog log)
        {
            _store = store;
            _dns = dns;
            _runner = runner;
            _probe = probe;
            _hardener = hardener;
            _log = log;
            _settings = _store.Load().Normalize();
        }

        // ------------------------------------------------------------ vòng đời

        public void Start()
        {
            if (_stopped) return;
            AppSettings s;
            lock (_gate) { s = _settings.Clone(); }

            string hardenFailure = TryApplyProtection(s.Protection);
            if (hardenFailure != null && s.Protection.Any)
            {
                SetError("Một số lớp tự bảo vệ chưa được áp dụng: " + hardenFailure);
            }

            if (!s.Enabled || !ProfileId.IsValid(s.ProfileId))
            {
                if (!s.Enabled) TryRecoverOrphans(); // bảo vệ đang tắt mà DNS còn trỏ loopback chết -> dọn
                return;
            }
            try
            {
                EnsureBinaryTrusted();
                lock (_lifecycle)
                {
                    if (_stopped) return; // dịch vụ đã nhận lệnh dừng trong lúc khởi động
                    _runner.Start(s.ProfileId);
                    lock (_gate) { _enforce = true; }
                    _dns.RouteToLoopback();
                }
                _log.Info("Đã khởi động bảo vệ DNS.");
            }
            catch (ReclaimException ex)
            {
                SetError(ex.Message);
            }
            catch (Exception ex)
            {
                _log.Error("Start: " + ex);
                SetError("Không khởi động được nextdns.");
            }
        }

        public void Stop(bool resetDns)
        {
            // Sau dòng này, không tác vụ nền nào (Start, Tick, SaveSettings) còn có thể chạy nextdns hay đặt lại DNS.
            lock (_lifecycle) { _stopped = true; }
            lock (_gate) { _enforce = false; }
            if (resetDns)
            {
                // Trả DNS TRƯỚC khi dừng nextdns để rút ngắn thời gian mất mạng.
                try
                {
                    if (!_dns.ResetAll()) _log.Error("Chưa khôi phục hết DNS ban đầu; bản sao lưu được giữ lại để thử lại.");
                }
                catch (Exception ex) { _log.Error("ResetAll: " + ex.Message); }
            }
            _runner.Stop();
            if (resetDns) TryRecoverOrphans(); // nextdns đã dừng: dọn adapter loopback không có bản sao lưu
        }

        /// <summary>Gọi định kỳ: giữ DNS đúng và hết hạn chế độ bảo trì.</summary>
        public void Tick()
        {
            if (_stopped) return;
            bool enforce;
            bool relock = false;
            AppSettings s = null;
            lock (_gate)
            {
                if (_maintenanceUntilUtc != DateTime.MinValue && DateTime.UtcNow >= _maintenanceUntilUtc)
                {
                    _maintenanceUntilUtc = DateTime.MinValue;
                    relock = true;
                    s = _settings.Clone();
                }
                enforce = _enforce;
            }
            if (relock)
            {
                string failure = TryApplyProtection(s.Protection);
                if (failure != null && s.Protection.Any)
                    SetError("Một số lớp tự bảo vệ chưa được áp dụng: " + failure);
            }
            if (enforce)
            {
                lock (_lifecycle)
                {
                    if (_stopped) return;
                    try { _dns.RouteToLoopback(); }
                    catch (Exception ex) { _log.Warn("Tick DNS: " + ex.Message); }
                }
            }
        }

        // ------------------------------------------------------------ trạng thái

        public StateDto GetState(string token)
        {
            lock (_gate)
            {
                bool hasPw = _settings.HasPassword;
                long now = DateTime.UtcNow.Ticks;
                return new StateDto
                {
                    Enabled = _settings.Enabled,
                    ProfileId = _settings.ProfileId,
                    NextDnsRunning = _runner.IsRunning,
                    Enforcing = _enforce,
                    PasswordSet = hasPw,
                    Unlocked = hasPw && _sessions.Validate(token),
                    Protection = _settings.Protection.Clone(),
                    ThrottleSeconds = SecondsLeft(_settings.LockedUntilTicks - now),
                    MaintenanceSeconds = SecondsLeft(_maintenanceUntilUtc.Ticks - now),
                    LastError = _lastError,
                    RestartCount = _runner.RestartCount
                };
            }
        }

        public string[] GetLog(string token)
        {
            lock (_gate)
            {
                // Có mật khẩu thì xem nhật ký cũng cần mở khóa.
                Require(token, _settings.HasPassword);
            }
            IList<string> lines = _runner.RecentLog();
            var arr = new string[lines.Count];
            lines.CopyTo(arr, 0);
            return arr;
        }

        // ------------------------------------------------------------ cấu hình

        public StateDto SaveSettings(string token, string profileIdRaw, bool enabled)
        {
            lock (_applyLock)
            {
                if (_stopped) throw new ReclaimException(ErrorCodes.Unavailable, "Dịch vụ đang dừng.");
                AppSettings before;
                lock (_gate)
                {
                    Require(token, _settings.HasPassword && _settings.Protection.Settings);
                    before = _settings.Clone();
                }

                string id = ProfileId.Normalize(profileIdRaw);
                bool invalid = enabled ? !ProfileId.IsValid(id) : (id.Length > 0 && !ProfileId.IsValid(id));
                if (invalid)
                {
                    throw new ReclaimException(ErrorCodes.InvalidProfile,
                        "ID NextDNS không hợp lệ. ID gồm đúng 6 ký tự chữ thường hoặc số.");
                }

                if (enabled)
                {
                    string failure = Activate(id);
                    if (failure != null)
                    {
                        Rollback(before);
                        SetError(failure);
                        throw new ReclaimException(ErrorCodes.ApplyFailed, failure);
                    }
                }
                else
                {
                    // Khôi phục DNS TRƯỚC; nếu thất bại thì giữ nguyên bảo vệ (tắt nextdns khi DNS còn trỏ loopback = mất mạng).
                    lock (_gate) { _enforce = false; }
                    bool restored;
                    try { restored = _dns.ResetAll(); }
                    catch (Exception ex)
                    {
                        _log.Warn("ResetAll: " + ex.Message);
                        restored = false;
                    }
                    if (!restored)
                    {
                        lock (_gate) { _enforce = true; }
                        try { _dns.RouteToLoopback(); } catch (Exception ex) { _log.Warn("RouteToLoopback: " + ex.Message); }
                        const string msg = "Không khôi phục được DNS ban đầu nên chưa thể tắt bảo vệ. Hãy thử lại.";
                        SetError(msg);
                        throw new ReclaimException(ErrorCodes.ApplyFailed, msg);
                    }
                    _runner.Stop();
                    TryRecoverOrphans();
                }

                lock (_gate)
                {
                    _settings.ProfileId = id;
                    _settings.Enabled = enabled;
                    _lastError = string.Empty;
                    _store.Save(_settings);
                }
                return GetState(token);
            }
        }

        /// <returns>null nếu thành công, ngược lại là thông báo lỗi.</returns>
        private string Activate(string id)
        {
            // Cấu hình không đổi và nextdns đang chạy: không khởi động lại (khởi động lại = nextdns về trạng thái nguội,
            // vài giây tới ~13 giây không có DNS). Chỉ áp lại DNS nếu cần.
            bool unchanged;
            lock (_gate) { unchanged = _settings.Enabled && _enforce && _settings.ProfileId == id; }
            if (unchanged && _runner.IsRunning)
            {
                lock (_lifecycle)
                {
                    if (_stopped) return "Dịch vụ đang dừng.";
                    try { _dns.RouteToLoopback(); }
                    catch (Exception ex) { _log.Warn("RouteToLoopback: " + ex.Message); }
                }
                return null;
            }

            try { EnsureBinaryTrusted(); }
            catch (ReclaimException ex) { return ex.Message; }

            try
            {
                if (!StartRunnerGuarded(id)) return "Dịch vụ đang dừng.";
            }
            catch (Exception ex)
            {
                _log.Error("Activate: " + ex);
                return "Không khởi động được nextdns: " + ex.Message;
            }

            string health = WaitUntilHealthy();
            if (health != null) return health;

            // Chỉ trỏ DNS hệ thống về nextdns SAU KHI đã xác nhận nextdns hoạt động,
            // để nhập sai ID không làm mất mạng.
            lock (_lifecycle)
            {
                if (_stopped) return "Dịch vụ đang dừng.";
                lock (_gate) { _enforce = true; }
                try { _dns.RouteToLoopback(); }
                catch (Exception ex) { _log.Warn("RouteToLoopback: " + ex.Message); }
            }
            return null;
        }

        private bool StartRunnerGuarded(string profileId)
        {
            lock (_lifecycle)
            {
                if (_stopped) return false;
                _runner.Start(profileId);
                return true;
            }
        }

        private void Rollback(AppSettings before)
        {
            if (before.Enabled && ProfileId.IsValid(before.ProfileId))
            {
                try { StartRunnerGuarded(before.ProfileId); }
                catch (Exception ex) { _log.Error("Rollback: " + ex.Message); }
            }
            else
            {
                _runner.Stop();
                lock (_gate) { _enforce = false; }
                try { _dns.ResetAll(); }
                catch (Exception ex) { _log.Warn("ResetAll: " + ex.Message); }
                TryRecoverOrphans();
            }
        }

        private void TryRecoverOrphans()
        {
            try
            {
                if (!_dns.RecoverOrphans(_probe.IsResolverAnswering)) _log.Warn("Chưa dọn hết DNS loopback mồ côi.");
            }
            catch (Exception ex)
            {
                _log.Warn("RecoverOrphans: " + ex.Message);
            }
        }

        /// <returns>null nếu nextdns đã trả lời hợp lệ; ngược lại là thông báo lỗi.</returns>
        private string WaitUntilHealthy()
        {
            DateTime started = DateTime.UtcNow;
            DateTime deadline = started + HealthTimeout;
            int last = -1;
            bool answered = false;
            while (DateTime.UtcNow < deadline)
            {
                last = _probe.QueryRcode(ProbeTimeoutMs);
                if (last == 0 || last == 3)
                {
                    _log.Info("nextdns sẵn sàng sau " + (int)(DateTime.UtcNow - started).TotalSeconds + " giây.");
                    return null;
                }
                if (last >= 0) answered = true;
                Thread.Sleep(300);
            }
            _log.Warn("nextdns chưa sẵn sàng sau " + (int)HealthTimeout.TotalSeconds + " giây (RCODE cuối: " + last + ").");
            return answered
                ? "NextDNS trả lỗi (mã DNS " + last + "). Hãy kiểm tra lại ID NextDNS."
                : "nextdns chưa kết nối được tới NextDNS sau " + (int)HealthTimeout.TotalSeconds + " giây. Hãy kiểm tra kết nối mạng rồi thử lại.";
        }

        private void EnsureBinaryTrusted()
        {
            string actual = _runner.ComputeBinaryHash();
            if (actual == null)
            {
                throw new ReclaimException(ErrorCodes.MissingBinary, "Không tìm thấy nextdns.exe. Hãy cài đặt lại Reclaim.");
            }
            lock (_gate)
            {
                if (string.IsNullOrEmpty(_settings.NextDnsSha256))
                {
                    _settings.NextDnsSha256 = actual; // tin tưởng bản được cài đặt
                    _store.Save(_settings);
                    return;
                }
                if (!string.Equals(_settings.NextDnsSha256, actual, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ReclaimException(ErrorCodes.BinaryChanged,
                        "nextdns.exe đã bị thay đổi so với lúc cài đặt. Hãy cài đặt lại Reclaim.");
                }
            }
        }

        // ------------------------------------------------------------ mật khẩu / khóa

        public string Unlock(string password)
        {
            lock (_authLock)
            {
                CheckPassword(password);
                return _sessions.Issue();
            }
        }

        public void Lock(string token)
        {
            _sessions.Revoke(token);
        }

        public string SetInitialPassword(string newPassword)
        {
            string problem = PasswordPolicy.Validate(newPassword);
            if (problem != null) throw new ReclaimException(ErrorCodes.WeakPassword, problem);

            lock (_authLock)
            {
                lock (_gate)
                {
                    if (_settings.HasPassword)
                        throw new ReclaimException(ErrorCodes.PasswordExists, "Đã có mật khẩu. Hãy dùng chức năng đổi mật khẩu.");
                }
                PasswordRecord record = PasswordHasher.Create(newPassword);
                lock (_gate)
                {
                    _settings.Password = record;
                    _settings.FailedAttempts = 0;
                    _settings.LockedUntilTicks = 0;
                    _store.Save(_settings);
                    return _sessions.Issue();
                }
            }
        }

        public string ChangePassword(string oldPassword, string newPassword)
        {
            string problem = PasswordPolicy.Validate(newPassword);
            if (problem != null) throw new ReclaimException(ErrorCodes.WeakPassword, problem);

            lock (_authLock)
            {
                CheckPassword(oldPassword);
                PasswordRecord record = PasswordHasher.Create(newPassword);
                lock (_gate)
                {
                    _settings.Password = record;
                    _store.Save(_settings);
                    _sessions.Clear();
                    return _sessions.Issue();
                }
            }
        }

        public void RemovePassword(string password)
        {
            lock (_authLock)
            {
                CheckPassword(password);
                ProtectionOptions previous;
                lock (_gate) { previous = _settings.Protection.Clone(); }
                if (previous.Any)
                {
                    string failure = TryApplyProtection(new ProtectionOptions());
                    if (failure != null)
                    {
                        TryApplyProtection(previous);
                        throw new ReclaimException(ErrorCodes.ApplyFailed, "Không gỡ được các lớp bảo vệ: " + failure);
                    }
                }
                lock (_gate)
                {
                    _settings.Password = null;
                    _settings.Protection = new ProtectionOptions();
                    _settings.FailedAttempts = 0;
                    _settings.LockedUntilTicks = 0;
                    _store.Save(_settings);
                    _sessions.Clear();
                }
            }
        }

        public StateDto SetProtection(string token, ProtectionOptions options)
        {
            if (options == null) throw new ReclaimException(ErrorCodes.BadRequest, "Thiếu tùy chọn bảo vệ.");
            ProtectionOptions requested = options.Clone();
            ProtectionOptions previous;
            lock (_gate)
            {
                if (!_settings.HasPassword)
                    throw new ReclaimException(ErrorCodes.NoPassword, "Hãy đặt mật khẩu trước khi bật tự bảo vệ.");
                Require(token, true);
                previous = _settings.Protection.Clone();
            }

            // Áp dụng TRƯỚC, chỉ lưu cấu hình khi mọi lớp bảo vệ cần thiết đã áp dụng thành công.
            string failure = TryApplyProtection(requested);
            if (failure != null)
            {
                TryApplyProtection(previous); // hoàn nguyên về trạng thái cũ
                throw new ReclaimException(ErrorCodes.ApplyFailed, "Không áp dụng được tự bảo vệ: " + failure);
            }

            lock (_gate)
            {
                if (!_settings.HasPassword)
                    throw new ReclaimException(ErrorCodes.NoPassword, "Mật khẩu đã bị xóa.");
                _settings.Protection = requested;
                _maintenanceUntilUtc = DateTime.MinValue;
                _store.Save(_settings);
            }
            return GetState(token);
        }

        /// <summary>Gỡ bảo vệ tạm thời (5 phút) để nâng cấp hoặc gỡ cài đặt. Cần mật khẩu nếu đang bảo vệ.</summary>
        public StateDto BeginMaintenance(string token)
        {
            ProtectionOptions current;
            lock (_gate)
            {
                Require(token, _settings.HasPassword && _settings.Protection.Any);
                current = _settings.Protection.Clone();
            }

            string failure = TryApplyProtection(new ProtectionOptions());
            if (failure != null)
            {
                TryApplyProtection(current);
                throw new ReclaimException(ErrorCodes.ApplyFailed, "Không gỡ được các lớp bảo vệ: " + failure);
            }
            lock (_gate) { _maintenanceUntilUtc = DateTime.UtcNow + MaintenanceWindow; }
            _log.Info("Bắt đầu chế độ bảo trì.");
            return GetState(token);
        }

        // ------------------------------------------------------------ helpers

        /// <summary>Gọi bên trong _authLock.</summary>
        private void CheckPassword(string password)
        {
            PasswordRecord record;
            lock (_gate)
            {
                if (!_settings.HasPassword)
                    throw new ReclaimException(ErrorCodes.NoPassword, "Chưa đặt mật khẩu.");
                long now = DateTime.UtcNow.Ticks;
                if (_settings.LockedUntilTicks > now)
                {
                    throw new ReclaimException(ErrorCodes.Throttled,
                        "Nhập sai quá nhiều lần. Thử lại sau " + SecondsLeft(_settings.LockedUntilTicks - now) + " giây.");
                }
                record = _settings.Password;
            }

            bool ok = PasswordHasher.Verify(password, record);

            lock (_gate)
            {
                if (ok)
                {
                    if (_settings.FailedAttempts != 0 || _settings.LockedUntilTicks != 0)
                    {
                        _settings.FailedAttempts = 0;
                        _settings.LockedUntilTicks = 0;
                        _store.Save(_settings);
                    }
                    return;
                }

                _settings.FailedAttempts++;
                if (_settings.FailedAttempts >= FreeAttempts)
                {
                    int exp = Math.Min(_settings.FailedAttempts - FreeAttempts, 6);
                    int delay = Math.Min(900, 15 * (1 << exp));
                    _settings.LockedUntilTicks = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(delay).Ticks;
                }
                _store.Save(_settings);
            }
            throw new ReclaimException(ErrorCodes.BadPassword, "Mật khẩu không đúng.");
        }

        /// <summary>Gọi bên trong _gate.</summary>
        private void Require(string token, bool needed)
        {
            if (needed && !_sessions.Validate(token))
            {
                throw new ReclaimException(ErrorCodes.NeedUnlock, "Cần mở khóa bằng mật khẩu.");
            }
        }

        /// <returns>null nếu thành công, ngược lại là mô tả lỗi.</returns>
        private string TryApplyProtection(ProtectionOptions options)
        {
            try
            {
                _hardener.Apply(options);
                return null;
            }
            catch (Exception ex)
            {
                _log.Warn("ApplyProtection: " + ex.Message);
                return ex.Message;
            }
        }

        private void SetError(string message)
        {
            lock (_gate) { _lastError = message ?? string.Empty; }
        }

        private static int SecondsLeft(long ticks)
        {
            if (ticks <= 0) return 0;
            return (int)Math.Ceiling(ticks / (double)TimeSpan.TicksPerSecond);
        }
    }
}
