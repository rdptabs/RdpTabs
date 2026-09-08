using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace RdpTabs
{
    /// <summary>
    /// The app icon is the one from Windows' own Remote Desktop client (mstsc.exe).
    /// RdpTabs.ico is a byte-for-byte copy of mstsc.exe's icon resource; the build embeds it both as the
    /// win32 icon (Explorer / taskbar) and as a managed resource (so we can pick a size at run time).
    /// </summary>
    internal static class AppIcon
    {
        private const string ResourceName = "RdpTabs.ico";

        private static Icon _icon;
        private static bool _loaded;

        public static Icon Value
        {
            get
            {
                if (_loaded) return _icon;
                _loaded = true;
                _icon = Load();
                return _icon;
            }
        }

        /// <summary>Sets the form icon; falls back to the WinForms default without throwing.</summary>
        public static void Apply(Form form)
        {
            Icon icon = Value;
            if (icon != null) form.Icon = icon;
        }

        private static Icon Load()
        {
            // 1) the embedded managed resource (all sizes, best quality)
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                {
                    if (stream != null) return new Icon(stream);
                }
            }
            catch (Exception)
            {
            }

            // 2) our own exe's win32 icon
            try
            {
                return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception)
            {
            }

            // 3) straight from the system's mstsc.exe
            try
            {
                string mstsc = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "mstsc.exe");
                if (File.Exists(mstsc)) return Icon.ExtractAssociatedIcon(mstsc);
            }
            catch (Exception)
            {
            }

            return null;
        }
    }
}
