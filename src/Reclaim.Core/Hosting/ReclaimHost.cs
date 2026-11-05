using System;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading;
using Reclaim.Core.Application;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Ipc;

namespace Reclaim.Core.Hosting
{
    /// <summary>Composition root của dịch vụ: lắp ráp mọi thành phần. Dùng chung cho dịch vụ thật và chế độ "run" (console).</summary>
    public sealed class ReclaimHost : IDisposable
    {
        private readonly bool _echo;
        private ILog _log;
        private ReclaimEngine _engine;
        private PipeServer _pipe;
        private Timer _tick;
        private readonly object _tickLock = new object();
        private bool _stopped;
        private Mutex _singleHost;

        public ReclaimHost(bool echoToConsole)
        {
            _echo = echoToConsole;
        }

        public void Start()
        {
            AcquireSingleHost();
            Directory.CreateDirectory(AppPaths.DataDir);
            var fileLog = new FileLog(AppPaths.LogFile, _echo);
            _log = fileLog;
            _log.Info("==================== Reclaim khởi động ====================");
            LogEnvironment();

            var runner = new NextDnsProcessRunner(AppPaths.NextDnsExe, AppPaths.NextDnsConfFile, _log);
            _engine = new ReclaimEngine(
                new JsonSettingsStore(AppPaths.SettingsFile, _log),
                new NetshDnsConfigurator(_log),
                runner,
                new DnsProbe(),
                new WindowsProtectionHardener(_log),
                new WindowsBypassGuard(_log),
                new NextDnsProfileVerifier(AppPaths.NextDnsExe, Path.Combine(AppPaths.DataDir, "verify.conf"), _log),
                _log);

            _engine.LogTail = fileLog.Tail;
            _pipe = new PipeServer(new RequestRouter(_engine, _log).Handle, _log);
            _pipe.Start();

            // Việc nặng (khởi động nextdns, ACL) chạy nền để OnStart trả về nhanh.
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { _engine.Start(); }
                catch (Exception ex) { _log.Error("Start: " + ex); }
            });

            _tick = new Timer(OnTick, null, 3000, 3000);
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        }

        /// <summary>Ghi môi trường chạy một lần mỗi lần khởi động: đủ để chẩn đoán từ xa mà không cần hỏi người dùng.</summary>
        private void LogEnvironment()
        {
            try
            {
                _log.Info("Phiên bản Reclaim " + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version +
                          "; Windows " + Environment.OSVersion.Version + (Environment.Is64BitOperatingSystem ? " 64-bit" : " 32-bit") +
                          "; tiến trình " + (Environment.Is64BitProcess ? "64-bit" : "32-bit") + "; .NET " + Environment.Version);
                _log.Info("Thư mục chạy: " + AppPaths.BaseDir + "; nextdns.exe " + (File.Exists(AppPaths.NextDnsExe) ? "có" : "KHÔNG có"));
                _log.Info("IPv6 loopback bind được: " + NextDnsConfig.Ipv6LoopbackAvailable() +
                          "; địa chỉ nextdns sẽ nghe: " + string.Join(", ", NextDnsConfig.DefaultListens()));
                _log.Info("Adapter: " + new NetshDnsConfigurator(NullLog.Instance).DescribeAdapters());
            }
            catch (Exception ex)
            {
                _log.Warn("Không ghi được thông tin môi trường: " + ex.Message);
            }
        }

        /// <summary>
        /// Chỉ cho phép MỘT máy chủ Reclaim (dịch vụ hoặc chế độ console) chạy cùng lúc. Nếu có hai, chúng sẽ tranh cổng 53,
        /// ghi đè cấu hình của nhau và có thể dừng nhầm nextdns của nhau.
        /// </summary>
        private void AcquireSingleHost()
        {
            bool createdNew;
            try
            {
                _singleHost = new Mutex(false, @"Global\Reclaim.ServiceHost", out createdNew);
            }
            catch (UnauthorizedAccessException)
            {
                createdNew = false; // mutex đã tồn tại và thuộc phiên bản khác
            }
            if (!createdNew)
            {
                if (_singleHost != null) { _singleHost.Dispose(); _singleHost = null; }
                throw new Domain.ReclaimException(Domain.ErrorCodes.Internal,
                    "Một phiên bản Reclaim khác (dịch vụ hoặc chế độ console) đang chạy. Hãy dừng nó trước.");
            }
        }

        public void Stop(bool resetDns)
        {
            if (_stopped) return;
            _stopped = true;
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            if (_tick != null) _tick.Dispose();
            if (_pipe != null) _pipe.Dispose();
            if (_engine != null) _engine.Stop(resetDns);
            if (_log != null) _log.Info("Reclaim dừng" + (resetDns ? " (đã trả DNS)." : "."));
            if (_singleHost != null) { _singleHost.Dispose(); _singleHost = null; }
        }

        public void Dispose()
        {
            Stop(false);
        }

        private void OnNetworkChanged(object sender, EventArgs e)
        {
            Kick();
        }

        private void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            Kick();
        }

        private void Kick()
        {
            try { if (_tick != null && !_stopped) _tick.Change(500, 3000); }
            catch (ObjectDisposedException) { }
        }

        private void OnTick(object state)
        {
            if (!Monitor.TryEnter(_tickLock)) return;
            try
            {
                if (!_stopped) _engine.Tick();
            }
            catch (Exception ex)
            {
                _log.Warn("Tick: " + ex.Message);
            }
            finally
            {
                Monitor.Exit(_tickLock);
            }
        }
    }
}
