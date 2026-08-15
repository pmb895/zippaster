using static ZipPaster.Interop.NativeMethods;

namespace ZipPaster.Interop;

/// <summary>Which value a hotkey pastes.</summary>
public enum HotkeyAction
{
    Zip,
    City,
    State,
}

/// <summary>A modifier + key combination, stored as a single parseable string.</summary>
public readonly record struct Hotkey(uint Modifiers, uint VirtualKey)
{
    public static readonly Hotkey None = new(0, 0);

    public bool IsSet => VirtualKey != 0;

    /// <summary>Round-trips through settings as e.g. "Ctrl+Alt+Z".</summary>
    public override string ToString()
    {
        if (!IsSet)
            return "(none)";

        var parts = new List<string>(4);
        if ((Modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((Modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((Modifiers & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(((Keys)VirtualKey).ToString());

        return string.Join("+", parts);
    }

    public static Hotkey Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return None;

        uint mods = 0;
        uint vk = 0;

        foreach (string raw in value.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Trim();
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                default:
                    if (Enum.TryParse<Keys>(token, ignoreCase: true, out var key))
                        vk = (uint)key;
                    break;
            }
        }

        return vk == 0 ? None : new Hotkey(mods, vk);
    }

    public static Hotkey FromKeyData(Keys keyData)
    {
        uint mods = 0;
        if (keyData.HasFlag(Keys.Control)) mods |= MOD_CONTROL;
        if (keyData.HasFlag(Keys.Alt)) mods |= MOD_ALT;
        if (keyData.HasFlag(Keys.Shift)) mods |= MOD_SHIFT;

        var key = keyData & Keys.KeyCode;
        return key is Keys.None or Keys.ControlKey or Keys.Menu or Keys.ShiftKey
            ? None
            : new Hotkey(mods, (uint)key);
    }
}

public sealed class HotkeyPressedEventArgs(HotkeyAction action) : EventArgs
{
    public HotkeyAction Action { get; } = action;
}

/// <summary>
/// Owns the system-wide hotkey registrations and raises an event when one fires.
/// </summary>
/// <remarks>
/// Registrations are attached to a dedicated hidden window rather than the main
/// form, so hotkeys keep working no matter what the UI is doing -- including
/// while the main window is hidden to the tray.
/// </remarks>
public sealed class HotkeyManager : IDisposable
{
    private readonly MessageWindow _window;
    private readonly Dictionary<int, HotkeyAction> _registered = [];
    private bool _disposed;

    public HotkeyManager()
    {
        _window = new MessageWindow(OnHotkeyMessage);
    }

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    /// <summary>
    /// Replaces all current registrations. Returns the actions that could not be
    /// registered, which happens when another application already owns the combo.
    /// </summary>
    public IReadOnlyList<HotkeyAction> Register(IReadOnlyDictionary<HotkeyAction, Hotkey> bindings)
    {
        UnregisterAll();

        var failed = new List<HotkeyAction>();

        foreach ((HotkeyAction action, Hotkey hotkey) in bindings)
        {
            if (!hotkey.IsSet)
                continue;

            int id = (int)action + 1;

            // MOD_NOREPEAT stops a held-down combo from firing continuously and
            // burning through the ZIP list.
            if (RegisterHotKey(_window.Handle, id, hotkey.Modifiers | MOD_NOREPEAT, hotkey.VirtualKey))
                _registered[id] = action;
            else
                failed.Add(action);
        }

        return failed;
    }

    public void UnregisterAll()
    {
        foreach (int id in _registered.Keys)
            UnregisterHotKey(_window.Handle, id);

        _registered.Clear();
    }

    private void OnHotkeyMessage(int id)
    {
        if (_registered.TryGetValue(id, out HotkeyAction action))
            HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(action));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        UnregisterAll();
        _window.DestroyHandle();
    }

    /// <summary>Invisible window that exists only to receive WM_HOTKEY.</summary>
    private sealed class MessageWindow : NativeWindow
    {
        private readonly Action<int> _onHotkey;

        public MessageWindow(Action<int> onHotkey)
        {
            _onHotkey = onHotkey;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
                _onHotkey((int)m.WParam);

            base.WndProc(ref m);
        }
    }
}
