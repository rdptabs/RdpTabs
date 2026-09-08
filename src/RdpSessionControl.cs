using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace RdpTabs
{
    /// <summary>
    /// One RDP session inside a tab: the hosted ActiveX control plus a status overlay.
    ///
    /// Connection state comes from polling the Connected property every 250 ms rather than from events --
    /// late binding cannot reach IMsTscAxEvents. See the notes at the top of Com.cs.
    /// </summary>
    internal sealed class RdpSessionControl : Panel
    {
        private const int PollIntervalMs = 250;
        private const int ResizeDebounceMs = 450;
        private const int PreflightTimeoutMs = 3000;
        private const int MinDisplayUpdateGapMs = 900;

        /// <summary>
        /// Our own hard timeout. The control's overallConnectionTimeout does not always fire -- verified:
        /// after the user cancels the system credential prompt the control sits at Connected==2 forever,
        /// neither failing nor disconnecting.
        /// The timer is paused while a prompt is up (the user may be typing a password).
        /// </summary>
        private const int ConnectTimeoutSeconds = 30;

        // IMsTscAx.Connected values. Verified empirically (via UpdateSessionDisplaySettings, which only
        // succeeds on a live session): 1 = connected, 2 = connecting. Do not mix these up again.
        private const int RawDisconnected = 0;
        private const int RawConnectedValue = 1;
        private const int RawConnecting = 2;

        private static readonly string[] AdvancedSettingsNames =
        {
            "AdvancedSettings9", "AdvancedSettings8", "AdvancedSettings7", "AdvancedSettings6",
            "AdvancedSettings5", "AdvancedSettings4", "AdvancedSettings3", "AdvancedSettings2",
            "AdvancedSettings"
        };

        private readonly ConnectionProfile _profile;
        private readonly SessionOverlay _overlay;
        private readonly System.Windows.Forms.Timer _poll;
        private readonly System.Windows.Forms.Timer _resizeDebounce;
        private readonly Stopwatch _sinceDisplayUpdate = Stopwatch.StartNew();

        private RdpAxHost _host;
        private object _ocx;
        private object _advanced;
        private string _advancedName = "(none)";

        private SessionState _state = SessionState.Idle;
        private string _status = string.Empty;
        private bool _everConnected;
        private int _displayUpdateFailures;
        private int _displayUpdatesApplied;
        private int _appliedDesktopScale;
        private int _appliedDeviceScale;
        private bool _pendingInitialScale;
        private int _initialScaleAttempts;
        private FormWindowState _lastWindowState = FormWindowState.Normal;
        private Size _appliedDesktop = Size.Empty;
        private int _connectToken;
        private bool _shuttingDown;

        private readonly Stopwatch _connecting = new Stopwatch();
        private readonly Stopwatch _dialogFree = new Stopwatch();
        private IntPtr _promptDialog = IntPtr.Zero;
        private int _shownElapsedSeconds = -1;

        public event EventHandler StateChanged;
        public event EventHandler EditRequested;

        public RdpSessionControl(ConnectionProfile profile)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            _profile = profile;

            DoubleBuffered = true;
            BackColor = Theme.Overlay;
            AutoScroll = profile.Display == DisplayMode.Fixed;

            _overlay = new SessionOverlay();
            _overlay.PrimaryClicked += OnOverlayPrimary;
            _overlay.SecondaryClicked += OnOverlaySecondary;
            Controls.Add(_overlay);

            _poll = new System.Windows.Forms.Timer();
            _poll.Interval = PollIntervalMs;
            _poll.Tick += OnPoll;

            _resizeDebounce = new System.Windows.Forms.Timer();
            _resizeDebounce.Interval = ResizeDebounceMs;
            _resizeDebounce.Tick += OnResizeDebounce;

            UpdateOverlay();
        }

        public ConnectionProfile Profile
        {
            get { return _profile; }
        }

        public SessionState State
        {
            get { return _state; }
        }

        public string StatusMessage
        {
            get { return _status; }
        }

        public bool IsLive
        {
            get
            {
                return _state == SessionState.Checking || _state == SessionState.Connecting ||
                       _state == SessionState.Connected;
            }
        }

        /// <summary>Diagnostics for the self test.</summary>
        public string AdvancedSettingsName
        {
            get { return _advancedName; }
        }

        /// <summary>Raw value of the control's Connected property (self test uses it to pin down semantics).</summary>
        public int RawConnected
        {
            get { return _ocx == null ? -1 : Com.GetInt(_ocx, "Connected", -2); }
        }

        /// <summary>
        /// Calls UpdateSessionDisplaySettings once: it only succeeds on a truly established session, which is
        /// how we determined which Connected value actually means "connected".
        /// </summary>
        public bool ProbeLiveSession(out string detail)
        {
            detail = "no control instance";
            if (_ocx == null) return false;
            Size target = DesiredDesktopSize();
            int desktopScale, deviceScale;
            GetScaleFactors(out desktopScale, out deviceScale);
            try
            {
                Com.Call(_ocx, "UpdateSessionDisplaySettings",
                    (uint)target.Width, (uint)target.Height, (uint)0, (uint)0, (uint)0,
                    (uint)desktopScale, (uint)deviceScale);
                detail = "UpdateSessionDisplaySettings succeeded -> the session is live";
                return true;
            }
            catch (Exception ex)
            {
                detail = "UpdateSessionDisplaySettings failed: " + Com.Unwrap(ex).Message;
                return false;
            }
        }

        /// <summary>Live session settings, for the self test.</summary>
        public string LiveSettingsReport
        {
            get
            {
                if (_ocx == null) return "(no control instance)";
                return "SmartSizing=" + Com.GetInt(_advanced, "SmartSizing", -1) +
                       ", remote desktop " + Com.GetInt(_ocx, "DesktopWidth", -1) + "x" +
                       Com.GetInt(_ocx, "DesktopHeight", -1) +
                       ", control area " + ClientSize.Width + "x" + ClientSize.Height;
            }
        }

        /// <summary>How many display-setting updates succeeded, for the self test.</summary>
        public int DisplayUpdateCount
        {
            get { return _displayUpdatesApplied; }
        }

        /// <summary>What dynamic resolution actually did, for the self test.</summary>
        public string DynamicResolutionReport
        {
            get
            {
                if (_profile.Display == DisplayMode.Fixed) return "off (fixed resolution)";
                if (!_profile.DynamicResolution) return "off (scaling only)";
                if (_displayUpdateFailures > 0)
                    return _displayUpdateFailures + " call(s) failed, fell back to scaling";
                if (_pendingInitialScale) return "still retrying the initial scale";
                if (_displayUpdatesApplied == 0)
                    return "no update during the session (remote resolution still the initial " +
                           _appliedDesktop.Width + "x" + _appliedDesktop.Height + " at 100%)";
                return _displayUpdatesApplied + " successful update(s), now " +
                       _appliedDesktop.Width + "x" + _appliedDesktop.Height +
                       " @ desktop scale " + _appliedDesktopScale + "% / device scale " + _appliedDeviceScale + "%";
            }
        }

        // ---------------- connection flow ----------------

        public void Start()
        {
            if (IsLive) return;

            Form startForm = FindForm();
            if (startForm != null) _lastWindowState = startForm.WindowState;

            try
            {
                EnsureOcx();
            }
            catch (Exception ex)
            {
                SetState(SessionState.Failed, "Could not initialize the Remote Desktop control: " + Com.Unwrap(ex).Message);
                return;
            }

            // Through a gateway the target is usually not directly reachable, so the pre-flight is pointless.
            if (_profile.UseGateway)
            {
                Connect();
                return;
            }

            SetState(SessionState.Checking, "Checking whether " + _profile.Endpoint + " is reachable...");

            int token = ++_connectToken;
            string host = _profile.Host;
            int port = _profile.Port;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string error = RdpErrors.Preflight(host, port, PreflightTimeoutMs);
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (IsDisposed || _shuttingDown || token != _connectToken) return;
                        if (error != null) SetState(SessionState.Failed, error);
                        else Connect();
                    }));
                }
                catch (Exception)
                {
                    // the control is already disposed; ignore
                }
            });
        }

        public void Reconnect()
        {
            _everConnected = false;
            _appliedDesktop = Size.Empty;
            _displayUpdateFailures = 0;
            _displayUpdatesApplied = 0;
            _pendingInitialScale = false;
            _initialScaleAttempts = 0;

            // Reconnecting needs a fresh control instance: calling Connect again on a disconnected OCX is unreliable.
            DisposeOcx();
            Start();
        }

        public void Disconnect()
        {
            _connectToken++;
            if (_ocx == null)
            {
                SetState(SessionState.Failed, "The connection was cancelled.");
                return;
            }
            object ignored;
            Com.TryCall(_ocx, "Disconnect", out ignored);
            SetState(_everConnected ? SessionState.Disconnected : SessionState.Failed,
                _everConnected ? "The connection was closed." : "The connection was cancelled.");
        }

        private void Connect()
        {
            try
            {
                EnsureOcx();
                ApplySettings();
                _connecting.Restart();
                _shownElapsedSeconds = -1;
                SetState(SessionState.Connecting, ConnectingMessage());
                Com.Call(_ocx, "Connect");
                _poll.Start();
            }
            catch (Exception ex)
            {
                SetState(SessionState.Failed, "Could not start the connection: " + Com.Unwrap(ex).Message);
            }
        }

        private void EnsureOcx()
        {
            if (_ocx != null) return;

            if (_host == null)
            {
                _host = new RdpAxHost();
                _host.TabStop = false;
                Controls.Add(_host);
                _host.SendToBack();
                _overlay.BringToFront();
            }

            LayoutChildren();
            _host.EnsureCreated();
            _ocx = _host.Ocx;
            if (_ocx == null)
                throw new InvalidOperationException("Could not create the Remote Desktop control (GetOcx returned null).");

            _advanced = Com.FirstAvailable(_ocx, out _advancedName, AdvancedSettingsNames);
            if (_advancedName == null) _advancedName = "(none)";
        }

        private void DisposeOcx()
        {
            _poll.Stop();
            _resizeDebounce.Stop();
            if (_ocx != null)
            {
                try
                {
                    if (Com.GetInt(_ocx, "Connected", RawDisconnected) != RawDisconnected)
                    {
                        object ignored;
                        Com.TryCall(_ocx, "Disconnect", out ignored);
                        WaitForDisconnect(2000);
                    }
                }
                catch (Exception)
                {
                }
            }
            _ocx = null;
            _advanced = null;
            if (_host != null)
            {
                Controls.Remove(_host);
                _host.Dispose();
                _host = null;
            }
        }

        /// <summary>Call before closing: disconnect first, then release, or the COM side can hang.</summary>
        public void ShutdownForClose()
        {
            _shuttingDown = true;
            _connectToken++;
            _poll.Stop();
            _resizeDebounce.Stop();
            try
            {
                if (_ocx != null && Com.GetInt(_ocx, "Connected", RawDisconnected) != RawDisconnected)
                {
                    object ignored;
                    Com.TryCall(_ocx, "Disconnect", out ignored);
                    WaitForDisconnect(3000);
                }
            }
            catch (Exception)
            {
            }
            _ocx = null;
            _advanced = null;
        }

        private void WaitForDisconnect(int timeoutMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                if (Com.GetInt(_ocx, "Connected", RawDisconnected) == RawDisconnected) return;
                Application.DoEvents();   // the OCX needs a message pump to finish disconnecting
                System.Threading.Thread.Sleep(25);
            }
        }

        // ---------------- pushing settings to the control ----------------

        private void ApplySettings()
        {
            ConnectionProfile p = _profile;

            Com.Set(_ocx, "Server", p.Host);
            Com.TrySet(_ocx, "UserName", p.UserName ?? string.Empty);
            if (!string.IsNullOrEmpty(p.Domain)) Com.TrySet(_ocx, "Domain", p.Domain);

            Size desktop = DesiredDesktopSize();
            Com.TrySet(_ocx, "DesktopWidth", desktop.Width);
            Com.TrySet(_ocx, "DesktopHeight", desktop.Height);
            Com.TrySet(_ocx, "ColorDepth", p.ColorDepth);
            _appliedDesktop = desktop;

            if (_advanced == null) return;

            Com.TrySet(_advanced, "RDPPort", p.Port);
            Com.TrySet(_advanced, "AuthenticationLevel", (uint)p.AuthenticationLevel);
            Com.TrySet(_advanced, "EnableCredSspSupport", true);
            Com.TrySet(_advanced, "PublicMode", false);

            // Only the NotSafeForScripting class accepts a plaintext password. With none set, the control prompts.
            if (!string.IsNullOrEmpty(p.Password))
                Com.TrySet(_advanced, "ClearTextPassword", p.Password);

            // Essential for tabs: a background tab must not steal focus while connecting.
            Com.TrySet(_advanced, "GrabFocusOnConnect", false);
            Com.TrySet(_advanced, "DisplayConnectionBar", false);
            Com.TrySet(_advanced, "allowBackgroundInput", 1);

            Com.TrySet(_advanced, "SmartSizing", p.Display != DisplayMode.Fixed);

            Com.TrySet(_advanced, "RedirectClipboard", p.RedirectClipboard);
            Com.TrySet(_advanced, "RedirectPrinters", p.RedirectPrinters);
            Com.TrySet(_advanced, "RedirectDrives", p.RedirectDrives);
            Com.TrySet(_advanced, "RedirectPorts", p.RedirectPorts);
            Com.TrySet(_advanced, "RedirectSmartCards", p.RedirectSmartCards);
            Com.TrySet(_advanced, "AudioRedirectionMode", (uint)p.AudioMode);
            Com.TrySet(_advanced, "AudioCaptureRedirectionMode", p.AudioCapture);

            Com.TrySet(_advanced, "PerformanceFlags", p.PerformanceFlags);
            Com.TrySet(_advanced, "NetworkConnectionType", (uint)p.NetworkConnectionType);
            Com.TrySet(_advanced, "BandwidthDetection", p.BandwidthDetection);
            Com.TrySet(_advanced, "EnableAutoReconnect", p.AutoReconnect);
            Com.TrySet(_advanced, "MaxReconnectAttempts", p.AutoReconnect ? 20 : 0);
            Com.TrySet(_advanced, "overallConnectionTimeout", 20);
            Com.TrySet(_advanced, "singleConnectionTimeout", 15);

            if (p.UseGateway && !string.IsNullOrEmpty(p.GatewayHost))
            {
                Com.TrySet(_advanced, "GatewayUsageMethod", (uint)1);       // always use the gateway
                Com.TrySet(_advanced, "GatewayProfileUsageMethod", (uint)1); // use explicit settings
                Com.TrySet(_advanced, "GatewayHostname", p.GatewayHost);
                Com.TrySet(_advanced, "GatewayCredsSource", (uint)0);        // user name / password
                Com.TrySet(_advanced, "GatewayBypassLocal", p.GatewayBypassLocal ? 1 : 0);
            }
            else
            {
                Com.TrySet(_advanced, "GatewayUsageMethod", (uint)0);
            }

            // Who gets the Windows key combos (Win, Alt+Tab): 0 = local, 1 = remote, 2 = full screen only.
            string securedName;
            object secured = Com.FirstAvailable(_ocx, out securedName,
                "SecuredSettings3", "SecuredSettings2", "SecuredSettings");
            if (secured != null)
                Com.TrySet(secured, "KeyboardHookMode", p.KeyboardHookMode);
        }

        private Size DesiredDesktopSize()
        {
            if (_profile.Display == DisplayMode.Fixed)
                return new Size(Even(_profile.FixedWidth), Even(_profile.FixedHeight));

            Size size = ClientSize;
            int width = Even(Clamp(size.Width, 400, 8192));
            int height = Even(Clamp(size.Height, 300, 8192));
            return new Size(width, height);
        }

        /// <summary>Desktop scale (100-500) and device scale (only 100/140/180 are allowed).</summary>
        private void GetScaleFactors(out int desktopScale, out int deviceScale)
        {
            int percent = _profile.ScalePercent;
            if (percent <= 0)
            {
                int dpi = Dpi.Value;
                percent = (int)Math.Round(dpi * 100.0 / 96.0);
            }
            desktopScale = Clamp(percent, 100, 500);
            deviceScale = desktopScale >= 170 ? 180 : (desktopScale >= 130 ? 140 : 100);
        }

        private static int Even(int value)
        {
            return value % 2 == 0 ? value : value - 1;
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        // ---------------- state polling ----------------

        private void OnPoll(object sender, EventArgs e)
        {
            if (_ocx == null || _shuttingDown)
            {
                _poll.Stop();
                return;
            }

            int connected = Com.GetInt(_ocx, "Connected", -1);
            switch (connected)
            {
                case RawConnectedValue:
                    if (_state != SessionState.Connected)
                    {
                        _everConnected = true;
                        _promptDialog = IntPtr.Zero;
                        _dialogFree.Reset();
                        _connecting.Stop();
                        SetState(SessionState.Connected, string.Empty);
                        FocusSession();

                        // A freshly connected session is still at 100% scaling (the control does not let us set
                        // the scale factors before connecting), so remote text comes out tiny. Push the DPI scale
                        // right away instead of waiting for the user to resize the window.
                        // SmartSizing also has to be set again after connecting: the pre-connect write does not
                        // affect an established session.
                        Com.TrySet(_advanced, "SmartSizing", _profile.Display != DisplayMode.Fixed);

                        _pendingInitialScale = true;
                        _initialScaleAttempts = 0;
                        ApplyInitialScale();
                    }
                    break;
                case RawConnecting:
                    if (_state != SessionState.Connecting)
                    {
                        if (!_connecting.IsRunning) _connecting.Restart();
                        _shownElapsedSeconds = -1;
                        SetState(SessionState.Connecting, ConnectingMessage());
                    }
                    else
                    {
                        UpdateConnectingMessage();
                        if (_dialogFree.IsRunning &&
                            _dialogFree.Elapsed.TotalSeconds > ConnectTimeoutSeconds)
                        {
                            FailWithTimeout();
                        }
                    }
                    break;
                case RawDisconnected:
                    if (_state == SessionState.Connecting || _state == SessionState.Connected ||
                        _state == SessionState.Checking)
                    {
                        SetState(_everConnected ? SessionState.Disconnected : SessionState.Failed,
                            DescribeDisconnect());
                    }
                    _poll.Stop();
                    break;
            }
        }

        /// <summary>
        /// Status text while connecting. This has to handle a situation that looks exactly like a hang:
        /// with no password set the RDP control pops the system "Windows Security" credential prompt (or a
        /// certificate warning). That dialog is modal and disables our main window, so the UI appears frozen
        /// while Connected stays at "connecting".
        /// When such a dialog is detected we bring it to the front and tell the user what is going on.
        /// </summary>
        private string ConnectingMessage()
        {
            IntPtr dialog = FindPromptDialog();
            if (dialog != IntPtr.Zero)
            {
                _dialogFree.Reset();   // the user may be typing credentials; do not run the timeout
                if (dialog != _promptDialog)
                {
                    _promptDialog = dialog;
                    Native.BringDialogToFront(dialog);   // it can be hidden behind the main window
                }
                return "Windows is asking for credentials for " + _profile.Endpoint + ".\n" +
                       "Enter the user name and password in the \"Windows Security\" window that just opened " +
                       "(tick \"Remember me\" to skip it next time).\n" +
                       "That window is system-modal, so this window cannot be used until it closes.";
            }

            if (_promptDialog != IntPtr.Zero)
            {
                // the dialog just closed: restart the timeout
                _promptDialog = IntPtr.Zero;
                _dialogFree.Restart();
            }
            else if (!_dialogFree.IsRunning)
            {
                _dialogFree.Restart();
            }

            int seconds = (int)_connecting.Elapsed.TotalSeconds;
            string text = "Connecting to " + _profile.Endpoint + "...";
            if (seconds >= 3)
                text += " (" + seconds.ToString(CultureInfo.InvariantCulture) + "s)";
            if (seconds >= 25)
                text += "\nThe server is not responding. If it requires Network Level Authentication (NLA), " +
                        "cancel and fill in the user name, domain and password under \"Edit connection\".";
            return text;
        }

        /// <summary>The handshake never completes: disconnect and explain the most likely cause.</summary>
        private void FailWithTimeout()
        {
            _poll.Stop();
            _dialogFree.Reset();
            _connecting.Stop();
            object ignored;
            Com.TryCall(_ocx, "Disconnect", out ignored);
            SetState(SessionState.Failed,
                "Connection timed out: the handshake did not finish within " +
                ConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + " seconds.\n" +
                "If a \"Windows Security\" credential window appeared and was cancelled or closed, the Remote " +
                "Desktop control stays stuck at this step and never reports an error. Retry and enter the " +
                "credentials, or fill in user name / domain / password under \"Edit connection\" first.");
        }

        private IntPtr FindPromptDialog()
        {
            Form form = FindForm();
            if (form == null || !form.IsHandleCreated) return IntPtr.Zero;
            return Native.FindBlockingDialog(form.Handle);
        }

        /// <summary>Refreshes the connecting text once a second (immediately while a dialog is up).</summary>
        private void UpdateConnectingMessage()
        {
            int seconds = (int)_connecting.Elapsed.TotalSeconds;
            bool prompt = FindPromptDialog() != IntPtr.Zero;
            if (!prompt && seconds == _shownElapsedSeconds) return;
            _shownElapsedSeconds = seconds;

            string message = ConnectingMessage();
            if (message == _status) return;
            _status = message;
            UpdateOverlay();
        }

        private string DescribeDisconnect()
        {
            int extended = Com.GetInt(_ocx, "ExtendedDisconnectReason", 0);
            string text = RdpErrors.DescribeExtendedReason(extended);

            object description;
            if (Com.TryCall(_ocx, "GetErrorDescription", out description, (uint)0, (uint)extended))
            {
                string extra = description as string;
                if (!string.IsNullOrEmpty(extra) && extra.Trim().Length > 0)
                    text += "\nThe control reports: " + extra.Trim();
            }

            if (!_everConnected)
                text += "\nIf the target requires Network Level Authentication (NLA), check the user name, password and domain.";
            return text;
        }

        private void SetState(SessionState state, string message)
        {
            _state = state;
            _status = message ?? string.Empty;
            UpdateOverlay();
            EventHandler handler = StateChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void UpdateOverlay()
        {
            bool connected = _state == SessionState.Connected;
            _overlay.Visible = !connected;
            if (connected) return;

            switch (_state)
            {
                case SessionState.Checking:
                case SessionState.Connecting:
                    // No cancel button: connecting is normally quick, a 30 s timeout backs it up, and closing the tab aborts
                    _overlay.SetContent(_profile.DisplayName, _status, true, null, null);
                    break;
                case SessionState.Disconnected:
                    _overlay.SetContent("Disconnected", _status, false, "Reconnect", "Edit connection");
                    break;
                case SessionState.Failed:
                    _overlay.SetContent("Cannot connect to " + _profile.Endpoint, _status, false, "Retry", "Edit connection");
                    break;
                default:
                    _overlay.SetContent(_profile.DisplayName, "Ready to connect", false, "Connect", "Edit connection");
                    break;
            }
            _overlay.BringToFront();
        }

        private void OnOverlayPrimary(object sender, EventArgs e)
        {
            if (_state == SessionState.Checking || _state == SessionState.Connecting) Disconnect();
            else Reconnect();
        }

        private void OnOverlaySecondary(object sender, EventArgs e)
        {
            EventHandler handler = EditRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        /// <summary>Re-applies colors after a theme change.</summary>
        public void ApplyTheme()
        {
            BackColor = Theme.Overlay;
            if (IsHandleCreated) Native.UseThemedScrollbars(Handle, Theme.IsDark);
            _overlay.ApplyTheme();
            Invalidate(true);
        }

        public void FocusSession()
        {
            if (_host != null && _host.IsHandleCreated && _state == SessionState.Connected)
            {
                _host.Focus();
            }
            else if (_overlay.Visible)
            {
                _overlay.FocusPrimary();
            }
        }

        // ---------------- layout and dynamic resolution ----------------

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();

            if (_state != SessionState.Connected) return;
            if (_profile.Display == DisplayMode.Fixed || !_profile.DynamicResolution) return;
            if (_displayUpdateFailures >= 2) return;
            if (!IsUserResize()) return;

            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        }

        /// <summary>
        /// Should this size change make the remote resolution follow?
        ///
        /// While minimized the window size degenerates to 0x0, and following that would shrink the remote
        /// desktop to a tiny patch -- so neither minimizing nor restoring from minimized follows (restoring
        /// returns to the previous size anyway).
        /// Maximizing and restoring from maximized do follow: this control's SmartSizing only scales the
        /// picture down, never up (verified), so without following, a maximized window just shows the old
        /// picture centred with a border around it.
        /// </summary>
        private bool IsUserResize()
        {
            Form form = FindForm();
            FormWindowState state = form != null ? form.WindowState : FormWindowState.Normal;
            FormWindowState previous = _lastWindowState;
            _lastWindowState = state;

            if (state == FormWindowState.Minimized) return false;
            if (previous == FormWindowState.Minimized) return false;
            return true;
        }

        private bool IsWindowMinimized()
        {
            Form form = FindForm();
            return form != null && form.WindowState == FormWindowState.Minimized;
        }

        private void LayoutChildren()
        {
            int width = Math.Max(1, ClientSize.Width);
            int height = Math.Max(1, ClientSize.Height);

            if (_host != null)
            {
                _host.Bounds = _profile.Display == DisplayMode.Fixed
                    ? new Rectangle(0, 0, _profile.FixedWidth, _profile.FixedHeight)
                    : new Rectangle(0, 0, width, height);
            }
            _overlay.Bounds = new Rectangle(0, 0, width, height);
        }

        private void OnResizeDebounce(object sender, EventArgs e)
        {
            _resizeDebounce.Stop();
            if (_pendingInitialScale)
            {
                ApplyInitialScale();
                return;
            }
            if (_sinceDisplayUpdate.ElapsedMilliseconds < MinDisplayUpdateGapMs)
            {
                _resizeDebounce.Start();   // called too often the control refuses; wait a moment and retry
                return;
            }
            ApplyDynamicResolution();
        }

        /// <summary>
        /// Pushes the scale factors for the local DPI as soon as the session is up.
        /// No late-bindable property can set the scale before connecting (DesktopScaleFactor /
        /// DeviceScaleFactor live only on IMsRdpExtendedSettings, a pure vtable interface -- guessing its
        /// layout crashes the process), so the only option is to do it right after connecting.
        /// The session may not be ready at that instant; on failure the debounce timer retries a few times.
        /// </summary>
        private void ApplyInitialScale()
        {
            if (!_pendingInitialScale || _ocx == null || _state != SessionState.Connected) return;

            // While minimized the size is degenerate: wait for a restore (this attempt does not count)
            if (IsWindowMinimized())
            {
                _resizeDebounce.Stop();
                _resizeDebounce.Start();
                return;
            }

            _initialScaleAttempts++;
            if (ApplyDynamicResolution(true))
            {
                _pendingInitialScale = false;
                return;
            }

            if (_initialScaleAttempts >= 4)
            {
                // The server/protocol has no display control: fall back to scaling instead of retrying forever
                _pendingInitialScale = false;
                Com.TrySet(_advanced, "SmartSizing", _profile.Display != DisplayMode.Fixed);
                return;
            }
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        }

        private bool ApplyDynamicResolution()
        {
            return ApplyDynamicResolution(false);
        }

        private bool ApplyDynamicResolution(bool force)
        {
            if (_ocx == null || _state != SessionState.Connected) return false;
            if (IsWindowMinimized()) return false;   // never base the resolution on a minimized window

            Size target = DesiredDesktopSize();
            if (!force && target == _appliedDesktop) return true;

            int desktopScale, deviceScale;
            GetScaleFactors(out desktopScale, out deviceScale);

            try
            {
                Com.Call(_ocx, "UpdateSessionDisplaySettings",
                    (uint)target.Width, (uint)target.Height, (uint)0, (uint)0, (uint)0,
                    (uint)desktopScale, (uint)deviceScale);
                _appliedDesktop = target;
                _appliedDesktopScale = desktopScale;
                _appliedDeviceScale = deviceScale;
                _displayUpdateFailures = 0;
                _displayUpdatesApplied++;
                _sinceDisplayUpdate.Restart();
                return true;
            }
            catch (Exception)
            {
                _displayUpdateFailures++;
                _sinceDisplayUpdate.Restart();
                if (_displayUpdateFailures >= 2 && !_pendingInitialScale)
                {
                    // The server or protocol version has no dynamic resolution: fall back to scaling.
                    Com.TrySet(_advanced, "SmartSizing", true);
                }
                return false;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _shuttingDown = true;
                _poll.Stop();
                _poll.Dispose();
                _resizeDebounce.Stop();
                _resizeDebounce.Dispose();
                _ocx = null;
                _advanced = null;
            }
            base.Dispose(disposing);
        }
    }
}
