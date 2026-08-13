using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Deckhand;

// Re-reading the config: on ↻, on the file changing underneath us, and from the remote
// status window, where the panel's own ↻ is not on any screen to tap. A reload nobody
// asked for stays quiet — the watcher fires mid-edit, when a half-written file is a
// normal thing to see for a moment, and a dialog for that would punish saving often.
public partial class MainWindow
{
    private void Reload_Click(object sender, RoutedEventArgs e) => ReloadConfig();

    /// <summary>
    /// Re-reads dashboard.json and rescans the source sections' folders, so config
    /// edits and new directories show up without restarting. Also reached from the remote status
    /// window, where the panel's own ↻ is not on any screen to tap — and from the file
    /// watcher, with <paramref name="interactive"/> false: a reload nobody asked for
    /// mustn't put a dialog over their work or unpair their tablet, so that path only
    /// logs its complaints and only restarts the server when the remote settings
    /// actually changed.
    /// </summary>
    internal void ReloadConfig(bool interactive = true)
    {
        var hadRemote = _config.Remote;
        _config = DashboardConfig.Load(out string? configError);
        _folderScans.Clear(); // keyed by the section objects just replaced
        ShowConfigSource();
        _log.Add(LogKind.Panel, $"re-read {_config.SourcePath}");

        // The address and the token can both have changed, so ↻ is a restart rather
        // than a refresh — deliberately: restarting is also what lets a new device pair.
        // Turning "enabled" off in the config closes the port here.
        if (interactive || RemoteSettingsChanged(hadRemote, _config.Remote))
        {
            StartRemote();
        }

        // Rendering as "nothing focused" also resets the state the Poll below compares
        // against, so re-matching the focused app counts as a change and brings its
        // sections back. Without the reset the Poll would see no change and skip it.
        Render(null);
        _watcher?.Poll();    // re-match the focused app against the new profiles

        ReportConfigProblems(configError, interactive);
    }

    private static bool RemoteSettingsChanged(RemoteSettings before, RemoteSettings after) =>
        before.Enabled != after.Enabled
        || before.Listen != after.Listen
        || before.Token != after.Token
        || !before.AllowedHosts.SequenceEqual(after.AllowedHosts);

    /// <summary>Watches the config files, so an edit shows up on save with nothing to tap.</summary>
    private FileSystemWatcher? _configWatcher;

    /// <summary>
    /// One save is several filesystem events, and some editors write the file twice —
    /// so the events only wind this timer, and its tick is the one reload.
    /// </summary>
    private DispatcherTimer? _reloadDebounce;

    private void WatchConfig()
    {
        string? directory = Path.GetDirectoryName(DashboardConfig.ResolvePath());
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

        _reloadDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _reloadDebounce.Tick += (_, _) =>
        {
            _reloadDebounce.Stop();
            _log.Add(LogKind.Panel, "config changed on disk — reloading");
            ReloadConfig(interactive: false);
        };

        try
        {
            // Both config files, and cheap enough not to care that the filter also
            // catches the schema. Events arrive on a worker thread; the dispatch hops
            // to the UI thread, where restarting the timer coalesces the burst.
            _configWatcher = new FileSystemWatcher(directory, "dashboard*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                               | NotifyFilters.Size,
            };
            _configWatcher.Changed += (_, _) => NudgeReload();
            _configWatcher.Created += (_, _) => NudgeReload();
            _configWatcher.Renamed += (_, _) => NudgeReload();
            _configWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException)
        {
            _configWatcher = null; // no watcher is no hot reload, not a reason to fail
        }
    }

    private void NudgeReload() => Dispatcher.BeginInvoke(() =>
    {
        _reloadDebounce?.Stop();
        _reloadDebounce?.Start();
    });

    /// <summary>
    /// Names the file ↻ re-reads, so a reload that changes nothing is explicable, and
    /// the remote panel's address when one is being served — otherwise the only place
    /// to find it is the config.
    /// </summary>
    private void ShowConfigSource() =>
        ReloadButton.ToolTip = $"Reload and rescan folders\n{_config.SourcePath}"
                               + (_remote is null ? "" : $"\nRemote panel: {_remote.Address}");

    /// <summary>
    /// Says what a config mistake cost. A parse failure is the loud one — the whole file
    /// was dropped for defaults — but the quiet ones get the same dialog, because their
    /// only symptom is tiles that never appear: a section written for a profile name
    /// that nothing carries looks exactly like a section that isn't meant to be there.
    /// </summary>
    private void ReportConfigProblems(string? error, bool interactive = true)
    {
        var problems = new List<string>();

        if (error is not null)
        {
            problems.Add("dashboard.json couldn't be read, so the dashboard is showing "
                         + $"its defaults.\n\n{error}");
            _log.Add(LogKind.Warning, "the config couldn't be read — running on defaults");
        }

        foreach (string warning in _config.Warnings)
        {
            problems.Add(warning);
            _log.Add(LogKind.Warning, warning);
        }

        if (problems.Count == 0 || !interactive) return;

        // Only for a reload someone asked for. The file watcher reloads mid-edit, when
        // a half-saved file is a normal thing to see for a moment — a dialog for that
        // would punish saving often. The log above keeps the record either way.
        MessageBox.Show(string.Join("\n\n", problems), "Deckhand",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
