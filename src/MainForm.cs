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

        private readonly ProfileStore _store;
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

            _content.BackColor = Theme.ActiveTab;
            Controls.Add(_content);

            _strip.TabSelected += OnTabSelected;
            _strip.TabCloseRequested += OnTabCloseRequested;
            _strip.NewTabRequested += delegate { AddNewTabPage(true); };
            _strip.TabsReordered += OnTabsReordered;
            _strip.TabContextMenuRequested += OnTabContextMenu;
            _strip.WindowCommandRequested += OnWindowCommand;
            Controls.Add(_strip);

            RestoreWindowPlacement();
            AddNewTabPage(true);
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

            menu.Items.Add(Menus.Item("Close tab", delegate { CloseTab(index); }));
            if (_tabs.Count > 1)
            {
                menu.Items.Add(Menus.Item("Close other tabs", delegate
                {
                    for (int i = _tabs.Count - 1; i >= 0; i--)
                        if (_tabs[i] != tab) CloseTab(i);
                }));
            }

            menu.Show(e.ScreenLocation);
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
            int stripHeight = _strip.StripHeight;
            _strip.SetBounds(0, 0, ClientSize.Width, stripHeight);
            _content.SetBounds(0, stripHeight, ClientSize.Width,
                Math.Max(0, ClientSize.Height - stripHeight));
            LayoutPages();
        }

        private void LayoutPages()
        {
            Rectangle area = new Rectangle(0, 0, _content.ClientSize.Width, _content.ClientSize.Height);
            foreach (SessionTab tab in _tabs)
                if (tab.Page != null) tab.Page.Bounds = area;
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
