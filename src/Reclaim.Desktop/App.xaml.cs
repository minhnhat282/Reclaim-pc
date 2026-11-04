using System;
using System.Linq;
using System.Threading;
using System.Windows;
using Reclaim.Desktop.Services;

namespace Reclaim.Desktop
{
    public partial class App : Application
    {
        private Mutex _singleInstance;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            Theme.Apply();

            // /uninstall (từ Apps & features) phải chạy được kể cả khi cửa sổ chính đang mở ở tiến trình khác.
            if (e.Args.Any(a => string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                int code = await UninstallFlow.RunAsync(null, new ReclaimClient());
                if (code == 0) CloseOtherInstances(); // đóng cửa sổ Reclaim còn mở để thư mục cài đặt được xóa sạch
                Shutdown(code);
                return;
            }

            bool createdNew;
            _singleInstance = new Mutex(true, "Local\\Reclaim.Desktop", out createdNew);
            if (!createdNew)
            {
                Shutdown(0);
                return;
            }

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }

        private static void CloseOtherInstances()
        {
            int self = System.Diagnostics.Process.GetCurrentProcess().Id;
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName("Reclaim"))
            {
                try
                {
                    if (p.Id != self) p.Kill();
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
    }
}
