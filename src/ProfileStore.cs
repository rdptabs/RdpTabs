using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;

namespace RdpTabs
{
    /// <summary>
    /// Reads and writes %APPDATA%\RdpTabs\profiles.json. Passwords are encrypted with the current user's
    /// DPAPI key before being written (they cannot be decrypted by another user or on another machine).
    /// </summary>
    internal sealed class ProfileStore
    {
        private const int CurrentVersion = 1;
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RdpTabs/v1/password");
        private const int MaxRecent = 8;

        public readonly List<ConnectionProfile> Profiles = new List<ConnectionProfile>();
        public readonly List<ConnectionProfile> Recent = new List<ConnectionProfile>();

        public Rectangle WindowBounds = Rectangle.Empty;
        public bool WindowMaximized;
        public ThemeMode Theme = ThemeMode.System;

        /// <summary>Why loading failed (corrupt config etc). Surfaced by the UI; never blocks startup.</summary>
        public string LoadError { get; private set; }

        public static string DirectoryPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpTabs");
            }
        }

        public static string FilePath
        {
            get { return Path.Combine(DirectoryPath, "profiles.json"); }
        }

        public static ProfileStore Load()
        {
            ProfileStore store = new ProfileStore();
            try
            {
                if (!File.Exists(FilePath)) return store;
                Json root = Json.Parse(File.ReadAllText(FilePath, Encoding.UTF8));

                foreach (Json item in root["profiles"].Items)
                {
                    ConnectionProfile profile = FromJson(item);
                    if (profile != null) store.Profiles.Add(profile);
                }
                foreach (Json item in root["recent"].Items)
                {
                    ConnectionProfile profile = FromJson(item);
                    if (profile != null) store.Recent.Add(profile);
                }

                int themeValue = root["theme"].AsInt(0);
                store.Theme = themeValue == 1 ? ThemeMode.Dark
                    : (themeValue == 2 ? ThemeMode.Light : ThemeMode.System);

                Json window = root["window"];
                if (window.IsObject)
                {
                    int w = window["width"].AsInt(0);
                    int h = window["height"].AsInt(0);
                    if (w > 200 && h > 150)
                    {
                        store.WindowBounds = new Rectangle(
                            window["x"].AsInt(0), window["y"].AsInt(0), w, h);
                    }
                    store.WindowMaximized = window["maximized"].AsBool(false);
                }
            }
            catch (Exception ex)
            {
                store.LoadError = ex.Message;
            }
            return store;
        }

        public void Save()
        {
            Json root = Json.NewObject();
            root.Set("version", CurrentVersion);

            Json profiles = Json.NewArray();
            foreach (ConnectionProfile profile in Profiles) profiles.Add(ToJson(profile));
            root["profiles"] = profiles;

            Json recent = Json.NewArray();
            foreach (ConnectionProfile profile in Recent) recent.Add(ToJson(profile));
            root["recent"] = recent;

            root.Set("theme", (int)Theme);

            Json window = Json.NewObject();
            window.Set("x", WindowBounds.X);
            window.Set("y", WindowBounds.Y);
            window.Set("width", WindowBounds.Width);
            window.Set("height", WindowBounds.Height);
            window.Set("maximized", WindowMaximized);
            root["window"] = window;

            Directory.CreateDirectory(DirectoryPath);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, root.ToJson(true), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
            else File.Move(temp, FilePath);
        }

        /// <summary>A failed save must not interrupt the user; the caller decides whether to report it.</summary>
        public bool TrySave(out string error)
        {
            error = null;
            try
            {
                Save();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public ConnectionProfile FindById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (ConnectionProfile profile in Profiles)
                if (profile.Id == id) return profile;
            return null;
        }

        public void AddOrUpdate(ConnectionProfile profile)
        {
            for (int i = 0; i < Profiles.Count; i++)
            {
                if (Profiles[i].Id == profile.Id)
                {
                    Profiles[i] = profile;
                    return;
                }
            }
            Profiles.Add(profile);
        }

        public void Remove(string id)
        {
            Profiles.RemoveAll(p => p.Id == id);
        }

        /// <summary>Records an actual connection attempt in the Recent list (deduplicated by user@host:port).</summary>
        public void TouchRecent(ConnectionProfile profile)
        {
            ConnectionProfile snapshot = profile.Clone();
            snapshot.LastUsedUtc = DateTime.UtcNow;
            if (!snapshot.SavePassword) snapshot.Password = "";

            Recent.RemoveAll(p =>
                string.Equals(p.Host, snapshot.Host, StringComparison.OrdinalIgnoreCase) &&
                p.Port == snapshot.Port &&
                string.Equals(p.FullUserName, snapshot.FullUserName, StringComparison.OrdinalIgnoreCase));

            Recent.Insert(0, snapshot);
            while (Recent.Count > MaxRecent) Recent.RemoveAt(Recent.Count - 1);

            ConnectionProfile saved = FindById(profile.Id);
            if (saved != null) saved.LastUsedUtc = snapshot.LastUsedUtc;
        }

        // ---------------- JSON mapping ----------------

        private static Json ToJson(ConnectionProfile p)
        {
            Json j = Json.NewObject();
            j.Set("id", p.Id);
            j.Set("name", p.Name);
            j.Set("host", p.Host);
            j.Set("port", p.Port);
            j.Set("username", p.UserName);
            j.Set("domain", p.Domain);
            j.Set("savePassword", p.SavePassword);
            if (p.SavePassword && !string.IsNullOrEmpty(p.Password))
                j.Set("password", ProtectPassword(p.Password));
            j.Set("display", (int)p.Display);
            j.Set("fixedWidth", p.FixedWidth);
            j.Set("fixedHeight", p.FixedHeight);
            j.Set("scalePercent", p.ScalePercent);
            j.Set("colorDepth", p.ColorDepth);
            j.Set("dynamicResolution", p.DynamicResolution);
            j.Set("redirectClipboard", p.RedirectClipboard);
            j.Set("redirectPrinters", p.RedirectPrinters);
            j.Set("redirectDrives", p.RedirectDrives);
            j.Set("redirectPorts", p.RedirectPorts);
            j.Set("redirectSmartCards", p.RedirectSmartCards);
            j.Set("audioMode", p.AudioMode);
            j.Set("audioCapture", p.AudioCapture);
            j.Set("keyboardHookMode", p.KeyboardHookMode);
            j.Set("autoReconnect", p.AutoReconnect);
            j.Set("bandwidthDetection", p.BandwidthDetection);
            j.Set("performanceProfile", p.PerformanceProfile);
            j.Set("disableAnimations", p.DisableAnimations);
            j.Set("authenticationLevel", p.AuthenticationLevel);
            j.Set("useGateway", p.UseGateway);
            j.Set("gatewayHost", p.GatewayHost);
            j.Set("gatewayBypassLocal", p.GatewayBypassLocal);
            if (p.LastUsedUtc != DateTime.MinValue)
                j.Set("lastUsed", p.LastUsedUtc.ToString("o", CultureInfo.InvariantCulture));
            return j;
        }

        private static ConnectionProfile FromJson(Json j)
        {
            if (!j.IsObject) return null;
            string host = j["host"].AsString("");
            if (string.IsNullOrEmpty(host)) return null;

            ConnectionProfile p = new ConnectionProfile();
            p.Id = j["id"].AsString(Guid.NewGuid().ToString("N"));
            p.Name = j["name"].AsString(host);
            p.Host = host;
            p.Port = Clamp(j["port"].AsInt(3389), 1, 65535);
            p.UserName = j["username"].AsString("");
            p.Domain = j["domain"].AsString("");
            p.SavePassword = j["savePassword"].AsBool(false);
            string stored = j["password"].AsString("");
            if (p.SavePassword && stored.Length > 0) p.Password = UnprotectPassword(stored);
            p.Display = j["display"].AsInt(0) == 1 ? DisplayMode.Fixed : DisplayMode.FitWindow;
            p.FixedWidth = Clamp(j["fixedWidth"].AsInt(1920), 640, 8192);
            p.FixedHeight = Clamp(j["fixedHeight"].AsInt(1080), 480, 8192);
            p.ScalePercent = j["scalePercent"].AsInt(0);
            p.ColorDepth = j["colorDepth"].AsInt(32);
            p.DynamicResolution = j["dynamicResolution"].AsBool(true);
            p.RedirectClipboard = j["redirectClipboard"].AsBool(true);
            p.RedirectPrinters = j["redirectPrinters"].AsBool(false);
            p.RedirectDrives = j["redirectDrives"].AsBool(false);
            p.RedirectPorts = j["redirectPorts"].AsBool(false);
            p.RedirectSmartCards = j["redirectSmartCards"].AsBool(false);
            p.AudioMode = Clamp(j["audioMode"].AsInt(0), 0, 2);
            p.AudioCapture = j["audioCapture"].AsBool(false);
            p.KeyboardHookMode = Clamp(j["keyboardHookMode"].AsInt(2), 0, 2);
            p.AutoReconnect = j["autoReconnect"].AsBool(true);
            p.BandwidthDetection = j["bandwidthDetection"].AsBool(true);
            p.PerformanceProfile = Clamp(j["performanceProfile"].AsInt(0), 0, 2);
            p.DisableAnimations = j["disableAnimations"].AsBool(true);
            p.AuthenticationLevel = Clamp(j["authenticationLevel"].AsInt(2), 0, 2);
            p.UseGateway = j["useGateway"].AsBool(false);
            p.GatewayHost = j["gatewayHost"].AsString("");
            p.GatewayBypassLocal = j["gatewayBypassLocal"].AsBool(true);

            DateTime lastUsed;
            if (DateTime.TryParse(j["lastUsed"].AsString(""), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out lastUsed))
                p.LastUsedUtc = lastUsed;
            return p;
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        // ---------------- password protection ----------------

        public static string ProtectPassword(string plain)
        {
            try
            {
                byte[] cipher = Native.Protect(Encoding.UTF8.GetBytes(plain ?? ""), Entropy);
                return Convert.ToBase64String(cipher);
            }
            catch (Exception)
            {
                return ""; // if it cannot be encrypted, store nothing -- never write plaintext
            }
        }

        public static string UnprotectPassword(string stored)
        {
            try
            {
                byte[] plain = Native.Unprotect(Convert.FromBase64String(stored), Entropy);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception)
            {
                return ""; // different user/machine, or the config was tampered with
            }
        }
    }
}
