using System.Diagnostics;
using System.Runtime.InteropServices;
using ZipPaster.Data;
using ZipPaster.UI;

namespace ZipPaster;

internal static partial class Program
{
    /// <summary>Keeps a second launch from fighting the first over hotkeys.</summary>
    private const string MutexName = "Local\\ZipPaster.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Isolated paste destination for --selftest. Must be checked before the
        // single-instance guard, since the test runs it alongside the parent.
        int targetIndex = Array.FindIndex(args, a => a.Equals("--pastetarget", StringComparison.OrdinalIgnoreCase));
        if (targetIndex >= 0 && targetIndex + 1 < args.Length)
            return PasteTarget.Run(args[targetIndex + 1]);

        int selfTestIndex = Array.FindIndex(args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        if (selfTestIndex >= 0)
        {
            string? resultPath = selfTestIndex + 1 < args.Length ? args[selfTestIndex + 1] : null;
            return SelfTest.Run(resultPath);
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "ZipPaster is already running. Look for it in the notification area "
                + "(the arrow near the clock).",
                "ZipPaster",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        try
        {
            var catalog = ZipCatalog.Load();
            var state = AppState.Load();

            Application.Run(new MainForm(catalog, state));
            return 0;
        }
        catch (Exception ex)
        {
            ReportCrash(ex);
            return 1;
        }
    }

    private static void ReportCrash(Exception? ex)
    {
        if (ex is null)
            return;

        string logPath = Path.Combine(AppState.DefaultDirectory, "error.log");

        try
        {
            Directory.CreateDirectory(AppState.DefaultDirectory);
            File.AppendAllText(logPath, $"[{DateTimeOffset.Now:O}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Reporting must never itself crash the handler.
        }

        MessageBox.Show(
            $"ZipPaster hit an unexpected error:{Environment.NewLine}{Environment.NewLine}{ex.Message}"
            + $"{Environment.NewLine}{Environment.NewLine}Details were written to:{Environment.NewLine}{logPath}",
            "ZipPaster",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
