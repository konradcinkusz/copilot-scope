using System.Diagnostics;

namespace CopilotScope.Local;

/// <summary>Opens the dashboard in the user's browser, where there is one to open.</summary>
internal static class Browser
{
    /// <summary>
    /// Whether this looks like a desktop session a browser could appear in. Not in CI, not over
    /// SSH (the browser would open on the remote machine, or fail), not on a Linux machine with
    /// no display — and not when output is going somewhere other than a terminal, which is how a
    /// script or a service manager runs this.
    /// </summary>
    public static bool Available()
    {
        if (!Environment.UserInteractive || Console.IsOutputRedirected) return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))) return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CONNECTION"))) return false;
        if (OperatingSystem.IsLinux()
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) return false;
        return true;
    }

    public static bool TryOpen(string url)
    {
        try
        {
            // UseShellExecute hands the URL to the platform's opener: the default browser on
            // Windows, `open` on macOS, xdg-open on Linux.
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
