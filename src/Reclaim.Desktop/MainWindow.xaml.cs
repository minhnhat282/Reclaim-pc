using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Reclaim.Core.Contracts;
using Reclaim.Core.Domain;
using Reclaim.Core.Infrastructure;
using Reclaim.Desktop.Services;

namespace Reclaim.Desktop
{
    public partial class MainWindow : Window
    {
        private readonly ReclaimClient _client = new ReclaimClient();
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        private StateDto _state;
        private bool _dirty;      // người dùng đã sửa ô nhập, đừng ghi đè bằng trạng thái từ dịch vụ
        private bool _busy;       // đang thực hiện thao tác (khóa giao diện)
        private bool _polling;
        private bool _updating;   // đang gán giá trị từ dịch vụ vào ô nhập (không phải người dùng sửa)

        public MainWindow()
        {
            InitializeComponent();
            Loaded += async delegate
            {
                await RefreshAsync();
                _timer.Start();
            };
            _timer.Tick += async delegate
            {
                if (!_busy) await RefreshAsync();
            };
            DataObject.AddPastingHandler(ProfileBox, OnProfilePaste);
            Closing += OnClosing;
            Closed += delegate { Application.Current.Shutdown(); };
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            _timer.Stop();
            _client.LockQuietly(); // đóng cửa sổ = khóa lại
        }

        // ------------------------------------------------------------ hiển thị trạng thái

        private async Task RefreshAsync()
        {
            if (_polling) return;
            _polling = true;
            try
            {
                StateDto s = await _client.GetStateAsync();
                _state = s;
                ApplyState(s);
            }
            catch (ReclaimException ex)
            {
                _state = null;
                ShowUnavailable(ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                _state = null;
                ShowUnavailable(ErrorCodes.Internal, ex.Message);
            }
            finally
            {
                _polling = false;
            }
        }

        /// <summary>Phân loại lý do không liên lạc được: không còn coi mọi lỗi là "dịch vụ chưa chạy".</summary>
        private void ShowUnavailable(string code, string reason)
        {
            MainPanel.Visibility = Visibility.Collapsed;
            UnavailablePanel.Visibility = Visibility.Visible;

            bool installed = AppInstaller.IsServiceInstalled();
            switch (code)
            {
                case ErrorCodes.Unavailable:
                    UnavailableText.Text = installed ? Strings.ServiceNotRunning : Strings.ServiceNotInstalled;
                    InstallButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case ErrorCodes.BadServer:
                    UnavailableText.Text = Strings.ServiceUntrusted;
                    InstallButton.Visibility = Visibility.Collapsed;
                    break;
                default:
                    UnavailableText.Text = Strings.ServiceCommError + " " + reason;
                    InstallButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
                    break;
            }
            SetStatus(Strings.StatusInactive, "Danger");
        }

        private void ApplyState(StateDto s)
        {
            UnavailablePanel.Visibility = Visibility.Collapsed;
            MainPanel.Visibility = Visibility.Visible;

            bool locked = s.PasswordSet && !s.Unlocked;
            bool settingsLocked = locked && s.Protection.Settings;

            if (!_dirty)
            {
                _updating = true;
                try
                {
                    ProfileBox.Text = s.ProfileId ?? string.Empty;
                    EnabledToggle.IsChecked = s.Enabled;
                }
                finally
                {
                    _updating = false;
                }
            }
            ProfileBox.IsEnabled = !settingsLocked;
            EnabledToggle.IsEnabled = !settingsLocked;
            SaveButton.IsEnabled = !settingsLocked && !_busy;

            ProtUninstall.IsChecked = s.Protection.Uninstall;
            ProtSettings.IsChecked = s.Protection.Settings;
            ProtService.IsChecked = s.Protection.Service;
            bool canEditProtection = !locked;
            ProtUninstall.IsEnabled = ProtSettings.IsEnabled = ProtService.IsEnabled = canEditProtection;

            if (!s.PasswordSet)
            {
                LockButton.Content = Strings.SetPassword;
                LockInfoText.Text = Strings.NoPasswordInfo;
            }
            else if (locked)
            {
                LockButton.Content = Strings.Unlock;
                LockInfoText.Text = Strings.LockedInfo;
            }
            else
            {
                LockButton.Content = Strings.Lock;
                LockInfoText.Text = Strings.UnlockedInfo;
            }
            if (s.ThrottleSeconds > 0)
                LockInfoText.Text += " Tạm khóa nhập mật khẩu " + s.ThrottleSeconds + " giây.";
            if (s.MaintenanceSeconds > 0)
                LockInfoText.Text += " Đang bảo trì, bảo vệ bật lại sau " + s.MaintenanceSeconds + " giây.";

            ChangePwItem.IsEnabled = s.PasswordSet;
            RemovePwItem.IsEnabled = s.PasswordSet;
            MaintenanceItem.IsEnabled = s.PasswordSet && s.Protection.Any;

            // Trạng thái tổng quát
            if (!string.IsNullOrEmpty(s.LastError))
            {
                SetStatus(Strings.StatusError, "Danger");
                ShowMessage(s.LastError, true);
            }
            else if (!s.Enabled)
            {
                SetStatus(Strings.StatusOff, "Warn");
            }
            else if (s.NextDnsRunning && s.Enforcing)
            {
                SetStatus(Strings.StatusProtected, "Ok");
            }
            else
            {
                SetStatus(Strings.StatusStarting, "Warn");
            }

            WebLink.Visibility = ProfileId.IsValid(s.ProfileId) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetStatus(string text, string brushKey)
        {
            StatusText.Text = text;
            StatusDot.Fill = (Brush)FindResource(brushKey);
        }

        private void ShowMessage(string text, bool isError)
        {
            MessageText.Text = text;
            MessageText.Foreground = (Brush)FindResource(isError ? "Danger" : "Ok");
            MessageText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        // ------------------------------------------------------------ chạy thao tác

        /// <summary>Chạy một thao tác: khóa giao diện, hiển thị lỗi, làm mới trạng thái khi xong.</summary>
        private async Task RunAsync(Func<Task> operation, string busyText)
        {
            _busy = true;
            ShowMessage(busyText ?? string.Empty, false);
            MainPanel.IsEnabled = false;
            try
            {
                await operation();
            }
            catch (ReclaimException ex)
            {
                if (ex.Code != ErrorCodes.Cancelled) ShowMessage(ex.Message, true);
            }
            finally
            {
                MainPanel.IsEnabled = true;
                _busy = false;
                await RefreshAsync();
            }
        }

        /// <summary>Thực hiện thao tác; nếu dịch vụ yêu cầu mở khóa thì hỏi mật khẩu rồi thử lại một lần.</summary>
        private async Task<T> GuardedAsync<T>(Func<Task<T>> operation)
        {
            try
            {
                return await operation();
            }
            catch (ReclaimException ex) when (ex.Code == ErrorCodes.NeedUnlock)
            {
                if (!PromptUnlock()) throw new ReclaimException(ErrorCodes.Cancelled, string.Empty);
                return await operation();
            }
        }

        private bool PromptUnlock()
        {
            var dialog = new PasswordDialog(PasswordDialogMode.Unlock, async input =>
            {
                try { await _client.UnlockAsync(input.Current); return null; }
                catch (ReclaimException ex) { return ex.Message; }
            });
            dialog.Owner = this;
            return dialog.ShowDialog() == true;
        }

        /// <summary>
        /// Đặt mật khẩu lần đầu: dịch vụ chỉ chấp nhận từ quản trị viên, nên mở lại Reclaim.exe với quyền nâng cao
        /// (hộp thoại UAC) để hiện hộp thoại đặt mật khẩu. Tiến trình đó không chuyển token cho cửa sổ này nên sau đó cần "Mở khóa".
        /// </summary>
        private async Task CreatePasswordAsync()
        {
            string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
            ElevationResult result = await Task.Run(() => Elevation.Run(exe, "/setpassword", true));
            if (result.Ok)
            {
                ShowMessage(Strings.PasswordCreated, false);
            }
            else if (result.Message != "cancelled")
            {
                ShowMessage(result.Message, true);
            }
        }

        // ------------------------------------------------------------ sự kiện: kết nối

        private void ProfileBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (!_updating) _dirty = true; // mọi thay đổi không do dịch vụ gán đều là do người dùng (gõ, dán, menu chuột phải)
        }

        /// <summary>Dán ID: bỏ khoảng trắng/xuống dòng thừa để dán từ trang web không bị lỗi.</summary>
        private void OnProfilePaste(object sender, DataObjectPastingEventArgs e)
        {
            string text = e.DataObject.GetDataPresent(DataFormats.UnicodeText)
                ? e.DataObject.GetData(DataFormats.UnicodeText) as string
                : null;
            if (text == null)
            {
                e.CancelCommand();
                return;
            }
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()));
            e.DataObject = data;
        }

        private void EnabledToggle_Click(object sender, RoutedEventArgs e)
        {
            _dirty = true;
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string id = ProfileBox.Text;
            bool enabled = EnabledToggle.IsChecked == true;
            await RunAsync(async delegate
            {
                await GuardedAsync(() => _client.SaveAsync(id, enabled));
                _dirty = false;
                ShowMessage(Strings.Applied, false);
            }, Strings.Applying);
        }

        private void WebLink_Click(object sender, MouseButtonEventArgs e)
        {
            string id = _state == null ? null : _state.ProfileId;
            string url = ProfileId.IsValid(id) ? "https://my.nextdns.io/" + id + "/setup" : "https://my.nextdns.io";
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        // ------------------------------------------------------------ sự kiện: tự bảo vệ

        private async void LockButton_Click(object sender, RoutedEventArgs e)
        {
            if (_state == null) return;
            if (!_state.PasswordSet)
            {
                await RunAsync(() => CreatePasswordAsync(), null);
            }
            else if (!_state.Unlocked)
            {
                await RunAsync(async delegate { PromptUnlock(); await Task.FromResult(0); }, null);
            }
            else
            {
                await RunAsync(() => _client.LockAsync(), null);
            }
        }

        private async void Protection_Click(object sender, RoutedEventArgs e)
        {
            if (_state != null && !_state.PasswordSet)
            {
                await RefreshAsync(); // hoàn nguyên công tắc
                ShowMessage(Strings.NeedPasswordFirst, true);
                await RunAsync(() => CreatePasswordAsync(), null);
                return;
            }

            var options = new ProtectionOptions
            {
                Uninstall = ProtUninstall.IsChecked == true,
                Settings = ProtSettings.IsChecked == true,
                Service = ProtService.IsChecked == true
            };
            await RunAsync(async delegate
            {
                await GuardedAsync(() => _client.SetProtectionAsync(options));
                ShowMessage(Strings.ProtectionUpdated, false);
            }, null);
        }

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            MoreMenu.PlacementTarget = MoreButton;
            MoreMenu.IsOpen = true;
        }

        private async void ChangePw_Click(object sender, RoutedEventArgs e)
        {
            await RunAsync(async delegate
            {
                var dialog = new PasswordDialog(PasswordDialogMode.Change, async input =>
                {
                    try { await _client.ChangePasswordAsync(input.Current, input.New); return null; }
                    catch (ReclaimException ex) { return ex.Message; }
                });
                dialog.Owner = this;
                dialog.ShowDialog();
                await Task.FromResult(0);
            }, null);
        }

        private async void RemovePw_Click(object sender, RoutedEventArgs e)
        {
            await RunAsync(async delegate
            {
                var dialog = new PasswordDialog(PasswordDialogMode.Remove, async input =>
                {
                    try { await _client.RemovePasswordAsync(input.Current); return null; }
                    catch (ReclaimException ex) { return ex.Message; }
                });
                dialog.Owner = this;
                dialog.ShowDialog();
                await Task.FromResult(0);
            }, null);
        }

        private async void Maintenance_Click(object sender, RoutedEventArgs e)
        {
            await RunAsync(async delegate
            {
                await GuardedAsync(async delegate { await _client.BeginMaintenanceAsync(); return 0; });
                ShowMessage(Strings.MaintenanceStarted, false);
            }, null);
        }

        private async void Log_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string[] lines = await GuardedAsync(() => _client.GetLogAsync());
                string text = lines.Length == 0
                    ? "Chưa có dòng nhật ký nào từ nextdns.\n\nNhật ký đầy đủ: " + AppPaths.LogFile + " và Event Viewer (nguồn \"nextdns\")."
                    : string.Join(Environment.NewLine, lines);
                MessageBox.Show(this, text, "Nhật ký nextdns", MessageBoxButton.OK, MessageBoxImage.None);
            }
            catch (ReclaimException ex)
            {
                if (ex.Code != ErrorCodes.Cancelled) ShowMessage(ex.Message, true);
            }
        }

        // ------------------------------------------------------------ cài đặt / gỡ cài đặt

        private async void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            string exe = Path.Combine(AppPaths.BaseDir, AppPaths.ServiceExeName);
            if (!File.Exists(exe))
            {
                ShowMessage("Không tìm thấy " + AppPaths.ServiceExeName + " cạnh ứng dụng.", true);
                return;
            }
            InstallButton.IsEnabled = false;
            ElevationResult result = await Task.Run(() => Elevation.Run(exe, "install"));
            InstallButton.IsEnabled = true;
            if (!result.Ok)
            {
                UnavailablePanel.Visibility = Visibility.Visible;
                UnavailableText.Text = result.Message;
                return;
            }
            await Task.Delay(1500); // chờ dịch vụ mở pipe
            await RefreshAsync();
        }

        private async void Uninstall_Click(object sender, MouseButtonEventArgs e)
        {
            _timer.Stop();
            int code = await UninstallFlow.RunAsync(this, _client);
            if (code == 0)
            {
                Application.Current.Shutdown();
            }
            else
            {
                _timer.Start();
            }
        }
    }
}
