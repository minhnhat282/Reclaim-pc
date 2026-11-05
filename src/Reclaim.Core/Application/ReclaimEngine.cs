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

        private readonly ISettingsStore _store;
        private readonly IDnsConfigurator _dns;
        private readonly INextDnsRunner _runner;
        private readonly IHealthProbe _probe;
        private readonly IProtectionHardener _hardener;
        private readonly IBypassGuard _guard;
        private readonly IProfileVerifier _verifier;
        private readonly ILog _log;
        private readonly EngineOptions _opt;
        private readonly SessionStore _sessions = new SessionStore(TimeSpan.FromMinutes(10), 8);

        // Thứ tự khóa (luôn theo chiều này, không bao giờ ngược): _applyLock -> _lifecycle -> _gate / _watchLock (lá).
        private readonly object _gate = new object();      // bảo vệ trạng thái trong bộ nhớ (ngắn)
        private readonly object _authLock = new object();  // tuần tự hóa việc kiểm tra mật khẩu
        private readonly object _applyLock = new object(); // tuần tự hóa việc áp dụng cấu hình (có thể lâu)
        private readonly object _watchLock = new object(); // trạng thái watchdog (ngắn)
        private readonly object _hardenLock = new object(); // tuần tự hóa các lần đổi ACL/DACL (lá, không gọi ngược vào engine)

        private readonly object _lifecycle = new object(); // tuần tự hóa khởi động nextdns/đặt DNS với Stop
        private volatile bool _stopped;

        private AppSettings _settings;
        private bool _enforce;
        private DateTime _maintenanceUntilUtc = DateTime.MinValue;
        private string _lastError = string.Empty;

        // Vấn đề đang diễn ra do Tick phát hiện (khác _lastError là lỗi của một thao tác): tự mất khi hết lỗi.
        private readonly Dictionary<string, string> _issues = new Dictionary<string, string>();

        // Watchdog (truy cập dưới _watchLock)
        private DateTime _nextHealthUtc = DateTime.MinValue;
        private DateTime _unhealthySinceUtc = DateTime.MinValue;
        private DateTime _downSinceUtc = DateTime.MinValue;
        private DateTime _lastRestartUtc = DateTime.MinValue;
        private DateTime _nextGuardUtc = DateTime.MinValue;
        private int _noResponseStreak;

        // Bộ đếm chẩn đoán (dưới _watchLock), được ghi ra nhật ký thành một dòng tóm tắt định kỳ.
        private DateTime _nextSummaryUtc = DateTime.MinValue;
        private int _cProbeOk, _cProbeDnsError, _cProbeNoResponse;
        private long _probeMsSum;
        private int _probeMsMax;
        private int _cDnsFixed, _cDnsFixedTotal;
        private int _cRestarts, _cRestartsTotal;

        /// <summary>Đọc N dòng cuối của nhật ký dịch vụ (do host gắn vào); dùng cho nút "Nhật ký" trong giao diện.</summary>
        public Func<int, IList<string>> LogTail { get; set; }

        public ReclaimEngine(ISettingsStore store, IDnsConfigurator dns, INextDnsRunner runner,
            IHealthProbe probe, IProtectionHardener hardener, IBypassGuard guard, IProfileVerifier verifier,
            ILog log, EngineOptions options = null)
        {
            _store = store;
            _dns = dns;
            _runner = runner;
            _probe = probe;
            _hardener = hardener;
            _guard = guard;
            _verifier = verifier;
            _log = log;
            _opt = options ?? new EngineOptions();
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
                if (!s.Enabled)
                {
                    // Bảo vệ đang tắt: gỡ mọi chặn đường vòng còn sót (nếu không, quy tắc chặn DNS ngoài sẽ làm mất DNS)
                    // TRƯỚC khi dọn DNS loopback mồ côi.
                    TryRemoveGuard();
                    TryRecoverOrphans();
                }
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
                    ResetWatchdog();
                    RouteDns();
                }
                EnsureGuardIfDue(true);
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
                // Gỡ chặn DNS ngoài TRƯỚC khi trả DNS (nếu không, DNS ban đầu của máy sẽ bị chính quy tắc đó chặn).
                // Dịch vụ đang dừng nên không hủy được; ghi rõ vào nhật ký để chẩn đoán nếu máy mất DNS sau đó.
                if (!TryRemoveGuard()) _log.Error("Dừng dịch vụ nhưng chưa gỡ hết quy tắc chặn DNS: máy có thể mất DNS cho tới khi chạy lại Reclaim.");
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

        /// <summary>Gọi định kỳ: giữ DNS đúng, kiểm tra sức khỏe nextdns, giữ chặn đường vòng, hết hạn chế độ bảo trì.</summary>
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
                    RouteDns(true);
                }
                EnsureGuardIfDue(false);
                RunWatchdog();
                LogSummaryIfDue();
            }
        }

        // ------------------------------------------------------------ trạng thái

        public StateDto GetState(string token)
        {
            lock (_gate)
            {
                bool hasPw = _settings.HasPassword;
                long now = DateTime.UtcNow.Ticks;
                // Lỗi = DNS không dùng được (nextdns hỏng) hoặc một thao tác vừa thất bại. Cảnh báo = bảo vệ vẫn chạy nhưng
                // thiếu một lớp phụ (adapter lẻ chưa đặt được DNS, chưa tạo được quy tắc chặn đường vòng).
                string error = _lastError;
                var warnings = new List<string>();
                foreach (KeyValuePair<string, string> issue in _issues)
                {
                    if (issue.Key == "nextdns") { if (string.IsNullOrEmpty(error)) error = issue.Value; }
                    else warnings.Add(issue.Value);
                }
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
                    LastError = error,
                    Warning = string.Join(" ", warnings),
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
            var all = new List<string>();
            Func<int, IList<string>> tail = LogTail;
            if (tail != null)
            {
                try
                {
                    all.Add("===== Nhật ký Reclaim (reclaim.log, 400 dòng cuối) =====");
                    all.AddRange(tail(400));
                }
                catch (Exception ex) { all.Add("(không đọc được reclaim.log: " + ex.Message + ")"); }
            }
            all.Add("===== Nhật ký nextdns =====");
            all.AddRange(_runner.RecentLog());
            return all.ToArray();
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
                    bool touched;
                    string failure = Activate(id, out touched);
                    if (failure != null)
                    {
                        if (touched) Rollback(before); // chưa đụng tới nextdns đang chạy thì không có gì để hoàn tác
                        SetError(failure);
                        throw new ReclaimException(ErrorCodes.ApplyFailed, failure);
                    }
                }
                else
                {
                    // Khôi phục DNS TRƯỚC; nếu thất bại thì giữ nguyên bảo vệ (tắt nextdns khi DNS còn trỏ loopback = mất mạng).
                    lock (_gate) { _enforce = false; }
                    // Quy tắc chặn DNS ngoài phải biến mất trước khi DNS được trả về ban đầu. Không gỡ được thì giữ nguyên bảo vệ
                    // (tắt lúc này = DNS gốc bị chính quy tắc đó chặn = mất mạng).
                    if (!TryRemoveGuard())
                    {
                        lock (_gate) { _enforce = true; }
                        EnsureGuardIfDue(true);
                        const string guardMsg = "Không gỡ được quy tắc chặn DNS của Reclaim nên chưa thể tắt bảo vệ (tắt lúc này sẽ làm máy mất DNS). Hãy thử lại.";
                        SetError(guardMsg);
                        throw new ReclaimException(ErrorCodes.ApplyFailed, guardMsg);
                    }
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
                        lock (_lifecycle) { if (!_stopped) RouteDns(); }
                        EnsureGuardIfDue(true); // khôi phục lại các chặn vừa gỡ
                        const string msg = "Không khôi phục được DNS ban đầu nên chưa thể tắt bảo vệ. Hãy thử lại.";
                        SetError(msg);
                        throw new ReclaimException(ErrorCodes.ApplyFailed, msg);
                    }
                    _runner.Stop();
                    TryRecoverOrphans();
                    ClearIssues();
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

        /// <param name="touched">true nếu đã đụng tới nextdns/DNS đang chạy (cần Rollback khi thất bại).</param>
        /// <returns>null nếu thành công, ngược lại là thông báo lỗi.</returns>
        private string Activate(string id, out bool touched)
        {
            touched = false;

            // Cấu hình không đổi và nextdns đang chạy: không khởi động lại (khởi động lại = nextdns về trạng thái nguội,
            // vài giây tới ~16 giây không có DNS). Chỉ áp lại DNS và các chặn đường vòng nếu cần.
            bool unchanged;
            bool changingWhileEnforcing;
            lock (_gate)
            {
                unchanged = _settings.Enabled && _enforce && _settings.ProfileId == id;
                changingWhileEnforcing = _enforce && _settings.ProfileId != id;
            }
            if (unchanged && _runner.IsRunning)
            {
                lock (_lifecycle)
                {
                    if (_stopped) return "Dịch vụ đang dừng.";
                    RouteDns();
                }
                EnsureGuardIfDue(true);
                return null;
            }

            try { EnsureBinaryTrusted(); }
            catch (ReclaimException ex) { return ex.Message; }

            // Đang bảo vệ bằng ID cũ mà đổi sang ID mới: thử ID mới bằng một nextdns tạm ở cổng khác TRƯỚC, để nhập sai
            // ID không phải dừng nextdns đang phục vụ (dừng = DNS của máy chết tới khi khôi phục xong).
            if (changingWhileEnforcing)
            {
                string bad = VerifyProfile(id);
                if (bad != null) return bad;
            }

            touched = true;
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
                DnsRouteResult route = RouteDns();
                if (route == null || !route.Usable)
                {
                    return "Không đặt được DNS hệ thống về nextdns" +
                           (route == null ? "." : " (adapter lỗi: " + route.Describe() + ").");
                }
                ResetWatchdog();
            }
            EnsureGuardIfDue(true);
            return null;
        }

        private string VerifyProfile(string id)
        {
            try
            {
                return _verifier.Verify(id, (int)_opt.VerifyTimeout.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                // Lỗi của chính công cụ kiểm tra không được chặn người dùng: đi tiếp theo đường kiểm tra thường.
                _log.Warn("Verify: " + ex.Message);
                return null;
            }
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
                try
                {
                    if (StartRunnerGuarded(before.ProfileId)) ResetWatchdog();
                }
                catch (Exception ex) { _log.Error("Rollback: " + ex.Message); }
            }
            else
            {
                _runner.Stop();
                lock (_gate) { _enforce = false; }
                TryRemoveGuard();
                try { _dns.ResetAll(); }
                catch (Exception ex) { _log.Warn("ResetAll: " + ex.Message); }
                TryRecoverOrphans();
                ClearIssues();
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
            DateTime deadline = started + _opt.HealthTimeout;
            int last = -1;
            bool answered = false;
            while (DateTime.UtcNow < deadline)
            {
                last = _probe.QueryRcode(_opt.ProbeTimeoutMs);
                if (last == 0 || last == 3)
                {
                    _log.Info("nextdns sẵn sàng sau " + (int)(DateTime.UtcNow - started).TotalSeconds + " giây.");
                    return null;
                }
                if (last >= 0) answered = true;

                // nextdns đã thoát nhiều lần liên tiếp (cổng 53 bận, bị antivirus cách ly...): báo nguyên nhân thật ngay,
                // không chờ hết thời gian.
                if (!_runner.IsRunning && _runner.ConsecutiveFailures >= 2)
                {
                    return "nextdns không chạy được" + Detail(_runner.LastProblemLine());
                }
                Thread.Sleep(_opt.PollSleepMs);
            }
            _log.Warn("nextdns chưa sẵn sàng sau " + (int)_opt.HealthTimeout.TotalSeconds + " giây (RCODE cuối: " + last + ").");
            string detail = Detail(_runner.LastProblemLine());
            return answered
                ? "NextDNS trả lỗi (mã DNS " + last + "). Hãy kiểm tra lại ID NextDNS" + detail
                : "nextdns chưa kết nối được tới NextDNS sau " + (int)_opt.HealthTimeout.TotalSeconds +
                  " giây. Hãy kiểm tra kết nối mạng rồi thử lại" + detail;
        }

        /// <summary>Biến dòng lỗi thô của nextdns thành phần đuôi câu thông báo ("" nếu không có dòng nào).</summary>
        private static string Detail(string line)
        {
            if (string.IsNullOrEmpty(line)) return ".";
            string lower = line.ToLowerInvariant();
            if (lower.Contains("only one usage") || lower.Contains("address already in use") || lower.Contains("access permissions"))
                return " (cổng 53 đang bị chương trình khác dùng hoặc bị chặn). Chi tiết: " + line;
            return ". Chi tiết: " + line;
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

        // ------------------------------------------------------------ DNS, chặn đường vòng, watchdog

        /// <summary>Ép DNS về loopback và ghi nhận adapter lỗi thành "vấn đề" hiển thị. Gọi dưới _lifecycle.</summary>
        /// <returns>null nếu việc gọi ném ngoại lệ.</returns>
        private DnsRouteResult RouteDns(bool fromTick = false)
        {
            DnsRouteResult result;
            try
            {
                result = _dns.RouteToLoopback();
            }
            catch (Exception ex)
            {
                _log.Warn("RouteToLoopback: " + ex.Message);
                SetIssue("dns", "Không đặt được DNS về nextdns: " + ex.Message);
                return null;
            }
            SetIssue("dns", result.AllOk ? null : "Chưa đặt được DNS cho adapter: " + result.Describe() + ".");
            if (fromTick && result.Changed)
            {
                // Đang bảo vệ mà DNS của adapter bị đổi (DHCP gia hạn, VPN, người dùng, phần mềm khác...): đã được ép lại.
                int n;
                lock (_watchLock) { _cDnsFixed++; _cDnsFixedTotal++; n = _cDnsFixedTotal; }
                _log.Warn("DNS của adapter đã bị đổi và được Reclaim ép lại về loopback (lần thứ " + n + " từ khi dịch vụ chạy).");
            }
            return result;
        }

        /// <summary>Áp dụng/kiểm tra lại các chặn đường vòng (tường lửa, chính sách trình duyệt). Gọi định kỳ từ Tick.</summary>
        private void EnsureGuardIfDue(bool force)
        {
            DateTime now = DateTime.UtcNow;
            lock (_watchLock)
            {
                if (!force && now < _nextGuardUtc) return;
                _nextGuardUtc = now + _opt.GuardCheckInterval;
            }
            lock (_lifecycle)
            {
                if (_stopped) return;
                bool enforce;
                lock (_gate) { enforce = _enforce; }
                if (!enforce) return;

                IList<string> problems;
                try { problems = _guard.Ensure(); }
                catch (Exception ex) { problems = new List<string> { ex.Message }; }
                // Không chặn được đường vòng chỉ làm giảm mức bảo vệ chứ không làm mất mạng: báo lỗi, không dừng.
                SetIssue("guard", problems == null || problems.Count == 0
                    ? null
                    : "Chưa chặn được hết các đường vòng bộ lọc: " + string.Join("; ", problems) + ".");
            }
        }

        /// <returns>true nếu đã gỡ sạch. Khi false, KHÔNG được trả DNS về ban đầu: quy tắc chặn DNS còn sót sẽ làm mất DNS.</returns>
        private bool TryRemoveGuard()
        {
            bool removed;
            try
            {
                removed = _guard.Remove();
                if (!removed) _log.Warn("Chưa gỡ hết các chặn đường vòng của Reclaim.");
            }
            catch (Exception ex)
            {
                _log.Warn("Guard.Remove: " + ex.Message);
                removed = false;
            }
            if (removed) SetIssue("guard", null);
            return removed;
        }

        private void ResetWatchdog()
        {
            lock (_watchLock)
            {
                _nextHealthUtc = DateTime.UtcNow + _opt.WatchdogInterval; // nextdns vừa (khởi động lại) nguội: chờ nó ổn định
                _unhealthySinceUtc = DateTime.MinValue;
                _downSinceUtc = DateTime.MinValue;
                _noResponseStreak = 0;
            }
            SetIssue("nextdns", null);
        }

        /// <summary>
        /// Kiểm tra sức khỏe định kỳ khi đang bảo vệ. Quy tắc:
        ///  - nextdns trả lời hợp lệ: khỏe.
        ///  - nextdns trả lời nhưng lỗi (SERVFAIL...): sự cố phía mạng/NextDNS, KHÔNG khởi động lại (không giúp được gì).
        ///  - nextdns không trả lời gì (treo) 3 lần liên tiếp, kéo dài đủ lâu, máy đang có mạng, và cách lần khởi động lại
        ///    trước đủ xa: khởi động lại nextdns. Thời gian máy không có mạng không được tính vào 3 lần đó.
        ///  - Chính sách khi nextdns không chạy được vẫn là fail-closed (không tự trả DNS): chỉ báo nguyên nhân.
        /// </summary>
        private void RunWatchdog()
        {
            DateTime now = DateTime.UtcNow;
            lock (_watchLock) { if (now < _nextHealthUtc) return; }
            if (!Monitor.TryEnter(_applyLock)) return; // đang áp dụng cấu hình: để SaveSettings tự lo
            try
            {
                if (_stopped) return;
                bool enforce;
                lock (_gate) { enforce = _enforce; }
                if (!enforce) return;

                if (!_runner.IsRunning)
                {
                    string down = Detail(_runner.LastProblemLine());
                    bool report;
                    bool first;
                    lock (_watchLock)
                    {
                        first = _downSinceUtc == DateTime.MinValue;
                        if (first) _downSinceUtc = now;
                        report = now - _downSinceUtc >= _opt.IssueAfter;
                        _nextHealthUtc = now + _opt.WatchdogRetryInterval;
                    }
                    if (first) _log.Warn("Giám sát: tiến trình nextdns không chạy.");
                    if (report) SetIssue("nextdns", "nextdns không chạy được" + down);
                    return;
                }
                lock (_watchLock) { _downSinceUtc = DateTime.MinValue; }

                var timer = System.Diagnostics.Stopwatch.StartNew();
                int rcode = _probe.QueryRcode(_opt.WatchdogProbeTimeoutMs);
                timer.Stop();
                now = DateTime.UtcNow;
                int ms = (int)timer.ElapsedMilliseconds;

                if (rcode == 0 || rcode == 3)
                {
                    TimeSpan wasBad = TimeSpan.Zero;
                    bool recovered;
                    lock (_watchLock)
                    {
                        recovered = _unhealthySinceUtc != DateTime.MinValue;
                        if (recovered) wasBad = now - _unhealthySinceUtc;
                        _cProbeOk++;
                        _probeMsSum += ms;
                        if (ms > _probeMsMax) _probeMsMax = ms;
                        _unhealthySinceUtc = DateTime.MinValue;
                        _noResponseStreak = 0;
                        _nextHealthUtc = now + _opt.WatchdogInterval;
                    }
                    if (recovered) _log.Info("Giám sát: nextdns khỏe lại sau " + (int)wasBad.TotalSeconds + " giây không ổn định.");
                    SetIssue("nextdns", null);
                    return;
                }

                string detail = Detail(_runner.LastProblemLine());
                bool network = _probe.IsNetworkAvailable();
                bool reportIssue;
                bool restart;
                bool firstBad;
                lock (_watchLock)
                {
                    firstBad = _unhealthySinceUtc == DateTime.MinValue;
                    if (firstBad) _unhealthySinceUtc = now;
                    if (rcode < 0) _cProbeNoResponse++; else _cProbeDnsError++;
                    // Không có mạng thì "không trả lời" là chuyện bình thường: không cộng dồn vào điều kiện khởi động lại.
                    if (!network || rcode >= 0) _noResponseStreak = 0; else _noResponseStreak++;
                    TimeSpan bad = now - _unhealthySinceUtc;
                    _nextHealthUtc = now + _opt.WatchdogRetryInterval;
                    reportIssue = bad >= _opt.IssueAfter;
                    restart = network
                              && rcode < 0
                              && _noResponseStreak >= _opt.NoResponseChecksBeforeRestart
                              && bad >= _opt.UnhealthyRestartAfter
                              && now - _lastRestartUtc >= _opt.MinRestartGap;
                }

                if (firstBad)
                {
                    _log.Warn("Giám sát: nextdns bắt đầu không ổn định (mã DNS " + rcode + ", chờ " + ms + " ms, " +
                              (network ? "máy có mạng" : "máy KHÔNG có mạng") + ").");
                }
                if (reportIssue)
                {
                    string message;
                    if (!network) message = "Máy đang không có kết nối mạng; DNS tạm dừng cho tới khi có mạng trở lại.";
                    else if (rcode < 0) message = "nextdns không trả lời truy vấn DNS" + detail;
                    else message = "NextDNS trả lỗi (mã DNS " + rcode + "). Hãy kiểm tra kết nối mạng và ID NextDNS" + detail;
                    SetIssue("nextdns", message);
                }
                if (restart) RestartNextDns();
            }
            finally
            {
                Monitor.Exit(_applyLock);
            }
        }

        /// <summary>Mỗi SummaryInterval ghi một dòng tóm tắt + ảnh chụp DNS các adapter: để đọc xu hướng khi gửi nhật ký đi chẩn đoán.</summary>
        private void LogSummaryIfDue()
        {
            DateTime now = DateTime.UtcNow;
            string issues;
            lock (_gate) { issues = _issues.Count == 0 ? "không" : string.Join(" | ", _issues.Values); }

            string line;
            lock (_watchLock)
            {
                if (now < _nextSummaryUtc) return;
                _nextSummaryUtc = now + _opt.SummaryInterval;
                int probes = _cProbeOk + _cProbeDnsError + _cProbeNoResponse;
                line = "Tóm tắt: thăm dò " + probes + " lần (ổn " + _cProbeOk + ", NextDNS trả lỗi " + _cProbeDnsError +
                       ", không trả lời " + _cProbeNoResponse + "); độ trễ " +
                       (_cProbeOk == 0 ? "-" : (_probeMsSum / _cProbeOk) + " ms TB, " + _probeMsMax + " ms tối đa") +
                       "; DNS bị ép lại " + _cDnsFixed + " lần; nextdns khởi động lại " + _cRestarts +
                       " lần (tổng " + _cRestartsTotal + "); sự cố hiện tại: " + issues + ".";
                _cProbeOk = _cProbeDnsError = _cProbeNoResponse = 0;
                _probeMsSum = 0;
                _probeMsMax = 0;
                _cDnsFixed = 0;
                _cRestarts = 0;
            }
            _log.Info(line);
            try { _log.Info("Adapter: " + _dns.DescribeAdapters()); }
            catch (Exception ex) { _log.Warn("DescribeAdapters: " + ex.Message); }
        }

        private void RestartNextDns()
        {
            string id;
            lock (_gate) { id = _settings.ProfileId; }
            lock (_lifecycle)
            {
                if (_stopped) return;
                try
                {
                    EnsureBinaryTrusted(); // khởi động lại cũng phải qua kiểm tra mã băm
                    _log.Warn("nextdns không phản hồi: khởi động lại.");
                    _runner.Start(id);
                    lock (_watchLock) { _cRestarts++; _cRestartsTotal++; }
                }
                catch (ReclaimException ex)
                {
                    SetIssue("nextdns", ex.Message);
                }
                catch (Exception ex)
                {
                    _log.Error("Restart: " + ex.Message);
                }
                lock (_watchLock)
                {
                    DateTime now = DateTime.UtcNow;
                    _lastRestartUtc = now;
                    _noResponseStreak = 0;
                    _unhealthySinceUtc = DateTime.MinValue;
                    _nextHealthUtc = now + _opt.HealthTimeout;
                }
            }
        }

        private void SetIssue(string key, string message)
        {
            bool changed;
            lock (_gate)
            {
                string old;
                _issues.TryGetValue(key, out old);
                if (string.IsNullOrEmpty(message))
                {
                    changed = _issues.Remove(key);
                }
                else
                {
                    changed = old != message;
                    _issues[key] = message;
                }
            }
            if (changed && !string.IsNullOrEmpty(message)) _log.Warn(message);
        }

        private void ClearIssues()
        {
            lock (_gate) { _issues.Clear(); }
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
                // SetProtection / BeginMaintenance / RemovePassword / Tick có thể gọi cùng lúc: không để hai lần đổi ACL xen kẽ nhau.
                lock (_hardenLock)
                {
                    _hardener.Apply(options);
                }
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
