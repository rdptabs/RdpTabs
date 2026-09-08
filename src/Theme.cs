using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RdpTabs
{
    internal enum ThemeMode
    {
        System = 0,
        Dark = 1,
        Light = 2
    }

    /// <summary>
    /// Global DPI scaling. The process is system-DPI aware, so there is exactly one DPI for its lifetime;
    /// it is read once at startup, which means it also works inside constructors (before the window handle
    /// exists, while Control.DeviceDpi still reports 96).
    /// Every form sets AutoScaleMode.None so all scaling goes through here -- otherwise WinForms' font
    /// auto-scaling stacks on top of the hand-written layout and sizes become unpredictable.
    /// </summary>
    internal static class Dpi
    {
        public static readonly int Value = ReadDpi();

        private static int ReadDpi()
        {
            try
            {
                using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero))
                    return (int)Math.Round(graphics.DpiX);
            }
            catch (Exception)
            {
                return 96;
            }
        }

        public static int Scale(int value)
        {
            return (int)Math.Round(value * Value / 96.0);
        }

        public static float ScaleF(float value)
        {
            return value * Value / 96f;
        }
    }

    /// <summary>
    /// UI fonts. Sizes are in points so they scale with DPI automatically.
    /// Prefer the OS UI font; if a profile name contains characters the UI font lacks (CJK on an English
    /// Windows, for example), GDI font-linking renders them much wider than expected -- which is exactly why
    /// every layout in this app measures its text with TextRenderer.MeasureText instead of hard-coding widths.
    /// </summary>
    internal static class Fonts
    {
        private static readonly FontFamily Family = ResolveFamily();

        private static FontFamily ResolveFamily()
        {
            try
            {
                return SystemFonts.MessageBoxFont.FontFamily;   // Segoe UI on a normal Windows install
            }
            catch (Exception)
            {
            }
            try
            {
                return new FontFamily("Segoe UI");
            }
            catch (ArgumentException)
            {
                return FontFamily.GenericSansSerif;
            }
        }

        public static readonly Font Small = new Font(Family, 8.25f);
        public static readonly Font Body = new Font(Family, 9f);
        public static readonly Font BodyBold = new Font(Family, 9f, FontStyle.Bold);
        public static readonly Font Tab = new Font(Family, 9f);
        public static readonly Font Title = new Font(Family, 13.5f);
        public static readonly Font Heading = new Font(Family, 19f);
    }

    /// <summary>The whole app's palette. Follows the system app mode (light/dark) unless overridden.</summary>
    internal static class Theme
    {
        public static bool IsDark { get; private set; }

        /// <summary>The user's choice: follow the system, force dark, or force light.</summary>
        public static ThemeMode Mode { get; private set; }

        // tab strip / window frame
        public static Color Frame;
        public static Color ActiveTab;
        public static Color TabHover;
        public static Color TabSeparator;
        public static Color CloseHover;
        public static Color WindowButtonHover;
        public static Color WindowCloseHover = Color.FromArgb(0xE8, 0x11, 0x23);

        // text
        public static Color Text;
        public static Color TextDim;
        public static Color TextOnAccent = Color.White;

        // status dots
        public static Color StatusIdle;
        public static Color StatusConnecting = Color.FromArgb(0xE8, 0xB3, 0x3A);
        public static Color StatusConnected = Color.FromArgb(0x3F, 0xBF, 0x6F);
        public static Color StatusError = Color.FromArgb(0xE5, 0x48, 0x4D);

        // pages / cards / inputs
        public static Color PageBackground;
        public static Color Panel;
        public static Color Card;
        public static Color CardHover;
        public static Color CardBorder;
        public static Color InputBackground;
        public static Color InputBorder;
        public static Color Accent;
        public static Color AccentHover;
        public static Color Overlay;

        static Theme()
        {
            Mode = ThemeMode.System;
            Apply(ReadSystemPrefersDark());
        }

        /// <summary>Switches theme; returns true when the palette actually changed.</summary>
        public static bool SetMode(ThemeMode mode)
        {
            Mode = mode;
            bool dark = ResolveDark(mode);
            if (dark == IsDark) return false;
            Apply(dark);
            return true;
        }

        /// <summary>Called when the system theme changes (only the "follow system" mode reacts).</summary>
        public static bool Refresh()
        {
            bool dark = ResolveDark(Mode);
            if (dark == IsDark) return false;
            Apply(dark);
            return true;
        }

        private static bool ResolveDark(ThemeMode mode)
        {
            if (mode == ThemeMode.Dark) return true;
            if (mode == ThemeMode.Light) return false;
            return ReadSystemPrefersDark();
        }

        private static bool ReadSystemPrefersDark()
        {
            try
            {
                object value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                return value != null && Convert.ToInt32(value) == 0;
            }
            catch
            {
                return false;
            }
        }

        private static void Apply(bool dark)
        {
            IsDark = dark;
            if (dark)
            {
                Frame = Rgb(0x17, 0x18, 0x1A);
                ActiveTab = Rgb(0x2A, 0x2C, 0x30);
                TabHover = Rgb(0x23, 0x25, 0x28);
                TabSeparator = Rgb(0x3C, 0x3F, 0x45);
                CloseHover = Rgb(0x3E, 0x41, 0x47);
                WindowButtonHover = Rgb(0x30, 0x33, 0x38);
                Text = Rgb(0xE6, 0xE8, 0xEA);
                TextDim = Rgb(0x9A, 0xA0, 0xA8);
                StatusIdle = Rgb(0x7A, 0x80, 0x85);
                PageBackground = Rgb(0x1B, 0x1C, 0x1F);
                Panel = Rgb(0x22, 0x24, 0x27);
                Card = Rgb(0x25, 0x27, 0x2B);
                CardHover = Rgb(0x2E, 0x31, 0x36);
                CardBorder = Rgb(0x36, 0x39, 0x3E);
                InputBackground = Rgb(0x2A, 0x2C, 0x30);
                InputBorder = Rgb(0x44, 0x47, 0x4D);
                Accent = Rgb(0x3D, 0x7E, 0xE8);
                AccentHover = Rgb(0x52, 0x8C, 0xEC);
                Overlay = Rgb(0x1B, 0x1C, 0x1F);
            }
            else
            {
                Frame = Rgb(0xDE, 0xE1, 0xE6);
                ActiveTab = Rgb(0xFF, 0xFF, 0xFF);
                TabHover = Rgb(0xEB, 0xED, 0xF0);
                TabSeparator = Rgb(0xBD, 0xC1, 0xC6);
                CloseHover = Rgb(0xD3, 0xD7, 0xDC);
                WindowButtonHover = Rgb(0xCB, 0xCF, 0xD5);
                Text = Rgb(0x1F, 0x21, 0x24);
                TextDim = Rgb(0x5F, 0x63, 0x68);
                StatusIdle = Rgb(0x9A, 0xA0, 0xA6);
                PageBackground = Rgb(0xF6, 0xF7, 0xF9);
                Panel = Rgb(0xFF, 0xFF, 0xFF);
                Card = Rgb(0xFF, 0xFF, 0xFF);
                CardHover = Rgb(0xF0, 0xF2, 0xF5);
                CardBorder = Rgb(0xDA, 0xDC, 0xE0);
                InputBackground = Rgb(0xFF, 0xFF, 0xFF);
                InputBorder = Rgb(0xC4, 0xC7, 0xCC);
                Accent = Rgb(0x1A, 0x73, 0xE8);
                AccentHover = Rgb(0x35, 0x85, 0xEC);
                Overlay = Rgb(0xF6, 0xF7, 0xF9);
            }
        }

        private static Color Rgb(int r, int g, int b)
        {
            return Color.FromArgb(r, g, b);
        }

        public static Color StatusColor(SessionState state)
        {
            switch (state)
            {
                case SessionState.Connected: return StatusConnected;
                case SessionState.Checking:
                case SessionState.Connecting: return StatusConnecting;
                case SessionState.Failed: return StatusError;
                case SessionState.Disconnected: return StatusError;
                default: return StatusIdle;
            }
        }
    }
}
