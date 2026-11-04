using System.ServiceProcess;
using Reclaim.Core.Hosting;
using Reclaim.Core.Infrastructure;

namespace Reclaim.Service
{
    /// <summary>Vỏ mỏng của dịch vụ Windows; toàn bộ logic nằm trong ReclaimHost (Reclaim.Core).</summary>
    internal sealed class ReclaimService : ServiceBase
    {
        private ReclaimHost _host;

        public ReclaimService()
        {
            ServiceName = AppPaths.ServiceName;
            CanStop = true;
            CanShutdown = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            _host = new ReclaimHost(false);
            _host.Start();
        }

        // Dừng chủ động (bảo trì, gỡ cài đặt): trả DNS về như cũ để máy không mất mạng.
        protected override void OnStop()
        {
            StopHost(true);
        }

        // Tắt máy: giữ nguyên DNS (fail-closed) để lần khởi động sau không có khoảng hở.
        protected override void OnShutdown()
        {
            StopHost(false);
        }

        private void StopHost(bool resetDns)
        {
            ReclaimHost host = _host;
            _host = null;
            if (host != null) host.Stop(resetDns);
        }
    }
}
