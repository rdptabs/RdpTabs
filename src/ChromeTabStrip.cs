using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace RdpTabs
{
    internal enum TabHitKind
    {
        None,
        Empty,       // blank strip area: handed to the window as caption (drag / double-click to maximize)
        TopEdge,     // the thin gap on top: handed to the window for vertical resizing
        Tab,
        TabClose,
        NewTab,
        WindowMinimize,
        WindowMaximize,
        WindowClose
    }

    internal struct TabHit
    {
        public TabHitKind Kind;
        public int Index;

        public TabHit(TabHitKind kind, int index)
        {
            Kind = kind;
            Index = index;
        }

        public static readonly TabHit None = new TabHit(TabHitKind.None, -1);
    }

    internal enum WindowCommand
    {
        Minimize,
        MaximizeOrRestore,
        Close
    }

    /// <summary>Everything the strip needs to draw one tab.</summary>
    internal sealed class TabModel
    {
        public string Title = string.Empty;
        public string Tooltip = string.Empty;
        public SessionState Status = SessionState.Idle;
        public object Tag;
    }

    /// <summary>
    /// Browser-style tab strip: Firefox-shaped tabs, status dots, close buttons, the "+" button and
    /// the window buttons.
    /// It is a translucent island: only as wide as its contents, centred at the top, floating over the
    /// session rather than taking a band of its own. Because the session lies directly beneath it, answering
    /// HTTRANSPARENT for the blank bits would hand the click to the remote desktop instead of to the frame, so
    /// the island keeps the message and starts the window drag itself -- see TryFrameGesture.
    /// </summary>
    internal sealed class ChromeTabStrip : Form
    {
        private readonly List<TabModel> _tabs = new List<TabModel>();
        private readonly System.Windows.Forms.Timer _anim = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _tooltipDelay = new System.Windows.Forms.Timer();
        private readonly ToolTip _tooltip = new ToolTip();

        private int _selectedIndex = -1;
        private TabHit _hover = TabHit.None;
        private int _spinAngle;
        private int _scrollOffset;

        private bool _mouseDownOnTab;
        private int _dragIndex = -1;
        private int _dragGrabOffset;
        private Point _mouseDownPoint;
        private bool _dragging;
        private bool _islandDragging;
        private int _islandGrabOffsetX;
        private bool _tooltipVisible;

        // Metrics in 96-DPI units, multiplied by the DPI ratio when used
        private int _dpi = 96;
        private int _stripHeight, _topPad, _tabHeight, _tabBottomPad, _radius, _shoulder;
        private int _slotMax, _slotMin, _iconSize, _closeSize, _paddingX, _newTabSize, _windowButtonWidth;
        private int _dragThreshold;
        private int _slant;
        private int _dragGrip;

        public event EventHandler<TabIndexEventArgs> TabSelected;
        public event EventHandler<TabIndexEventArgs> TabCloseRequested;
        public event EventHandler NewTabRequested;
        public event EventHandler<TabReorderEventArgs> TabsReordered;
        public event EventHandler<TabContextMenuEventArgs> TabContextMenuRequested;
        public event EventHandler<WindowCommandEventArgs> WindowCommandRequested;

        /// <summary>The user is sliding the island sideways; the form decides where it may land.</summary>
        public event EventHandler<IslandMoveEventArgs> IslandMoved;

        /// <summary>The island wants a different width; only the owner form can place it.</summary>
        public event EventHandler PreferredWidthChanged;

        public ChromeTabStrip()
        {
            // A top-level layered window rather than a child control: only UpdateLayeredWindow offers
            // per-pixel alpha, which is what lets the background be translucent while the labels stay solid.
            // Verified the hard way -- the same call against a child HWND fails with ERROR_INVALID_PARAMETER.
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            MinimizeBox = false;
            MaximizeBox = false;
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Font = Fonts.Tab;

            _anim.Interval = 80;
            _anim.Tick += delegate
            {
                _spinAngle = (_spinAngle + 30) % 360;
                Repaint();
            };

            _tooltipDelay.Interval = 600;
            _tooltipDelay.Tick += OnTooltipDelay;

            _tooltip.OwnerDraw = false;
            _tooltip.ShowAlways = true;
        }

        // ---------------- tab collection ----------------

        public int Count
        {
            get { return _tabs.Count; }
        }

        public TabModel this[int index]
        {
            get { return index >= 0 && index < _tabs.Count ? _tabs[index] : null; }
        }

        public int SelectedIndex
        {
            get { return _selectedIndex; }
            set
            {
                int clamped = _tabs.Count == 0 ? -1 : Math.Max(0, Math.Min(value, _tabs.Count - 1));
                if (clamped == _selectedIndex) return;
                _selectedIndex = clamped;
                EnsureVisible(_selectedIndex);
                Repaint();
            }
        }

        public int IndexOf(TabModel tab)
        {
            return _tabs.IndexOf(tab);
        }

        public void Add(TabModel tab)
        {
            Insert(_tabs.Count, tab);
        }

        public void Insert(int index, TabModel tab)
        {
            if (tab == null) throw new ArgumentNullException("tab");
            index = Math.Max(0, Math.Min(index, _tabs.Count));
            _tabs.Insert(index, tab);
            if (_selectedIndex >= index) _selectedIndex++;
            SyncAnimation();
            RequestParentLayout();
            Repaint();
        }

        public void RemoveAt(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;
            _tabs.RemoveAt(index);
            if (_selectedIndex > index) _selectedIndex--;
            if (_selectedIndex >= _tabs.Count) _selectedIndex = _tabs.Count - 1;
            _hover = TabHit.None;
            SyncAnimation();
            RequestParentLayout();
            Repaint();
        }

        /// <summary>
        /// PreferredWidth just changed, so ask the form to re-place the island. Without this the island keeps
        /// its old width and divides it among the new tab count -- the tabs get narrower until something else
        /// (a resize, a minimize) happens to trigger a layout.
        /// </summary>
        private void RequestParentLayout()
        {
            if (PreferredWidthChanged != null) PreferredWidthChanged(this, EventArgs.Empty);
        }

        public void MoveTab(int from, int to)
        {
            if (from < 0 || from >= _tabs.Count) return;
            to = Math.Max(0, Math.Min(to, _tabs.Count - 1));
            if (from == to) return;
            TabModel tab = _tabs[from];
            _tabs.RemoveAt(from);
            _tabs.Insert(to, tab);
            if (_selectedIndex == from) _selectedIndex = to;
            else if (from < _selectedIndex && to >= _selectedIndex) _selectedIndex--;
            else if (from > _selectedIndex && to <= _selectedIndex) _selectedIndex++;
            Repaint();
        }

        /// <summary>A tab's title or status changed: repaint (and start/stop the "connecting" animation).</summary>
        public void RefreshTab(int index)
        {
            SyncAnimation();
            Repaint();
        }

        public int StripHeight
        {
            get
            {
                EnsureMetrics();
                return _stripHeight;
            }
        }

        private void SyncAnimation()
        {
            bool needed = false;
            for (int i = 0; i < _tabs.Count; i++)
            {
                SessionState status = _tabs[i].Status;
                if (status == SessionState.Connecting || status == SessionState.Checking)
                {
                    needed = true;
                    break;
                }
            }
            if (needed && !_anim.Enabled) _anim.Start();
            else if (!needed && _anim.Enabled) _anim.Stop();
        }

        // ---------------- metrics and layout ----------------

        private void EnsureMetrics()
        {
            int dpi = Dpi.Value;
            if (dpi == _dpi && _stripHeight > 0) return;
            _dpi = dpi;
            float s = dpi / 96f;

            // Only a very thin gap above the tabs: over blank areas it is the window's top resize/drag band,
            // while the gap directly above a tab belongs to that tab (see HitTestTab). Tab height is unchanged.
            // Firefox-style tabs: each one is a free-standing rounded rectangle rather than a Chrome tab
            // fused to the strip, so it wants equal air above and below plus a modest radius. That leaves a
            // 28pt tab in a 36pt strip, 4pt of air above and below: the strip's total height is what costs
            // remote screen area, so the air comes out of the tab rather than being added on top. The shoulder
            // is no longer an interlocking foot, just the gap left between neighbours.
            _topPad = Scale(4, s);
            _tabHeight = Scale(36, s);
            _stripHeight = _topPad + _tabHeight;
            _tabBottomPad = Scale(4, s);
            _radius = Scale(6, s);
            _shoulder = Scale(5, s);
            _slotMax = Scale(240, s);
            _slotMin = Scale(58, s);
            _iconSize = Scale(16, s);
            _closeSize = Scale(16, s);
            _paddingX = Scale(10, s);
            _newTabSize = Scale(28, s);
            _windowButtonWidth = Scale(46, s);
            _dragThreshold = Scale(5, s);
            // How far each side leans in towards the bottom, giving the island its inverted-trapezoid
            // silhouette. The tab area and the window buttons are inset by it so nothing gets clipped.
            _slant = Scale(18, s);
            // Blank room before the window buttons: a place to grab the island that is nowhere near the
            // close button.
            _dragGrip = Scale(22, s);
        }

        private static int Scale(int value, float factor)
        {
            return (int)Math.Round(value * factor);
        }

        private int WindowButtonsLeft
        {
            get
            {
                EnsureMetrics();
                return Width - _slant - _windowButtonWidth * 3;
            }
        }

        private int TabsLeft
        {
            get
            {
                EnsureMetrics();
                return _slant + Scale(2, _dpi / 96f);
            }
        }

        private int TabAreaRight
        {
            get { return WindowButtonsLeft - _dragGrip - _newTabSize - Scale(10, _dpi / 96f); }
        }

        /// <summary>Slot width of one tab, including the shoulder on each side.</summary>
        private int SlotWidth
        {
            get
            {
                EnsureMetrics();
                if (_tabs.Count == 0) return _slotMax;
                int available = Math.Max(_slotMin, TabAreaRight - TabsLeft);
                int slot = (int)Math.Floor((available + (_tabs.Count - 1) * (double)_shoulder) / _tabs.Count);
                return Math.Max(_slotMin, Math.Min(_slotMax, slot));
            }
        }

        private int Stride
        {
            get { return SlotWidth - _shoulder; }
        }

        private int ContentWidth
        {
            get { return Stride * Math.Max(0, _tabs.Count - 1) + SlotWidth; }
        }

        private int MaxScroll
        {
            get { return Math.Max(0, ContentWidth - (TabAreaRight - TabsLeft)); }
        }

        private Rectangle TabRect(int index)
        {
            EnsureMetrics();
            int x = TabsLeft - _scrollOffset + index * Stride;
            // Shorter than the strip: the difference is the gap the tab floats above the strip's bottom edge.
            // Icons, title and close button all centre on this rectangle, so they follow automatically.
            return new Rectangle(x, _topPad, SlotWidth, _tabHeight - _tabBottomPad);
        }

        private Rectangle CloseRect(int index)
        {
            Rectangle tab = TabRect(index);
            int right = tab.Right - _shoulder - _paddingX;
            return new Rectangle(right - _closeSize, tab.Y + (tab.Height - _closeSize) / 2,
                _closeSize, _closeSize);
        }

        private Rectangle IconRect(int index)
        {
            Rectangle tab = TabRect(index);
            int left = tab.X + _shoulder + _paddingX;
            return new Rectangle(left, tab.Y + (tab.Height - _iconSize) / 2, _iconSize, _iconSize);
        }

        private Rectangle NewTabRect()
        {
            EnsureMetrics();
            int x = _tabs.Count == 0
                ? TabsLeft + _shoulder
                : TabRect(_tabs.Count - 1).Right - _shoulder + Scale(4, _dpi / 96f);
            x = Math.Min(x, TabAreaRight + Scale(2, _dpi / 96f));
            x = Math.Max(x, TabsLeft);
            int y = _topPad + (_tabHeight - _newTabSize) / 2;
            return new Rectangle(x, y, _newTabSize, _newTabSize);
        }

        private Rectangle WindowButtonRect(int slot)
        {
            EnsureMetrics();
            return new Rectangle(WindowButtonsLeft + slot * _windowButtonWidth, 0,
                _windowButtonWidth, _stripHeight);
        }

        // Exposed for the self test's geometry checks (and handy later for tab tear-off)
        public Rectangle TabBounds(int index)
        {
            return TabRect(index);
        }

        public Rectangle CloseButtonBounds(int index)
        {
            return CloseRect(index);
        }

        public Rectangle NewTabButtonBounds()
        {
            return NewTabRect();
        }

        public Rectangle WindowButtonBounds(int slot)
        {
            return WindowButtonRect(slot);
        }

        private bool ShowCloseButton(int index)
        {
            Rectangle tab = TabRect(index);
            int inner = tab.Width - _shoulder * 2 - _paddingX * 2;
            if (inner >= _iconSize + _closeSize + Scale(24, _dpi / 96f)) return true;
            // When tabs get narrow, only show the close button on the active or hovered tab
            return index == _selectedIndex || (_hover.Index == index && _hover.Kind != TabHitKind.None);
        }

        private void EnsureVisible(int index)
        {
            if (index < 0 || index >= _tabs.Count || MaxScroll == 0) return;
            int left = index * Stride;
            int right = left + SlotWidth;
            int viewWidth = TabAreaRight - TabsLeft;
            if (_scrollOffset > left) _scrollOffset = left;
            else if (_scrollOffset < right - viewWidth) _scrollOffset = right - viewWidth;
            ClampScroll();
        }

        private void ClampScroll()
        {
            _scrollOffset = Math.Max(0, Math.Min(_scrollOffset, MaxScroll));
        }

        // ---------------- hit testing ----------------

        public TabHit HitTest(Point point)
        {
            EnsureMetrics();

            for (int slot = 0; slot < 3; slot++)
            {
                if (WindowButtonRect(slot).Contains(point)) return new TabHit(WindowButtonKind(slot), -1);
            }

            if (NewTabRect().Contains(point)) return new TabHit(TabHitKind.NewTab, -1);

            // The active tab is on top, so test it first
            if (_selectedIndex >= 0)
            {
                TabHit hit = HitTestTab(_selectedIndex, point);
                if (hit.Kind != TabHitKind.None) return hit;
            }
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (i == _selectedIndex) continue;
                TabHit hit = HitTestTab(i, point);
                if (hit.Kind != TabHitKind.None) return hit;
            }

            // The gap above blank areas still belongs to the window's top edge (resize/drag)
            if (point.Y < _topPad) return new TabHit(TabHitKind.TopEdge, -1);

            return new TabHit(TabHitKind.Empty, -1);
        }

        private TabHit HitTestTab(int index, Point point)
        {
            Rectangle tab = TabRect(index);
            if (tab.Right <= TabsLeft || tab.X >= TabAreaRight + _shoulder) return TabHit.None;

            // Only the tab body counts; the gaps to either side belong to the neighbours or the blank area.
            // Vertically it covers the whole strip, so the thin gaps above and below the floating tab still
            // select it rather than falling through to the window as caption and starting a drag.
            Rectangle body = new Rectangle(tab.X + _shoulder / 2, 0,
                tab.Width - _shoulder, _stripHeight);
            if (!body.Contains(point)) return TabHit.None;
            if (point.X > TabAreaRight + _shoulder) return TabHit.None;

            if (ShowCloseButton(index) && Rectangle.Inflate(CloseRect(index), 2, 2).Contains(point))
                return new TabHit(TabHitKind.TabClose, index);
            return new TabHit(TabHitKind.Tab, index);
        }

        // ---------------- mouse ----------------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_islandDragging)
            {
                if (IslandMoved != null && Owner != null)
                {
                    Point wanted = Owner.PointToClient(
                        new Point(Cursor.Position.X - _islandGrabOffsetX, 0));
                    IslandMoved(this, new IslandMoveEventArgs(wanted.X, false));
                }
                return;
            }

            if (_mouseDownOnTab && !_dragging &&
                Math.Abs(e.X - _mouseDownPoint.X) > _dragThreshold)
            {
                _dragging = true;
            }

            if (_dragging && _dragIndex >= 0)
            {
                int wouldBeLeft = e.X - _dragGrabOffset - TabsLeft + _scrollOffset;
                int target = (int)Math.Round(wouldBeLeft / (double)Stride);
                target = Math.Max(0, Math.Min(target, _tabs.Count - 1));
                if (target != _dragIndex)
                {
                    int from = _dragIndex;
                    MoveTab(from, target);
                    _dragIndex = target;
                    EventHandler<TabReorderEventArgs> handler = TabsReordered;
                    if (handler != null) handler(this, new TabReorderEventArgs(from, target));
                }
                return;
            }

            TabHit hit = HitTest(e.Location);
            if (hit.Kind != _hover.Kind || hit.Index != _hover.Index)
            {
                _hover = hit;
                // Always keep the default arrow cursor over the strip
                RestartTooltip();
                Repaint();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = TabHit.None;
            HideTooltip();
            Repaint();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            HideTooltip();
            TabHit hit = HitTest(e.Location);

            if (TryFrameGesture(hit, e)) return;

            if (e.Button == MouseButtons.Left && hit.Kind == TabHitKind.Tab)
            {
                if (hit.Index != _selectedIndex) RaiseTabSelected(hit.Index);
                if ((ModifierKeys & Keys.Control) != 0)
                {
                    _mouseDownOnTab = true;          // Ctrl+drag reorders the tabs
                    _dragIndex = hit.Index;
                    _dragGrabOffset = e.X - TabRect(hit.Index).X;
                    _mouseDownPoint = e.Location;
                }
                else
                {
                    // There is no blank grip left on the island, so a tab is the handle: a plain drag slides
                    // the whole island sideways. A click without movement still just selects the tab.
                    BeginIslandDrag(e);
                }
            }
            else if (e.Button == MouseButtons.Middle && hit.Kind == TabHitKind.Tab)
            {
                RaiseCloseRequested(hit.Index);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_islandDragging)
            {
                _islandDragging = false;
                Capture = false;
                if (IslandMoved != null && Owner != null)
                    IslandMoved(this, new IslandMoveEventArgs(Owner.PointToClient(Location).X, true));
                return;
            }

            bool wasDragging = _dragging;
            _mouseDownOnTab = false;
            _dragging = false;
            _dragIndex = -1;

            if (wasDragging || e.Button != MouseButtons.Left) return;

            TabHit hit = HitTest(e.Location);
            switch (hit.Kind)
            {
                case TabHitKind.TabClose:
                    RaiseCloseRequested(hit.Index);
                    break;
                case TabHitKind.NewTab:
                    if (NewTabRequested != null) NewTabRequested(this, EventArgs.Empty);
                    break;
                case TabHitKind.WindowMinimize:
                    RaiseWindowCommand(WindowCommand.Minimize);
                    break;
                case TabHitKind.WindowMaximize:
                    RaiseWindowCommand(WindowCommand.MaximizeOrRestore);
                    break;
                case TabHitKind.WindowClose:
                    RaiseWindowCommand(WindowCommand.Close);
                    break;
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Right) return;
            TabHit hit = HitTest(e.Location);
            if (hit.Kind != TabHitKind.Tab && hit.Kind != TabHitKind.TabClose) return;
            EventHandler<TabContextMenuEventArgs> handler = TabContextMenuRequested;
            if (handler != null)
                handler(this, new TabContextMenuEventArgs(hit.Index, PointToScreen(e.Location)));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (MaxScroll == 0) return;
            _scrollOffset -= Math.Sign(e.Delta) * (SlotWidth / 2);
            ClampScroll();
            Repaint();
        }

        private void RaiseTabSelected(int index)
        {
            _selectedIndex = index;
            EnsureVisible(index);
            Repaint();
            EventHandler<TabIndexEventArgs> handler = TabSelected;
            if (handler != null) handler(this, new TabIndexEventArgs(index));
        }

        private void RaiseCloseRequested(int index)
        {
            EventHandler<TabIndexEventArgs> handler = TabCloseRequested;
            if (handler != null) handler(this, new TabIndexEventArgs(index));
        }

        private void RaiseWindowCommand(WindowCommand command)
        {
            EventHandler<WindowCommandEventArgs> handler = WindowCommandRequested;
            if (handler != null) handler(this, new WindowCommandEventArgs(command));
        }

        // ---------------- tooltips ----------------

        private void RestartTooltip()
        {
            HideTooltip();
            if (_hover.Kind == TabHitKind.Tab && _hover.Index >= 0 &&
                !string.IsNullOrEmpty(_tabs[_hover.Index].Tooltip))
            {
                _tooltipDelay.Stop();
                _tooltipDelay.Start();
            }
        }

        private void OnTooltipDelay(object sender, EventArgs e)
        {
            _tooltipDelay.Stop();
            if (_hover.Kind != TabHitKind.Tab || _hover.Index < 0 || _hover.Index >= _tabs.Count) return;
            Rectangle tab = TabRect(_hover.Index);
            _tooltip.Show(_tabs[_hover.Index].Tooltip, this,
                tab.X + _shoulder, tab.Bottom + 2, 6000);
            _tooltipVisible = true;
        }

        private void HideTooltip()
        {
            _tooltipDelay.Stop();
            if (!_tooltipVisible) return;
            _tooltip.Hide(this);
            _tooltipVisible = false;
        }

        // ---------------- painting ----------------

        /// <summary>
        /// Renders the island into a premultiplied 32bpp surface. Outside the trapezoid stays fully
        /// transparent, the trapezoid gets the background colour at the chosen alpha, and everything drawn on
        /// top of it is opaque -- that split is the entire reason for compositing this ourselves.
        /// </summary>
        public Bitmap RenderToBitmap()
        {
            EnsureMetrics();
            ClampScroll();
            Bitmap bitmap = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                PaintIsland(g);
            }
            return bitmap;
        }

        /// <summary>Re-renders and hands the surface to the compositor; replaces Invalidate for this window.</summary>
        private void Repaint()
        {
            if (!IsHandleCreated || !Visible || Width <= 0 || Height <= 0) return;
            using (Bitmap bitmap = RenderToBitmap())
                Native.PushLayeredSurface(Handle, bitmap);
        }

        private void PaintIsland(Graphics g)
        {
            using (GraphicsPath island = IslandPath())
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(_islandAlpha, Theme.Frame)))
                g.FillPath(brush, island);

            GraphicsState state = g.Save();
            g.IntersectClip(new Rectangle(0, 0, Math.Max(0, TabAreaRight + _shoulder), _stripHeight));

            for (int i = 0; i < _tabs.Count; i++)
            {
                if (i == _selectedIndex) continue;
                DrawTab(g, i, false);
            }
            DrawSeparators(g);
            if (_selectedIndex >= 0 && _selectedIndex < _tabs.Count) DrawTab(g, _selectedIndex, true);

            g.Restore(state);

            DrawNewTabButton(g);
            DrawWindowButtons(g);
        }

        /// <summary>
        /// The inverted trapezoid: wide along the top, leaning in towards the bottom. Per-pixel alpha makes the
        /// slanted sides anti-aliased, unlike the hard-edged region this needed before.
        /// </summary>
        private GraphicsPath IslandPath()
        {
            int slant = Math.Min(_slant, Width / 4);
            GraphicsPath path = new GraphicsPath();
            path.AddPolygon(new Point[]
            {
                new Point(0, 0),
                new Point(Width, 0),
                new Point(Width - slant, Height),
                new Point(slant, Height)
            });
            return path;
        }

        private void DrawSeparators(Graphics g)
        {
            for (int i = 0; i + 1 < _tabs.Count; i++)
            {
                if (i == _selectedIndex || i + 1 == _selectedIndex) continue;
                if (_hover.Index == i || _hover.Index == i + 1) continue;
                Rectangle tab = TabRect(i);
                float x = tab.Right - _shoulder / 2f;
                using (Pen pen = new Pen(Theme.TabSeparator, 1f))
                    g.DrawLine(pen, x, tab.Y + tab.Height * 0.25f, x, tab.Y + tab.Height * 0.75f);
            }
        }

        private void DrawTab(Graphics g, int index, bool active)
        {
            TabModel tab = _tabs[index];
            Rectangle bounds = TabRect(index);
            if (bounds.Right < -_shoulder || bounds.X > Width) return;

            bool hovered = _hover.Index == index &&
                           (_hover.Kind == TabHitKind.Tab || _hover.Kind == TabHitKind.TabClose);

            if (active || hovered)
            {
                using (GraphicsPath path = BuildTabPath(bounds))
                using (SolidBrush brush = new SolidBrush(active ? Theme.ActiveTab : Theme.TabHover))
                    g.FillPath(brush, path);
            }

            // status dot, or the spinner while connecting
            Rectangle icon = IconRect(index);
            if (tab.Status == SessionState.Connecting || tab.Status == SessionState.Checking)
            {
                Draw.DrawSpinner(g, new RectangleF(icon.X + 2, icon.Y + 2, icon.Width - 4, icon.Height - 4),
                    Theme.StatusConnecting, Math.Max(1.6f, _iconSize / 8f), _spinAngle);
            }
            else
            {
                int dot = Math.Max(6, _iconSize / 2);
                Rectangle dotRect = new Rectangle(icon.X + (icon.Width - dot) / 2,
                    icon.Y + (icon.Height - dot) / 2, dot, dot);
                using (SolidBrush brush = new SolidBrush(Theme.StatusColor(tab.Status)))
                    g.FillEllipse(brush, dotRect);
            }

            // title
            bool showClose = ShowCloseButton(index);
            int textLeft = icon.Right + Math.Max(4, _paddingX / 2);
            int textRight = showClose ? CloseRect(index).X - Math.Max(2, _paddingX / 3)
                                      : bounds.Right - _shoulder - _paddingX;
            if (textRight - textLeft > Math.Max(12, _iconSize))
            {
                Rectangle textRect = new Rectangle(textLeft, bounds.Y, textRight - textLeft, bounds.Height);
                TextRenderer.DrawText(g, tab.Title, Fonts.Tab, textRect,
                    active ? Theme.Text : Theme.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            if (!showClose) return;

            Rectangle close = CloseRect(index);
            bool closeHover = _hover.Kind == TabHitKind.TabClose && _hover.Index == index;
            if (closeHover)
                Draw.FillRounded(g, new RectangleF(close.X - 3, close.Y - 3, close.Width + 6, close.Height + 6),
                    (close.Width + 6) / 2f, Theme.CloseHover);
            float inset = close.Width * 0.28f;
            Draw.DrawCross(g, new RectangleF(close.X + inset, close.Y + inset,
                    close.Width - inset * 2, close.Height - inset * 2),
                closeHover ? Theme.Text : Theme.TextDim, Math.Max(1.3f, _closeSize / 12f));
        }

        /// <summary>
        /// The Firefox tab shape: a free-standing rounded rectangle. Unlike Chrome's, it is not fused to the
        /// strip -- there is no concave foot, and the shoulder is simply the gap left to its neighbours, so
        /// the body spans bounds.Width - 2 * shoulder.
        /// </summary>
        private GraphicsPath BuildTabPath(Rectangle bounds)
        {
            float r = Math.Min(_radius, Math.Min(bounds.Width / 2f - _shoulder, bounds.Height / 2f));
            RectangleF body = new RectangleF(bounds.Left + _shoulder, bounds.Top,
                Math.Max(1f, bounds.Width - _shoulder * 2f), bounds.Height);

            GraphicsPath path = new GraphicsPath();
            if (r <= 0.5f)
            {
                path.AddRectangle(body);
                return path;
            }
            float d = r * 2f;
            path.AddArc(body.Left, body.Top, d, d, 180f, 90f);                    // top-left
            path.AddArc(body.Right - d, body.Top, d, d, 270f, 90f);               // top-right
            path.AddArc(body.Right - d, body.Bottom - d, d, d, 0f, 90f);          // bottom-right
            path.AddArc(body.Left, body.Bottom - d, d, d, 90f, 90f);              // bottom-left
            path.CloseFigure();
            return path;
        }

        private void DrawNewTabButton(Graphics g)
        {
            Rectangle box = NewTabRect();
            bool hovered = _hover.Kind == TabHitKind.NewTab;
            if (hovered)
                Draw.FillRounded(g, box, Math.Max(4, box.Width / 4f), Theme.TabHover);
            float inset = box.Width * 0.3f;
            Draw.DrawPlus(g, new RectangleF(box.X + inset, box.Y + inset,
                    box.Width - inset * 2, box.Height - inset * 2),
                hovered ? Theme.Text : Theme.TextDim, Math.Max(1.4f, box.Width / 16f));
        }

        private static TabHitKind WindowButtonKind(int slot)
        {
            return slot == 0 ? TabHitKind.WindowMinimize
                : (slot == 1 ? TabHitKind.WindowMaximize : TabHitKind.WindowClose);
        }

        private void DrawWindowButtons(Graphics g)
        {
            for (int slot = 0; slot < 3; slot++)
            {
                TabHitKind kind = WindowButtonKind(slot);
                Rectangle box = WindowButtonRect(slot);
                bool hovered = _hover.Kind == kind;

                if (hovered)
                {
                    Color background = kind == TabHitKind.WindowClose
                        ? Theme.WindowCloseHover : Theme.WindowButtonHover;
                    using (SolidBrush brush = new SolidBrush(background))
                        g.FillRectangle(brush, box);
                }

                Color foreground = hovered && kind == TabHitKind.WindowClose ? Color.White : Theme.TextDim;
                float glyph = Math.Max(8f, _windowButtonWidth * 0.22f);
                float cx = box.X + box.Width / 2f;
                float cy = box.Y + box.Height / 2f;
                float half = glyph / 2f;
                float width = Math.Max(1f, _dpi / 96f);

                if (kind == TabHitKind.WindowMinimize)
                {
                    using (Pen pen = new Pen(foreground, width))
                        g.DrawLine(pen, cx - half, cy, cx + half, cy);
                }
                else if (kind == TabHitKind.WindowMaximize)
                {
                    // The island is its own window now, so the state to reflect is the owner's, not ours.
                    bool maximized = Owner != null && Owner.WindowState == FormWindowState.Maximized;
                    using (Pen pen = new Pen(foreground, width))
                    {
                        if (maximized)
                        {
                            g.DrawRectangle(pen, cx - half, cy - half + 2f, glyph - 2f, glyph - 2f);
                            g.DrawLine(pen, cx - half + 2f, cy - half, cx + half, cy - half);
                            g.DrawLine(pen, cx + half, cy - half, cx + half, cy + half - 2f);
                        }
                        else
                        {
                            g.DrawRectangle(pen, cx - half, cy - half, glyph, glyph);
                        }
                    }
                }
                else
                {
                    Draw.DrawCross(g, new RectangleF(cx - half, cy - half, glyph, glyph), foreground, width);
                }
            }
        }

        /// <summary>
        /// Overlay mode: the strip floats over the session, translucent, instead of taking a band of its own.
        /// </summary>
        /// <summary>
        /// Width the floating island asks for: a comfortable slot per tab, plus the "+" button and the window
        /// buttons. MainForm centres the strip at this width, clamped to the window; once clamped, SlotWidth
        /// squeezes the tabs, so the island grows with the tab count and then stops at the window edge.
        /// </summary>
        public int PreferredWidth
        {
            get
            {
                EnsureMetrics();
                float s = _dpi / 96f;
                int slot = _slotMax;
                int tabs = _tabs.Count <= 1 ? slot : slot + (_tabs.Count - 1) * (slot - _shoulder);
                return TabsLeft + tabs + Scale(10, s) + _newTabSize + _dragGrip +
                       _windowButtonWidth * 3 + _slant;
            }
        }

        /// <summary>
        /// The island floats over the session, so it is translucent and the remote picture shows through it.
        /// A uniform alpha is all a layered child window offers, which suits an island: everything it covers is
        /// its own content, so there are no chroma-keyed edges to fringe.
        /// </summary>
        private const byte DefaultIslandAlpha = 235;   // 92%

        private byte _islandAlpha = DefaultIslandAlpha;

        /// <summary>Only the background fill takes this alpha; labels and glyphs stay opaque.</summary>
        public void SetOpacityPercent(int percent)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            _islandAlpha = (byte)Math.Round(percent * 255 / 100.0);
            Repaint();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // NOACTIVATE so clicking the island never pulls focus off the remote session; TOOLWINDOW keeps
                // it out of the taskbar and out of Alt+Tab.
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Repaint();
        }


        /// <summary>True while the user is mid-gesture, so auto-hide knows to stay put.</summary>
        public bool IsBusy
        {
            get { return _dragging || _mouseDownOnTab; }
        }

        private void BeginIslandDrag(MouseEventArgs e)
        {
            _islandDragging = true;
            // Absolute anchoring: remember where inside the island the cursor grabbed it and afterwards drive
            // the position straight from the cursor. Accumulating per-move deltas wobbles, because each move
            // shifts the very coordinate system the next delta is measured in.
            _islandGrabOffsetX = Cursor.Position.X - PointToScreen(Point.Empty).X;
            Capture = true;
        }

        /// <summary>True while the island is being dragged, so the form leaves its position alone.</summary>
        public bool IsDraggingIsland
        {
            get { return _islandDragging; }
        }

        /// <summary>In overlay mode the blank strip area has to drag or resize the frame explicitly.</summary>
        private bool TryFrameGesture(TabHit hit, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return false;
            if (hit.Kind != TabHitKind.Empty && hit.Kind != TabHitKind.TopEdge) return false;

            Form form = Owner;
            if (form == null) return false;

            // Has to be decided before the drag starts: BeginFrameDrag does not return until the drag loop
            // ends, so the second click of a double-click would only ever start another drag.
            if (e.Clicks >= 2 && hit.Kind == TabHitKind.Empty)
            {
                RaiseWindowCommand(WindowCommand.MaximizeOrRestore);
                return true;
            }

            // Plain drag slides the island along the top; the island's blank area is the only grip the
            // frameless window has left, so Shift still hands the drag to the frame itself.
            if (hit.Kind == TabHitKind.Empty && (ModifierKeys & Keys.Shift) == 0)
            {
                BeginIslandDrag(e);
                return true;
            }

            Point screen = PointToScreen(e.Location);
            Native.BeginFrameDrag(form.Handle,
                hit.Kind == TabHitKind.TopEdge ? Native.HTTOP : Native.HTCAPTION,
                screen.X, screen.Y);
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _anim.Stop();
                _anim.Dispose();
                _tooltipDelay.Stop();
                _tooltipDelay.Dispose();
                _tooltip.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class TabIndexEventArgs : EventArgs
    {
        public readonly int Index;

        public TabIndexEventArgs(int index)
        {
            Index = index;
        }
    }

    internal sealed class TabReorderEventArgs : EventArgs
    {
        public readonly int FromIndex;
        public readonly int ToIndex;

        public TabReorderEventArgs(int from, int to)
        {
            FromIndex = from;
            ToIndex = to;
        }
    }

    internal sealed class TabContextMenuEventArgs : EventArgs
    {
        public readonly int Index;
        public readonly Point ScreenLocation;

        public TabContextMenuEventArgs(int index, Point screenLocation)
        {
            Index = index;
            ScreenLocation = screenLocation;
        }
    }

    /// <summary>
    /// Where the dragged island wants its left edge, in the parent's client coordinates. Absolute rather than a
    /// delta: deltas accumulate rounding and fight the layout, which shows up as the island wobbling.
    /// </summary>
    internal sealed class IslandMoveEventArgs : EventArgs
    {
        public readonly int DesiredLeft;
        public readonly bool Final;

        public IslandMoveEventArgs(int desiredLeft, bool final)
        {
            DesiredLeft = desiredLeft;
            Final = final;
        }
    }

    internal sealed class WindowCommandEventArgs : EventArgs
    {
        public readonly WindowCommand Command;

        public WindowCommandEventArgs(WindowCommand command)
        {
            Command = command;
        }
    }
}
