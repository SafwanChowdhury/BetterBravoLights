using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace BravoLights.UI
{
    public enum AppTheme
    {
        System,
        Light,
        Dark
    }

    /// <summary>
    /// Switches between the light and dark palettes at runtime.
    /// </summary>
    /// <remarks>
    /// Styles and views refer to palette brushes with DynamicResource, so swapping the palette dictionary restyles
    /// everything. Value converters that look brushes up in code are re-run by refreshing each window's DataContext.
    /// </remarks>
    public static class ThemeManager
    {
        private const string LightPalette = "pack://application:,,,/BetterBravoLights;component/UI/Themes/Light.xaml";
        private const string DarkPalette = "pack://application:,,,/BetterBravoLights;component/UI/Themes/Dark.xaml";

        private static AppTheme requested = AppTheme.System;
        private static bool listeningForSystemChanges;

        public static bool IsDark { get; private set; } = true;

        public static event EventHandler ThemeChanged;

        public static void Apply(AppTheme theme)
        {
            requested = theme;

            if (!listeningForSystemChanges)
            {
                listeningForSystemChanges = true;
                SystemEvents.UserPreferenceChanged += (s, e) =>
                {
                    if (requested == AppTheme.System && e.Category == UserPreferenceCategory.General)
                    {
                        Application.Current?.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
                    }
                };
            }

            var dark = theme == AppTheme.Dark || (theme == AppTheme.System && SystemUsesDarkTheme());
            var changed = dark != IsDark;
            IsDark = dark;

            var app = Application.Current;
            if (app == null)
            {
                return;
            }

            var dictionaries = app.Resources.MergedDictionaries;
            var palette = new ResourceDictionary { Source = new Uri(dark ? DarkPalette : LightPalette) };
            var existing = dictionaries.FirstOrDefault(d => d.Source != null &&
                (d.Source.OriginalString.EndsWith("Dark.xaml") || d.Source.OriginalString.EndsWith("Light.xaml")));
            if (existing != null)
            {
                dictionaries[dictionaries.IndexOf(existing)] = palette;
            }
            else
            {
                dictionaries.Insert(0, palette);
            }

            foreach (Window window in app.Windows)
            {
                StyleTitleBar(window);
                if (changed)
                {
                    RefreshBindings(window);
                }
            }

            if (changed)
            {
                ThemeChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Re-evaluates every binding in a window, so converters pick up the new palette.
        /// </summary>
        private static void RefreshBindings(Window window)
        {
            var context = window.DataContext;
            if (context != null)
            {
                window.DataContext = null;
                window.DataContext = context;
            }
        }

        public static bool SystemUsesDarkTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch
            {
                return true;
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        /// <summary>
        /// Makes the Windows title bar match the app: dark or light, and (on Windows 11) the page background colour.
        /// </summary>
        public static void StyleTitleBar(Window window)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                {
                    return;
                }

                var dark = IsDark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

                if (Application.Current.TryFindResource("BgColor") is Color bg && Application.Current.TryFindResource("TextColor") is Color text)
                {
                    var caption = ToColorRef(bg);
                    var captionText = ToColorRef(text);
                    DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
                    DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref captionText, sizeof(int));
                }
            }
            catch
            {
                // Older versions of Windows don't support these attributes; never mind.
            }
        }

        private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
    }
}
