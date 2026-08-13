using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Deckhand;

// The tablet's half of the panel. The server is started and fed from here: a publish
// walks the elements this window has already built and turns them into ids, and a tap
// coming back raises Click on the very button it names. Nothing a tile does is
// reimplemented for the remote — see RemoteServer for the protocol and what guards it.
public partial class MainWindow
{
    private void StartRemote()
    {
        _remote?.Dispose();
        _remote = null;
        _remoteError = null;
        _remoteAddresses = Array.Empty<string>();

        // Local mode never listens. The mode chosen at startup is the switch — not
        // remote.enabled, which only decides whether the question was asked at all.
        if (_mode != DashboardMode.Remote) return;

        _remote = RemoteServer.TryStart(_config.Remote, Dispatcher, RemoteTap, _log,
                                       out _remoteError);

        // No dialog if it didn't start: in this mode the status window is on screen and
        // shows the reason, where it stays visible instead of needing to be dismissed.
        if (_remote is null)
        {
            _log.Add(LogKind.Warning, $"not serving — {_remoteError}");
            return;
        }

        _remoteAddresses = _remote.Addresses();
        _log.Add(LogKind.Panel, $"serving on {_remote.Address}");
        PublishRemote();
        ShowConfigSource(); // the tooltip carries the address to open on the tablet
    }

    /// <summary>
    /// Re-reads the address list when Windows says the network changed — another Wi-Fi,
    /// a fresh DHCP lease. Without this the status window keeps listing addresses the
    /// machine no longer has, and "waiting for the tablet…" sits over a list the tablet
    /// can't reach with nothing to say why. The event arrives on a threadpool thread,
    /// hence the dispatch; the status window redraws from its own timer.
    /// </summary>
    private void OnNetworkAddressChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (_remote is null) return;

            var addresses = _remote.Addresses();
            if (_remoteAddresses.SequenceEqual(addresses)) return;

            _remoteAddresses = addresses;
            _log.Add(LogKind.Panel, "the network changed — addresses re-read");
        });

    /// <summary>
    /// This process's own name, to recognise our own windows in a foreground report.
    /// </summary>
    private static readonly string OwnProcessName = Process.GetCurrentProcess().ProcessName;

    /// <summary>
    /// Whether one of the dashboard's own windows has focus. In remote mode the status
    /// window is an ordinary window that can be clicked into, and synthesized input goes
    /// wherever focus is — including, then, to us.
    /// </summary>
    private static bool DashboardHasFocus()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// Everything the remote status window shows. Gathered here because this is the
    /// window that knows both halves — what the panel is showing, and what the server
    /// has seen.
    /// </summary>
    internal RemoteStatus Status()
    {
        var activity = _remote?.Activity ?? (null, null, 0, null);

        return new RemoteStatus(
            Serving: _remote is not null,
            Error: _remoteError,
            Addresses: _remoteAddresses,
            Token: _config.Remote.Token,
            TokenSource: _config.Remote.TokenSource,
            Context: _contextText,
            Tiles: _remoteTiles.Count,
            Ready: _unlocked is null && !DashboardHasFocus(),
            activity.LastSeenUtc, activity.Peer, activity.Taps, activity.Screen,
            Pinned: _remote?.Pinned);
    }

    /// <summary>
    /// Log lines the status window hasn't drawn yet. It passes back the last sequence
    /// number it saw, so nothing is dropped between two polls and nothing is repeated.
    /// </summary>
    internal List<LogEntry> LogSince(long sequence) => _log.Since(sequence);

    /// <summary>
    /// Tells the tablet what the panel is showing. Called after every render and
    /// whenever the lock state changes, which is what makes the remote view follow the
    /// focused app rather than needing its own idea of one.
    /// </summary>
    private void PublishRemote()
    {
        if (_remote is null) return;

        // The grid's own size travels with the groups: without it the rectangles below
        // are fractions of nothing, and the tablet can only stack.
        var sectionGrid = ContentRoot.Children.Count > 0 ? ContentRoot.Children[0] as Grid : null;

        // Order matters: the groups clear the id table, and the overlay adds to it.
        var groups = RemoteGroups(sectionGrid);
        _remoteGroups = groups.Count;
        var overlay = RemoteOverlayOf();

        _remote.Publish(_contextText, ready: _unlocked is null,
                        columns: sectionGrid?.ColumnDefinitions.Count ?? 1,
                        rows: sectionGrid?.RowDefinitions.Count ?? 1,
                        groups, overlay);
    }

    /// <summary>
    /// The picker as the tablet gets it: the same card, read off the same elements, so a
    /// tap on the tablet raises the same button a finger on the panel would.
    /// </summary>
    private RemoteOverlay? RemoteOverlayOf()
    {
        _overlayTiles.Clear();
        if (OverlayRoot.Visibility != Visibility.Visible) return null;

        var (columns, tiles) = RemoteTiles("picker", OverlayBody);
        foreach (var tile in tiles) _overlayTiles.Add(tile.Id);

        // Cancel is a tile like the rest, and naming it lets the page's backdrop dismiss
        // the modal by tapping the same button the card shows.
        string dismiss = tiles.FirstOrDefault(t =>
            ReferenceEquals(_remoteTiles.GetValueOrDefault(t.Id), OverlayDismiss))?.Id ?? "";

        return new RemoteOverlay(OverlayTitle.Text, columns, tiles, dismiss);
    }

    /// <summary>
    /// A tap from the tablet, raised on the tile it names so it runs exactly what a
    /// finger on the panel runs.
    /// </summary>
    private RemoteServer.TapResult RemoteTap(string id)
    {
        // An unlocked panel can hold focus, so a snippet would type into the dashboard
        // itself. Local taps are inert for the same reason.
        if (_unlocked is not null)
        {
            _log.Add(LogKind.Warning, "a tap arrived while the panel was unlocked — refused");
            return RemoteServer.TapResult.PanelUnlocked;
        }

        // Same hazard from the other direction: in remote mode the status window is an
        // ordinary window, and while it has focus the keystrokes would land on it — a
        // command that ends in Enter could press whichever of its buttons has focus.
        if (DashboardHasFocus())
        {
            _log.Add(LogKind.Warning, "a tap arrived while the dashboard's own window had "
                                      + "focus — refused");
            return RemoteServer.TapResult.DashboardFocused;
        }

        // A modal is a modal on both screens: while the picker is up it covers the panel,
        // so a finger can't reach the tiles behind it, and neither may a tap.
        if (_overlayTiles.Count > 0 && !_overlayTiles.Contains(id))
        {
            _log.Add(LogKind.Warning, "a tap named a tile behind the picker — refused");
            return RemoteServer.TapResult.BehindOverlay;
        }

        if (!_remoteTiles.TryGetValue(id, out var tile)
            || tile.Visibility != Visibility.Visible)
        {
            _log.Add(LogKind.Warning, "a tap named a tile that isn't on screen — the page "
                                      + "is showing an older panel");
            return RemoteServer.TapResult.UnknownTile;
        }

        // Logged before firing, so the lines read in the order things happened: the tap
        // arrives, then whatever it did.
        _log.Add(LogKind.Action, $"tap — \"{TileLabel(tile)}\"");

        tile.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        // A folder tile's ▾ changes which tiles exist without going through Render,
        // so the tablet needs the new list.
        PublishRemote();
        return RemoteServer.TapResult.Fired;
    }

    /// <summary>
    /// The panel as groups of tiles, walking what was just built rather than the
    /// config, so the two views can't disagree about what's on screen.
    /// </summary>
    private List<RemoteGroup> RemoteGroups(Grid? sectionGrid)
    {
        _remoteTiles.Clear();
        var groups = new List<RemoteGroup>();

        if (sectionGrid is null) return groups;

        foreach (var child in sectionGrid.Children)
        {
            if (child is not Border { Child: DockPanel dock } group) continue;

            string label = dock.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "";
            if (dock.Children.OfType<ScrollViewer>().FirstOrDefault()?.Content
                is not DependencyObject body) continue;

            // The page has one grid per group, so the panel's nested tiles are flattened
            // onto it here — cell by cell, rather than in order, or a folder tile's ▾
            // lands on the row below the label it belongs to.
            var (tileColumns, tiles) = RemoteTiles(label, body);

            if (tiles.Count > 0)
            {
                groups.Add(new RemoteGroup(
                    label, tileColumns,
                    Column: Grid.GetColumn(group) + 1, ColumnSpan: Grid.GetColumnSpan(group),
                    Row: Grid.GetRow(group) + 1, RowSpan: Grid.GetRowSpan(group),
                    tiles));
            }
        }

        return groups;
    }

    /// <summary>
    /// One container's buttons as tiles, registered so a tap can name them. Shared by the
    /// groups and by the picker's card, which is drawn from the same kind of layout.
    /// <paramref name="scope"/> is the group label, or "picker": it only has to be stable
    /// and distinct, since it's what keeps two tiles with the same label apart.
    /// </summary>
    private (int Columns, List<RemoteTile> Tiles) RemoteTiles(string scope, DependencyObject body)
    {
        var (columns, cells) = RemoteLayout.Of(body);

        var tiles = new List<RemoteTile>();
        var repeats = new Dictionary<string, int>();

        foreach (var (button, row, column, span, trailing, indent, joined) in cells)
        {
            string text = (button.Content as TextBlock)?.Text ?? "";

            // Ids are content-addressed rather than positional, so adding a tile doesn't
            // renumber the others and a page left open on the tablet still names what it
            // drew. The count distinguishes tiles that share a label within one scope —
            // every folder tile's ▾, for instance.
            repeats.TryGetValue(text, out int repeat);
            repeats[text] = repeat + 1;

            string id = TileId(scope, text, repeat);
            _remoteTiles[id] = button;
            tiles.Add(new RemoteTile(id, text, row, column, span, trailing, indent, joined,
                                     Color: button.Tag as string));
        }

        return (columns, tiles);
    }

    /// <summary>A tile's label for the log, with the line break a folder tile carries
    /// flattened so one tap stays one line.</summary>
    private static string TileLabel(Button tile) =>
        ((tile.Content as TextBlock)?.Text ?? "").Replace("\n", " — ");

    private static string TileId(string group, string label, int repeat)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{group}\0{label}\0{repeat}"));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// The status window's Unpair: lets the session go so another device with the right
    /// token can take it, without restarting the server or re-reading anything.
    /// </summary>
    internal void UnpairRemote() => _remote?.Unpair();
}
