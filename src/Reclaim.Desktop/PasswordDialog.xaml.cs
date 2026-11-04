using System;
using System.Threading.Tasks;
using System.Windows;
using Reclaim.Core.Domain;
using Reclaim.Core.Security;

namespace Reclaim.Desktop
{
    public enum PasswordDialogMode
    {
        Unlock,
        Create,
        Change,
        Remove
    }

    public sealed class PasswordInput
    {
        public string Current { get; set; }
        public string New { get; set; }
    }

    /// <summary>
    /// Hộp thoại mật khẩu dùng chung. Hộp thoại không tự xác thực: nó gọi submit (do bên gọi cung cấp),
    /// nếu submit trả về lỗi thì hiển thị và giữ hộp thoại mở để người dùng thử lại.
    /// </summary>
    public partial class PasswordDialog : Window
    {
        private readonly PasswordDialogMode _mode;
        private readonly Func<PasswordInput, Task<string>> _submit;

        public PasswordDialog(PasswordDialogMode mode, Func<PasswordInput, Task<string>> submit)
        {
            InitializeComponent();
            _mode = mode;
            _submit = submit;
            Configure();
            Loaded += delegate { Box1.Focus(); };
        }

        private void Configure()
        {
            Row2.Visibility = Visibility.Collapsed;
            Row3.Visibility = Visibility.Collapsed;

            switch (_mode)
            {
                case PasswordDialogMode.Unlock:
                    TitleText.Text = Strings.UnlockTitle;
                    InfoText.Text = Strings.UnlockInfo;
                    Label1.Text = "Mật khẩu";
                    break;

                case PasswordDialogMode.Create:
                    TitleText.Text = Strings.CreateTitle;
                    InfoText.Text = Strings.CreateInfo;
                    Label1.Text = "Mật khẩu mới";
                    Label2.Text = "Nhập lại mật khẩu";
                    Row2.Visibility = Visibility.Visible;
                    break;

                case PasswordDialogMode.Change:
                    TitleText.Text = Strings.ChangeTitle;
                    InfoText.Text = Strings.CreateInfo;
                    Label1.Text = "Mật khẩu hiện tại";
                    Label2.Text = "Mật khẩu mới";
                    Label3.Text = "Nhập lại mật khẩu mới";
                    Row2.Visibility = Visibility.Visible;
                    Row3.Visibility = Visibility.Visible;
                    break;

                case PasswordDialogMode.Remove:
                    TitleText.Text = Strings.RemoveTitle;
                    InfoText.Text = Strings.RemoveInfo;
                    Label1.Text = "Mật khẩu hiện tại";
                    break;
            }
        }

        private async void OkButton_Click(object sender, RoutedEventArgs e)
        {
            string error = Validate(out PasswordInput input);
            if (error != null)
            {
                ShowError(error);
                return;
            }

            SetBusy(true);
            try
            {
                string serverError = await _submit(input);
                if (serverError == null)
                {
                    DialogResult = true;
                    return;
                }
                ShowError(serverError);
            }
            catch (ReclaimException ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private string Validate(out PasswordInput input)
        {
            input = new PasswordInput();
            switch (_mode)
            {
                case PasswordDialogMode.Unlock:
                case PasswordDialogMode.Remove:
                    if (Box1.Password.Length == 0) return "Hãy nhập mật khẩu.";
                    input.Current = Box1.Password;
                    return null;

                case PasswordDialogMode.Create:
                {
                    string problem = PasswordPolicy.Validate(Box1.Password);
                    if (problem != null) return problem;
                    if (Box1.Password != Box2.Password) return "Hai lần nhập mật khẩu không khớp.";
                    input.New = Box1.Password;
                    return null;
                }
                default: // Change
                {
                    if (Box1.Password.Length == 0) return "Hãy nhập mật khẩu hiện tại.";
                    string problem = PasswordPolicy.Validate(Box2.Password);
                    if (problem != null) return problem;
                    if (Box2.Password != Box3.Password) return "Hai lần nhập mật khẩu mới không khớp.";
                    input.Current = Box1.Password;
                    input.New = Box2.Password;
                    return null;
                }
            }
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void SetBusy(bool busy)
        {
            OkButton.IsEnabled = !busy;
            CancelButton.IsEnabled = !busy;
            Box1.IsEnabled = Box2.IsEnabled = Box3.IsEnabled = !busy;
            if (busy) ErrorText.Visibility = Visibility.Collapsed;
        }
    }
}
