using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace RdpTabs
{
    /// <summary>
    /// Verifies the critical plumbing without needing an RDP server: can the ActiveX host be created, can late
    /// binding read and write properties, does the dynamic-resolution method exist, do DPAPI and JSON round-trip.
    /// With a host[:port] argument it also makes a real connection and logs the state changes.
    /// </summary>
    internal static class SelfTest
    {
        private static readonly StringBuilder Log = new StringBuilder();
        private static int _failures;
        private static Form _probeHost;

        public static int Run(string target)
        {
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch (Exception)
            {
            }

            Line("RdpTabs self test");
            Line("================================================");
            Line("Time:    " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            Line("Runtime: .NET " + Environment.Version + " (64-bit process: " + Environment.Is64BitProcess + ")");
            Line("Theme:   " + (Theme.IsDark ? "dark" : "light"));
            Line("");

            CheckMstscax();
            object ocx = CheckActiveXHost();
            if (ocx != null) CheckLateBinding(ocx);
            CheckDpapi();
            CheckJson();
            CheckPreflight();
            CheckAppIcon();
            CheckTabStrip();
            CheckDialogLayout();

            Line("");
            Line(_failures == 0 ? "Result: everything passed." : "Result: " + _failures + " check(s) failed.");

            Line("Log: " + LogPath);
            SaveLog();

            if (!string.IsNullOrEmpty(target)) RunConnectTest(target);

            if (_probeHost != null)
            {
                _probeHost.Close();
                _probeHost.Dispose();
                _probeHost = null;
            }
            SaveLog();
            return _failures == 0 ? 0 : 1;
        }

        private static string LogPath
        {
            get { return Path.Combine(Path.GetTempPath(), "RdpTabs-selftest.log"); }
        }

        private static void SaveLog()
        {
            try
            {
                File.WriteAllText(LogPath, Log.ToString(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // If it cannot be written, the console output is still there
            }
        }

        // ---------------- checks ----------------

        private static void CheckMstscax()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "mstscax.dll");
            if (!File.Exists(path))
            {
                Fail("mstscax.dll", "not found at " + path);
                return;
            }
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            Pass("mstscax.dll", path + ", version " + info.FileVersion);
        }

        private static object CheckActiveXHost()
        {
            string progId;
            try
            {
                progId = RdpAxHost.ResolvedProgId;
                Pass("ActiveX lookup", progId + " -> " + RdpAxHost.ResolvedClsid);
            }
            catch (Exception ex)
            {
                Fail("ActiveX lookup", Com.Unwrap(ex).Message);
                return null;
            }

            try
            {
                Form host = new Form();
                _probeHost = host;
                host.AutoScaleMode = AutoScaleMode.None;
                host.FormBorderStyle = FormBorderStyle.None;
                host.ShowInTaskbar = false;
                host.StartPosition = FormStartPosition.Manual;
                host.Location = new Point(-4000, -4000);   // off-screen, so it does not bother the user
                host.Size = new Size(1024, 768);
                host.Show();

                RdpAxHost ax = new RdpAxHost();
                ax.Bounds = new Rectangle(0, 0, 1024, 768);
                host.Controls.Add(ax);
                ax.EnsureCreated();
                Application.DoEvents();

                object ocx = ax.Ocx;
                if (ocx == null)
                {
                    Fail("control creation", "GetOcx() returned null");
                    return null;
                }
                Pass("control creation", "COM object type " + ocx.GetType().Name);
                return ocx;
            }
            catch (Exception ex)
            {
                Fail("control creation", Com.Unwrap(ex).ToString());
                return null;
            }
        }

        private static void CheckLateBinding(object ocx)
        {
            // basic property read/write
            try
            {
                Com.Set(ocx, "Server", "selftest.invalid");
                Com.Set(ocx, "DesktopWidth", 1280);
                Com.Set(ocx, "DesktopHeight", 800);
                string server = Com.GetString(ocx, "Server", null);
                int width = Com.GetInt(ocx, "DesktopWidth", -1);
                if (server == "selftest.invalid" && width == 1280)
                    Pass("property access", "Server / DesktopWidth round-trip fine");
                else
                    Fail("property access", "wrong values read back: Server=" + server + ", DesktopWidth=" + width);
            }
            catch (Exception ex)
            {
                Fail("property access", Com.Unwrap(ex).Message);
            }

            // AdvancedSettings version fallback
            string advancedName;
            object advanced = Com.FirstAvailable(ocx, out advancedName,
                "AdvancedSettings9", "AdvancedSettings8", "AdvancedSettings7", "AdvancedSettings6",
                "AdvancedSettings5", "AdvancedSettings4", "AdvancedSettings3", "AdvancedSettings2",
                "AdvancedSettings");
            if (advanced == null)
            {
                Fail("AdvancedSettings", "none of them are available");
            }
            else
            {
                Pass("AdvancedSettings", "using " + advancedName);

                if (Com.TrySet(advanced, "RDPPort", 3390) && Com.GetInt(advanced, "RDPPort", -1) == 3390)
                    Pass("port setting", "RDPPort is readable and writable");
                else
                    Fail("port setting", "writing RDPPort had no effect");

                // Only the NotSafeForScripting class accepts a plaintext password -- this decides whether we can sign in without prompting
                if (Com.TrySet(advanced, "ClearTextPassword", "selftest-dummy"))
                    Pass("password passthrough", "ClearTextPassword is writable (NotSafeForScripting class)");
                else
                    Fail("password passthrough", "writing ClearTextPassword was rejected");

                if (Com.TrySet(advanced, "GrabFocusOnConnect", false))
                    Pass("no focus stealing", "GrabFocusOnConnect = false");
                else
                    Fail("no focus stealing", "writing GrabFocusOnConnect failed");

                if (Com.TrySet(advanced, "SmartSizing", true))
                    Pass("scaling fallback", "SmartSizing is writable");
                else
                    Fail("scaling fallback", "writing SmartSizing failed");

                CheckPerformanceFlags(advanced);

                // Do the connection timeouts actually apply? They are the backstop when a connect hangs
                bool overallOk = Com.TrySet(advanced, "overallConnectionTimeout", 20);
                int overallBack = Com.GetInt(advanced, "overallConnectionTimeout", -1);
                bool singleOk = Com.TrySet(advanced, "singleConnectionTimeout", 15);
                int singleBack = Com.GetInt(advanced, "singleConnectionTimeout", -1);
                if (overallOk && overallBack == 20 && singleOk && singleBack == 15)
                    Pass("connect timeouts", "overallConnectionTimeout=20 / singleConnectionTimeout=15 both applied");
                else
                    Fail("connect timeouts", "write/read mismatch: overall write=" + overallOk + " read=" + overallBack +
                        ", single write=" + singleOk + " read=" + singleBack);
            }

            string securedName;
            object secured = Com.FirstAvailable(ocx, out securedName,
                "SecuredSettings3", "SecuredSettings2", "SecuredSettings");
            if (secured != null && Com.TrySet(secured, "KeyboardHookMode", 2))
                Pass("keyboard hook mode", securedName + ".KeyboardHookMode is writable");
            else
                Fail("keyboard hook mode", "SecuredSettings is not available");

            // Dynamic resolution: the call must fail while disconnected, but "method missing" and "call failed" differ
            try
            {
                Com.Call(ocx, "UpdateSessionDisplaySettings",
                    (uint)1280, (uint)800, (uint)0, (uint)0, (uint)0, (uint)100, (uint)100);
                Pass("dynamic resolution", "UpdateSessionDisplaySettings succeeded (unusual while disconnected, but fine)");
            }
            catch (MissingMemberException)
            {
                Fail("dynamic resolution", "the control has no UpdateSessionDisplaySettings (needs an RDP 8.1+ client)");
            }
            catch (Exception ex)
            {
                Exception inner = Com.Unwrap(ex);
                if (inner is MissingMemberException)
                    Fail("dynamic resolution", "the control has no UpdateSessionDisplaySettings");
                else
                    Pass("dynamic resolution", "the method exists (failing while disconnected is expected: " + inner.GetType().Name + ")");
            }

            // disconnect-reason text interface
            object description;
            if (Com.TryCall(ocx, "GetErrorDescription", out description, (uint)0, (uint)0))
                Pass("error description", "GetErrorDescription is callable");
            else
                Line("  - note: GetErrorDescription is unavailable, so disconnects will only show the extended reason code.");
        }

        /// <summary>
        /// Does "disable remote animations" turn into the right PerformanceFlags bits, and does the control
        /// accept them? Bits: 0x2 full-window drag, 0x4 menu animations, 0x20 cursor shadow, 0x1 wallpaper,
        /// 0x8 theming, 0x80 font smoothing.
        /// </summary>
        private static void CheckPerformanceFlags(object advanced)
        {
            const int DisableFullWindowDrag = 0x2;
            const int DisableMenuAnimations = 0x4;
            const int DisableCursorShadow = 0x20;
            const int AnimationBits = DisableFullWindowDrag | DisableMenuAnimations | DisableCursorShadow;

            ConnectionProfile profile = new ConnectionProfile();   // defaults: automatic tier, animations off
            int withAnimationsOff = profile.PerformanceFlags;
            profile.DisableAnimations = false;
            int withAnimationsOn = profile.PerformanceFlags;

            bool bitsOk = (withAnimationsOff & AnimationBits) == AnimationBits &&
                          (withAnimationsOn & AnimationBits) == 0;

            bool accepted = Com.TrySet(advanced, "PerformanceFlags", withAnimationsOff);
            int readBack = Com.GetInt(advanced, "PerformanceFlags", -1);

            string detail = "default tier flags=0x" + withAnimationsOff.ToString("X") +
                            " (animations off) / 0x" + withAnimationsOn.ToString("X") + " (animations on)" +
                            ", control read back 0x" + readBack.ToString("X");
            if (bitsOk && accepted && readBack == withAnimationsOff) Pass("remote animation switch", detail);
            else Fail("remote animation switch", detail + ", bits correct=" + bitsOk + ", write ok=" + accepted);
        }

        private static void CheckDpapi()
        {
            const string secret = "p@ssw0rd-test-\u00e4\u00f6-\u4f60\u597d";
            string stored = ProfileStore.ProtectPassword(secret);
            if (string.IsNullOrEmpty(stored))
            {
                Fail("DPAPI", "CryptProtectData failed");
                return;
            }
            string round = ProfileStore.UnprotectPassword(stored);
            if (round == secret) Pass("DPAPI", "round-trip matches, ciphertext " + stored.Length + " Base64 chars");
            else Fail("DPAPI", "decrypted value differs: " + round);
        }

        private static void CheckJson()
        {
            try
            {
                Json root = Json.NewObject();
                root.Set("host", "ex\u00e4mple.local");
                root.Set("port", 3390);
                root.Set("save", true);
                Json list = Json.NewArray();
                list.Add(Json.From("a\"b\\c\nd"));
                root["list"] = list;

                Json parsed = Json.Parse(root.ToJson(true));
                bool ok = parsed["host"].AsString("") == "ex\u00e4mple.local" &&
                          parsed["port"].AsInt(0) == 3390 &&
                          parsed["save"].AsBool(false) &&
                          parsed["list"][0].AsString("") == "a\"b\\c\nd";
                if (ok) Pass("JSON round-trip", "objects / arrays / escapes / non-ASCII all fine");
                else Fail("JSON round-trip", "wrong values read back: " + parsed.ToJson(false));
            }
            catch (Exception ex)
            {
                Fail("JSON round-trip", ex.Message);
            }
        }

        private static void CheckPreflight()
        {
            string dnsError = RdpErrors.Preflight("rdptabs-selftest-does-not-exist.invalid", 3389, 3000);
            if (dnsError != null) Pass("pre-flight (unknown host)", dnsError);
            else Fail("pre-flight (unknown host)", "it actually resolved?");

            // Even a refused loopback connect takes ~2 s to return RST on Windows, so allow more time
            string refused = RdpErrors.Preflight("127.0.0.1", 1, 5000);
            if (refused != null) Pass("pre-flight (closed port)", refused);
            else Line("  - note: 127.0.0.1:1 actually accepted the connection, skipping this check.");

            System.Net.Sockets.TcpListener listener =
                new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                string reachable = RdpErrors.Preflight("127.0.0.1", port, 3000);
                if (reachable == null) Pass("pre-flight (reachable)", "127.0.0.1:" + port + " passed through");
                else Fail("pre-flight (reachable)", "should have been reachable but reported: " + reachable);
            }
            finally
            {
                listener.Stop();
            }
        }

        private static void CheckAppIcon()
        {
            Icon icon = AppIcon.Value;
            if (icon == null)
            {
                Fail("app icon", "no icon available, falling back to the WinForms default");
                return;
            }
            string png = Path.Combine(Path.GetTempPath(), "RdpTabs-icon.png");
            try
            {
                using (Bitmap bitmap = icon.ToBitmap()) bitmap.Save(png, ImageFormat.Png);
            }
            catch (Exception ex)
            {
                png = "(render failed: " + ex.Message + ")";
            }
            Pass("app icon", icon.Width + "x" + icon.Height + ", the Remote Desktop icon from mstsc.exe, preview " + png);
        }

        /// <summary>
        /// The tab strip is fully owner-drawn with hand-written hit testing, so every mouse interaction depends
        /// on that geometry. This fills it with tabs in every state and checks the hit result at the tab centre,
        /// the close button, "+", the window buttons and the blank area, then renders a preview.
        /// </summary>
        private static void CheckTabStrip()
        {
            Form host = null;
            try
            {
                host = new Form();
                host.FormBorderStyle = FormBorderStyle.None;
                host.ShowInTaskbar = false;
                host.StartPosition = FormStartPosition.Manual;
                host.Location = new Point(-4000, -4000);
                host.BackColor = Theme.ActiveTab;

                ChromeTabStrip strip = new ChromeTabStrip();
                host.ClientSize = new Size(Dpi.Scale(1100), strip.StripHeight + Dpi.Scale(6));
                strip.SetBounds(0, 0, host.ClientSize.Width, strip.StripHeight);
                host.Controls.Add(strip);
                host.Show();

                string[] titles = { "10.0.0.5", "srv-db-prod-01.corp.example.com", "jump-host", "New connection", "192.168.1.7" };
                SessionState[] states =
                {
                    SessionState.Connected, SessionState.Connecting, SessionState.Failed,
                    SessionState.Idle, SessionState.Disconnected
                };
                for (int i = 0; i < titles.Length; i++)
                {
                    TabModel tab = new TabModel();
                    tab.Title = titles[i];
                    tab.Tooltip = titles[i];
                    tab.Status = states[i];
                    strip.Add(tab);
                }
                strip.SelectedIndex = 1;
                strip.Refresh();
                for (int i = 0; i < 6; i++)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(40);
                }

                int problems = 0;
                for (int i = 0; i < strip.Count; i++)
                {
                    Rectangle bounds = strip.TabBounds(i);
                    problems += ExpectHit(strip, Center(bounds), TabHitKind.Tab, i, "tab " + i + " centre");
                    problems += ExpectHit(strip, Center(strip.CloseButtonBounds(i)),
                        TabHitKind.TabClose, i, "tab " + i + " close button");
                    // The gap directly above a tab must hit that tab (otherwise clicks there do nothing)
                    problems += ExpectHit(strip, new Point(bounds.X + bounds.Width / 2, 1),
                        TabHitKind.Tab, i, "gap above tab " + i + "");
                }
                problems += ExpectHit(strip, Center(strip.NewTabButtonBounds()), TabHitKind.NewTab, -1, "new tab button");
                problems += ExpectHit(strip, Center(strip.WindowButtonBounds(0)),
                    TabHitKind.WindowMinimize, -1, "minimize button");
                problems += ExpectHit(strip, Center(strip.WindowButtonBounds(1)),
                    TabHitKind.WindowMaximize, -1, "maximize button");
                problems += ExpectHit(strip, Center(strip.WindowButtonBounds(2)),
                    TabHitKind.WindowClose, -1, "window close button");

                Rectangle newTab = strip.NewTabButtonBounds();
                int emptyX = (newTab.Right + strip.WindowButtonBounds(0).Left) / 2;
                problems += ExpectHit(strip, new Point(emptyX, strip.StripHeight / 2),
                    TabHitKind.Empty, -1, "blank area (window drag)");
                // The gap above blank areas still belongs to the window's top edge (vertical resize)
                problems += ExpectHit(strip, new Point(emptyX, 1),
                    TabHitKind.TopEdge, -1, "gap above blank area (resize)");

                string png = Path.Combine(Path.GetTempPath(), "RdpTabs-tabstrip.png");
                try
                {
                    using (Bitmap bitmap = new Bitmap(host.Width, host.Height))
                    {
                        using (Graphics graphics = Graphics.FromImage(bitmap))
                        {
                            IntPtr hdc = graphics.GetHdc();
                            Native.PrintWindow(host.Handle, hdc, Native.PW_RENDERFULLCONTENT);
                            graphics.ReleaseHdc(hdc);
                        }
                        bitmap.Save(png, ImageFormat.Png);
                    }
                }
                catch (Exception ex)
                {
                    png = "(render failed: " + ex.Message + ")";
                }

                if (problems == 0)
                    Pass("tab strip hit test", "geometry correct for all " + strip.Count + " tabs, preview " + png);
                else
                    Fail("tab strip hit test", problems + " hit result(s) did not match, preview " + png);
            }
            catch (Exception ex)
            {
                Fail("tab strip hit test", Com.Unwrap(ex).ToString());
            }
            finally
            {
                if (host != null)
                {
                    host.Close();
                    host.Dispose();
                }
            }
        }

        /// <summary>Performs one window operation and logs how the display-update count changed.</summary>
        private static void RunResizeStep(Form form, RdpSessionControl session, Stopwatch clock,
            string what, int stepIndex)
        {
            int before = session.DisplayUpdateCount;
            switch (stepIndex)
            {
                case 1: form.WindowState = FormWindowState.Minimized; break;
                case 2: form.WindowState = FormWindowState.Normal; break;
                case 3: form.WindowState = FormWindowState.Maximized; break;
                case 4: form.WindowState = FormWindowState.Normal; break;
                default:
                    form.ClientSize = new Size(form.ClientSize.Width - Dpi.Scale(160),
                        form.ClientSize.Height - Dpi.Scale(120));
                    break;
            }

            // Wait out the debounce window to see whether an update was actually pushed
            for (int i = 0; i < 40; i++)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(50);
            }

            int after = session.DisplayUpdateCount;
            Line(string.Format(CultureInfo.InvariantCulture,
                "  [{0,5:0.0}s] {1}: {2}, updates pushed {3} -> {4}{5}",
                clock.Elapsed.TotalSeconds, what, session.LiveSettingsReport,
                before, after, after == before ? " (remote resolution unchanged)" : " (remote resolution changed)"));
        }

        private static Point Center(Rectangle bounds)
        {
            return new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        }

        private static int ExpectHit(ChromeTabStrip strip, Point point, TabHitKind expectedKind,
            int expectedIndex, string what)
        {
            TabHit hit = strip.HitTest(point);
            bool ok = hit.Kind == expectedKind && (expectedIndex < 0 || hit.Index == expectedIndex);
            if (ok) return 0;
            Line("  ! " + what + " at " + point + " hit " + hit.Kind + "/" + hit.Index +
                 ", expected " + expectedKind + "/" + expectedIndex);
            return 1;
        }

        /// <summary>
        /// The connection dialog is laid out by hand, which invites bugs like negative widths or controls
        /// running past the right edge. So build a real one, check every control's size, and render a preview.
        /// </summary>
        private static void CheckDialogLayout()
        {
            ConnectionProfile sample = new ConnectionProfile();
            sample.Host = "10.0.0.5";
            sample.Name = "Example server";
            sample.UserName = "administrator";
            sample.Domain = "CORP";
            sample.Password = "example-password";
            sample.SavePassword = true;

            ConnectDialog dialog = null;
            try
            {
                dialog = new ConnectDialog(sample, true);
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Location = new Point(-4000, -4000);   // keep it out of the user's way
                dialog.ShowInTaskbar = false;
                dialog.Show();
                for (int i = 0; i < 6; i++)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(30);
                }

                int problems = ValidateLayout(dialog);
                string png = Path.Combine(Path.GetTempPath(), "RdpTabs-dialog.png");
                try
                {
                    // PrintWindow rather than DrawToBitmap: the latter only paints the client area, so the
                    // input borders we draw in WM_NCPAINT would be missing from the preview.
                    using (Bitmap bitmap = new Bitmap(Math.Max(1, dialog.Width), Math.Max(1, dialog.Height)))
                    {
                        bool printed;
                        using (Graphics graphics = Graphics.FromImage(bitmap))
                        {
                            IntPtr hdc = graphics.GetHdc();
                            printed = Native.PrintWindow(dialog.Handle, hdc, Native.PW_RENDERFULLCONTENT);
                            graphics.ReleaseHdc(hdc);
                        }
                        if (!printed)
                            dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
                        bitmap.Save(png, ImageFormat.Png);
                    }
                }
                catch (Exception ex)
                {
                    png = "(render failed: " + ex.Message + ")";
                }

                if (problems == 0)
                    Pass("connection dialog", "control layout is fine, preview " + png);
                else
                    Fail("connection dialog", problems + " control(s) laid out wrong, preview " + png);
            }
            catch (Exception ex)
            {
                Fail("connection dialog", Com.Unwrap(ex).ToString());
            }
            finally
            {
                if (dialog != null)
                {
                    dialog.Close();
                    dialog.Dispose();
                }
            }
        }

        private static int ValidateLayout(Control parent)
        {
            int problems = 0;
            foreach (Control child in parent.Controls)
            {
                if (child.Width <= 0 || child.Height <= 0)
                {
                    problems++;
                    Line("  ! " + Describe(child) + " has a bad size " + child.Width + "x" + child.Height);
                }
                else if (child.Right > parent.ClientSize.Width + 2)
                {
                    problems++;
                    Line("  ! " + Describe(child) + " right edge " + child.Right +
                         " exceeds its parent's " + parent.ClientSize.Width);
                }
                else if (IsTextClipped(child))
                {
                    problems++;
                    Size needed = TextRenderer.MeasureText(child.Text, child.Font);
                    Line("  ! " + Describe(child) + " text is clipped: needs " + needed.Width +
                         "px but has " + child.Width + "px (bounds " + child.Bounds +
                         ", font " + child.Font.Name + " " + child.Font.SizeInPoints + "pt)");
                }
                problems += ValidateLayout(child);
            }
            return problems;
        }

        /// <summary>Clipped text on a label, check box or button is a layout bug (text boxes scroll, so they are exempt).</summary>
        private static bool IsTextClipped(Control control)
        {
            if (string.IsNullOrEmpty(control.Text)) return false;
            int reserved;
            if (control is Label) reserved = 2;
            else if (control is CheckBox) reserved = 26;
            else if (control is FlatButton) reserved = 8;
            else return false;
            return TextRenderer.MeasureText(control.Text, control.Font).Width + reserved > control.Width;
        }

        private static string Describe(Control control)
        {
            string text = control.Text;
            if (!string.IsNullOrEmpty(text) && text.Length > 16) text = text.Substring(0, 16) + "...";
            return control.GetType().Name + (string.IsNullOrEmpty(text) ? "" : " \"" + text + "\"");
        }

        // ---------------- live connection test ----------------

        private static void RunConnectTest(string target)
        {
            string error;
            ConnectionProfile profile = ConnectionProfile.FromQuickConnect(target, out error);
            if (profile == null)
            {
                Line("Connection test: cannot parse the address -- " + error);
                return;
            }

            // Take the password from the environment, not the command line (which shows up in process lists)
            string password = Environment.GetEnvironmentVariable("RDPTABS_TEST_PASSWORD");
            Line("");
            if (!string.IsNullOrEmpty(password))
            {
                profile.Password = password;
                Line("Connection test: " + profile.DetailLine + " (password from RDPTABS_TEST_PASSWORD)");
            }
            else
            {
                Line("Connection test: " + profile.Endpoint + " (no password given, so the control will prompt)");
            }
            Line("Watching for up to 40 s; once connected the window is resized to verify dynamic resolution.");

            Form form = new Form();
            form.AutoScaleMode = AutoScaleMode.None;
            form.Text = "RdpTabs self test connection - " + profile.Endpoint;
            form.ClientSize = new Size(Dpi.Scale(900), Dpi.Scale(600));
            form.StartPosition = FormStartPosition.CenterScreen;
            form.BackColor = Theme.Overlay;

            RdpSessionControl session = new RdpSessionControl(profile);
            session.Bounds = new Rectangle(0, 0, form.ClientSize.Width, form.ClientSize.Height);
            session.Dock = DockStyle.Fill;
            form.Controls.Add(session);

            SessionState last = (SessionState)(-1);
            int lastRaw = -99;
            bool probed = false;
            bool resized = false;
            int step = 0;
            double connectedAt = 0;
            System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer();
            watch.Interval = 250;
            Stopwatch clock = Stopwatch.StartNew();
            watch.Tick += delegate
            {
                if (session.State != last)
                {
                    last = session.State;
                    Line(string.Format(CultureInfo.InvariantCulture, "  [{0,5:0.0}s] {1} {2}",
                        clock.Elapsed.TotalSeconds, last,
                        string.IsNullOrEmpty(session.StatusMessage)
                            ? "" : "-- " + session.StatusMessage.Replace("\n", " / ")));
                    if (last == SessionState.Connected) connectedAt = clock.Elapsed.TotalSeconds;
                }

                int raw = session.RawConnected;
                if (raw != lastRaw)
                {
                    lastRaw = raw;
                    Line(string.Format(CultureInfo.InvariantCulture,
                        "  [{0,5:0.0}s] raw Connected property = {1}", clock.Elapsed.TotalSeconds, raw));
                }

                // Use a call that only succeeds on an established session to pin down the semantics
                if (!probed && clock.Elapsed.TotalSeconds > 10)
                {
                    probed = true;
                    string detail;
                    bool live = session.ProbeLiveSession(out detail);
                    Line(string.Format(CultureInfo.InvariantCulture,
                        "  [{0,5:0.0}s] probe: Connected={1}, session live={2} ({3})",
                        clock.Elapsed.TotalSeconds, raw, live, detail));
                }

                // Once connected, run a scripted sequence of window operations
                if (last == SessionState.Connected && !resized)
                {
                    double since = clock.Elapsed.TotalSeconds - connectedAt;
                    if (since > 3 && step == 0) { step = 1; RunResizeStep(form, session, clock, "minimize", 1); }
                    else if (since > 6 && step == 1) { step = 2; RunResizeStep(form, session, clock, "restore from minimized", 2); }
                    else if (since > 9 && step == 2) { step = 3; RunResizeStep(form, session, clock, "maximize", 3); }
                    else if (since > 12 && step == 3) { step = 4; RunResizeStep(form, session, clock, "restore from maximized", 4); }
                    else if (since > 15 && step == 4)
                    {
                        step = 5;
                        resized = true;
                        RunResizeStep(form, session, clock, "manual resize", 5);
                    }
                }

                if (clock.Elapsed.TotalSeconds > 40) form.Close();
            };
            watch.Start();

            form.Shown += delegate { session.Start(); };
            form.FormClosing += delegate
            {
                watch.Stop();
                session.ShutdownForClose();
            };
            Application.Run(form);
            Line("Connection test finished, final state: " + last);
            Line("Dynamic resolution: " + session.DynamicResolutionReport);
            Line("Expected: minimize and restore-from-minimized must not change the remote resolution; " +
                 "maximize, restore-from-maximized and a manual resize must (otherwise the picture does not fill the window).");
        }

        // ---------------- output ----------------

        private static void Pass(string name, string detail)
        {
            Line("[ ok ] " + name + " -- " + detail);
        }

        private static void Fail(string name, string detail)
        {
            _failures++;
            Line("[FAIL] " + name + " -- " + detail);
        }

        private static void Line(string text)
        {
            Log.AppendLine(text);
            try
            {
                Console.WriteLine(text);
            }
            catch (IOException)
            {
                // No console to write to; the log file still has everything
            }
        }
    }
}
