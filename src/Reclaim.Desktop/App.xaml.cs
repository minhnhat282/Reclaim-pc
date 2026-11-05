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

        /// <summary>Nhật ký phía giao diện (lỗi liên lạc với dịch vụ, tiến trình pipe lạ...). Nằm trong thư mục của tài khoản người dùng.</summary>
        public static readonly string GuiLogFile = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reclaim", "gui.log");

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            Theme.Apply();
            Reclaim.Core.Ipc.PipeClient.Log = new Reclaim.Core.Infrastructure.FileLog(GuiLogFile, false);

            // /uninstall (từ Apps & features) phải chạy được kể cả khi cửa sổ chính đang mở ở tiến trình khác.
            if (e.Args.Any(a => string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                int code = await UninstallFlow.RunAsync(null, new ReclaimClient());
                if (code == 0) CloseOtherInstances(); // đóng cửa sổ Reclaim còn mở để thư mục cài đặt được xóa sạch
                Shutdown(code);
                return;
            }

            // /setpassword: tiến trình ĐƯỢC NÂNG QUYỀN chỉ để đặt mật khẩu lần đầu (dịch vụ yêu cầu quản trị viên).
            if (e.Args.Any(a => string.Equals(a, "/setpassword", StringComparison.OrdinalIgnoreCase)))
            {
                Shutdown(RunSetPassword(e.Args));
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

        /// <summary>
        /// Hộp thoại đặt mật khẩu chạy trong tiến trình nâng quyền; kết quả ("OK" hoặc "cancelled") ghi vào file --result
        /// để tiến trình gọi đọc. Token mở khóa của tiến trình này bị thu hồi ngay: người dùng mở khóa lại trong cửa sổ chính.
        /// </summary>
        private static int RunSetPassword(string[] args)
        {
            string resultFile = null;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], "--result", StringComparison.OrdinalIgnoreCase)) resultFile = args[i + 1];
            }

            var client = new ReclaimClient();
            var dialog = new PasswordDialog(PasswordDialogMode.Create, async input =>
            {
                try { await client.SetPasswordAsync(input.New); return null; }
                catch (Reclaim.Core.Domain.ReclaimException ex) { return ex.Message; }
            });
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            bool ok = dialog.ShowDialog() == true;
            client.LockQuietly();

            if (resultFile != null)
            {
                try { System.IO.File.WriteAllText(resultFile, ok ? "OK" : "cancelled"); }
                catch { }
            }
            return ok ? 0 : 1;
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
