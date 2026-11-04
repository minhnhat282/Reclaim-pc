namespace Reclaim.Desktop
{
    /// <summary>Toàn bộ chuỗi hiển thị trong mã (chuỗi trong XAML nằm ở các file .xaml). Tách ra để dễ bản địa hóa sau này.</summary>
    internal static class Strings
    {
        public const string StatusChecking = "Đang kiểm tra…";
        public const string StatusProtected = "Đang bảo vệ";
        public const string StatusOff = "Đã tắt";
        public const string StatusStarting = "Đang khởi động…";
        public const string StatusError = "Có lỗi";
        public const string StatusInactive = "Không hoạt động";

        public const string SetPassword = "Đặt mật khẩu";
        public const string Unlock = "Mở khóa";
        public const string Lock = "Khóa lại";

        public const string NoPasswordInfo = "Chưa đặt mật khẩu - tự bảo vệ đang tắt.";
        public const string LockedInfo = "Đã khóa bằng mật khẩu.";
        public const string UnlockedInfo = "Đã mở khóa - tự khóa sau 10 phút không dùng.";

        public const string Applying = "Đang áp dụng…";
        public const string Applied = "Đã áp dụng.";
        public const string ProtectionUpdated = "Đã cập nhật tự bảo vệ.";
        public const string NeedPasswordFirst = "Hãy đặt mật khẩu trước khi bật tự bảo vệ.";
        public const string MaintenanceStarted = "Đã tạm gỡ bảo vệ trong 5 phút. Bảo vệ sẽ tự bật lại sau đó.";

        public const string ServiceNotInstalled = "Reclaim chưa được cài đặt trên máy này.";
        public const string ServiceNotRunning = "Dịch vụ Reclaim chưa chạy. Hãy khởi động lại máy hoặc cài đặt lại.";

        public const string ConfirmUninstall = "Gỡ cài đặt Reclaim? DNS của máy sẽ được trả về như ban đầu.";
        public const string Uninstalled = "Đã gỡ cài đặt Reclaim.";

        public const string CreateTitle = "Đặt mật khẩu";
        public const string CreateInfo =
            "Mật khẩu 8-64 ký tự (chữ, số, ký tự đặc biệt). KHÔNG có cách khôi phục nếu bạn quên - hãy ghi nhớ hoặc cất ở nơi an toàn.";
        public const string UnlockTitle = "Mở khóa";
        public const string UnlockInfo = "Nhập mật khẩu để tiếp tục.";
        public const string ChangeTitle = "Đổi mật khẩu";
        public const string RemoveTitle = "Xóa mật khẩu";
        public const string RemoveInfo = "Xóa mật khẩu sẽ tắt toàn bộ tự bảo vệ.";
    }
}
