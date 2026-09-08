using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RdpTabs
{
    /// <summary>Overlay drawn on top of the RDP control while the session is not connected: status text plus actions.</summary>
    internal sealed class SessionOverlay : Control
    {
        private const int IconSize = 34;
        private const int MaxTextWidth = 560;

        private readonly FlatButton _primary = new FlatButton();
        private readonly FlatButton _secondary = new FlatButton();
        private readonly System.Windows.Forms.Timer _spin = new System.Windows.Forms.Timer();

        private string _title = string.Empty;
        private string _message = string.Empty;
        private bool _busy;
        private int _angle;

        private Rectangle _iconRect;
        private Rectangle _titleRect;
        private Rectangle _messageRect;

        public event EventHandler PrimaryClicked;
        public event EventHandler SecondaryClicked;

        public SessionOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Overlay;

            _primary.Primary = true;
            _primary.Font = Fonts.Body;
            _primary.Click += delegate
            {
                EventHandler handler = PrimaryClicked;
                if (handler != null) handler(this, EventArgs.Empty);
            };

            _secondary.Font = Fonts.Body;
            _secondary.Click += delegate
            {
                EventHandler handler = SecondaryClicked;
                if (handler != null) handler(this, EventArgs.Empty);
            };

            Controls.Add(_primary);
            Controls.Add(_secondary);

            _spin.Interval = 60;
            _spin.Tick += delegate
            {
                _angle = (_angle + 24) % 360;
                Invalidate(_iconRect);
            };
        }

        public void SetContent(string title, string message, bool busy, string primaryText, string secondaryText)
        {
            _title = title ?? string.Empty;
            _message = message ?? string.Empty;
            _busy = busy;

            _primary.Text = primaryText ?? string.Empty;
            _primary.Visible = !string.IsNullOrEmpty(primaryText);
            _secondary.Text = secondaryText ?? string.Empty;
            _secondary.Visible = !string.IsNullOrEmpty(secondaryText);

            if (busy && Visible) _spin.Start();
            else _spin.Stop();

            Relayout();
            Invalidate();
        }

        /// <summary>Re-applies colors after a theme change.</summary>
        public void ApplyTheme()
        {
            BackColor = Theme.Overlay;
            _primary.Invalidate();
            _secondary.Invalidate();
            Relayout();
            Invalidate();
        }

        public void FocusPrimary()
        {
            if (_primary.Visible && _primary.CanFocus) _primary.Focus();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && _busy) _spin.Start();
            else _spin.Stop();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        private void Relayout()
        {
            if (Width <= 0 || Height <= 0) return;

            int textWidth = Math.Min(MaxTextWidth, Math.Max(160, Width - 80));
            // Give the measured height 2px of slack so descenders are never clipped
            Size titleSize = TextRenderer.MeasureText(_title, Fonts.Title,
                new Size(textWidth, int.MaxValue), TextFlags);
            titleSize.Height += 2;
            Size messageSize = _message.Length == 0
                ? Size.Empty
                : TextRenderer.MeasureText(_message, Fonts.Body, new Size(textWidth, int.MaxValue), TextFlags);
            if (messageSize.Height > 0) messageSize.Height += 2;

            _primary.SizeToText(18);
            _secondary.SizeToText(18);
            int buttonHeight = _primary.Visible || _secondary.Visible ? _primary.Height : 0;

            int gap = 14;
            int total = IconSize + gap + titleSize.Height;
            if (messageSize.Height > 0) total += 10 + messageSize.Height;
            if (buttonHeight > 0) total += 24 + buttonHeight;

            int top = Math.Max(16, (Height - total) / 2);
            int centerX = Width / 2;

            _iconRect = new Rectangle(centerX - IconSize / 2, top, IconSize, IconSize);
            top += IconSize + gap;
            _titleRect = new Rectangle(centerX - textWidth / 2, top, textWidth, titleSize.Height);
            top += titleSize.Height;

            if (messageSize.Height > 0)
            {
                top += 10;
                _messageRect = new Rectangle(centerX - textWidth / 2, top, textWidth, messageSize.Height);
                top += messageSize.Height;
            }
            else
            {
                _messageRect = Rectangle.Empty;
            }

            if (buttonHeight > 0)
            {
                top += 24;
                int spacing = 10;
                int rowWidth = (_primary.Visible ? _primary.Width : 0) +
                               (_secondary.Visible ? _secondary.Width : 0) +
                               (_primary.Visible && _secondary.Visible ? spacing : 0);
                int x = centerX - rowWidth / 2;
                if (_primary.Visible)
                {
                    _primary.Location = new Point(x, top);
                    x += _primary.Width + spacing;
                }
                if (_secondary.Visible) _secondary.Location = new Point(x, top);
            }
        }

        private static TextFormatFlags TextFlags
        {
            get
            {
                return TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter |
                       TextFormatFlags.NoPrefix;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Overlay);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (_busy)
            {
                Draw.DrawSpinner(g, new RectangleF(
                    _iconRect.X + 3, _iconRect.Y + 3, _iconRect.Width - 6, _iconRect.Height - 6),
                    Theme.Accent, 3f, _angle);
            }
            else
            {
                DrawMonitorGlyph(g, _iconRect);
            }

            TextRenderer.DrawText(g, _title, Fonts.Title, _titleRect, Theme.Text, TextFlags);
            if (!_messageRect.IsEmpty)
                TextRenderer.DrawText(g, _message, Fonts.Body, _messageRect, Theme.TextDim, TextFlags);
        }

        /// <summary>A simple monitor glyph.</summary>
        private static void DrawMonitorGlyph(Graphics g, Rectangle box)
        {
            RectangleF screen = new RectangleF(box.X + 1.5f, box.Y + 3.5f, box.Width - 3f, box.Height * 0.66f);
            using (Pen pen = new Pen(Theme.TextDim, 2f))
            {
                using (GraphicsPath path = Draw.RoundedRect(screen, 3f))
                    g.DrawPath(pen, path);
                float centerX = box.X + box.Width / 2f;
                g.DrawLine(pen, centerX, screen.Bottom, centerX, box.Bottom - 3f);
                g.DrawLine(pen, centerX - box.Width * 0.22f, box.Bottom - 3f,
                    centerX + box.Width * 0.22f, box.Bottom - 3f);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _spin.Stop();
                _spin.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
