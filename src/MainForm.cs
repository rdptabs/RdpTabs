using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RdpTabs
{
    internal sealed class MainForm : Form
    {
        /// <summary>One tab: either the new-connection page or an RDP session.</summary>
        private sealed class SessionTab
        {
            public TabModel Model;
            public Control Page;
            public RdpSessionControl Session;
            public ConnectionProfile Profile;
        }

        // Auto-hide: polled because a live session swallows every mouse message (see OnAutoHideTick)
        private const int AutoHidePollMs = 100;
        private const int AutoHideGraceTicks = 6;   // ~600 ms below the strip before it slides away
        private const int RevealBandPt = 4;         // how close to the top edge counts as "reveal"

        private readonly ProfileStore _store;
        private readonly System.Windows.Forms.Timer _autoHide = new System.Windows.Forms.Timer();
        private int _hideCountdown;
        private bool _stripMenuOpen;
        private readonly ChromeTabStrip _strip = new ChromeTabStrip();
        private readonly Panel _content = new Panel();
        private readonly List<SessionTab> _tabs = new List<SessionTab>();
        private readonly List<ConnectionProfile> _startupProfiles;

        /// <summary>Each profile in startupProfiles (from the command line) opens its own tab and connects.</summary>
        public MainForm(ProfileStore store, List<ConnectionProfile> startupProfiles)
        {
            if (store == null) throw new ArgumentNullException("store");
            _store = store;
            _startupProfiles = startupProfiles ?? new List<ConnectionProfile>();

            Text = "RdpTabs - Remote Desktop with tabs";
            Font = Fonts.Body;
            AutoScaleMode = AutoScaleMode.None;   // all scaling goes through Dpi.Scale; no WinForms layer on top
            BackColor = Theme.ActiveTab;
            FormBorderStyle = FormBorderStyle.Sizable;   // keep native snap/resize/rounded corners; the caption is removed in WM_NCCALCSIZE
            MinimumSize = new Size(Sc(760), Sc(480));
            DoubleBuffered = true;

            _content.BackColor = Theme.PageBackground;   // matches the Home page, so no seam under the island
            Controls.Add(_content);

            _strip.SetOpacityPercent(_store.IslandOpacityPercent);
            _autoHide.Interval = AutoHidePollMs;
            _autoHide.Tick += OnAutoHideTick;

            _strip.TabSelected += OnTabSelected;
            _strip.TabCloseRequested += OnTabCloseRequested;
            _strip.TabsReordered += OnTabsReordered;
            _strip.TabContextMenuRequested += OnTabContextMenu;
            _strip.WindowCommandRequested += OnWindowCommand;
            _strip.NewTabRequested += delegate { AddNewTabPage(true); };
            _strip.IslandMoved += OnIslandMoved;
            _strip.PreferredWidthChanged += delegate { LayoutIsland(); };


            RestoreWindowPlacement();
            AddNewTabPage(true);
            SyncAutoHide();
        }

        private static int Sc(int value)
        {
            return Dpi.Scale(value);
        }

        private SessionTab ActiveTab
        {
            get
            {
                int index = _strip.SelectedIndex;
                return index >= 0 && index < _tabs.Count ? _tabs[index] : null;
            }
        }

        // ---------------- tab orchestration ----------------

        private SessionTab AddNewTabPage(bool activate)
        {
            NewTabPage page = new NewTabPage(_store);
            SessionTab tab = new SessionTab();
            tab.Page = page;
            tab.Model = new TabModel();
            tab.Model.Title = "New connection";
            tab.Model.Tooltip = "New connection";
            tab.Model.Status = SessionState.Idle;
            tab.Model.Tag = tab;

            page.ConnectRequested += delegate(object sender, ProfileEventArgs e)
            {
                ConnectInTab(tab, e.Profile);
            };
            page.EditRequested += delegate(object sender, ProfileEventArgs e)
            {
                ConnectionProfile profile = e.Profile != null ? e.Profile : new ConnectionProfile();
                ShowConnectDialog(profile, e.Profile != null && _store.FindById(profile.Id) != null, tab);
            };
            page.DeleteRequested += delegate(object sender, ProfileEventArgs e)
            {
                DeleteProfile(e.Profile);
            };
            page.OpacityChangeRequested += delegate(object sender, OpacityEventArgs e)
            {
                _store.IslandOpacityPercent = e.Percent;
                _strip.SetOpacityPercent(e.Percent);       // live, so dragging previews the result
                if (e.Committed) _store.Save();            // only write the file once the drag ends
            };
            page.ThemeChangeRequested += delegate(object sender, ThemeModeEventArgs e)
            {
                ChangeTheme(e.Mode);
            };

            _tabs.Add(tab);
            _content.Controls.Add(page);
            _strip.Add(tab.Model);
            LayoutPages();

            if (activate) ActivateTab(tab);
            return tab;
        }

        private void ConnectInTab(SessionTab tab, ConnectionProfile profile)
        {
            ConnectionProfile working = profile.Clone();
            _store.TouchRecent(working);
            TrySaveStore();
            ReloadNewTabPages();

            RdpSessionControl session = new RdpSessionControl(working);
            session.StateChanged += delegate { UpdateTabModel(tab); };
            session.EditRequested += delegate { EditTabConnection(tab); };

            ReplacePage(tab, session);
            tab.Profile = working;
            UpdateTabModel(tab);
            session.Start();
            if (ActiveTab == tab) FocusActivePage();
        }

        private void ReplacePage(SessionTab tab, Control newPage)
        {
            Control old = tab.Page;
            if (old != null)
            {
                RdpSessionControl session = old as RdpSessionControl;
                if (session != null) session.ShutdownForClose();
                _content.Controls.Remove(old);
                old.Dispose();
            }

            tab.Page = newPage;
            tab.Session = newPage as RdpSessionControl;
            _content.Controls.Add(newPage);
            LayoutPages();
            if (ActiveTab == tab) newPage.BringToFront();
        }

        private void ActivateTab(SessionTab tab)
        {
            int index = _tabs.IndexOf(tab);
            if (index < 0) return;
            _strip.SelectedIndex = index;
            tab.Page.BringToFront();
            FocusActivePage();
        }

        private void FocusActivePage()
        {
            SessionTab tab = ActiveTab;
            if (tab == null) return;
            if (tab.Session != null) tab.Session.FocusSession();
            else
            {
                NewTabPage page = tab.Page as NewTabPage;
                if (page != null) page.FocusInput();
            }
        }

        private void UpdateTabModel(SessionTab tab)
        {
            if (tab.Session != null && tab.Profile != null)
            {
                tab.Model.Title = tab.Profile.DisplayName;
                tab.Model.Status = tab.Session.State;
                tab.Model.Tooltip = BuildTooltip(tab);
            }
            else
            {
                tab.Model.Title = "New connection";
                tab.Model.Status = SessionState.Idle;
                tab.Model.Tooltip = "New connection";
            }
            _strip.RefreshTab(_tabs.IndexOf(tab));
        }

        private static string BuildTooltip(SessionTab tab)
        {
            string account = tab.Profile.FullUserName;
            string line = string.IsNullOrEmpty(account)
                ? tab.Profile.Endpoint
                : account + " @ " + tab.Profile.Endpoint;
            return tab.Profile.DisplayName + "\n" + line + "\n" + StatusText(tab.Session.State);
        }

        private static string StatusText(SessionState state)
        {
            switch (state)
            {
                case SessionState.Checking: return "Checking reachability...";
                case SessionState.Connecting: return "Connecting...";
                case SessionState.Connected: return "Connected";
                case SessionState.Disconnected: return "Disconnected";
                case SessionState.Failed: return "Connection failed";
                default: return "Not connected";
            }
        }

        private void CloseTab(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;
            SessionTab tab = _tabs[index];
            SessionTab previouslyActive = ActiveTab;

            if (tab.Session != null) tab.Session.ShutdownForClose();
            _content.Controls.Remove(tab.Page);
            tab.Page.Dispose();
            _tabs.RemoveAt(index);
            _strip.RemoveAt(index);

            if (_tabs.Count == 0)
            {
                Close();   // closing the last tab closes the window, like a browser
                return;
            }

            // Closing a background tab must not switch away from the current one
            SessionTab next = previouslyActive != null && previouslyActive != tab && _tabs.Contains(previouslyActive)
                ? previouslyActive
                : _tabs[Math.Min(index, _tabs.Count - 1)];
            ActivateTab(next);
        }

        // ---------------- tab strip events ----------------

        private void OnTabSelected(object sender, TabIndexEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _tabs.Count) return;
            _tabs[e.Index].Page.BringToFront();
            FocusActivePage();
        }

        private void OnTabCloseRequested(object sender, TabIndexEventArgs e)
        {
            CloseTab(e.Index);
        }

        private void OnTabsReordered(object sender, TabReorderEventArgs e)
        {
            if (e.FromIndex < 0 || e.FromIndex >= _tabs.Count) return;
            SessionTab tab = _tabs[e.FromIndex];
            _tabs.RemoveAt(e.FromIndex);
            _tabs.Insert(Math.Max(0, Math.Min(e.ToIndex, _tabs.Count)), tab);
        }

        private void OnWindowCommand(object sender, WindowCommandEventArgs e)
        {
            switch (e.Command)
            {
                case WindowCommand.Minimize:
                    WindowState = FormWindowState.Minimized;
                    break;
                case WindowCommand.MaximizeOrRestore:
                    WindowState = WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal : FormWindowState.Maximized;
                    _strip.Invalidate();
                    break;
                case WindowCommand.Close:
                    Close();
                    break;
            }
        }

        private void OnTabContextMenu(object sender, TabContextMenuEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _tabs.Count) return;
            SessionTab tab = _tabs[e.Index];
            int index = e.Index;

            ContextMenuStrip menu = Menus.Create();

            if (tab.Session != null)
            {
                if (tab.Session.IsLive)
                    menu.Items.Add(Menus.Item("Disconnect", delegate { tab.Session.Disconnect(); }));
                else
                    menu.Items.Add(Menus.Item("Reconnect", delegate { tab.Session.Reconnect(); }));

                menu.Items.Add(Menus.Item("Open another in a new tab", delegate
                {
                    SessionTab duplicate = AddNewTabPage(true);
                    ConnectInTab(duplicate, tab.Profile);
                }));
                menu.Items.Add(Menus.Item("Edit connection...", delegate { EditTabConnection(tab); }));

                if (_store.FindById(tab.Profile.Id) == null)
                    menu.Items.Add(Menus.Item("Save as connection", delegate { SaveProfile(tab.Profile.Clone()); }));

                menu.Items.Add(new ToolStripSeparator());
            }

            ToolStripMenuItem autoHide = Menus.Item("Auto-hide tab bar", delegate { ToggleAutoHide(); });
            autoHide.Checked = _store.AutoHideStrip;
            menu.Items.Add(autoHide);
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(Menus.Item("Close tab", delegate { CloseTab(index); }));
            if (_tabs.Count > 1)
            {
                menu.Items.Add(Menus.Item("Close other tabs", delegate
                {
                    for (int i = _tabs.Count - 1; i >= 0; i--)
                        if (_tabs[i] != tab) CloseTab(i);
                }));
            }

            _stripMenuOpen = true;
            menu.Closed += delegate { _stripMenuOpen = false; };
            menu.Show(e.ScreenLocation);
        }

        private void ToggleAutoHide()
        {
            _store.AutoHideStrip = !_store.AutoHideStrip;
            _store.Save();
            SyncAutoHide();
            PerformLayout();
            _strip.Invalidate();
        }

        /// <summary>Starts or stops the poll, and pins the strip back when auto-hide is switched off.</summary>
        private void SyncAutoHide()
        {
            _hideCountdown = 0;
            if (_store.AutoHideStrip)
            {
                if (!_autoHide.Enabled) _autoHide.Start();
                return;
            }
            _autoHide.Stop();
            if (!_strip.Visible) _strip.Visible = true;
        }

        /// <summary>
        /// Polls the cursor rather than listening for mouse moves: once the pointer is over a live session the
        /// RDP control owns every mouse message, so the strip would never hear about it. GetCursorPos is global
        /// and does not care who has the input.
        /// </summary>
        private void OnAutoHideTick(object sender, EventArgs e)
        {
            if (!_store.AutoHideStrip)
            {
                SyncAutoHide();
                return;
            }

            bool visible;
            if (_stripMenuOpen || _strip.IsBusy || !ActiveTabIsSession())
            {
                visible = true;            // pinned: no remote picture to get out of the way of, or mid-gesture
                _hideCountdown = 0;
            }
            else
            {
                Rectangle client = RectangleToScreen(new Rectangle(Point.Empty, ClientSize));
                Point cursor = Cursor.Position;
                bool insideX = cursor.X >= client.Left && cursor.X < client.Right;
                // A little above the client top as well, so the reveal band is reachable when not maximized
                bool atTopEdge = insideX && cursor.Y >= client.Top - Sc(2) &&
                                 cursor.Y < client.Top + Sc(RevealBandPt);
                Rectangle island = RectangleToScreen(_strip.Bounds);
                bool overStrip = island.Contains(cursor);

                if (atTopEdge || (_strip.Visible && overStrip))
                {
                    visible = true;
                    _hideCountdown = 0;
                }
                else if (_strip.Visible)
                {
                    _hideCountdown++;
                    visible = _hideCountdown < AutoHideGraceTicks;
                }
                else
                {
                    visible = false;
                }
            }

            if (visible == _strip.Visible) return;
            _strip.Visible = visible;
            if (visible) _strip.BringToFront();
            _hideCountdown = 0;
        }

        private bool ActiveTabIsSession()
        {
            SessionTab tab = ActiveTab;
            return tab != null && tab.Page is RdpSessionControl;
        }

        // ---------------- connection settings ----------------

        private void EditTabConnection(SessionTab tab)
        {
            if (tab.Profile == null) return;
            bool alreadySaved = _store.FindById(tab.Profile.Id) != null;
            ShowConnectDialog(tab.Profile, alreadySaved, tab);
        }

        /// <summary>Opens the connection dialog. With a targetTab it connects there, otherwise in a new tab.</summary>
        private void ShowConnectDialog(ConnectionProfile profile, bool alreadySaved, SessionTab targetTab)
        {
            using (ConnectDialog dialog = new ConnectDialog(profile, alreadySaved))
            {
                DialogResult result = dialog.ShowDialog(this);
                if (result == DialogResult.Cancel) return;

                ConnectionProfile edited = dialog.Result;
                if (dialog.SaveRequested) SaveProfile(edited.Clone());
                else ReloadNewTabPages();

                if (result == DialogResult.OK)
                {
                    SessionTab tab = targetTab != null ? targetTab : AddNewTabPage(true);
                    ConnectInTab(tab, edited);
                }
            }
        }

        private void SaveProfile(ConnectionProfile profile)
        {
            _store.AddOrUpdate(profile);
            TrySaveStore();
            ReloadNewTabPages();
        }

        private void DeleteProfile(ConnectionProfile profile)
        {
            DialogResult confirm = MessageBox.Show(this,
                string.Format(CultureInfo.CurrentCulture, "Delete the saved connection \"{0}\"?", profile.DisplayName),
                "RdpTabs", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            _store.Remove(profile.Id);
            TrySaveStore();
            ReloadNewTabPages();
        }

        private void ReloadNewTabPages()
        {
            foreach (SessionTab tab in _tabs)
            {
                NewTabPage page = tab.Page as NewTabPage;
                if (page != null) page.Reload();
            }
        }

        private void TrySaveStore()
        {
            CaptureWindowPlacement();
            string error;
            if (_store.TrySave(out error))
                return;
            MessageBox.Show(this, "Could not save the settings file: " + error + "\nPath: " + ProfileStore.FilePath,
                "RdpTabs", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ---------------- layout ----------------

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            LayoutIsland();
            // The session takes the whole client area and the island floats on top of it, so the remote
            // desktop keeps every pixel and shows through the translucent strip.
            _content.SetBounds(0, 0, ClientSize.Width, ClientSize.Height);
            if (_strip.Visible) _strip.BringToFront();
            LayoutPages();
        }

        /// <summary>
        /// Sizes the island to its contents and places it along the top at the remembered ratio, clamped so it
        /// always stays fully inside the window. The island is a separate top-level window, so its bounds are
        /// in screen coordinates -- everything else here works in the form's client space.
        /// </summary>
        private void LayoutIsland()
        {
            if (!IsHandleCreated || WindowState == FormWindowState.Minimized) return;
            int height = _strip.StripHeight;
            int width = Math.Min(ClientSize.Width, _strip.PreferredWidth);
            int left;
            if (_strip.IsDraggingIsland)
            {
                // Mid-drag only the size may change: recomputing the position from the stored permille would
                // snap the island back onto that grid and fight the drag.
                left = PointToClient(_strip.Location).X;
            }
            else
            {
                left = (int)Math.Round(ClientSize.Width * (_store.IslandCenterPermille / 1000.0)) - width / 2;
            }
            left = Math.Max(0, Math.Min(left, ClientSize.Width - width));
            _strip.Bounds = RectangleToScreen(new Rectangle(left, 0, width, height));
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            LayoutIsland();
        }

        /// <summary>
        /// Brings the island up as an owned window: owned so it always floats above this form and never above
        /// other apps, and so it disappears with us when minimised.
        /// </summary>
        private void ShowIsland()
        {
            if (_strip.Owner == this) return;
            _strip.Owner = this;
            LayoutIsland();
            _strip.Show();
            SyncAutoHide();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _autoHide.Stop();
            if (!_strip.IsDisposed) _strip.Close();
            base.OnFormClosed(e);
        }

        private void OnIslandMoved(object sender, IslandMoveEventArgs e)
        {
            int width = _strip.Width;
            int left = Math.Max(0, Math.Min(e.DesiredLeft, ClientSize.Width - width));
            Point screen = PointToScreen(new Point(left, 0));
            if (screen.X != _strip.Left) _strip.Left = screen.X;
            if (ClientSize.Width > 0)
            {
                double centre = (left + width / 2.0) / ClientSize.Width;
                _store.IslandCenterPermille = Math.Max(0, Math.Min(1000, (int)Math.Round(centre * 1000)));
            }
            if (e.Final) _store.Save();
        }

        private void LayoutPages()
        {
            Rectangle area = new Rectangle(0, 0, _content.ClientSize.Width, _content.ClientSize.Height);
            // Every page fills the whole area, including the band the island floats over: the pages are stacked
            // and only reordered by z, so a page that stopped below the island would let the session behind it
            // show through up there. The Home page keeps its content clear of the island with a top inset
            // instead, which still paints its own background across that band.
            int inset = _strip.StripHeight;
            foreach (SessionTab tab in _tabs)
            {
                if (tab.Page == null) continue;
                tab.Page.Bounds = area;
                NewTabPage home = tab.Page as NewTabPage;
                if (home != null) home.TopInset = inset;
            }
        }

        // ---------------- keyboard shortcuts (only while focus is not inside a remote session) ----------------

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.T:
                    AddNewTabPage(true);
                    return true;
                case Keys.Control | Keys.W:
                    CloseTab(_strip.SelectedIndex);
                    return true;
                case Keys.Control | Keys.Tab:
                case Keys.Control | Keys.Next:
                    StepTab(1);
                    return true;
                case Keys.Control | Keys.Shift | Keys.Tab:
                case Keys.Control | Keys.Prior:
                    StepTab(-1);
                    return true;
            }

            if ((keyData & Keys.Control) == Keys.Control)
            {
                Keys key = keyData & Keys.KeyCode;
                if (key >= Keys.D1 && key <= Keys.D9)
                {
                    int index = key - Keys.D1;
                    if (index < _tabs.Count) ActivateTab(_tabs[index]);
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void StepTab(int delta)
        {
            if (_tabs.Count == 0) return;
            int index = (_strip.SelectedIndex + delta + _tabs.Count) % _tabs.Count;
            ActivateTab(_tabs[index]);
        }

        // ---------------- frameless window ----------------

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.EnableRoundedCorners(Handle);
            AppIcon.Apply(this);
            PerformLayout();   // DeviceDpi is only accurate once the handle exists, so lay out again
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ShowIsland();
            FocusActivePage();
            if (_startupProfiles.Count == 0) return;

            // Creating the ActiveX control after the window is shown is the most reliable order.
            // The first profile reuses the empty tab; the rest get their own.
            List<ConnectionProfile> pending = new List<ConnectionProfile>(_startupProfiles);
            _startupProfiles.Clear();
            for (int i = 0; i < pending.Count; i++)
            {
                SessionTab tab = i == 0 ? _tabs[0] : AddNewTabPage(false);
                ConnectInTab(tab, pending[i]);
            }
            ActivateTab(_tabs[0]);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_NCCALCSIZE:
                    if (m.WParam != IntPtr.Zero)
                    {
                        HandleNcCalcSize(ref m);
                        return;
                    }
                    break;

                case Native.WM_NCHITTEST:
                    base.WndProc(ref m);
                    AdjustHitTest(ref m);
                    return;

                case Native.WM_SETTINGCHANGE:
                    base.WndProc(ref m);
                    if (Theme.Refresh()) ApplyTheme();
                    return;
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// Removes only the top caption and leaves left/right/bottom to the system, so snapping, maximizing to
        /// the work area, edge resizing, rounded corners and the drop shadow all stay native. When maximized the
        /// top still has to be inset by the frame thickness, or content lands off-screen.
        /// </summary>
        private void HandleNcCalcSize(ref Message m)
        {
            Native.NCCALCSIZE_PARAMS parameters =
                (Native.NCCALCSIZE_PARAMS)Marshal.PtrToStructure(m.LParam, typeof(Native.NCCALCSIZE_PARAMS));
            int windowTop = parameters.NewBounds.Top;

            base.WndProc(ref m);   // let the system compute the default client area first

            parameters = (Native.NCCALCSIZE_PARAMS)
                Marshal.PtrToStructure(m.LParam, typeof(Native.NCCALCSIZE_PARAMS));
            parameters.NewBounds.Top = windowTop +
                (Native.IsZoomed(Handle) ? Native.FrameThickness : 0);
            Marshal.StructureToPtr(parameters, m.LParam, false);
        }

        private void AdjustHitTest(ref Message m)
        {
            if ((int)m.Result != Native.HTCLIENT) return;

            Point client = PointToClient(new Point(Native.LoWord(m.LParam), Native.HiWord(m.LParam)));
            if (client.Y >= _strip.Bottom) return;

            TabHit hit = _strip.HitTest(new Point(client.X - _strip.Left, client.Y - _strip.Top));
            if (hit.Kind == TabHitKind.Empty || hit.Kind == TabHitKind.TopEdge)
                m.Result = (IntPtr)Native.HTCAPTION;   // drag / double-click maximize / snap handled by the system
        }

        private void ChangeTheme(ThemeMode mode)
        {
            bool changed = Theme.SetMode(mode);
            _store.Theme = mode;
            TrySaveStore();

            // Even when the palette did not change (system already dark and dark was picked), update the buttons
            if (!changed)
            {
                foreach (SessionTab tab in _tabs)
                {
                    NewTabPage page = tab.Page as NewTabPage;
                    if (page != null) page.ApplyTheme();
                }
                return;
            }
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            Native.SetAppDarkMode(Theme.IsDark);
            BackColor = Theme.ActiveTab;
            _content.BackColor = Theme.ActiveTab;
            _strip.BackColor = Theme.Frame;

            foreach (SessionTab tab in _tabs)
            {
                NewTabPage page = tab.Page as NewTabPage;
                if (page != null) page.ApplyTheme();
                if (tab.Session != null) tab.Session.ApplyTheme();
            }
            _strip.Invalidate();
            Invalidate(true);
        }

        // ---------------- window placement and closing ----------------

        private void RestoreWindowPlacement()
        {
            Rectangle saved = _store.WindowBounds;
            if (saved.Width > 0 && saved.Height > 0 && IsOnAnyScreen(saved))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = saved;
            }
            else
            {
                StartPosition = FormStartPosition.CenterScreen;
                Size working = Screen.PrimaryScreen.WorkingArea.Size;
                Size = new Size(Math.Min(Sc(1280), (int)(working.Width * 0.8)),
                    Math.Min(Sc(820), (int)(working.Height * 0.85)));
            }
            if (_store.WindowMaximized) WindowState = FormWindowState.Maximized;
        }

        private static bool IsOnAnyScreen(Rectangle bounds)
        {
            foreach (Screen screen in Screen.AllScreens)
                if (screen.WorkingArea.IntersectsWith(bounds)) return true;
            return false;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _autoHide.Stop();
            // No "there are active connections" confirmation on close: just disconnect and exit.
            // Server-side the sessions become "disconnected but still signed in", so nothing is lost.
            SaveWindowPlacement();
            foreach (SessionTab tab in _tabs)
                if (tab.Session != null) tab.Session.ShutdownForClose();

            base.OnFormClosing(e);
        }

        /// <summary>Copies the current window placement into the settings object before every save.</summary>
        private void CaptureWindowPlacement()
        {
            if (!IsHandleCreated) return;
            _store.WindowMaximized = WindowState == FormWindowState.Maximized;
            Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (bounds.Width > 200 && bounds.Height > 150) _store.WindowBounds = bounds;
        }

        private void SaveWindowPlacement()
        {
            CaptureWindowPlacement();
            string error;
            _store.TrySave(out error);   // a failed save while closing is not worth a dialog
        }
    }
}
