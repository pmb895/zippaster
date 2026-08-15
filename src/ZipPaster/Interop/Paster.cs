using System.Runtime.InteropServices;
using static ZipPaster.Interop.NativeMethods;

namespace ZipPaster.Interop;

public enum PasteMode
{
    /// <summary>Put the text on the clipboard and send Ctrl+V. Default.</summary>
    Clipboard,

    /// <summary>Synthesize the characters directly. For sites that block paste.</summary>
    TypeCharacters,
}

/// <summary>
/// Sends text to whatever window currently has focus.
/// </summary>
/// <remarks>
/// This never activates our own window, so the browser stays foreground the whole
/// time. Must be called on an STA thread (the WinForms UI thread) because the
/// clipboard APIs require it.
/// </remarks>
public static class Paster
{
    /// <summary>Let the clipboard settle before the target reads it.</summary>
    private const int ClipboardSettleMs = 60;

    /// <summary>Give the target time to read the clipboard before we restore it.</summary>
    private const int RestoreDelayMs = 400;

    /// <summary>Clipboard access fails transiently while another app holds it open.</summary>
    private const int ClipboardRetries = 10;
    private const int ClipboardRetryDelayMs = 100;

    public static async Task SendAsync(string text, PasteMode mode)
    {
        if (string.IsNullOrEmpty(text))
            return;

        // The user is physically holding the hotkey's modifiers right now. Any
        // input we synthesize is combined with that live keyboard state, so
        // Ctrl+V would actually arrive as Ctrl+Alt+V and do nothing. Every path
        // below has to start from a clean modifier state.
        ReleaseHeldModifiers();

        if (mode == PasteMode.TypeCharacters)
        {
            TypeUnicode(text);
            return;
        }

        string? saved = TryGetClipboardText();

        if (!TrySetClipboardText(text))
        {
            // Could not own the clipboard; typing still gets the job done.
            TypeUnicode(text);
            return;
        }

        await Task.Delay(ClipboardSettleMs).ConfigureAwait(true);
        SendCtrlV();

        // Restore asynchronously so the UI thread keeps pumping messages; the
        // target app needs a moment to actually read the clipboard first.
        await Task.Delay(RestoreDelayMs).ConfigureAwait(true);

        if (saved is not null)
            TrySetClipboardText(saved);
        else
            TryClearClipboard();
    }

    /// <summary>
    /// Sends key-up for every modifier, so synthesized input is not polluted by
    /// the keys the user is still holding from the hotkey itself.
    /// </summary>
    /// <remarks>
    /// Both the generic and the left/right specific codes are released: Windows
    /// tracks them separately and a stale VK_RMENU is enough to break the paste.
    /// Releasing an already-up key is harmless.
    /// </remarks>
    private static void ReleaseHeldModifiers()
    {
        ushort[] modifiers =
        [
            VK_CONTROL, VK_LCONTROL, VK_RCONTROL,
            VK_MENU, VK_LMENU, VK_RMENU,
            VK_SHIFT, VK_LSHIFT, VK_RSHIFT,
            VK_LWIN, VK_RWIN,
        ];

        var inputs = new INPUT[modifiers.Length];
        for (int i = 0; i < modifiers.Length; i++)
            inputs[i] = KeyInput(modifiers[i], keyUp: true);

        Send(inputs);
    }

    private static void SendCtrlV()
    {
        INPUT[] inputs =
        [
            KeyInput(VK_CONTROL, keyUp: false),
            KeyInput(VK_V, keyUp: false),
            KeyInput(VK_V, keyUp: true),
            KeyInput(VK_CONTROL, keyUp: true),
        ];

        Send(inputs);
    }

    /// <summary>
    /// Types text as Unicode input, bypassing the keyboard layout entirely.
    /// </summary>
    private static void TypeUnicode(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);

        foreach (char c in text)
        {
            // A literal newline would submit the form; treat it as a space.
            char ch = c is '\r' or '\n' ? ' ' : c;

            inputs.Add(UnicodeInput(ch, keyUp: false));
            inputs.Add(UnicodeInput(ch, keyUp: true));
        }

        Send([.. inputs]);
    }

    private static INPUT KeyInput(ushort vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = 0,
            },
        },
    };

    private static INPUT UnicodeInput(char c, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = 0,
            },
        },
    };

    private static void Send(INPUT[] inputs)
    {
        if (inputs.Length == 0)
            return;

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

        if (sent != inputs.Length)
        {
            // Most commonly UIPI: the foreground window belongs to an elevated
            // process and we are (correctly) not elevated, so input is blocked.
            throw new PasteBlockedException(
                "Windows blocked the keystrokes. This usually means the target window is "
                + "running as administrator. Either run that app normally, or run ZipPaster "
                + "as administrator too.");
        }
    }

    // ---- Clipboard helpers ------------------------------------------------
    // WinForms' Clipboard throws ExternalException when another process has the
    // clipboard open, which happens routinely. Retry rather than fail the paste.

    private static string? TryGetClipboardText()
    {
        for (int attempt = 0; attempt < ClipboardRetries; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (ExternalException)
            {
                Thread.Sleep(ClipboardRetryDelayMs);
            }
        }

        return null;
    }

    private static bool TrySetClipboardText(string text)
    {
        for (int attempt = 0; attempt < ClipboardRetries; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(ClipboardRetryDelayMs);
            }
        }

        return false;
    }

    private static void TryClearClipboard()
    {
        try
        {
            Clipboard.Clear();
        }
        catch (ExternalException)
        {
            // Leaving our value on the clipboard is a cosmetic problem only.
        }
    }
}

/// <summary>Raised when Windows refuses the synthesized input.</summary>
public sealed class PasteBlockedException(string message) : Exception(message);
