using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace RdpTabs
{
    /// <summary>
    /// Connection editor. DialogResult.OK = connect (saving first if asked), DialogResult.Yes = save only.
    /// </summary>
    internal sealed class ConnectDialog : Form
    {
        private readonly ConnectionProfile _profile;
        private readonly Panel _content = new Panel();

        private TextBox _host, _port, _user, _domain, _password, _name;
        private TextBox _fixedWidth, _fixedHeight, _gatewayHost;
        private ThemedCheckBox _savePassword, _saveProfile;
        private ThemedCheckBox _redirClipboard, _redirPrinters, _redirDrives, _redirPorts, _redirSmartCards;
        private ThemedCheckBox _audioCapture, _autoReconnect, _bandwidth, _useGateway, _gatewayBypass;
        private ThemedCheckBox _disableAnimations;
        private ComboBox _displayMode, _scale, _colorDepth, _audioMode, _keyboardMode, _performance, _authLevel;

        private int _y;
        private int _labelWidth;
        private int _fieldLeft;
        private int _rowHeight;
        private int _contentWidth;
        private int _pad;

        public ConnectDialog(ConnectionProfile profile, bool alreadySaved)
        {
            _profile = profile.Clone();

            Text = alreadySaved ? "Edit connection" : "New connection";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.PageBackground;
            ForeColor = Theme.Text;
            Font = Fonts.Body;
            AutoScaleMode = AutoScaleMode.None;   // all scaling goes through Dpi.Scale
            // Do not exceed the work area on high DPI; the content panel scrolls anyway
            int maxHeight = Screen.PrimaryScreen.WorkingArea.Height - Sc(60);
            ClientSize = new Size(Sc(600), Math.Min(Sc(680), Math.Max(Sc(420), maxHeight)));

            _pad = Sc(20);
            // Measure the label column instead of hard-coding it: font family, DPI and localization all change text width
            _labelWidth = MaxTextWidth(ColumnLabels) + Sc(12);
            _rowHeight = Math.Max(Sc(26), Fonts.Body.Height + Sc(8));
            _contentWidth = ClientSize.Width - _pad * 2 - SystemInformation.VerticalScrollBarWidth;
            _fieldLeft = _pad + _labelWidth;

            _content.SetBounds(0, 0, ClientSize.Width, ClientSize.Height - Sc(58));
            _content.AutoScroll = true;
            _content.BackColor = Theme.PageBackground;
            Controls.Add(_content);

            BuildGeneral(alreadySaved);
            BuildDisplay();
            BuildLocalResources();
            BuildExperience();
            BuildGateway();
            BuildFooter(alreadySaved);

            UpdateEnabledState();
        }

        public ConnectionProfile Result
        {
            get { return _profile; }
        }

        public bool SaveRequested
        {
            get { return _saveProfile.Checked; }
        }

        /// <summary>Every left-column label, used to compute the column width.</summary>
        private static readonly string[] ColumnLabels =
        {
            "Computer", "User name", "Password", "Display name", "View", "Resolution", "Scale", "Color depth",
            "Redirect", "Remote audio", "Windows keys", "Performance", "Server auth", "Gateway"
        };

        private static int Sc(int value)
        {
            return Dpi.Scale(value);
        }

        private static int TextWidth(string text)
        {
            return TextRenderer.MeasureText(text, Fonts.Body).Width;
        }

        private static int MaxTextWidth(string[] texts)
        {
            int max = 0;
            foreach (string text in texts) max = Math.Max(max, TextWidth(text));
            return max;
        }

        // ---------------- sections ----------------

        private void BuildGeneral(bool alreadySaved)
        {
            _y = _pad;
            Section("General");

            int gap = Sc(8);

            int portLabelWidth = TextWidth("Port") + Sc(4);
            int portWidth = Sc(72);
            _host = Field("Computer", _profile.Host,
                _contentWidth - _labelWidth - portLabelWidth - portWidth - gap * 2);
            Label("Port", _host.Right + gap, _y, portLabelWidth);
            _port = new ThemedTextBox();
            _port.Text = _profile.Port.ToString(CultureInfo.InvariantCulture);
            Place(_port, _host.Right + gap + portLabelWidth + gap, _y, portWidth, _port.PreferredHeight);
            NextRow();

            int domainLabelWidth = TextWidth("Domain") + Sc(4);
            int domainWidth = Sc(150);
            _user = Field("User name", _profile.UserName,
                _contentWidth - _labelWidth - domainLabelWidth - domainWidth - gap * 2);
            Label("Domain", _user.Right + gap, _y, domainLabelWidth);
            _domain = new ThemedTextBox();
            _domain.Text = _profile.Domain;
            Place(_domain, _user.Right + gap + domainLabelWidth + gap, _y,
                domainWidth, _domain.PreferredHeight);
            NextRow();

            _password = Field("Password", _profile.Password, Sc(240));
            _password.UseSystemPasswordChar = true;
            NextRow();

            _savePassword = Check("Save password (encrypted with Windows DPAPI)", _profile.SavePassword);
            Place(_savePassword, _fieldLeft, _y, _contentWidth - _labelWidth, _rowHeight);
            NextRow();

            _name = Field("Display name", _profile.Name, Sc(240));
            NextRow();

            _saveProfile = Check("Add to saved connections", alreadySaved);
            Place(_saveProfile, _fieldLeft, _y, _contentWidth - _labelWidth, _rowHeight);
            NextRow();
        }

        private void BuildDisplay()
        {
            Section("Display");

            _displayMode = Combo(new[]
            {
                "Fit window - remote resolution follows the window (recommended)",
                "Fit window - scale the remote picture",
                "Fixed resolution"
            }, _profile.Display == DisplayMode.Fixed ? 2 : (_profile.DynamicResolution ? 0 : 1));
            LabelFor("View", _displayMode);
            Place(_displayMode, _fieldLeft, _y, _contentWidth - _labelWidth, _displayMode.PreferredHeight);
            _displayMode.SelectedIndexChanged += delegate { UpdateEnabledState(); };
            NextRow();

            Label("Resolution", _pad, _y, _labelWidth);
            int timesWidth = TextWidth("×") + Sc(4);
            _fixedWidth = new ThemedTextBox();
            _fixedWidth.Text = _profile.FixedWidth.ToString(CultureInfo.InvariantCulture);
            Place(_fixedWidth, _fieldLeft, _y, Sc(72), _fixedWidth.PreferredHeight);
            Label("×", _fixedWidth.Right + Sc(6), _y, timesWidth);
            _fixedHeight = new ThemedTextBox();
            _fixedHeight.Text = _profile.FixedHeight.ToString(CultureInfo.InvariantCulture);
            Place(_fixedHeight, _fixedWidth.Right + Sc(12) + timesWidth, _y, Sc(72),
                _fixedHeight.PreferredHeight);
            NextRow();

            _scale = Combo(new[] { "Automatic (follow local DPI)", "100%", "125%", "150%", "175%", "200%" },
                ScaleToIndex(_profile.ScalePercent));
            LabelFor("Scale", _scale);
            Place(_scale, _fieldLeft, _y, Sc(220), _scale.PreferredHeight);
            NextRow();

            _colorDepth = Combo(new[] { "32-bit true color", "24-bit", "16-bit" },
                _profile.ColorDepth >= 32 ? 0 : (_profile.ColorDepth >= 24 ? 1 : 2));
            LabelFor("Color depth", _colorDepth);
            Place(_colorDepth, _fieldLeft, _y, Sc(220), _colorDepth.PreferredHeight);
            NextRow();
        }

        private void BuildLocalResources()
        {
            Section("Local resources");

            Label("Redirect", _pad, _y, _labelWidth);
            _redirClipboard = Check("Clipboard", _profile.RedirectClipboard);
            _redirPrinters = Check("Printers", _profile.RedirectPrinters);
            TwoChecks(_redirClipboard, _redirPrinters);

            _redirDrives = Check("Local drives", _profile.RedirectDrives);
            _redirPorts = Check("Serial / parallel ports", _profile.RedirectPorts);
            TwoChecks(_redirDrives, _redirPorts);

            _redirSmartCards = Check("Smart cards", _profile.RedirectSmartCards);
            _audioCapture = Check("Microphone", _profile.AudioCapture);
            TwoChecks(_redirSmartCards, _audioCapture);

            _audioMode = Combo(new[] { "Play on this computer", "Play on the remote computer", "Do not play" },
                Math.Max(0, Math.Min(2, _profile.AudioMode)));
            LabelFor("Remote audio", _audioMode);
            Place(_audioMode, _fieldLeft, _y, Sc(220), _audioMode.PreferredHeight);
            NextRow();

            _keyboardMode = Combo(new[] { "Keep on this computer", "Send to the remote session", "Only in full screen (default)" },
                Math.Max(0, Math.Min(2, _profile.KeyboardHookMode)));
            LabelFor("Windows keys", _keyboardMode);
            Place(_keyboardMode, _fieldLeft, _y, Sc(220), _keyboardMode.PreferredHeight);
            NextRow();
        }

        private void BuildExperience()
        {
            Section("Experience");

            _performance = Combo(new[] { "Automatic / LAN (full quality)", "Balanced", "Low bandwidth (no visual effects)" },
                Math.Max(0, Math.Min(2, _profile.PerformanceProfile)));
            LabelFor("Performance", _performance);
            Place(_performance, _fieldLeft, _y, Sc(260), _performance.PreferredHeight);
            NextRow();

            _autoReconnect = Check("Reconnect automatically", _profile.AutoReconnect);
            _bandwidth = Check("Detect bandwidth automatically", _profile.BandwidthDetection);
            TwoChecks(_autoReconnect, _bandwidth);

            _disableAnimations = Check("Disable remote animations (menu fades / full-window drag / cursor shadow)",
                _profile.DisableAnimations);
            Place(_disableAnimations, _fieldLeft, _y, _contentWidth - _labelWidth, _rowHeight);
            NextRow();

            _authLevel = Combo(new[] { "Do not authenticate the server", "Require authentication", "Try, and only warn on failure" },
                Math.Max(0, Math.Min(2, _profile.AuthenticationLevel)));
            LabelFor("Server auth", _authLevel);
            Place(_authLevel, _fieldLeft, _y, _contentWidth - _labelWidth, _authLevel.PreferredHeight);
            NextRow();
        }

        private void BuildGateway()
        {
            Section("Remote Desktop Gateway");

            _useGateway = Check("Connect through an RD Gateway", _profile.UseGateway);
            _useGateway.CheckedChanged += delegate { UpdateEnabledState(); };
            Place(_useGateway, _fieldLeft, _y, _contentWidth - _labelWidth, _rowHeight);
            NextRow();

            _gatewayHost = Field("Gateway", _profile.GatewayHost, _contentWidth - _labelWidth);
            NextRow();

            _gatewayBypass = Check("Bypass the gateway for local addresses", _profile.GatewayBypassLocal);
            Place(_gatewayBypass, _fieldLeft, _y, _contentWidth - _labelWidth, _rowHeight);
            NextRow();

            _content.Padding = new Padding(0, 0, 0, Sc(12));
        }

        private void BuildFooter(bool alreadySaved)
        {
            Panel footer = new Panel();
            footer.SetBounds(0, ClientSize.Height - Sc(58), ClientSize.Width, Sc(58));
            footer.BackColor = Theme.Panel;
            Controls.Add(footer);

            Panel line = new Panel();
            line.SetBounds(0, 0, footer.Width, 1);
            line.BackColor = Theme.CardBorder;
            footer.Controls.Add(line);

            FlatButton cancel = new FlatButton();
            cancel.Text = "Cancel";
            cancel.Font = Fonts.Body;
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(footer.Width - _pad - Sc(88), Sc(13), Sc(88), Sc(32));
            footer.Controls.Add(cancel);
            CancelButton = cancel;

            FlatButton connect = new FlatButton();
            connect.Text = "Connect";
            connect.Primary = true;
            connect.Font = Fonts.Body;
            connect.DialogResult = DialogResult.OK;
            connect.SetBounds(cancel.Left - Sc(8) - Sc(96), Sc(13), Sc(96), Sc(32));
            footer.Controls.Add(connect);
            AcceptButton = connect;

            FlatButton saveOnly = new FlatButton();
            saveOnly.Text = alreadySaved ? "Save" : "Save only";
            saveOnly.Font = Fonts.Body;
            saveOnly.DialogResult = DialogResult.Yes;
            saveOnly.SetBounds(_pad, Sc(13), Sc(96), Sc(32));
            footer.Controls.Add(saveOnly);
        }

        // ---------------- layout helpers ----------------

        private void Section(string title)
        {
            if (_y > _pad) _y += Sc(10);
            Label label = MakeLabel(title, Fonts.BodyBold, Theme.Text);
            Place(label, _pad, _y, _contentWidth, _rowHeight);
            _y += _rowHeight;

            Panel line = new Panel();
            line.BackColor = Theme.CardBorder;
            Place(line, _pad, _y, _contentWidth, 1);
            _y += Sc(10);
        }

        private void NextRow()
        {
            _y += _rowHeight + Sc(8);
        }

        private Label Label(string text, int x, int y, int width)
        {
            Label label = MakeLabel(text, Fonts.Body, Theme.TextDim);
            Place(label, x, y, width, _rowHeight);
            return label;
        }

        private void LabelFor(string text, Control field)
        {
            Label(text, _pad, _y, _labelWidth);
        }

        private Label MakeLabel(string text, Font font, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = font;
            label.ForeColor = color;
            label.BackColor = Theme.PageBackground;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.AutoSize = false;
            return label;
        }

        private TextBox Field(string label, string value, int width)
        {
            Label(label, _pad, _y, _labelWidth);
            TextBox box = new ThemedTextBox();
            box.Text = value ?? string.Empty;
            Place(box, _fieldLeft, _y, Math.Max(Sc(60), width), box.PreferredHeight);
            return box;
        }

        private ComboBox Combo(string[] items, int selectedIndex)
        {
            ComboBox combo = new ThemedComboBox();
            combo.Items.AddRange(items);
            combo.SelectedIndex = Math.Max(0, Math.Min(selectedIndex, items.Length - 1));
            return combo;
        }

        private ThemedCheckBox Check(string text, bool value)
        {
            ThemedCheckBox box = new ThemedCheckBox();
            box.Text = text;
            box.Checked = value;
            return box;
        }

        /// <summary>Width a check box actually needs: box plus gap plus text.</summary>
        private int CheckWidth(string text)
        {
            return TextWidth(text) + Fonts.Body.Height + Sc(14);
        }

        /// <summary>Puts two check boxes on one row, or one per row when they do not fit (text width varies).</summary>
        private void TwoChecks(ThemedCheckBox first, ThemedCheckBox second)
        {
            int available = _contentWidth - _labelWidth;
            int firstWidth = CheckWidth(first.Text);
            int secondWidth = CheckWidth(second.Text);
            int gap = Sc(16);

            if (firstWidth + gap + secondWidth <= available)
            {
                int secondLeft = Math.Max(firstWidth + gap, available / 2);
                if (secondLeft + secondWidth > available) secondLeft = firstWidth + gap;
                Place(first, _fieldLeft, _y, firstWidth, _rowHeight);
                Place(second, _fieldLeft + secondLeft, _y, secondWidth, _rowHeight);
                NextRow();
                return;
            }

            Place(first, _fieldLeft, _y, Math.Max(firstWidth, available), _rowHeight);
            NextRow();
            Place(second, _fieldLeft, _y, Math.Max(secondWidth, available), _rowHeight);
            NextRow();
        }

        private void Place(Control control, int x, int y, int width, int height)
        {
            control.SetBounds(x, y, width, height);
            if (control.Parent == null) _content.Controls.Add(control);
        }

        private void UpdateEnabledState()
        {
            bool fixedSize = _displayMode.SelectedIndex == 2;
            _fixedWidth.Enabled = fixedSize;
            _fixedHeight.Enabled = fixedSize;

            bool gateway = _useGateway.Checked;
            _gatewayHost.Enabled = gateway;
            _gatewayBypass.Enabled = gateway;
        }

        // ---------------- validation and write-back ----------------

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            AppIcon.Apply(this);
            if (Theme.IsDark) Native.EnableDarkTitleBar(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (string.IsNullOrEmpty(_host.Text)) _host.Focus();
            else if (string.IsNullOrEmpty(_user.Text)) _user.Focus();
            else _password.Focus();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK || DialogResult == DialogResult.Yes)
            {
                string error;
                if (!TryApply(out error))
                {
                    MessageBox.Show(this, error, "Check your input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    e.Cancel = true;
                    DialogResult = DialogResult.None;
                }
            }
            base.OnFormClosing(e);
        }

        private bool TryApply(out string error)
        {
            error = null;

            string host = _host.Text.Trim();
            if (host.Length == 0)
            {
                error = "Enter a computer name or IP address.";
                _host.Focus();
                return false;
            }

            int port;
            if (!int.TryParse(_port.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) ||
                port < 1 || port > 65535)
            {
                error = "The port must be a whole number between 1 and 65535.";
                _port.Focus();
                return false;
            }

            int width = _profile.FixedWidth, height = _profile.FixedHeight;
            if (_displayMode.SelectedIndex == 2)
            {
                if (!int.TryParse(_fixedWidth.Text.Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out width) || width < 640 || width > 8192)
                {
                    error = "The fixed width must be between 640 and 8192.";
                    _fixedWidth.Focus();
                    return false;
                }
                if (!int.TryParse(_fixedHeight.Text.Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out height) || height < 480 || height > 8192)
                {
                    error = "The fixed height must be between 480 and 8192.";
                    _fixedHeight.Focus();
                    return false;
                }
            }

            if (_useGateway.Checked && _gatewayHost.Text.Trim().Length == 0)
            {
                error = "RD Gateway is enabled, so a gateway address is required.";
                _gatewayHost.Focus();
                return false;
            }

            _profile.Host = host;
            _profile.Port = port;
            _profile.UserName = _user.Text.Trim();
            _profile.Domain = _domain.Text.Trim();
            _profile.Password = _password.Text;
            _profile.SavePassword = _savePassword.Checked;
            _profile.Name = _name.Text.Trim().Length == 0 ? host : _name.Text.Trim();

            switch (_displayMode.SelectedIndex)
            {
                case 0:
                    _profile.Display = DisplayMode.FitWindow;
                    _profile.DynamicResolution = true;
                    break;
                case 1:
                    _profile.Display = DisplayMode.FitWindow;
                    _profile.DynamicResolution = false;
                    break;
                default:
                    _profile.Display = DisplayMode.Fixed;
                    break;
            }
            _profile.FixedWidth = width;
            _profile.FixedHeight = height;
            _profile.ScalePercent = IndexToScale(_scale.SelectedIndex);
            _profile.ColorDepth = _colorDepth.SelectedIndex == 0 ? 32 : (_colorDepth.SelectedIndex == 1 ? 24 : 16);

            _profile.RedirectClipboard = _redirClipboard.Checked;
            _profile.RedirectPrinters = _redirPrinters.Checked;
            _profile.RedirectDrives = _redirDrives.Checked;
            _profile.RedirectPorts = _redirPorts.Checked;
            _profile.RedirectSmartCards = _redirSmartCards.Checked;
            _profile.AudioCapture = _audioCapture.Checked;
            _profile.AudioMode = _audioMode.SelectedIndex;
            _profile.KeyboardHookMode = _keyboardMode.SelectedIndex;

            _profile.PerformanceProfile = _performance.SelectedIndex;
            _profile.AutoReconnect = _autoReconnect.Checked;
            _profile.BandwidthDetection = _bandwidth.Checked;
            _profile.DisableAnimations = _disableAnimations.Checked;
            _profile.AuthenticationLevel = _authLevel.SelectedIndex;

            _profile.UseGateway = _useGateway.Checked;
            _profile.GatewayHost = _gatewayHost.Text.Trim();
            _profile.GatewayBypassLocal = _gatewayBypass.Checked;
            return true;
        }

        private static int ScaleToIndex(int percent)
        {
            switch (percent)
            {
                case 100: return 1;
                case 125: return 2;
                case 150: return 3;
                case 175: return 4;
                case 200: return 5;
                default: return 0;
            }
        }

        private static int IndexToScale(int index)
        {
            switch (index)
            {
                case 1: return 100;
                case 2: return 125;
                case 3: return 150;
                case 4: return 175;
                case 5: return 200;
                default: return 0;
            }
        }
    }
}
