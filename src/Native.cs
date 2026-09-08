using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RdpTabs
{
    /// <summary>Win32 / DWM / DPAPI interop. Deliberately P/Invoke only, so net48 and net8 share one source tree with no NuGet packages.</summary>
    internal static class Native
    {
        // ---- window messages ----
        public const int WM_SETTINGCHANGE = 0x001A;
        public const int WM_NCCALCSIZE = 0x0083;
        public const int WM_NCHITTEST = 0x0084;
        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int WM_PAINT = 0x000F;
        public const int WM_DPICHANGED = 0x02E0;

        // ---- hit-test results ----
        public const int HTTRANSPARENT = -1;
        public const int HTNOWHERE = 0;
        public const int HTCLIENT = 1;
        public const int HTCAPTION = 2;

        // ---- system metrics ----
        public const int SM_CYSIZEFRAME = 33;
        public const int SM_CXPADDEDBORDER = 92;

        // ---- edit control cue banner ----
        public const int EM_SETCUEBANNER = 0x1501;

        // ---- DWM ----
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWCP_ROUND = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NCCALCSIZE_PARAMS
        {
            public RECT NewBounds;      // rgrc[0]
            public RECT OldBounds;      // rgrc[1]
            public RECT OldClientArea;  // rgrc[2]
            public IntPtr lppos;
        }

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        public static extern bool IsZoomed(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowEnabled(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hwnd);

        /// <summary>Retrieves the enabled popup (modal dialog) owned by the window.</summary>
        public const int GW_ENABLEDPOPUP = 6;

        /// <summary>
        /// When the window is blocked by a modal dialog (the RDP control pops the system credential prompt
        /// or a certificate warning), returns that dialog's handle, otherwise IntPtr.Zero.
        /// </summary>
        public static IntPtr FindBlockingDialog(IntPtr owner)
        {
            if (owner == IntPtr.Zero || IsWindowEnabled(owner)) return IntPtr.Zero;
            IntPtr popup = GetWindow(owner, GW_ENABLEDPOPUP);
            if (popup != IntPtr.Zero && popup != owner && IsWindowVisible(popup)) return popup;
            return IntPtr.Zero;
        }

        /// <summary>Brings the dialog to the front -- it can end up behind the main window, which looks like a hang.</summary>
        public static void BringDialogToFront(IntPtr dialog)
        {
            if (dialog == IntPtr.Zero) return;
            BringWindowToTop(dialog);
            SetForegroundWindow(dialog);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(int processId);

        public const int ATTACH_PARENT_PROCESS = -1;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public const int WM_NCPAINT = 0x0085;
        public const int RDW_INVALIDATE = 0x0001;
        public const int RDW_ERASE = 0x0004;
        public const int RDW_ALLCHILDREN = 0x0080;
        public const int RDW_FRAME = 0x0400;

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindowDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

        [DllImport("user32.dll")]
        public static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, int flags);

        /// <summary>Renders the whole window (including the non-client area) into a DC; the self test uses it for UI previews.</summary>
        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

        public const uint PW_RENDERFULLCONTENT = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Actual window border thickness (without the caption). Needed to inset the top when maximized, or content lands off-screen.</summary>
        public static int FrameThickness
        {
            get { return GetSystemMetrics(SM_CYSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER); }
        }

        public static void EnableRoundedCorners(IntPtr hwnd)
        {
            try
            {
                int pref = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch (DllNotFoundException)
            {
                // The attribute does not exist before Win10; ignore.
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        // ---- dark mode for system-drawn bits (scrollbars, message boxes) ----
        //
        // WinForms has no dark scrollbar: an AutoScroll panel gets the system's non-client scrollbars, which
        // are white in a dark UI. The fix is uxtheme's app mode plus "DarkMode_Explorer" on the scrolling
        // window. SetPreferredAppMode / FlushMenuThemes are undocumented exports (ordinals 135 / 136, Win10
        // 1809+), so every call is guarded and failure just leaves the light scrollbars in place.
        private const int AppModeDefault = 0;
        private const int AppModeForceDark = 2;
        private const int AppModeForceLight = 3;

        [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
        private static extern int SetPreferredAppMode(int mode);

        [DllImport("uxtheme.dll", EntryPoint = "#136")]
        private static extern void FlushMenuThemes();

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        /// <summary>Tells the system which app mode to draw its own controls in. Call before creating windows.</summary>
        public static void SetAppDarkMode(bool dark)
        {
            try
            {
                SetPreferredAppMode(dark ? AppModeForceDark : AppModeForceLight);
                FlushMenuThemes();
            }
            catch (EntryPointNotFoundException)
            {
                // Older Windows: no app mode to set.
            }
            catch (DllNotFoundException)
            {
            }
        }

        public static void ResetAppDarkMode()
        {
            try
            {
                SetPreferredAppMode(AppModeDefault);
                FlushMenuThemes();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Switches one window's scrollbars between the dark and the normal Explorer theme.</summary>
        public static void UseThemedScrollbars(IntPtr hwnd, bool dark)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                SetWindowTheme(hwnd, dark ? "DarkMode_Explorer" : "Explorer", null);
                RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
                    RDW_FRAME | RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Makes the system caption dark (dialogs still use the system frame).</summary>
        public static void EnableDarkTitleBar(IntPtr hwnd)
        {
            try
            {
                int enabled = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int)); // pre-2004 attribute number
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        public static void SetCueBanner(IntPtr editHwnd, string text)
        {
            SendMessage(editHwnd, EM_SETCUEBANNER, new IntPtr(1), text);
        }

        /// <summary>Extracts a signed coordinate from LPARAM (multi-monitor coordinates can be negative).</summary>
        public static int LoWord(IntPtr value)
        {
            return unchecked((short)(long)value);
        }

        public static int HiWord(IntPtr value)
        {
            return unchecked((short)((long)value >> 16));
        }

        // ---------------- DPAPI ----------------

        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DATA_BLOB dataIn, string description,
            ref DATA_BLOB entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr description,
            ref DATA_BLOB entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);

        /// <summary>Encrypts with the current user's DPAPI key (same as ProtectedData.Protect / CurrentUser).</summary>
        public static byte[] Protect(byte[] data, byte[] entropy)
        {
            DATA_BLOB inBlob = new DATA_BLOB(), entBlob = new DATA_BLOB(), outBlob = new DATA_BLOB();
            try
            {
                inBlob = Alloc(data);
                entBlob = Alloc(entropy);
                if (!CryptProtectData(ref inBlob, null, ref entBlob, IntPtr.Zero, IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CryptProtectData failed");
                return Read(outBlob);
            }
            finally
            {
                Release(inBlob);
                Release(entBlob);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        public static byte[] Unprotect(byte[] data, byte[] entropy)
        {
            DATA_BLOB inBlob = new DATA_BLOB(), entBlob = new DATA_BLOB(), outBlob = new DATA_BLOB();
            try
            {
                inBlob = Alloc(data);
                entBlob = Alloc(entropy);
                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CryptUnprotectData failed");
                return Read(outBlob);
            }
            finally
            {
                Release(inBlob);
                Release(entBlob);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        private static DATA_BLOB Alloc(byte[] data)
        {
            DATA_BLOB blob = new DATA_BLOB();
            if (data == null) data = new byte[0];
            blob.cbData = data.Length;
            blob.pbData = Marshal.AllocHGlobal(Math.Max(1, data.Length));
            if (data.Length > 0) Marshal.Copy(data, 0, blob.pbData, data.Length);
            return blob;
        }

        private static byte[] Read(DATA_BLOB blob)
        {
            byte[] result = new byte[blob.cbData];
            if (blob.cbData > 0) Marshal.Copy(blob.pbData, result, 0, blob.cbData);
            return result;
        }

        private static void Release(DATA_BLOB blob)
        {
            if (blob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(blob.pbData);
        }
    }
}
