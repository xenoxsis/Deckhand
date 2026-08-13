using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Deckhand;

// Going to a program, or starting another copy of it. The wide side of an app tile reads
// the open windows at the moment it is tapped rather than when it was drawn, so it can
// raise the one window, put the picker up for several, or fall back to launching when
// there is nothing to go back to.
public partial class MainWindow
{
    /// <summary>
    /// The wide side of an app tile: back to the program rather than another copy of it.
    /// One window is raised outright, several put the picker up to choose between, and
    /// none means there is nothing to go back to — so it starts the program, which is the
    /// only thing "go to Chrome" can mean when Chrome isn't running.
    ///
    /// The windows are read now rather than when the tile was drawn: a list taken then
    /// would name windows that have since closed and miss every one opened since.
    /// </summary>
    private void ActivateApp(AppEntry app)
    {
        // A URL is a tab in a window that already exists, so it has nothing of its own to
        // go back to and both sides of the tile open the page. Naming processes with
        // "windows" is the way to ask for a browser to be raised instead; left to the
        // lookup below, a URL would be searched for as a process named after its last
        // path segment ("jobs"), which is nothing.
        if (app.Windows.Count == 0 && UrlLauncher.IsUrl(app.Path))
        {
            LaunchApp(app);
            return;
        }

        var windows = PreferFolderWindows(app, WindowCatalog.Of(WindowMatch(app)));

        if (windows.Count == 0)
        {
            _log.Add(LogKind.Panel, $"nothing open for \"{app.Label}\" — starting it");
            LaunchApp(app);
            return;
        }

        // One window is a question with one answer. Skip the card and raise it — which is
        // what the one row on it would have done anyway.
        if (windows.Count == 1)
        {
            RaiseWindow(windows[0].Handle, windows[0].Title);
            return;
        }

        ShowWindowPicker(app, windows);
    }

    // ---- The window picker -------------------------------------------------

    /// <summary>How many windows the picker lists before it stops and says so.</summary>
    private const int MaxWindowRows = 12;

    /// <summary>
    /// Which of the app's windows to go to, a row apiece, frontmost first. Only ever put
    /// up for a real choice: <see cref="ActivateApp"/> deals with none and with one before
    /// getting here, so there is no such thing as an empty card.
    /// </summary>
    private void ShowWindowPicker(AppEntry app, List<WindowCatalog.OpenWindow> windows)
    {
        var rows = new List<(FrameworkElement, int)>();

        foreach (var window in windows.Take(MaxWindowRows))
        {
            var handle = window.Handle;
            string title = window.Title;
            rows.Add((MakeTile(WindowLabel(title), () => RaiseWindow(handle, title)), 1));
        }

        if (windows.Count > rows.Count)
        {
            rows.Add((MakeTile($"…{windows.Count - rows.Count} more", () => { }), 1));
        }

        FillTiles(OverlayList, rows, 1);
        OverlayTitle.Text = $"SWITCH TO A {app.Label.ToUpperInvariant()} WINDOW";

        CloseOpenList(); // a command list open behind the modal would only be in the way
        OverlayRoot.Visibility = Visibility.Visible;
        _log.Add(LogKind.Panel, $"picker open — {windows.Count} window(s) for \"{app.Label}\"");

        // The tablet has to be told, and a local tap publishes nothing by itself. It
        // matters more here than for a dropdown: while this is up, the tiles behind it
        // stop answering, and a page that didn't know would look broken.
        PublishRemote();
    }

    private void CloseWindowPicker(bool publish = true)
    {
        if (OverlayRoot.Visibility != Visibility.Visible) return;

        OverlayRoot.Visibility = Visibility.Collapsed;
        OverlayList.Children.Clear();
        OverlayList.RowDefinitions.Clear();
        _log.Add(LogKind.Panel, "closed the picker");

        if (publish) PublishRemote();
    }

    private void OverlayDismiss_Click(object sender, RoutedEventArgs e) => CloseWindowPicker();

    /// <summary>Anywhere on the dim backdrop dismisses, as a modal ought to.</summary>
    private void OverlayBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        CloseWindowPicker();

    /// <summary>
    /// Stops a tap on the card itself from reaching the backdrop behind it, which would
    /// close the picker while someone was reaching for a window in it.
    /// </summary>
    private void OverlayCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        e.Handled = true;

    /// <summary>
    /// Which processes' windows an app tile goes to. Usually the program itself, but a
    /// launcher isn't always what ends up on screen, and a folder opens in Explorer —
    /// which means that tile goes to any open Explorer window, not only this folder's.
    /// Naming the processes with "windows" is the way to correct either.
    /// </summary>
    private static IReadOnlyCollection<string> WindowMatch(AppEntry app)
    {
        if (app.Windows.Count > 0)
        {
            return app.Windows.Select(ProcessNames.Strip).ToArray();
        }

        string path = app.Path.Trim();
        if (System.IO.Directory.Exists(path)) return new[] { "explorer" };

        string program = ProcessNames.Strip(System.IO.Path.GetFileName(path));
        return Launchers.TryGetValue(program, out string[]? windows) ? windows : new[] { program };
    }

    /// <summary>
    /// A folder tile goes to *its* Explorer window when one is open. Explorer is one
    /// process for every folder, so the process match above finds all of them — but its
    /// window titles name the folder they show, so the list is narrowed to the ones
    /// naming this one. Nothing matching falls back to the full list: a wrong-folder
    /// Explorer window still beats launching another copy of the right one, and the
    /// picker is there to choose from. Tiles that named "windows" themselves are left
    /// alone — an explicit list is not something to second-guess.
    /// </summary>
    private static List<WindowCatalog.OpenWindow> PreferFolderWindows(
        AppEntry app, List<WindowCatalog.OpenWindow> windows)
    {
        if (app.Windows.Count > 0 || windows.Count == 0) return windows;

        string path = app.Path.Trim();
        if (!Directory.Exists(path)) return windows;

        string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (folder.Length == 0) return windows;

        var named = windows
            .Where(w => w.Title.Contains(folder, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return named.Count > 0 ? named : windows;
    }

    /// <summary>
    /// Programs that start something else and exit, so the process holding the windows
    /// never has the name that was launched. "windows" in the config covers the rest.
    /// </summary>
    private static readonly Dictionary<string, string[]> Launchers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["git-bash"] = new[] { "mintty" },
            ["git-cmd"] = new[] { "mintty" },
            ["wt"] = new[] { "WindowsTerminal" },
        };

    /// <summary>
    /// A window title short enough to stay one row tall. Titles carry the document and
    /// the program both — a Chrome tab's title runs well past the width of a tile — and a
    /// row that wrapped to three lines would push the tiles below it off the panel.
    /// </summary>
    private static string WindowLabel(string title)
    {
        string flat = title.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= 44 ? flat : flat[..43].TrimEnd() + "…";
    }

    /// <summary>
    /// Named for what it does rather than as <c>Activate</c>, which on a Window already
    /// means "focus the dashboard" — the one thing this panel must never do.
    /// </summary>
    private void RaiseWindow(IntPtr handle, string title)
    {
        // The picker is done with either way: it named the windows open when it opened,
        // and by now one of them is in front. A no-op when a tile went straight to its
        // single window and never put a card up.
        CloseWindowPicker();

        if (WindowCatalog.Activate(handle))
        {
            _log.Add(LogKind.Action, $"brought \"{WindowLabel(title)}\" to the front");
            return;
        }

        _log.Add(LogKind.Warning, $"couldn't bring \"{WindowLabel(title)}\" to the front — "
                                  + "it has probably closed");
    }

    // ---- Starting another copy ---------------------------------------------

    private void LaunchApp(AppEntry app)
    {
        try
        {
            // A URL is a tab, not a program: it belongs in the browser the user is
            // already in. Read the foreground now rather than using the cached
            // context, since a tap never changes it.
            if (UrlLauncher.IsUrl(app.Path))
            {
                UrlLauncher.Open(app.Path, app.Browser, app.Args,
                                 _watcher?.Current(), _config.Browsers);
                _log.Add(LogKind.Action, $"opened {app.Path}");
                return;
            }

            // Falls through to the normal launch if de-elevation isn't available,
            // which is what happens when the dashboard itself isn't elevated.
            if (app.DeElevate && DeElevatedLauncher.TryStart(app.Path, app.Args))
            {
                _log.Add(LogKind.Action, $"launched {app.Path} as you, not as administrator");
                return;
            }

            // ShellExecute resolves a bare name by itself, but it searches PATH before App
            // Paths — so "code" finds code.cmd, VS Code's CLI shim, in preference to
            // Code.exe. Running a .cmd that way puts a console window on the desktop, and
            // the shim holds it open with a `call` for as long as VS Code is running.
            // Resolved first, "code" is the program, and there's no shim and no window.
            var resolved = LaunchTarget.Of(app.Path);

            Process.Start(new ProcessStartInfo(resolved?.Path ?? app.Path, app.Args ?? "")
            {
                // Still needed for the shapes that have no image to name: a folder, a URL,
                // a document opened by whatever is associated with it.
                UseShellExecute = true,

                // A batch file has to be run by cmd whoever starts it, so the only thing
                // left to do about its console is not show it. Hidden only for that shape:
                // this reaches a GUI program as its nCmdShow, and one that honours it would
                // come up invisible.
                WindowStyle = resolved?.Kind == LaunchTarget.Kind.Script
                    ? ProcessWindowStyle.Hidden
                    : ProcessWindowStyle.Normal,
            });
            _log.Add(LogKind.Action, $"launched {resolved?.Path ?? app.Path}");
        }
        catch (Exception ex)
        {
            _log.Add(LogKind.Warning, $"couldn't launch {app.Path} — {ex.Message}");
            MessageBox.Show($"Couldn't launch \"{app.Label}\":\n{ex.Message}",
                            "Deckhand", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
