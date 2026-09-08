using System;
using System.Globalization;

namespace RdpTabs
{
    internal enum SessionState
    {
        Idle,
        Checking,     // reachability pre-flight before connecting
        Connecting,
        Connected,
        Disconnected, // was connected, then dropped
        Failed        // never got connected
    }

    internal enum DisplayMode
    {
        FitWindow = 0,
        Fixed = 1
    }

    /// <summary>One connection profile. Used both for saved connections and for ad-hoc (quick connect) sessions.</summary>
    internal sealed class ConnectionProfile
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Name = "";
        public string Host = "";
        public int Port = 3389;
        public string UserName = "";
        public string Domain = "";

        /// <summary>Plaintext password, in memory only. ProfileStore encrypts it with DPAPI before writing.</summary>
        public string Password = "";
        public bool SavePassword;

        public DisplayMode Display = DisplayMode.FitWindow;
        public int FixedWidth = 1920;
        public int FixedHeight = 1080;

        /// <summary>0 = follow the local DPI automatically, otherwise 100-500.</summary>
        public int ScalePercent;
        public int ColorDepth = 32;

        /// <summary>Make the remote resolution follow the window size (RDP 8.1+; falls back to SmartSizing).</summary>
        public bool DynamicResolution = true;

        public bool RedirectClipboard = true;
        public bool RedirectPrinters;
        public bool RedirectDrives;
        public bool RedirectPorts;
        public bool RedirectSmartCards;

        /// <summary>0 = play on this computer, 1 = play on the remote computer, 2 = do not play.</summary>
        public int AudioMode;
        public bool AudioCapture;

        /// <summary>0 = this computer, 1 = the remote session, 2 = only in full screen (mstsc's default).</summary>
        public int KeyboardHookMode = 2;

        public bool AutoReconnect = true;
        public bool BandwidthDetection = true;

        /// <summary>0 = automatic (LAN), 1 = balanced, 2 = low bandwidth.</summary>
        public int PerformanceProfile;

        /// <summary>
        /// Turn off animation-style effects in the remote session (menu fades, full-window drag, cursor shadow).
        /// On by default: they do not help responsiveness but every frame has to travel over the wire, which is a
        /// common source of RDP stutter.
        /// Note RDP can only control these few; Windows' own minimize/maximize animations must be turned off on
        /// the remote machine itself.
        /// </summary>
        public bool DisableAnimations = true;

        /// <summary>0 = do not authenticate the server, 1 = require authentication, 2 = try and only warn on failure.</summary>
        public int AuthenticationLevel = 2;

        public bool UseGateway;
        public string GatewayHost = "";
        public bool GatewayBypassLocal = true;

        public DateTime LastUsedUtc;

        public string Endpoint
        {
            get { return Port == 3389 ? Host : Host + ":" + Port.ToString(CultureInfo.InvariantCulture); }
        }

        public string DisplayName
        {
            get { return string.IsNullOrEmpty(Name) ? Endpoint : Name; }
        }

        public string DetailLine
        {
            get
            {
                string account = FullUserName;
                return string.IsNullOrEmpty(account) ? Endpoint : account + " @ " + Endpoint;
            }
        }

        public string FullUserName
        {
            get
            {
                if (string.IsNullOrEmpty(UserName)) return "";
                return string.IsNullOrEmpty(Domain) ? UserName : Domain + "\\" + UserName;
            }
        }

        public ConnectionProfile Clone()
        {
            return (ConnectionProfile)MemberwiseClone();
        }

        /// <summary>Clones into a fresh ad-hoc profile (new Id, so it is not confused with a saved connection).</summary>
        public ConnectionProfile CloneForNewSession()
        {
            ConnectionProfile copy = Clone();
            copy.Id = Guid.NewGuid().ToString("N");
            return copy;
        }

        /// <summary>
        /// Parses quick-connect input: host, host:port, user@host, DOMAIN\user@host:port, [::1]:3389.
        /// Returns null plus a reason when it cannot be parsed.
        /// </summary>
        public static ConnectionProfile FromQuickConnect(string text, out string error)
        {
            error = null;
            string input = (text ?? string.Empty).Trim();
            if (input.Length == 0)
            {
                error = "Enter a computer name or IP address.";
                return null;
            }

            ConnectionProfile profile = new ConnectionProfile();

            // account part (everything before the last @)
            int at = input.LastIndexOf('@');
            if (at > 0)
            {
                string account = input.Substring(0, at);
                input = input.Substring(at + 1).Trim();
                int slash = account.IndexOfAny(new[] { '\\', '/' });
                if (slash > 0)
                {
                    profile.Domain = account.Substring(0, slash).Trim();
                    profile.UserName = account.Substring(slash + 1).Trim();
                }
                else
                {
                    profile.UserName = account.Trim();
                }
            }

            // host and port
            string host = input;
            string portText = null;
            if (host.StartsWith("[", StringComparison.Ordinal))
            {
                int end = host.IndexOf(']');
                if (end < 0)
                {
                    error = "The IPv6 address is missing its closing bracket.";
                    return null;
                }
                string rest = host.Substring(end + 1);
                host = host.Substring(1, end - 1);
                if (rest.StartsWith(":", StringComparison.Ordinal)) portText = rest.Substring(1);
            }
            else
            {
                int colon = host.LastIndexOf(':');
                if (colon > 0 && host.IndexOf(':') == colon) // only treat it as a port when there is exactly one colon (bare IPv6)
                {
                    portText = host.Substring(colon + 1);
                    host = host.Substring(0, colon);
                }
            }

            host = host.Trim();
            if (host.Length == 0)
            {
                error = "Enter a computer name or IP address.";
                return null;
            }

            if (!string.IsNullOrEmpty(portText))
            {
                int port;
                if (!int.TryParse(portText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) ||
                    port < 1 || port > 65535)
                {
                    error = "Invalid port: " + portText;
                    return null;
                }
                profile.Port = port;
            }

            profile.Host = host;
            profile.Name = host;
            return profile;
        }

        /// <summary>PerformanceFlags: turns visual effects off according to the experience tier.</summary>
        public int PerformanceFlags
        {
            get
            {
                const int DisableWallpaper = 0x1;
                const int DisableFullWindowDrag = 0x2;
                const int DisableMenuAnimations = 0x4;
                const int DisableTheming = 0x8;
                const int DisableCursorShadow = 0x20;
                const int DisableCursorSettings = 0x40;
                const int EnableFontSmoothing = 0x80;
                const int EnableDesktopComposition = 0x100;

                int flags;
                switch (PerformanceProfile)
                {
                    case 2: // low bandwidth
                        flags = DisableWallpaper | DisableFullWindowDrag | DisableMenuAnimations |
                                DisableTheming | DisableCursorShadow | DisableCursorSettings;
                        break;
                    case 1: // balanced
                        flags = DisableWallpaper | DisableFullWindowDrag | DisableMenuAnimations |
                                EnableFontSmoothing;
                        break;
                    default: // automatic / LAN: full quality
                        flags = EnableFontSmoothing | EnableDesktopComposition;
                        break;
                }

                // "disable remote animations" is an independent switch that applies to every tier
                if (DisableAnimations)
                    flags |= DisableMenuAnimations | DisableFullWindowDrag | DisableCursorShadow;

                return flags;
            }
        }

        /// <summary>NetworkConnectionType: 1 = modem ... 6 = LAN, 7 = auto detect.</summary>
        public int NetworkConnectionType
        {
            get
            {
                switch (PerformanceProfile)
                {
                    case 2: return 2;   // low-speed broadband
                    case 1: return 4;   // high-speed broadband
                    default: return 7;  // auto detect: let the server measure the link instead of claiming LAN
                }
            }
        }
    }
}
