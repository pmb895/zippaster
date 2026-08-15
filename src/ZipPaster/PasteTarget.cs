using ZipPaster.UI;

namespace ZipPaster;

/// <summary>
/// A throwaway window used only as a paste destination during
/// <c>--selftest</c> (<c>ZipPaster.exe --pastetarget &lt;outfile&gt;</c>).
/// </summary>
/// <remarks>
/// The self-test needs a *separate process* to paste into, because the bugs it
/// guards against (stuck modifiers, UIPI blocking) only appear across a process
/// boundary. It deliberately does NOT drive a real application such as Notepad:
/// Windows 11 Notepad is single-instance and tabbed, so launching it attaches to
/// the user's existing window and synthetic Ctrl+A/Delete would land in their
/// open documents. This window is owned entirely by the test.
///
/// It mirrors its textbox to <paramref name="outputPath"/> on every change, which
/// is how the parent process reads the result without any IPC machinery.
/// </remarks>
internal static class PasteTarget
{
    /// <summary>Safety net so a stranded target never lingers.</summary>
    private const int WatchdogSeconds = 60;

    /// <summary>Sidecar file where the target publishes its window handle.</summary>
    public static string HandleFilePath(string outputPath) => outputPath + ".hwnd";

    public static int Run(string outputPath)
    {
        var form = new Form
        {
            Text = "ZipPaster self-test target",
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(420, 120),
            TopMost = true,
            Icon = AppIcon.Get(),
        };

        var input = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            Font = new Font("Consolas", 12f),
        };

        input.TextChanged += (_, _) =>
        {
            try
            {
                File.WriteAllText(outputPath, input.Text);
            }
            catch (IOException)
            {
                // The parent may be mid-read; the next change rewrites it.
            }
        };

        form.Controls.Add(input);
        form.Shown += (_, _) =>
        {
            form.Activate();
            input.Focus();

            // Publish the real window handle rather than leaving the parent to
            // infer it: Process.MainWindowHandle is unreliable for windows that
            // are not ordinary taskbar windows, and races window creation.
            try
            {
                File.WriteAllText(HandleFilePath(outputPath), form.Handle.ToString());
            }
            catch (IOException)
            {
                // Parent falls back to MainWindowHandle polling.
            }
        };

        var watchdog = new System.Windows.Forms.Timer { Interval = WatchdogSeconds * 1000 };
        watchdog.Tick += (_, _) => form.Close();
        watchdog.Start();

        Application.Run(form);
        return 0;
    }
}
