using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ZipPaster.Data;
using ZipPaster.Interop;

namespace ZipPaster;

/// <summary>
/// Diagnostic run (<c>ZipPaster.exe --selftest [resultFile]</c>) that exercises
/// the paste pipeline end to end against a real, separate process.
/// </summary>
/// <remarks>
/// Pasting cannot be covered by ordinary unit tests: it depends on live keyboard
/// state, clipboard ownership, and Windows' UIPI rules, all of which only matter
/// across a process boundary. The target is a window this app launches itself
/// (see <see cref="PasteTarget"/>) rather than a real application, so synthetic
/// keystrokes can never reach the user's own documents.
/// </remarks>
internal static partial class SelfTest
{
    private static readonly StringBuilder Log = new();

    /// <summary>
    /// Takes the foreground even when another application currently owns it.
    /// </summary>
    /// <remarks>
    /// Windows refuses SetForegroundWindow from a process that is not already in
    /// the foreground, which otherwise makes this test fail purely because of
    /// whatever happens to be on screen (a dialog, a browser prompt). Attaching
    /// our input queue to the current foreground thread lifts that restriction.
    /// This is test-only; the app itself never steals focus.
    /// </remarks>
    private static void ForceForeground(nint hwnd)
    {
        nint foreground = NativeMethods.GetForegroundWindow();
        uint foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, 0);
        uint currentThread = NativeMethods.GetCurrentThreadId();

        bool attached = false;
        if (foregroundThread != 0 && foregroundThread != currentThread)
            attached = NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    /// <summary>
    /// Runs the checks inside a real WinForms message loop.
    /// </summary>
    /// <remarks>
    /// Blocking on the task from a bare Main would leave no
    /// <see cref="SynchronizationContext"/>, so every <c>await</c> continuation
    /// would resume on a thread-pool thread and the clipboard's OLE calls would
    /// throw for want of an STA thread. Pumping a hidden form reproduces exactly
    /// the threading model the hotkey handler runs under in the real app.
    /// </remarks>
    public static int Run(string? resultPath)
    {
        int exitCode = 1;

        using var pump = new Form
        {
            ShowInTaskbar = false,
            WindowState = FormWindowState.Minimized,
            FormBorderStyle = FormBorderStyle.None,
            Size = new Size(1, 1),
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
        };

        pump.Shown += async (_, _) =>
        {
            try
            {
                exitCode = await RunAsync(resultPath);
            }
            finally
            {
                pump.Close();
            }
        };

        Application.Run(pump);
        return exitCode;
    }

    private static async Task<int> RunAsync(string? resultPath)
    {
        bool ok = true;

        void Report(string name, bool passed, string detail = "")
        {
            ok &= passed;
            Log.AppendLine($"[{(passed ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? " -- " + detail : "")}");
        }

        // ---- 1. Embedded dataset -----------------------------------------
        ZipCatalog? catalog = null;
        try
        {
            catalog = ZipCatalog.Load();
            Report("dataset loads", catalog.Rows.Count > 39_000,
                $"{catalog.Rows.Count:N0} ZIPs, {catalog.States.Count} states");
        }
        catch (Exception ex)
        {
            Report("dataset loads", false, ex.Message);
        }

        if (catalog is not null)
        {
            var byZip = catalog.Rows.ToDictionary(r => r.Zip, StringComparer.Ordinal);

            foreach ((string zip, string expect) in new[]
            {
                ("90210", "Beverly Hills, CA"),
                ("10001", "New York, NY"),
                ("00926", "San Juan, PR"),
                ("96910", "Hagatna, GU"),
            })
            {
                bool found = byZip.TryGetValue(zip, out var row);
                string actual = found ? $"{row!.City}, {row.StateCode}" : "missing";
                Report($"lookup {zip}", found && actual == expect, actual);
            }

            var empty = new HashSet<string>();

            // GeoNames stores "O Fallon"; the user will type "O'Fallon".
            var apostrophe = new ZipFilter(Search: "O'Fallon").Apply(catalog.Rows, empty);
            Report("search ignores apostrophes", apostrophe.Count > 0, $"{apostrophe.Count} match(es)");

            var hyphen = new ZipFilter(Search: "Wilkes-Barre").Apply(catalog.Rows, empty);
            Report("search ignores hyphens", hyphen.Count > 0, $"{hyphen.Count} match(es)");

            var prefix = new ZipFilter(Search: "787").Apply(catalog.Rows, empty);
            Report("numeric search is a ZIP prefix",
                prefix.Count > 0 && prefix.All(r => r.Zip.StartsWith("787", StringComparison.Ordinal)),
                $"{prefix.Count} match(es)");

            var texas = new ZipFilter(StateCode: "TX").Apply(catalog.Rows, empty);
            Report("state filter", texas.Count > 2000 && texas.All(r => r.StateCode == "TX"),
                $"{texas.Count} TX ZIPs");

            var hidden = new ZipFilter(StateCode: "TX", HideUsed: true)
                .Apply(catalog.Rows, new HashSet<string> { texas[0].Zip });
            Report("hide-used filter", hidden.Count == texas.Count - 1,
                $"{texas.Count} -> {hidden.Count}");

            var sorted = new ZipFilter(StateCode: "RI", Sort: ZipSort.City).Apply(catalog.Rows, empty);
            bool ascending = sorted
                .Zip(sorted.Skip(1))
                .All(p => string.Compare(p.First.City, p.Second.City, StringComparison.OrdinalIgnoreCase) <= 0);
            Report("sort by city", ascending, $"{sorted.Count} rows");
        }

        // ---- 2. State persistence ----------------------------------------
        string statePath = Path.Combine(Path.GetTempPath(), $"zippaster-selftest-{Guid.NewGuid():N}.json");
        try
        {
            var state = AppState.Load(statePath);
            var project = state.ActiveProject!;
            state.MarkUsed(project, "12345");
            state.MarkUsed(project, "12345"); // duplicate must not double-count
            state.Save();

            var reloaded = AppState.Load(statePath);
            var reloadedProject = reloaded.ActiveProject!;

            Report("state saves and reloads", reloadedProject.UsedZips.Contains("12345"));
            Report("marking twice is idempotent", reloadedProject.History.Count == 1,
                $"history={reloadedProject.History.Count}");

            string? undone = reloaded.UndoLast(reloadedProject);
            Report("undo removes the mark",
                undone == "12345" && !reloadedProject.UsedZips.Contains("12345"));
            Report("undo on empty history is safe", reloaded.UndoLast(reloadedProject) is null);
        }
        catch (Exception ex)
        {
            Report("state persistence", false, ex.Message);
        }
        finally
        {
            try { File.Delete(statePath); } catch (IOException) { }
        }

        // ---- 3. Real cross-process paste ---------------------------------
        string outputPath = Path.Combine(Path.GetTempPath(), $"zippaster-target-{Guid.NewGuid():N}.txt");
        Process? target = null;

        try
        {
            await File.WriteAllTextAsync(outputPath, "");

            target = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
            }.WithArgs("--pastetarget", outputPath));

            if (target is null)
                throw new InvalidOperationException("could not start the paste target");

            nint hwnd = await WaitForWindowAsync(target, PasteTarget.HandleFilePath(outputPath));
            Report("target window appeared", hwnd != 0);

            bool focused = false;
            for (int attempt = 0; attempt < 10 && !focused; attempt++)
            {
                ForceForeground(hwnd);
                await Task.Delay(400);
                focused = NativeMethods.GetForegroundWindow() == hwnd;
            }

            Report("target took foreground", focused,
                focused ? "" : "could not focus the target; the paste results below are unreliable");

            // Clipboard path.
            const string Probe = "78701";
            await Paster.SendAsync(Probe, PasteMode.Clipboard);
            string readBack = await WaitForContentAsync(outputPath, Probe);
            Report("clipboard paste reached another process", readBack == Probe, $"read back \"{readBack}\"");

            // The user's clipboard must come back exactly as it was.
            const string Sentinel = "zippaster-clipboard-sentinel";
            SetClipboard(Sentinel);
            await ClearTargetAsync();
            await Paster.SendAsync("90210", PasteMode.Clipboard);
            await WaitForContentAsync(outputPath, "90210");
            await Task.Delay(600); // restore happens after the paste settles
            Report("clipboard is restored afterwards", GetClipboard() == Sentinel,
                $"clipboard now \"{GetClipboard()}\"");

            // Typing fallback, including a multi-word value with a space.
            await ClearTargetAsync();
            const string Probe2 = "Beverly Hills";
            await Paster.SendAsync(Probe2, PasteMode.TypeCharacters);
            string typed = await WaitForContentAsync(outputPath, Probe2);
            Report("type-characters fallback works", typed == Probe2, $"read back \"{typed}\"");
        }
        catch (Exception ex)
        {
            Report("cross-process paste", false, ex.ToString());
        }
        finally
        {
            try
            {
                if (target is { HasExited: false })
                    target.Kill();
            }
            catch (Exception) { /* best effort */ }

            try { File.Delete(outputPath); } catch (IOException) { }
            try { File.Delete(PasteTarget.HandleFilePath(outputPath)); } catch (IOException) { }
        }

        Log.AppendLine();
        Log.AppendLine(ok ? "ALL CHECKS PASSED" : "SOME CHECKS FAILED");

        string report = Log.ToString();

        // A WinExe has no console of its own and redirected output does not
        // survive AttachConsole reliably, so the report goes to a file.
        string path = resultPath ?? Path.Combine(Path.GetTempPath(), "zippaster-selftest.txt");
        try
        {
            await File.WriteAllTextAsync(path, report);
        }
        catch (IOException)
        {
            MessageBox.Show(report, "ZipPaster self-test");
        }

        return ok ? 0 : 1;
    }

    private static async Task<nint> WaitForWindowAsync(Process process, string handleFile)
    {
        for (int i = 0; i < 60; i++)
        {
            process.Refresh();
            if (process.HasExited)
                return 0;

            // The target publishes its own handle; prefer it.
            try
            {
                if (File.Exists(handleFile)
                    && nint.TryParse(await File.ReadAllTextAsync(handleFile), out nint published)
                    && published != 0)
                {
                    return published;
                }
            }
            catch (IOException)
            {
                // Mid-write; retry.
            }

            if (process.MainWindowHandle != 0)
                return process.MainWindowHandle;

            await Task.Delay(100);
        }

        return 0;
    }

    /// <summary>
    /// Polls the target's mirror file until it matches, so the test is not tuned
    /// to a fixed sleep on slower machines.
    /// </summary>
    private static async Task<string> WaitForContentAsync(string path, string expected)
    {
        string last = "";

        for (int i = 0; i < 40; i++)
        {
            try
            {
                last = (await File.ReadAllTextAsync(path)).Trim();
                if (last == expected)
                    return last;
            }
            catch (IOException)
            {
                // Target is mid-write.
            }

            await Task.Delay(100);
        }

        return last;
    }

    private static async Task ClearTargetAsync()
    {
        SendCtrlKey(NativeMethods.VK_A);
        await Task.Delay(120);
        SendKey(NativeMethods.VK_DELETE);
        await Task.Delay(200);
    }

    private static string GetClipboard()
    {
        for (int i = 0; i < 10; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch (ExternalException) { Thread.Sleep(100); }
        }
        return "";
    }

    private static void SetClipboard(string text)
    {
        for (int i = 0; i < 10; i++)
        {
            try { Clipboard.SetText(text); return; }
            catch (ExternalException) { Thread.Sleep(100); }
        }
    }

    private static void SendCtrlKey(ushort vk)
    {
        NativeMethods.INPUT[] inputs =
        [
            Key(NativeMethods.VK_CONTROL, false),
            Key(vk, false),
            Key(vk, true),
            Key(NativeMethods.VK_CONTROL, true),
        ];
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void SendKey(ushort vk)
    {
        NativeMethods.INPUT[] inputs = [Key(vk, false), Key(vk, true)];
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static NativeMethods.INPUT Key(ushort vk, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = vk,
                dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0,
            },
        },
    };
}

internal static class ProcessStartInfoExtensions
{
    public static ProcessStartInfo WithArgs(this ProcessStartInfo info, params string[] args)
    {
        foreach (string arg in args)
            info.ArgumentList.Add(arg);

        return info;
    }
}
