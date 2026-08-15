using ZipPaster.Data;
using ZipPaster.Interop;

namespace ZipPaster.UI;

public sealed class SettingsForm : Form
{
    private readonly Settings _settings;

    private readonly HotkeyBox _zipBox = new();
    private readonly HotkeyBox _cityBox = new();
    private readonly HotkeyBox _stateBox = new();

    private readonly RadioButton _stateAbbrev = new() { Text = "Abbreviation (TX)" };
    private readonly RadioButton _stateFull = new() { Text = "Full name (Texas)" };

    private readonly RadioButton _modeClipboard = new() { Text = "Paste via clipboard (Ctrl+V)" };
    private readonly RadioButton _modeType = new() { Text = "Type the characters" };

    private readonly CheckBox _autoAdvance = new() { Text = "Mark ZIP used and move to the next one after pasting" };
    private readonly CheckBox _minimizeToTray = new() { Text = "Closing the window keeps ZipPaster running in the tray" };
    private readonly CheckBox _notifyOnPaste = new() { Text = "Show a notification on every paste" };
    private readonly CheckBox _runAtStartup = new() { Text = "Start ZipPaster when I sign in to Windows" };

    public SettingsForm(Settings settings)
    {
        _settings = settings;
        BuildUi();
        LoadFrom(settings);
    }

    private void BuildUi()
    {
        Text = "ZipPaster Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(460, 500);
        Icon = AppIcon.Get();

        int y = 12;

        Label Header(string text)
        {
            var label = new Label
            {
                Text = text,
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(14, y),
            };
            y += 24;
            return label;
        }

        Label Field(string text, Control control)
        {
            var label = new Label { Text = text, AutoSize = true, Location = new Point(28, y + 4) };
            control.Location = new Point(150, y);
            control.Width = 280;
            y += 30;
            return label;
        }

        Controls.Add(Header("Hotkeys"));
        Controls.Add(Field("Paste ZIP:", _zipBox));
        Controls.Add(_zipBox);
        Controls.Add(Field("Paste City:", _cityBox));
        Controls.Add(_cityBox);
        Controls.Add(Field("Paste State:", _stateBox));
        Controls.Add(_stateBox);

        var hint = new Label
        {
            Text = "Click a box and press the combination you want. Backspace clears it.",
            ForeColor = SystemColors.GrayText,
            AutoSize = false,
            Location = new Point(28, y),
            Size = new Size(410, 32),
        };
        Controls.Add(hint);
        y += 40;

        Controls.Add(Header("Paste the state as"));
        _stateAbbrev.Location = new Point(28, y); _stateAbbrev.AutoSize = true; y += 24;
        _stateFull.Location = new Point(28, y); _stateFull.AutoSize = true; y += 32;
        Controls.Add(_stateAbbrev);
        Controls.Add(_stateFull);

        Controls.Add(Header("How to send text"));
        _modeClipboard.Location = new Point(28, y); _modeClipboard.AutoSize = true; y += 24;
        _modeType.Location = new Point(28, y); _modeType.AutoSize = true; y += 24;

        var modeHint = new Label
        {
            Text = "Use \"type the characters\" only if a site ignores a normal paste.",
            ForeColor = SystemColors.GrayText,
            AutoSize = false,
            Location = new Point(28, y),
            Size = new Size(410, 18),
        };
        y += 30;
        Controls.Add(_modeClipboard);
        Controls.Add(_modeType);
        Controls.Add(modeHint);

        Controls.Add(Header("Behaviour"));
        foreach (var box in new[] { _autoAdvance, _minimizeToTray, _notifyOnPaste, _runAtStartup })
        {
            box.Location = new Point(28, y);
            box.AutoSize = true;
            Controls.Add(box);
            y += 24;
        }

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(268, ClientSize.Height - 40),
            Width = 84,
        };
        ok.Click += (_, _) => SaveTo(_settings);

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(360, ClientSize.Height - 40),
            Width = 84,
        };

        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void LoadFrom(Settings s)
    {
        _zipBox.Hotkey = Hotkey.Parse(s.ZipHotkey);
        _cityBox.Hotkey = Hotkey.Parse(s.CityHotkey);
        _stateBox.Hotkey = Hotkey.Parse(s.StateHotkey);

        _stateAbbrev.Checked = s.StateAsAbbreviation;
        _stateFull.Checked = !s.StateAsAbbreviation;

        _modeClipboard.Checked = s.PasteMode == PasteMode.Clipboard;
        _modeType.Checked = s.PasteMode == PasteMode.TypeCharacters;

        _autoAdvance.Checked = s.AutoAdvance;
        _minimizeToTray.Checked = s.MinimizeToTray;
        _notifyOnPaste.Checked = s.NotifyOnPaste;
        _runAtStartup.Checked = StartupShortcut.IsEnabled();
    }

    private void SaveTo(Settings s)
    {
        s.ZipHotkey = _zipBox.Hotkey.ToString();
        s.CityHotkey = _cityBox.Hotkey.ToString();
        s.StateHotkey = _stateBox.Hotkey.ToString();

        s.StateAsAbbreviation = _stateAbbrev.Checked;
        s.PasteMode = _modeType.Checked ? PasteMode.TypeCharacters : PasteMode.Clipboard;

        s.AutoAdvance = _autoAdvance.Checked;
        s.MinimizeToTray = _minimizeToTray.Checked;
        s.NotifyOnPaste = _notifyOnPaste.Checked;

        StartupShortcut.SetEnabled(_runAtStartup.Checked);
    }

    /// <summary>A read-only textbox that captures the next key combination pressed.</summary>
    private sealed class HotkeyBox : TextBox
    {
        private Hotkey _hotkey = Hotkey.None;

        public HotkeyBox()
        {
            ReadOnly = true;
            Cursor = Cursors.Hand;
            TextAlign = HorizontalAlignment.Center;
        }

        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Hotkey Hotkey
        {
            get => _hotkey;
            set
            {
                _hotkey = value;
                Text = value.ToString();
            }
        }

        protected override bool IsInputKey(Keys keyData) => true;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;

            if (e.KeyCode == Keys.Back)
            {
                Hotkey = Hotkey.None;
                return;
            }

            var candidate = Hotkey.FromKeyData(e.KeyData);

            // A bare letter would hijack normal typing system-wide, so require
            // at least one modifier.
            if (candidate.IsSet && candidate.Modifiers != 0)
                Hotkey = candidate;
        }
    }
}
