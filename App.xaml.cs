using System.Windows;

namespace Deckhand;

public partial class App : Application
{
    /// <summary>
    /// Asks where the panel is going to be used, then builds only what that answer
    /// needs: the always-on-top panel on this screen, or a small ordinary window and an
    /// HTTP server for a tablet's browser. Local mode is the app exactly as it was
    /// before there was a remote side — nothing listens, no port is opened, and the
    /// token is never read.
    ///
    /// The question is only asked when there's something to choose between: with
    /// <see cref="RemoteSettings.Enabled"/> false this is a panel and only a panel, and
    /// starting with --local or --remote answers it in advance, which is what a pinned
    /// shortcut wants.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The chooser opens and closes before anything else exists, and in remote mode
        // the panel is never shown at all. Neither must be read as "the last window
        // closed, so quit" — quitting is the panel's ✕, or closing the remote window.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // --check reads the config, reports everything wrong with it, and exits without
        // starting any of the rest — for editing the file on a machine where the panel
        // isn't running, and for anything scripted that wants a yes or a no.
        if (e.Args.Any(a => a.Equals("--check", StringComparison.OrdinalIgnoreCase)))
        {
            CheckConfig();
            return;
        }

        // Only the remote block is wanted here. MainWindow reads the file itself — it
        // has to, for ↻ — and it's the one that reports anything wrong with it.
        var remote = DashboardConfig.Load(out _).Remote;

        // Closing the chooser without picking means "don't start", not "start the
        // default": the panel takes over the screen corner it was last left in, and
        // that's not something to do to someone who just changed their mind.
        if (ResolveMode(e.Args, remote) is not { } mode)
        {
            RequestShutdown();
            return;
        }

        var panel = new MainWindow(mode);

        if (mode == DashboardMode.Local)
        {
            panel.Show();
            return;
        }

        // Remote: the panel stays unshown and keeps doing everything except being
        // looked at. This window is the only thing on screen.
        new RemoteWindow(panel).Show();
    }

    /// <summary>
    /// What --check does: the same load and the same warnings as starting up, minus
    /// the starting up. Exit code 0 means clean, 1 means the report has something in
    /// it, so a script can gate on it.
    /// </summary>
    private void CheckConfig()
    {
        var config = DashboardConfig.Load(out string? readError);

        var lines = new List<string> { $"read {config.SourcePath}" };

        if (readError is not null)
        {
            lines.Add("the file couldn't be parsed — running now would use built-in "
                      + $"defaults:\n{readError}");
        }
        lines.AddRange(config.Warnings);

        bool clean = readError is null && config.Warnings.Count == 0;
        lines.Add(clean
            ? $"OK — {config.EffectiveSections().Count()} sections, "
              + $"{config.Profiles.Count} profiles."
            : $"{config.Warnings.Count + (readError is null ? 0 : 1)} problem(s).");

        string report = string.Join(Environment.NewLine + Environment.NewLine, lines);

        // A WinExe never gets a console of its own, so borrow the one the command was
        // typed into. When there isn't one — double-clicked, or the UAC prompt put the
        // parent out of reach — a message box is the only voice left.
        if (NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS))
        {
            Console.WriteLine();
            Console.WriteLine(report);
        }
        else
        {
            MessageBox.Show(report, "Deckhand — config check", MessageBoxButton.OK,
                            clean ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        _shuttingDown = true;
        Shutdown(clean ? 0 : 1);
    }

    /// <summary>
    /// Already asked to stop — by one of our own buttons, or by Windows ending the
    /// session. Two windows own "the way out" in different modes and closing one closes
    /// the other, so the request can arrive twice (✕ shuts down, which closes the panel,
    /// whose OnClosed asks again). WPF happens to tolerate the second call today; this
    /// doesn't lean on that.
    /// </summary>
    private bool _shuttingDown;

    internal static void RequestShutdown()
    {
        if (Current is not App app || app._shuttingDown) return;
        app._shuttingDown = true;
        app.Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);

        // Windows is doing the shutting down; the windows it closes on the way out
        // mustn't ask for another one from inside it.
        if (!e.Cancel) _shuttingDown = true;
    }

    private static DashboardMode? ResolveMode(string[] args, RemoteSettings remote)
    {
        // A switch is the whole answer, config or no config: a shortcut that says
        // --remote means it, rather than depending on the file agreeing that day.
        if (args.Any(a => a.Equals("--local", StringComparison.OrdinalIgnoreCase))) return DashboardMode.Local;
        if (args.Any(a => a.Equals("--remote", StringComparison.OrdinalIgnoreCase))) return DashboardMode.Remote;

        if (!remote.Enabled) return DashboardMode.Local;

        var chooser = new ModeWindow(remote);
        chooser.ShowDialog();
        return chooser.Mode;
    }
}
