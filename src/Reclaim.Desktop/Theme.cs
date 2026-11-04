using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Reclaim.Desktop
{
    /// <summary>Chọn bảng màu sáng/tối theo cài đặt của Windows (đọc một lần khi khởi động).</summary>
    internal static class Theme
    {
        public static void Apply()
        {
            bool light = true;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    if (value is int) light = (int)value != 0;
                }
            }
            catch
            {
                // dùng giao diện sáng
            }

            if (light) return; // App.xaml đã là bảng màu sáng

            Set("Bg", "#101318");
            Set("Card", "#1A1E25");
            Set("Border", "#2A303A");
            Set("Text", "#E8ECF2");
            Set("SubText", "#9AA5B4");
            Set("Accent", "#5B8CFF");
            Set("AccentText", "#FFFFFF");
            Set("InputBg", "#12151A");
            Set("Ok", "#3CC585");
            Set("Warn", "#F0B34A");
            Set("Danger", "#FF6B6B");
            Set("Disabled", "#3A4250");
        }

        private static void Set(string key, string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            Application.Current.Resources[key] = brush;
        }
    }
}
