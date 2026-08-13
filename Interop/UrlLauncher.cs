using System.Diagnostics;
using System.IO;

namespace Deckhand;

/// <summary>
/// Opens a URL as a new tab in the browser the user is already working in.
///
/// ShellExecute on a URL hands it to the *default* browser, which is the wrong one
/// whenever the focused window is a different browser — the tab opens somewhere the
/// user isn't looking. So when a browser is focused we run that browser's own exe
/// with the URL instead: Chrome, Edge and Firefox all pass it to their already
/// running instance, which opens a tab rather than a second copy of the browser.
///
/// Every launch tries the de-elevated path first, whatever the tile says. The
/// dashboard runs as administrator, and an elevated browser process can't join the
/// user's ordinary browser session — it would refuse to start on a profile already
/// in use, or open a separate elevated window. Neither is a tab.
/// </summary>
internal static class UrlLauncher
{
    /// <summary>
    /// Whether a tile's "path" is a web address rather than a program or folder.
    /// Deliberately only http(s): other schemes are registered handlers that belong
    /// on the ordinary launch path, not in a browser.
    /// </summary>
    public static bool IsUrl(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Opens <paramref name="url"/>, choosing the browser as described on
    /// <see cref="AppEntry.Browser"/>. Throws on failure, like an app launch does.
    /// </summary>
    public static void Open(string url, string? browser, string? extraArgs,
                            ForegroundWatcher.Context? focus, IEnumerable<string> knownBrowsers)
    {
        string? exe = ResolveBrowser(browser, focus, knownBrowsers);

        // With no browser exe to name, the URL itself is the target and the shell
        // picks the default browser. Any extra flags are dropped there: they'd be
        // browser switches, and there's no browser on the command line to take them.
        string target = exe ?? url;
        string? args = exe is null ? null : Arguments(exe, url, extraArgs);

        if (DeElevatedLauncher.TryStart(target, args)) return;

        Process.Start(new ProcessStartInfo(target, args ?? "")
        {
            UseShellExecute = true, // resolves App Paths, so a bare "chrome.exe" works
        });
    }

    /// <summary>
    /// Full path to the browser to use, or null to leave the choice to the shell.
    /// </summary>
    private static string? ResolveBrowser(string? browser, ForegroundWatcher.Context? focus,
                                          IEnumerable<string> knownBrowsers)
    {
        if (string.Equals(browser, "default", StringComparison.OrdinalIgnoreCase)) return null;

        string? name = string.IsNullOrWhiteSpace(browser) ? null : ProcessNames.Strip(browser.Trim());

        if (name is null)
        {
            // Following the focus is only right when what's focused is a browser;
            // from an editor or a shell there is no "respective browser" to mean.
            string? focused = focus?.ProcessName;
            if (focused is null) return null;
            if (!knownBrowsers.Any(b =>
                    string.Equals(ProcessNames.Strip(b), focused, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
            name = focused;
        }

        // The running process is the most reliable source of the exe path — no
        // guessing at Program Files or per-user install locations. When the browser
        // isn't running yet, the bare name is enough for ShellExecute's App Paths
        // lookup, which is how "chrome" resolves from a Run box.
        return ExecutablePath(name) ?? name + ".exe";
    }

    private static string? ExecutablePath(string processName)
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName(processName); }
        catch { return null; }

        try
        {
            foreach (var process in processes)
            {
                // MainModule throws for processes we can't open. The dashboard is
                // elevated so this normally succeeds, but a browser's sandboxed
                // child processes are worth skipping past rather than failing on.
                try
                {
                    if (process.MainModule?.FileName is { Length: > 0 } path) return path;
                }
                catch { /* try the next instance */ }
            }
            return null;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static string Arguments(string exe, string url, string? extraArgs)
    {
        // Quoted so a URL with & or a query string survives cmd's parsing.
        string quoted = $"\"{url}\"";

        // Firefox needs telling: a bare URL on its command line honours the user's
        // "open links in" preference and may make a window. Chrome and Edge always
        // open a tab, and ignore the switch names they don't know.
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "firefox",
                          StringComparison.OrdinalIgnoreCase))
        {
            quoted = $"-new-tab {quoted}";
        }

        // Switches go first: Chrome stops treating arguments as switches once it has
        // seen a URL.
        return string.IsNullOrWhiteSpace(extraArgs) ? quoted : $"{extraArgs.Trim()} {quoted}";
    }
}
