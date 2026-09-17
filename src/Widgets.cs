using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RdpTabs
{
    internal static class Draw
    {
        public static GraphicsPath RoundedRect(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float r = Math.Max(0f, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f));
            if (r <= 0.5f)
            {
                path.AddRectangle(bounds);
                return path;
            }
            float d = r * 2f;
            path.AddArc(bounds.Left, bounds.Top, d, d, 180f, 90f);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270f, 90f);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0f, 90f);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        public static void FillRounded(Graphics g, RectangleF bounds, float radius, Color color)
        {
            using (GraphicsPath path = RoundedRect(bounds, radius))
            using (SolidBrush brush = new SolidBrush(color))
                g.FillPath(brush, path);
        }

        public static void DrawRounded(Graphics g, RectangleF bounds, float radius, Color color, float width)
        {
            using (GraphicsPath path = RoundedRect(bounds, radius))
            using (Pen pen = new Pen(color, width))
                g.DrawPath(pen, path);
        }

        /// <summary>Draws an X glyph (shared by the tab close and window close buttons).</summary>
        public static void DrawCross(Graphics g, RectangleF box, Color color, float width)
        {
            using (Pen pen = new Pen(color, width))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, box.Left, box.Top, box.Right, box.Bottom);
                g.DrawLine(pen, box.Right, box.Top, box.Left, box.Bottom);
            }
        }

        public static void DrawPlus(Graphics g, RectangleF box, Color color, float width)
        {
            using (Pen pen = new Pen(color, width))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                float cx = box.Left + box.Width / 2f;
                float cy = box.Top + box.Height / 2f;
                g.DrawLine(pen, box.Left, cy, box.Right, cy);
                g.DrawLine(pen, cx, box.Top, cx, box.Bottom);
            }
        }

        /// <summary>Spinning arc used for the "connecting" state.</summary>
        public static void DrawSpinner(Graphics g, RectangleF box, Color color, float thickness, int angle)
        {
            using (Pen pen = new Pen(color, thickness))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, box, angle, 270f);
            }
        }
    }

    /// <summary>Owner-drawn button. Implements IButtonControl so it can be a dialog's AcceptButton / CancelButton.</summary>
    internal sealed class FlatButton : Control, IButtonControl
    {
        private bool _hover;
        private bool _pressed;
        private bool _isDefault;

        public bool Primary { get; set; }
        public bool Danger { get; set; }

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            Height = 32;
        }

        public DialogResult DialogResult { get; set; }

        public void NotifyDefault(bool value)
        {
            _isDefault = value;
            Invalidate();
        }

        public void PerformClick()
        {
            if (Enabled) OnClick(EventArgs.Empty);
        }

        /// <summary>Sizes the button to fit its text.</summary>
        public void SizeToText(int horizontalPadding)
        {
            Size text = TextRenderer.MeasureText(Text, Font);
            Width = Math.Max(72, text.Width + horizontalPadding * 2);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Form form = FindForm();
            if (form != null && DialogResult != DialogResult.None) form.DialogResult = DialogResult;
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
                Focus();
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _pressed = false;
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Space) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                PerformClick();
                e.Handled = true;
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.PageBackground);

            RectangleF box = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Color background;
            Color foreground;

            if (!Enabled)
            {
                background = Theme.IsDark ? Theme.Panel : Theme.CardHover;
                foreground = Theme.TextDim;
            }
            else if (Danger)
            {
                background = _pressed || _hover ? Theme.StatusError : Color.FromArgb(0x30, Theme.StatusError);
                foreground = _pressed || _hover ? Color.White : Theme.StatusError;
            }
            else if (Primary)
            {
                background = _pressed ? Theme.Accent : (_hover ? Theme.AccentHover : Theme.Accent);
                foreground = Theme.TextOnAccent;
            }
            else
            {
                background = _pressed ? Theme.CardHover : (_hover ? Theme.CardHover : Theme.Card);
                foreground = Theme.Text;
            }

            Draw.FillRounded(g, box, 6f, background);
            if (!Primary && !Danger)
                Draw.DrawRounded(g, box, 6f, Theme.CardBorder, 1f);
            if (_isDefault && Enabled && !Primary)
                Draw.DrawRounded(g, box, 6f, Theme.Accent, 1f);
            if (Focused && Enabled)
                Draw.DrawRounded(g, RectangleF.Inflate(box, -2f, -2f), 4f, Theme.Accent, 1f);

            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), foreground,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Owner-drawn check box: the themed system glyph looks jarring on a dark background.</summary>
    /// <summary>
    /// A plain horizontal slider: track, filled portion, round thumb. Owner-drawn like the rest of the UI so it
    /// follows the palette and the DPI scale instead of the system control's own look.
    /// </summary>
    internal sealed class SliderBar : Control
    {
        private int _minimum;
        private int _maximum = 100;
        private int _value;
        private bool _dragging;
        private bool _hover;

        /// <summary>Fires continuously while dragging, so callers can preview the change.</summary>
        public event EventHandler ValueChanged;

        /// <summary>Fires once the user lets go, which is when a setting is worth saving.</summary>
        public event EventHandler ValueCommitted;

        public SliderBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            Height = Dpi.Scale(28);
        }

        public int Minimum
        {
            get { return _minimum; }
            set { _minimum = value; Value = _value; }
        }

        public int Maximum
        {
            get { return _maximum; }
            set { _maximum = value; Value = _value; }
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int clamped = Math.Max(_minimum, Math.Min(_maximum, value));
                if (clamped == _value) return;
                _value = clamped;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        /// <summary>Sets the value without raising ValueChanged, for loading a stored setting.</summary>
        public void SetValueQuietly(int value)
        {
            _value = Math.Max(_minimum, Math.Min(_maximum, value));
            Invalidate();
        }

        private int ThumbRadius
        {
            get { return Dpi.Scale(7); }
        }

        private Rectangle TrackRect
        {
            get
            {
                int h = Math.Max(Dpi.Scale(4), 2);
                int r = ThumbRadius;
                return new Rectangle(r, (Height - h) / 2, Math.Max(1, Width - r * 2), h);
            }
        }

        private int ValueToX(int value)
        {
            Rectangle track = TrackRect;
            int span = Math.Max(1, _maximum - _minimum);
            return track.Left + (int)Math.Round((value - _minimum) / (double)span * track.Width);
        }

        private int XToValue(int x)
        {
            Rectangle track = TrackRect;
            double t = (x - track.Left) / (double)Math.Max(1, track.Width);
            return _minimum + (int)Math.Round(t * (_maximum - _minimum));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            Capture = true;
            Focus();
            Value = XToValue(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) Value = XToValue(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragging) return;
            _dragging = false;
            Capture = false;
            if (ValueCommitted != null) ValueCommitted(this, EventArgs.Empty);
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
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Left || keyData == Keys.Right) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int step = (e.Modifiers & Keys.Control) != 0 ? 10 : 1;
            if (e.KeyCode == Keys.Left) { Value = _value - step; e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { Value = _value + step; e.Handled = true; }
            else return;
            if (ValueCommitted != null) ValueCommitted(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.PageBackground);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle track = TrackRect;
            int x = ValueToX(_value);
            float radius = track.Height / 2f;

            Draw.FillRounded(g, track, radius, Theme.InputBorder);
            Rectangle filled = new Rectangle(track.Left, track.Top, Math.Max(0, x - track.Left), track.Height);
            if (filled.Width > 0) Draw.FillRounded(g, filled, radius, Theme.Accent);

            int r = ThumbRadius;
            Rectangle thumb = new Rectangle(x - r, Height / 2 - r, r * 2, r * 2);
            using (SolidBrush brush = new SolidBrush(_hover || _dragging ? Theme.AccentHover : Theme.Accent))
                g.FillEllipse(brush, thumb);
            using (Pen pen = new Pen(Theme.PageBackground, Math.Max(1f, Dpi.Scale(2))))
                g.DrawEllipse(pen, thumb);

            if (Focused)
            {
                using (Pen pen = new Pen(Theme.Text, 1f))
                    g.DrawEllipse(pen, Rectangle.Inflate(thumb, 2, 2));
            }
        }
    }

    internal sealed class ThemedCheckBox : CheckBox
    {
        private bool _hover;

        public ThemedCheckBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            ForeColor = Theme.Text;
            Font = Fonts.Body;
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
            Invalidate();
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.PageBackground);

            int size = Math.Max(14, Math.Min(22, (int)Math.Round(Font.Height * 0.95)));
            RectangleF box = new RectangleF(0.5f, (Height - size) / 2f, size, size);

            if (Checked)
            {
                Draw.FillRounded(g, box, 3f, Enabled ? Theme.Accent : Theme.TextDim);
                using (Pen pen = new Pen(Color.White, Math.Max(1.6f, size / 9f)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, new[]
                    {
                        new PointF(box.Left + size * 0.24f, box.Top + size * 0.54f),
                        new PointF(box.Left + size * 0.44f, box.Top + size * 0.72f),
                        new PointF(box.Left + size * 0.76f, box.Top + size * 0.30f)
                    });
                }
            }
            else
            {
                Draw.FillRounded(g, box, 3f, Theme.InputBackground);
                Draw.DrawRounded(g, box, 3f, _hover && Enabled ? Theme.Accent : Theme.InputBorder, 1.2f);
            }

            Rectangle textRect = new Rectangle(size + 8, 0, Math.Max(10, Width - size - 8), Height);
            TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? ForeColor : Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            if (Focused)
                Draw.DrawRounded(g, new RectangleF(textRect.X - 2, 1.5f, textRect.Width, Height - 3f),
                    3f, Theme.Accent, 1f);
        }
    }

    /// <summary>Context-menu colors for the dark theme (WinForms menus are light by default).</summary>
    internal sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Panel; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Panel; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Panel; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Panel; } }
        public override Color MenuBorder { get { return Theme.CardBorder; } }
        public override Color MenuItemBorder { get { return Theme.CardHover; } }
        public override Color MenuItemSelected { get { return Theme.CardHover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.CardHover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.CardHover; } }
        public override Color MenuItemPressedGradientBegin { get { return Theme.CardHover; } }
        public override Color MenuItemPressedGradientEnd { get { return Theme.CardHover; } }
        public override Color SeparatorDark { get { return Theme.CardBorder; } }
        public override Color SeparatorLight { get { return Theme.CardBorder; } }
    }

    internal static class Menus
    {
        /// <summary>Creates a context menu that follows the current theme.</summary>
        public static ContextMenuStrip Create()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = Fonts.Body;
            menu.ShowImageMargin = false;
            if (Theme.IsDark)
            {
                menu.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
                menu.BackColor = Theme.Panel;
                menu.ForeColor = Theme.Text;
            }
            return menu;
        }

        public static ToolStripMenuItem Item(string text, EventHandler onClick)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            if (Theme.IsDark) item.ForeColor = Theme.Text;
            if (onClick != null) item.Click += onClick;
            return item;
        }
    }

    /// <summary>
    /// Dark text box. The WS_BORDER frame is painted light by the system in the non-client area, which looks
    /// wrong on a dark UI, so we repaint the border ourselves after WM_NCPAINT (accent color when focused).
    /// </summary>
    internal class ThemedTextBox : TextBox
    {
        public ThemedTextBox()
        {
            BorderStyle = BorderStyle.FixedSingle;
            BackColor = Theme.InputBackground;
            ForeColor = Theme.Text;
            Font = Fonts.Body;
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == Native.WM_NCPAINT || m.Msg == Native.WM_PAINT) PaintBorder();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            RefreshBorder();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            RefreshBorder();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            BackColor = Enabled ? Theme.InputBackground : Theme.Panel;
            ForeColor = Enabled ? Theme.Text : Theme.TextDim;
            RefreshBorder();
        }

        private void RefreshBorder()
        {
            if (IsHandleCreated)
                Native.RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero,
                    Native.RDW_FRAME | Native.RDW_INVALIDATE);
        }

        private void PaintBorder()
        {
            if (!IsHandleCreated) return;
            IntPtr hdc = Native.GetWindowDC(Handle);
            if (hdc == IntPtr.Zero) return;
            try
            {
                using (Graphics graphics = Graphics.FromHdc(hdc))
                using (Pen pen = new Pen(!Enabled ? Theme.CardBorder
                           : (Focused ? Theme.Accent : Theme.InputBorder)))
                {
                    graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
                }
            }
            finally
            {
                Native.ReleaseDC(Handle, hdc);
            }
        }
    }

    /// <summary>Dark combo box: owner-drawn items plus our own border and chevron (the system paints them light).</summary>
    internal sealed class ThemedComboBox : ComboBox
    {
        public ThemedComboBox()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat;
            DrawMode = DrawMode.OwnerDrawFixed;
            BackColor = Theme.InputBackground;
            ForeColor = Theme.Text;
            Font = Fonts.Body;
            ItemHeight = Fonts.Body.Height + Dpi.Scale(8);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count) return;

            bool highlighted = (e.State & DrawItemState.Selected) != 0 &&
                               (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (SolidBrush brush = new SolidBrush(highlighted ? Theme.CardHover : Theme.InputBackground))
                e.Graphics.FillRectangle(brush, e.Bounds);

            Rectangle text = new Rectangle(e.Bounds.X + Dpi.Scale(4), e.Bounds.Y,
                Math.Max(10, e.Bounds.Width - Dpi.Scale(8)), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, Convert.ToString(Items[e.Index]), Font, text,
                Enabled ? Theme.Text : Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != Native.WM_PAINT) return;

            using (Graphics graphics = Graphics.FromHwnd(Handle))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;

                // Cover the system-drawn drop-down button with our own chevron
                int buttonWidth = Dpi.Scale(18);
                Rectangle button = new Rectangle(Width - buttonWidth - 1, 1, buttonWidth, Height - 2);
                using (SolidBrush brush = new SolidBrush(Enabled ? Theme.InputBackground : Theme.Panel))
                    graphics.FillRectangle(brush, button);

                float centerX = button.X + button.Width / 2f;
                float centerY = Height / 2f;
                float size = Dpi.ScaleF(3.5f);
                using (Pen pen = new Pen(Enabled ? Theme.TextDim : Theme.CardBorder, Dpi.ScaleF(1.4f)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    graphics.DrawLines(pen, new[]
                    {
                        new PointF(centerX - size, centerY - size / 2f),
                        new PointF(centerX, centerY + size / 2f),
                        new PointF(centerX + size, centerY - size / 2f)
                    });
                }

                using (Pen pen = new Pen(Focused ? Theme.Accent : Theme.InputBorder))
                    graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }
    }

    /// <summary>Text box with a native cue banner (EM_SETCUEBANNER) as placeholder text.</summary>
    internal sealed class HintTextBox : TextBox
    {
        private string _hint = string.Empty;

        public string Hint
        {
            get { return _hint; }
            set
            {
                _hint = value ?? string.Empty;
                if (IsHandleCreated) Native.SetCueBanner(Handle, _hint);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_hint.Length > 0) Native.SetCueBanner(Handle, _hint);
        }
    }

    /// <summary>Wraps a borderless input in a rounded frame (WinForms' FixedSingle border looks bad in dark mode).</summary>
    internal sealed class InputFrame : Panel
    {
        private bool _focused;

        public InputFrame()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.InputBackground;
        }

        /// <summary>Hosts the text box and tracks its focus state for the border color.</summary>
        public void Attach(TextBox child, int paddingX, int paddingY)
        {
            child.BackColor = Theme.InputBackground;
            child.ForeColor = Theme.Text;
            child.BorderStyle = BorderStyle.None;
            Controls.Add(child);
            child.GotFocus += (s, e) => { _focused = true; Invalidate(); };
            child.LostFocus += (s, e) => { _focused = false; Invalidate(); };
            Resize += (s, e) => LayoutChild(child, paddingX, paddingY);
            LayoutChild(child, paddingX, paddingY);
        }

        private void LayoutChild(TextBox child, int paddingX, int paddingY)
        {
            int height = Math.Min(child.PreferredHeight, Math.Max(10, Height - paddingY * 2));
            child.SetBounds(paddingX, (Height - height) / 2, Math.Max(10, Width - paddingX * 2), height);
        }

        /// <summary>Re-applies colors after a theme change.</summary>
        public void ApplyTheme()
        {
            BackColor = Theme.InputBackground;
            foreach (Control child in Controls)
            {
                child.BackColor = Theme.InputBackground;
                child.ForeColor = Theme.Text;
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF box = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Draw.FillRounded(g, box, 6f, Theme.InputBackground);
            Draw.DrawRounded(g, box, 6f, _focused ? Theme.Accent : Theme.InputBorder, _focused ? 1.6f : 1f);
        }
    }
}
