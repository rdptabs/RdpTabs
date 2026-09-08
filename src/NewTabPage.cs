using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RdpTabs
{
    internal sealed class ThemeModeEventArgs : EventArgs
    {
        public readonly ThemeMode Mode;

        public ThemeModeEventArgs(ThemeMode mode)
        {
            Mode = mode;
        }
    }

    internal sealed class ProfileEventArgs : EventArgs
    {
        public readonly ConnectionProfile Profile;

        public ProfileEventArgs(ConnectionProfile profile)
        {
            Profile = profile;
        }
    }

    /// <summary>A saved or recent connection card. Click connects; right-click opens a menu.</summary>
    internal sealed class ProfileCard : Control
    {
        private bool _hover;
        private bool _pressed;

        public ConnectionProfile Profile { get; private set; }
        public bool Compact { get; private set; }

        public event EventHandler<ProfileEventArgs> Activated;
        public event EventHandler<ProfileEventArgs> ContextMenuRequested;

        public ProfileCard(ConnectionProfile profile, bool compact)
        {
            Profile = profile;
            Compact = compact;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            _pressed = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _pressed = true;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool wasPressed = _pressed;
            _pressed = false;
            Invalidate();

            if (e.Button == MouseButtons.Left && wasPressed && ClientRectangle.Contains(e.Location))
            {
                EventHandler<ProfileEventArgs> handler = Activated;
                if (handler != null) handler(this, new ProfileEventArgs(Profile));
            }
            else if (e.Button == MouseButtons.Right)
            {
                EventHandler<ProfileEventArgs> handler = ContextMenuRequested;
                if (handler != null) handler(this, new ProfileEventArgs(Profile));
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.PageBackground);

            RectangleF box = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Draw.FillRounded(g, box, 8f, _hover || _pressed ? Theme.CardHover : Theme.Card);
            Draw.DrawRounded(g, box, 8f, _hover ? Theme.Accent : Theme.CardBorder, 1f);

            int glyph = Dpi.Scale(Compact ? 18 : 22);
            Rectangle icon = new Rectangle(Dpi.Scale(Compact ? 10 : 14),
                (Height - glyph) / 2, glyph, glyph);
            DrawServerGlyph(g, icon);

            int left = icon.Right + Dpi.Scale(10);
            int right = Width - Dpi.Scale(10);
            if (Compact)
            {
                Rectangle nameRect = new Rectangle(left, 0, Math.Max(20, right - left), Height);
                TextRenderer.DrawText(g, Profile.DetailLine, Fonts.Body, nameRect, Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                return;
            }

            int textWidth = Math.Max(20, right - left);
            int titleHeight = Fonts.BodyBold.Height + 2;
            int detailHeight = Fonts.Small.Height + 2;
            int top = (Height - titleHeight - detailHeight) / 2;
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.Top |
                                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, Profile.DisplayName, Fonts.BodyBold,
                new Rectangle(left, top, textWidth, titleHeight), Theme.Text, flags);
            TextRenderer.DrawText(g, Profile.DetailLine, Fonts.Small,
                new Rectangle(left, top + titleHeight, textWidth, detailHeight), Theme.TextDim, flags);
        }

        private static void DrawServerGlyph(Graphics g, Rectangle box)
        {
            using (Pen pen = new Pen(Theme.Accent, 1.6f))
            {
                float h = box.Height / 2.6f;
                RectangleF top = new RectangleF(box.X, box.Y + 1, box.Width, h);
                RectangleF bottom = new RectangleF(box.X, box.Bottom - h - 1, box.Width, h);
                using (GraphicsPath p1 = Draw.RoundedRect(top, 2f))
                using (GraphicsPath p2 = Draw.RoundedRect(bottom, 2f))
                {
                    g.DrawPath(pen, p1);
                    g.DrawPath(pen, p2);
                }
                using (SolidBrush brush = new SolidBrush(Theme.Accent))
                {
                    g.FillEllipse(brush, box.X + 3, top.Y + h / 2f - 1.2f, 2.4f, 2.4f);
                    g.FillEllipse(brush, box.X + 3, bottom.Y + h / 2f - 1.2f, 2.4f, 2.4f);
                }
            }
        }
    }

    /// <summary>
    /// The page a new tab starts on: quick-connect box, saved connections and recent connections.
    /// Like a browser's new tab page -- picking a connection turns this tab into that session (MainForm swaps
    /// the page out).
    /// </summary>
    internal sealed class NewTabPage : Panel
    {
        private const int MaxContentWidth = 900;
        private const int CardWidth = 268;
        private const int CardHeight = 64;
        private const int CompactCardHeight = 34;
        private const int Gap = 12;

        private readonly ProfileStore _store;
        private readonly HintTextBox _quickInput = new HintTextBox();
        private readonly InputFrame _quickFrame = new InputFrame();
        private readonly FlatButton _connectButton = new FlatButton();
        private readonly FlatButton _advancedButton = new FlatButton();
        private readonly List<ProfileCard> _cards = new List<ProfileCard>();
        private readonly FlatButton[] _themeButtons = new FlatButton[3];

        private Rectangle _themeLabelRect;
        private Rectangle _headingRect;
        private Rectangle _subtitleRect;
        private Rectangle _savedLabelRect;
        private Rectangle _savedEmptyRect;
        private Rectangle _recentLabelRect;
        private bool _hasRecent;

        public event EventHandler<ProfileEventArgs> ConnectRequested;
        public event EventHandler<ProfileEventArgs> EditRequested;
        public event EventHandler<ProfileEventArgs> DeleteRequested;
        public event EventHandler<ThemeModeEventArgs> ThemeChangeRequested;

        public NewTabPage(ProfileStore store)
        {
            if (store == null) throw new ArgumentNullException("store");
            _store = store;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Theme.PageBackground;
            AutoScroll = true;
            Padding = new Padding(0, 0, 0, 24);

            _quickInput.Font = Fonts.Body;
            _quickInput.Hint = "Computer name or IP -- host:3390 and user@host also work";
            _quickInput.KeyDown += OnQuickInputKeyDown;
            _quickFrame.Attach(_quickInput, 10, 6);
            Controls.Add(_quickFrame);

            _connectButton.Text = "Connect";
            _connectButton.Primary = true;
            _connectButton.Font = Fonts.Body;
            _connectButton.Click += delegate { SubmitQuickConnect(); };
            Controls.Add(_connectButton);

            _advancedButton.Text = "Advanced...";
            _advancedButton.Font = Fonts.Body;
            _advancedButton.Click += delegate { RaiseEdit(null); };
            Controls.Add(_advancedButton);

            string[] themeNames = { "System", "Dark", "Light" };
            for (int i = 0; i < _themeButtons.Length; i++)
            {
                ThemeMode mode = (ThemeMode)i;
                FlatButton button = new FlatButton();
                button.Text = themeNames[i];
                button.Font = Fonts.Body;
                button.Click += delegate
                {
                    EventHandler<ThemeModeEventArgs> handler = ThemeChangeRequested;
                    if (handler != null) handler(this, new ThemeModeEventArgs(mode));
                };
                _themeButtons[i] = button;
                Controls.Add(button);
            }
            SyncThemeButtons();

            BuildCards();
        }

        public void FocusInput()
        {
            if (_quickInput.CanFocus) _quickInput.Focus();
        }

        private void SyncThemeButtons()
        {
            for (int i = 0; i < _themeButtons.Length; i++)
                _themeButtons[i].Primary = (int)Theme.Mode == i;
        }

        /// <summary>Re-applies colors to every child after a theme change.</summary>
        public void ApplyTheme()
        {
            BackColor = Theme.PageBackground;
            _quickFrame.ApplyTheme();
            _connectButton.Invalidate();
            _advancedButton.Invalidate();
            SyncThemeButtons();
            foreach (FlatButton button in _themeButtons) button.Invalidate();
            foreach (ProfileCard card in _cards) card.Invalidate();
            Relayout();
            Invalidate();
        }

        /// <summary>Rebuilds the cards after the saved/recent lists change.</summary>
        public void Reload()
        {
            BuildCards();
            Relayout();
            Invalidate();
        }

        private void BuildCards()
        {
            foreach (ProfileCard card in _cards)
            {
                Controls.Remove(card);
                card.Dispose();
            }
            _cards.Clear();

            foreach (ConnectionProfile profile in _store.Profiles)
                _cards.Add(CreateCard(profile, false));

            _hasRecent = false;
            foreach (ConnectionProfile profile in _store.Recent)
            {
                if (IsSaved(profile)) continue;   // do not repeat saved connections under Recent
                _cards.Add(CreateCard(profile, true));
                _hasRecent = true;
            }
        }

        private bool IsSaved(ConnectionProfile recent)
        {
            foreach (ConnectionProfile saved in _store.Profiles)
            {
                if (string.Equals(saved.Host, recent.Host, StringComparison.OrdinalIgnoreCase) &&
                    saved.Port == recent.Port &&
                    string.Equals(saved.FullUserName, recent.FullUserName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private ProfileCard CreateCard(ConnectionProfile profile, bool compact)
        {
            ProfileCard card = new ProfileCard(profile, compact);
            card.Activated += delegate(object sender, ProfileEventArgs e) { RaiseConnect(e.Profile); };
            card.ContextMenuRequested += OnCardContextMenu;
            Controls.Add(card);
            return card;
        }

        private void OnCardContextMenu(object sender, ProfileEventArgs e)
        {
            ConnectionProfile profile = e.Profile;
            ContextMenuStrip menu = Menus.Create();
            menu.Items.Add(Menus.Item("Connect", delegate { RaiseConnect(profile); }));
            menu.Items.Add(Menus.Item("Edit...", delegate { RaiseEdit(profile); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Menus.Item("Delete", delegate
            {
                EventHandler<ProfileEventArgs> handler = DeleteRequested;
                if (handler != null) handler(this, new ProfileEventArgs(profile));
            }));
            menu.Show(Cursor.Position);
        }

        private void OnQuickInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            SubmitQuickConnect();
        }

        private void SubmitQuickConnect()
        {
            string error;
            ConnectionProfile profile = ConnectionProfile.FromQuickConnect(_quickInput.Text, out error);
            if (profile == null)
            {
                MessageBox.Show(FindForm(), error, "Cannot parse address",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _quickInput.Focus();
                return;
            }
            RaiseConnect(profile);
        }

        private void RaiseConnect(ConnectionProfile profile)
        {
            EventHandler<ProfileEventArgs> handler = ConnectRequested;
            if (handler != null) handler(this, new ProfileEventArgs(profile));
        }

        private void RaiseEdit(ConnectionProfile profile)
        {
            EventHandler<ProfileEventArgs> handler = EditRequested;
            if (handler != null) handler(this, new ProfileEventArgs(profile));
        }

        // ---------------- layout ----------------

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            Invalidate();   // the painted headings have to scroll along
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Invalidate();
        }

        private static int Sc(int value)
        {
            return Dpi.Scale(value);
        }

        /// <summary>
        /// Size text rectangles from the measured height instead of a constant: line heights differ per font
        /// family and per script, and a hard-coded height clips glyphs.
        /// </summary>
        private static Rectangle MeasureRow(int left, int top, int width, string text, Font font, bool wrap)
        {
            TextFormatFlags flags = TextFormatFlags.NoPrefix | (wrap ? TextFormatFlags.WordBreak : 0);
            Size size = TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), flags);
            return new Rectangle(left, top, width, size.Height + 2);
        }

        private void Relayout()
        {
            if (Width <= 0) return;

            int gap = Sc(Gap);
            int cardWidth = Sc(CardWidth);
            int cardHeight = Sc(CardHeight);
            int compactHeight = Sc(CompactCardHeight);

            int contentWidth = Math.Min(Sc(MaxContentWidth), Math.Max(Sc(320), ClientSize.Width - Sc(96)));
            int left = Math.Max(Sc(24), (ClientSize.Width - contentWidth) / 2);
            Point scroll = AutoScrollPosition;   // with AutoScroll, child coordinates include the scroll offset
            int y = Sc(56);

            _headingRect = MeasureRow(left, y, contentWidth, HeadingText, Fonts.Heading, false);
            y = _headingRect.Bottom + Sc(6);
            _subtitleRect = MeasureRow(left, y, contentWidth, SubtitleText, Fonts.Body, false);
            y = _subtitleRect.Bottom + Sc(18);

            int buttonWidth = Sc(92);
            int advancedWidth = Sc(96);
            int inputHeight = Sc(40);
            int inputWidth = Math.Max(Sc(140), contentWidth - buttonWidth - advancedWidth - gap * 2);
            _quickFrame.SetBounds(left + scroll.X, y + scroll.Y, inputWidth, inputHeight);
            _connectButton.SetBounds(left + inputWidth + gap + scroll.X, y + scroll.Y,
                buttonWidth, inputHeight);
            _advancedButton.SetBounds(left + inputWidth + buttonWidth + gap * 2 + scroll.X, y + scroll.Y,
                advancedWidth, inputHeight);
            y += inputHeight + Sc(36);

            _savedLabelRect = MeasureRow(left, y, contentWidth, SavedLabelText, Fonts.BodyBold, false);
            y = _savedLabelRect.Bottom + Sc(10);

            int savedCount = _store.Profiles.Count;
            if (savedCount == 0)
            {
                _savedEmptyRect = MeasureRow(left, y, contentWidth, EmptyText, Fonts.Body, true);
                y = _savedEmptyRect.Bottom + Sc(12);
            }
            else
            {
                _savedEmptyRect = Rectangle.Empty;
                int perRow = Math.Max(1, (contentWidth + gap) / (cardWidth + gap));
                for (int i = 0; i < savedCount && i < _cards.Count; i++)
                {
                    int row = i / perRow;
                    int column = i % perRow;
                    _cards[i].SetBounds(
                        left + column * (cardWidth + gap) + scroll.X,
                        y + row * (cardHeight + gap) + scroll.Y,
                        cardWidth, cardHeight);
                }
                int rows = (savedCount + perRow - 1) / perRow;
                y += rows * (cardHeight + gap) + Sc(20);
            }

            if (_hasRecent)
            {
                _recentLabelRect = MeasureRow(left, y, contentWidth, RecentLabelText, Fonts.BodyBold, false);
                y = _recentLabelRect.Bottom + Sc(10);
                for (int i = savedCount; i < _cards.Count; i++)
                {
                    _cards[i].SetBounds(left + scroll.X, y + scroll.Y,
                        Math.Min(contentWidth, cardWidth * 2 + gap), compactHeight);
                    y += compactHeight + Sc(6);
                }
            }
            else
            {
                _recentLabelRect = Rectangle.Empty;
            }

            y += Sc(20);
            _themeLabelRect = MeasureRow(left, y + Sc(6), contentWidth, ThemeLabelText, Fonts.BodyBold, false);
            int themeLeft = left + TextRenderer.MeasureText(ThemeLabelText, Fonts.BodyBold).Width + gap * 2;
            foreach (FlatButton button in _themeButtons)
            {
                button.SizeToText(Sc(14));
                button.SetBounds(themeLeft + scroll.X, y + scroll.Y, button.Width, Sc(30));
                themeLeft += button.Width + Sc(6);
            }
        }

        private const string HeadingText = "New connection";
        private const string SubtitleText = "Type an address to connect, or pick one of the saved connections below.";
        private const string SavedLabelText = "Saved connections";
        private const string RecentLabelText = "Recent";
        private const string ThemeLabelText = "Theme";
        private const string EmptyText =
            "No saved connections yet. Use \"Advanced...\" to create one with Save ticked, or right-click a tab and choose \"Save as connection\".";

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.PageBackground);
            Point scroll = AutoScrollPosition;
            g.TranslateTransform(scroll.X, scroll.Y);

            TextRenderer.DrawText(g, HeadingText, Fonts.Heading, _headingRect, Theme.Text, LeftFlags);
            TextRenderer.DrawText(g, SubtitleText, Fonts.Body, _subtitleRect, Theme.TextDim, LeftFlags);
            TextRenderer.DrawText(g, SavedLabelText, Fonts.BodyBold, _savedLabelRect, Theme.Text, LeftFlags);

            if (!_savedEmptyRect.IsEmpty)
            {
                TextRenderer.DrawText(g, EmptyText, Fonts.Body, _savedEmptyRect, Theme.TextDim,
                    LeftFlags | TextFormatFlags.WordBreak);
            }

            if (!_recentLabelRect.IsEmpty)
                TextRenderer.DrawText(g, RecentLabelText, Fonts.BodyBold, _recentLabelRect, Theme.Text, LeftFlags);

            TextRenderer.DrawText(g, ThemeLabelText, Fonts.BodyBold, _themeLabelRect, Theme.Text, LeftFlags);
        }

        private static TextFormatFlags LeftFlags
        {
            get { return TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix; }
        }
    }
}
