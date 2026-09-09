using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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
    /// Chrome-style tab strip: shaped tabs, status dots, close buttons, the "+" button and the window buttons.
    /// Blank areas return HTTRANSPARENT so hit-testing falls through to the parent window, which reuses the
    /// native drag / snap / double-click-to-maximize / edge-resize behaviour (see MainForm's WM_NCHITTEST and
    /// WM_NCCALCSIZE).
    /// </summary>
    internal sealed class ChromeTabStrip : Control
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
        private bool _tooltipVisible;

        // Metrics in 96-DPI units, multiplied by the DPI ratio when used
        private int _dpi = 96;
        private int _stripHeight, _topPad, _tabHeight, _radius, _shoulder;
        private int _slotMax, _slotMin, _iconSize, _closeSize, _paddingX, _newTabSize, _windowButtonWidth;
        private int _dragThreshold;

        public event EventHandler<TabIndexEventArgs> TabSelected;
        public event EventHandler<TabIndexEventArgs> TabCloseRequested;
        public event EventHandler NewTabRequested;
        public event EventHandler<TabReorderEventArgs> TabsReordered;
        public event EventHandler<TabContextMenuEventArgs> TabContextMenuRequested;
        public event EventHandler<WindowCommandEventArgs> WindowCommandRequested;

        public ChromeTabStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = Theme.Frame;
            Font = Fonts.Tab;

            _anim.Interval = 80;
            _anim.Tick += delegate
            {
                _spinAngle = (_spinAngle + 30) % 360;
                Invalidate();
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
                Invalidate();
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
            Invalidate();
        }

        public void RemoveAt(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;
            _tabs.RemoveAt(index);
            if (_selectedIndex > index) _selectedIndex--;
            if (_selectedIndex >= _tabs.Count) _selectedIndex = _tabs.Count - 1;
            _hover = TabHit.None;
            SyncAnimation();
            Invalidate();
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
            Invalidate();
        }

        /// <summary>A tab's title or status changed: repaint (and start/stop the "connecting" animation).</summary>
        public void RefreshTab(int index)
        {
            SyncAnimation();
            Invalidate();
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
            _topPad = Scale(2, s);
            _tabHeight = Scale(34, s);
            _stripHeight = _topPad + _tabHeight;
            _radius = Scale(10, s);
            _shoulder = Scale(9, s);
            _slotMax = Scale(240, s);
            _slotMin = Scale(58, s);
            _iconSize = Scale(16, s);
            _closeSize = Scale(16, s);
            _paddingX = Scale(10, s);
            _newTabSize = Scale(28, s);
            _windowButtonWidth = Scale(46, s);
            _dragThreshold = Scale(5, s);
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
                return Width - _windowButtonWidth * 3;
            }
        }

        private int TabsLeft
        {
            get
            {
                EnsureMetrics();
                return Scale(2, _dpi / 96f);
            }
        }

        private int TabAreaRight
        {
            get { return WindowButtonsLeft - _newTabSize - Scale(10, _dpi / 96f); }
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
            return new Rectangle(x, _topPad, SlotWidth, _tabHeight);
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
                if (WindowButtonRect(slot).Contains(point))
                {
                    TabHitKind kind = slot == 0 ? TabHitKind.WindowMinimize
                        : (slot == 1 ? TabHitKind.WindowMaximize : TabHitKind.WindowClose);
                    return new TabHit(kind, -1);
                }
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

            // Only the tab body counts; the shoulders belong to the neighbours / blank area, which feels
            // closer to Chrome. Vertically it starts at the top of the strip so clicking the gap directly
            // above a tab still selects that tab.
            Rectangle body = new Rectangle(tab.X + _shoulder / 2, 0,
                tab.Width - _shoulder, tab.Bottom);
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
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = TabHit.None;
            HideTooltip();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            HideTooltip();
            TabHit hit = HitTest(e.Location);

            if (e.Button == MouseButtons.Left && hit.Kind == TabHitKind.Tab)
            {
                if (hit.Index != _selectedIndex) RaiseTabSelected(hit.Index);
                _mouseDownOnTab = true;
                _dragIndex = hit.Index;
                _dragGrabOffset = e.X - TabRect(hit.Index).X;
                _mouseDownPoint = e.Location;
            }
            else if (e.Button == MouseButtons.Middle && hit.Kind == TabHitKind.Tab)
            {
                RaiseCloseRequested(hit.Index);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
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
            Invalidate();
        }

        private void RaiseTabSelected(int index)
        {
            _selectedIndex = index;
            EnsureVisible(index);
            Invalidate();
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

        protected override void OnPaint(PaintEventArgs e)
        {
            EnsureMetrics();
            ClampScroll();

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Frame);

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

        private void DrawSeparators(Graphics g)
        {
            for (int i = 0; i + 1 < _tabs.Count; i++)
            {
                if (i == _selectedIndex || i + 1 == _selectedIndex) continue;
                if (_hover.Index == i || _hover.Index == i + 1) continue;
                Rectangle tab = TabRect(i);
                float x = tab.Right - _shoulder / 2f;
                using (Pen pen = new Pen(Theme.TabSeparator, 1f))
                    g.DrawLine(pen, x, tab.Y + _tabHeight * 0.25f, x, tab.Y + _tabHeight * 0.75f);
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
        /// The Chrome tab shape: two convex top corners plus a concave shoulder on each side at the bottom.
        /// The body spans bounds.Width - 2 * shoulder; the feet interlock with the neighbouring tabs.
        /// </summary>
        private GraphicsPath BuildTabPath(Rectangle bounds)
        {
            float left = bounds.Left;
            float right = bounds.Right;
            float top = bounds.Top;
            float bottom = bounds.Bottom;
            float r = _radius;
            float s = _shoulder;

            // A circular arc meets the straight edge with an abrupt jump in curvature, which is what reads as
            // a sharp corner. So each top corner is a cubic Bezier that starts Reach * radius away from the
            // vertex and keeps its control points near it: the curve is flatter in the middle and blends into
            // the edges over a longer run. Reach 1 with Pull 0.448 would reproduce a circle exactly.
            const float Reach = 1.15f;
            const float Pull = 0.55f;

            float bodyLeft = left + s;
            float bodyRight = right - s;
            // Never let the two corners meet in the middle of a narrow tab.
            float reach = Math.Min(r * Reach, (bodyRight - bodyLeft) / 2f);
            float pull = reach * (1f - Pull);

            GraphicsPath path = new GraphicsPath();
            // Left foot: a concave quarter circle centred at (left, bottom - s) -- outside the tab, which is
            // what makes it curve away. The centre has to sit there and nowhere else: it is the only position
            // whose tangents are horizontal where the foot meets the strip and vertical where it meets the
            // tab's side, so the flare flows into both instead of hitting them at a right angle.
            path.AddArc(left - s, bottom - s * 2f, s * 2f, s * 2f, 90f, -90f);
            path.AddLine(bodyLeft, bottom - s, bodyLeft, top + reach);
            path.AddBezier(bodyLeft, top + reach, bodyLeft, top + pull,
                           bodyLeft + pull, top, bodyLeft + reach, top);
            path.AddLine(bodyLeft + reach, top, bodyRight - reach, top);
            path.AddBezier(bodyRight - reach, top, bodyRight - pull, top,
                           bodyRight, top + pull, bodyRight, top + reach);
            path.AddLine(bodyRight, top + reach, bodyRight, bottom - s);
            // Right foot, mirrored: centred at (right, bottom - s)
            path.AddArc(right - s, bottom - s * 2f, s * 2f, s * 2f, 180f, -90f);
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

        private void DrawWindowButtons(Graphics g)
        {
            for (int slot = 0; slot < 3; slot++)
            {
                Rectangle box = WindowButtonRect(slot);
                TabHitKind kind = slot == 0 ? TabHitKind.WindowMinimize
                    : (slot == 1 ? TabHitKind.WindowMaximize : TabHitKind.WindowClose);
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

                using (Pen pen = new Pen(foreground, Math.Max(1f, _dpi / 96f)))
                {
                    switch (kind)
                    {
                        case TabHitKind.WindowMinimize:
                            g.DrawLine(pen, cx - half, cy, cx + half, cy);
                            break;
                        case TabHitKind.WindowMaximize:
                            Form form = FindForm();
                            if (form != null && form.WindowState == FormWindowState.Maximized)
                            {
                                // restore: two offset rectangles
                                g.DrawRectangle(pen, cx - half, cy - half + 2f, glyph - 2f, glyph - 2f);
                                g.DrawLine(pen, cx - half + 2f, cy - half, cx + half, cy - half);
                                g.DrawLine(pen, cx + half, cy - half, cx + half, cy + half - 2f);
                            }
                            else
                            {
                                g.DrawRectangle(pen, cx - half, cy - half, glyph, glyph);
                            }
                            break;
                        case TabHitKind.WindowClose:
                            Draw.DrawCross(g, new RectangleF(cx - half, cy - half, glyph, glyph),
                                foreground, Math.Max(1f, _dpi / 96f));
                            break;
                    }
                }
            }
        }

        // ---------------- turn blank areas into window caption ----------------

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_NCHITTEST)
            {
                Point screen = new Point(Native.LoWord(m.LParam), Native.HiWord(m.LParam));
                TabHit hit = HitTest(PointToClient(screen));
                // HTTRANSPARENT: hit-testing falls through to the parent, which returns HTCAPTION / HTTOP
                m.Result = hit.Kind == TabHitKind.Empty || hit.Kind == TabHitKind.TopEdge
                    ? (IntPtr)Native.HTTRANSPARENT
                    : (IntPtr)Native.HTCLIENT;
                return;
            }
            base.WndProc(ref m);
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

    internal sealed class WindowCommandEventArgs : EventArgs
    {
        public readonly WindowCommand Command;

        public WindowCommandEventArgs(WindowCommand command)
        {
            Command = command;
        }
    }
}
