using System;
using System.Threading.Tasks;
using System.Windows;
using Reclaim.Core.Contracts;
using Reclaim.Core.Domain;
using Reclaim.Core.Infrastructure;
using Reclaim.Desktop.Services;

namespace Reclaim.Desktop
{
    /// <summary>
    /// Quy trình gỡ cài đặt dùng chung cho nút trong ứng dụng và mục "Apps &amp; features" (Reclaim.exe /uninstall):
    /// xác nhận -> (nếu đang bảo vệ) mở khóa bằng mật khẩu + yêu cầu dịch vụ tạm gỡ bảo vệ -> chạy gỡ cài đặt với quyền quản trị.
    /// </summary>
    internal static class UninstallFlow
    {
        /// <returns>0 nếu đã gỡ cài đặt; khác 0 nếu hủy hoặc lỗi.</returns>
        public static async Task<int> RunAsync(Window owner, ReclaimClient client)
        {
            if (MessageBox.Show(Strings.ConfirmUninstall, "Reclaim", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return 1;

            StateDto state = null;
            try { state = await client.GetStateAsync(); }
            catch (ReclaimException) { /* dịch vụ không chạy: không có lớp bảo vệ nào đang hoạt động */ }

            if (state != null && state.PasswordSet && state.Protection.Any)
            {
                if (!state.Unlocked && !PromptUnlock(owner, client)) return 1;
                try
                {
                    await client.BeginMaintenanceAsync();
                }
                catch (ReclaimException ex)
                {
                    MessageBox.Show(ex.Message, "Reclaim", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return 1;
                }
            }

            string exe = AppPaths.FindServiceExe();
            ElevationResult result = await Task.Run(() => Elevation.Run(exe, "uninstall"));
            if (result.Ok)
            {
                MessageBox.Show(Strings.Uninstalled, "Reclaim", MessageBoxButton.OK, MessageBoxImage.Information);
                return 0;
            }
            MessageBox.Show(result.Message, "Reclaim", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }

        private static bool PromptUnlock(Window owner, ReclaimClient client)
        {
            var dialog = new PasswordDialog(PasswordDialogMode.Unlock, async input =>
            {
                try { await client.UnlockAsync(input.Current); return null; }
                catch (ReclaimException ex) { return ex.Message; }
            });
            if (owner != null) dialog.Owner = owner;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return dialog.ShowDialog() == true;
        }
    }
}
