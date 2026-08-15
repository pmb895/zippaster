using Microsoft.Win32;

namespace ZipPaster.UI;

/// <summary>
/// Controls whether ZipPaster launches at sign-in.
/// </summary>
/// <remarks>
/// Uses the per-user Run key rather than a Startup-folder shortcut: it needs no
/// COM interop to create, requires no elevation, and Windows still surfaces it in
/// Task Manager's Startup tab, so the entry stays easy to find and turn off.
/// </remarks>
internal static class StartupShortcut
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ZipPaster";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);

            if (enabled)
            {
                string? exe = Environment.ProcessPath;
                if (exe is null)
                    return;

                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show(
                "Windows would not let ZipPaster change the start-up setting.",
                "ZipPaster",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}
