using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Velopack;

namespace KitBrowser
{
    public partial class App : Application
    {
        [STAThread]
        public static void Main(string[] args)
        {
            VelopackApp.Build().Run();

            bool hasProfileArg = args.Any(a => a.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase));
            var launchUrl = Core.Services.DefaultBrowserService.ExtractLaunchUrlFromArgs(args);
            if (!hasProfileArg && !string.IsNullOrWhiteSpace(launchUrl) &&
                Core.Services.DefaultBrowserService.TrySendUrlToRunningInstance(launchUrl))
            {
                return;
            }

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }

        public static void ApplyTheme(string themeName, string? accentHex = null)
        {
            var isLight = string.Equals(themeName, "Light", StringComparison.OrdinalIgnoreCase);
            var uri = new Uri(isLight ? "UI/Themes/LightTheme.xaml" : "UI/Themes/DarkTheme.xaml", UriKind.Relative);

            var merged = Current.Resources.MergedDictionaries;
            if (merged.Count > 0)
                merged[0] = new ResourceDictionary { Source = uri };

            if (!string.IsNullOrWhiteSpace(accentHex))
                Core.Services.WindowsThemeHelper.ApplyAccentColor(accentHex);
        }
    }
}

namespace KitBrowser.Core.Services
{
    public static class WindowsThemeHelper
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWCP_ROUND = 2;
        private const int DWMSBT_MAINWINDOW = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static void ApplyWindowChromeEffects(Window window, bool isDark, bool enableMica)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int darkValue = isDark ? 1 : 0;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkValue, sizeof(int));

                int cornerPref = DWMWCP_ROUND;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

                int backdrop = enableMica ? DWMSBT_MAINWINDOW : 0;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            }
            catch { }
        }

        public static void ApplyAccentColor(string hexColor)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hexColor);
                var soft = Color.FromArgb(0x28, color.R, color.G, color.B);
                Application.Current.Resources["AccentBrush"] = new SolidColorBrush(color);
                Application.Current.Resources["BorderFocus"] = new SolidColorBrush(color);
                Application.Current.Resources["AccentSoft"] = new SolidColorBrush(soft);
            }
            catch { }
        }
    }
}
