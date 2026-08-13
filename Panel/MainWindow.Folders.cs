using System.Windows;
using System.Windows.Controls;

namespace Deckhand;

// Sections built from a directory: one tile per subdirectory, the ⇄ switch when a split
// has given the list two sides, and the ▾ list of commands under each. Tapping one either
// opens the folder or types a command template naming it, which is the whole of what used
// to be the branches feature — with the branch-shaped knowledge left in the config file,
// where someone who doesn't share that workflow can write something else.
public partial class MainWindow
{
    /// <summary>
    /// A source section's tiles: the ⇄ switch when the split has given it two sides,
    /// and under it one tile per folder on the side being shown.
    /// <paramref name="note"/> comes back non-null when the header should say something —
    /// a problem, which side these are, or that the list was capped — so a short list is
    /// never silently mistaken for the whole list, and tiles are never left sitting under
    /// a title that means the other half of them.
    /// </summary>
    private List<(FrameworkElement Tile, int Span)> FolderTiles(SectionEntry section,
        SourceSettings source, int tilesPerRow, out string? note)
    {
        if (!_folderScans.TryGetValue(section, out var scan))
        {
            var folders = FolderCatalog.Discover(source, out string? scanError);
            _folderScans[section] = scan = (folders, scanError);
        }

        var tiles = new List<(FrameworkElement, int)>();

        if (scan.Error is not null)
        {
            note = "unavailable";
            string detail = scan.Error;
            tiles.Add((MakeTile("⚠ No folders", () => MessageBox.Show(detail,
                "Deckhand", MessageBoxButton.OK, MessageBoxImage.Warning)), 1));
            return tiles;
        }

        bool showingOther = _showOtherSide.Contains(section.Label);
        var shown = scan.Folders.Where(f => f.Matched != showingOther).ToList();
        int other = scan.Folders.Count - shown.Count;

        // Only worth a tile when there's something on the other side of it — but kept
        // while the "other" side is the one on screen even if a rescan emptied it, or
        // there would be no way back.
        if (other > 0 || showingOther)
        {
            tiles.Add((SplitSwitch(section, source, other), tilesPerRow));
        }

        int max = source.Max > 0 ? source.Max : int.MaxValue;
        foreach (var folder in shown.Take(max))
        {
            tiles.Add((FolderTile(section, source, folder), 1));
        }

        var notes = new List<string>();
        if (showingOther) notes.Add(source.OtherLabel);
        if (shown.Count == 0) notes.Add("none"); // the switch is the only thing here
        else if (shown.Count > max) notes.Add($"{max} of {shown.Count}");

        note = notes.Count > 0 ? string.Join(", ", notes) : null;
        return tiles;
    }

    /// <summary>
    /// The tile above the folders that decides which side of the split is drawn — both
    /// sides come from the same scan, so this only chooses what to show.
    ///
    /// It's labelled with the side it switches to and how many are over there, the way a ▾
    /// says what tapping does rather than what is already open. That also makes it legible
    /// on the tablet, which draws every tile identically and has no way to show one as
    /// pressed in — the label is the whole of the state, so it can't disagree with itself.
    /// </summary>
    private Button SplitSwitch(SectionEntry section, SourceSettings source, int count)
    {
        bool showingOther = _showOtherSide.Contains(section.Label);
        string other = showingOther ? source.MatchLabel : source.OtherLabel;

        return MakeTile($"⇄ {other} ({count})", () =>
        {
            if (!_showOtherSide.Remove(section.Label)) _showOtherSide.Add(section.Label);
            _log.Add(LogKind.Panel, $"\"{section.Label}\" switched to {other}");

            // A whole redraw for one section's worth of tiles, because the folders under
            // the switch are its entire point and Render is what builds them. It costs no
            // more than a window switch does, and it publishes to the tablet on the way.
            Render(_activeProfile);
        });
    }

    /// <summary>
    /// The folder's name — with its subtitle under it when the section's subtitles file
    /// has one — plus a ▾ that expands the extra commands when the section has any.
    /// </summary>
    private FrameworkElement FolderTile(SectionEntry section, SourceSettings source,
                                        FolderInfo folder)
    {
        var main = MakeTile(FolderLabel(folder),
                            () => TapFolder(section, source, folder, source.Command));

        if (source.Commands.Count == 0)
        {
            var bare = new StackPanel();
            bare.Children.Add(main);
            return bare;
        }

        var commands = source.Commands
            .Select(c => ((FrameworkElement)MakeTile(c.Label,
                        () => TapFolder(section, source, folder, c.Command)), 1))
            .ToList();

        var list = SubList(commands, Math.Clamp(source.CommandColumns, 1, 8));

        Button? expander = null;
        expander = MakeTile(ChevronClosed, () => ToggleList(list, expander!, "commands"));

        var stack = new StackPanel();
        stack.Children.Add(SideBySide(main, expander, joined: false));
        stack.Children.Add(list);
        return stack;
    }

    // ---- Two-sided tiles ---------------------------------------------------

    /// <summary>
    /// A main tile with a narrow one beside it: what the tile is mostly for on the wide
    /// side, and the lesser of the two on the narrow one — another copy of an app, or a
    /// folder's commands. <paramref name="joined"/> is only about how it reads — an app
    /// tile is styled as one control divided by a line, a folder tile as a label with an
    /// arrow next to it — and the tablet is told which so it can match.
    ///
    /// Neither kind uses a ContextMenu or a Popup for what the narrow side opens: those
    /// live in their own HWND and take focus, which would make the dashboard the
    /// foreground window and send its own keystrokes to itself.
    /// </summary>
    private static Grid SideBySide(Button main, Button tail, bool joined)
    {
        // The tag is for the tablet: its page can't nest, so RemoteLayout has to be told
        // that the second column here is a narrow arrow rather than half the row.
        var header = new Grid
        {
            Tag = joined ? RemoteLayout.JoinedTail : RemoteLayout.NarrowTail,
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The cell decides the width, as it does for any tile on a grid. Left at the
        // style's MinWidth — which is there for the free-flowing layout — the label would
        // insist on 160px and push the arrow out of the side of a narrow group.
        main.MinWidth = 0;
        header.Children.Add(main);

        tail.MinWidth = 0;
        tail.Width = ExpanderWidth;
        Grid.SetColumn(tail, 1);
        header.Children.Add(tail);

        return header;
    }

    /// <summary>
    /// The block that hangs under a two-sided tile: collapsed until asked for, and
    /// indented so it reads as belonging to the tile above — which is also where the
    /// tablet's page takes its indent from.
    /// </summary>
    private static Grid SubList(List<(FrameworkElement Tile, int Span)> tiles, int columns)
    {
        var panel = GridTiles(tiles, columns);
        panel.Visibility = Visibility.Collapsed;
        panel.Margin = new Thickness(12, 0, 0, 6);
        return panel;
    }

    /// <summary>
    /// Expands one tile's list, collapsing whichever was open. Leaving them all open
    /// would push the rest of the dashboard off a panel this tall.
    /// </summary>
    private void ToggleList(Grid list, Button expander, string what)
    {
        bool show = list.Visibility != Visibility.Visible;
        CloseOpenList();

        if (!show)
        {
            _log.Add(LogKind.Panel, $"closed a {what} list");
            return;
        }

        list.Visibility = Visibility.Visible;
        ((TextBlock)expander.Content).Text = ChevronOpen;
        _openList = (list, expander);
        _log.Add(LogKind.Panel, $"opened a {what} list");
    }

    private void CloseOpenList()
    {
        if (_openList is not { } open) return;

        open.List.Visibility = Visibility.Collapsed;
        ((TextBlock)open.Expander.Content).Text = ChevronClosed;
        _openList = null;
    }

    private static string FolderLabel(FolderInfo folder) =>
        string.IsNullOrWhiteSpace(folder.Subtitle)
            ? folder.Name
            : $"{folder.Name}\n{folder.Subtitle}";

    /// <summary>
    /// What every part of a folder tile does: notes the tap — which is what keeps the
    /// list in recently-used order — then either types the command into the focused
    /// window or, when the section has no command, opens the folder itself, exactly
    /// like an app tile pointed at it, window matching and all.
    /// </summary>
    private void TapFolder(SectionEntry section, SourceSettings source, FolderInfo folder,
                           string? command)
    {
        TapHistory.Note(folder.FullPath);

        if (string.IsNullOrWhiteSpace(command))
        {
            ActivateApp(new AppEntry { Label = folder.Name, Path = folder.FullPath });
            return;
        }

        SendCommand(section, source, Substitute(command, folder));
    }

    /// <summary>
    /// Types <paramref name="command"/> into the window that already has focus. There
    /// is no launch fallback, and no built-in idea of what the window should be: the
    /// section's own profiles are the answer, so they're re-checked here rather than
    /// only at render time. Focus can change between the render and the tap, and
    /// typing "goto …" into an editor would insert junk. A section with no profiles
    /// skips the check — it said its tiles go anywhere.
    /// </summary>
    private void SendCommand(SectionEntry section, SourceSettings source, string command)
    {
        // Re-read the foreground rather than trusting the last watcher event, so this
        // reflects the window about to receive the keystrokes.
        var focus = _watcher?.Current();
        if (focus is not null && section.Profiles.Count > 0
            && !section.AppliesTo(_config.MatchProfile(focus.ProcessName, focus.WindowTitle)))
        {
            _log.Add(LogKind.Warning, $"not sent — \"{focus.ProcessName}\" is focused, "
                                      + $"which isn't what \"{section.Label}\" types into");
            MessageBox.Show($"\"{focus.ProcessName}\" isn't a window this section's "
                            + "commands are written for, so nothing was sent.\n\n"
                            + "Focus one that is and tap again.",
                            "Deckhand", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (command.Length == 0) return;

        // Typed, not pasted: not every target maps Ctrl+V to paste — mintty doesn't —
        // and the clipboard route would put a control character in those instead.
        if (!NativeMethods.TypeText(command))
        {
            WarnInputBlocked($"type \"{command}\" into the focused window");
            return;
        }

        if (source.Submit) NativeMethods.SendEnter();

        // The command is logged in full: unlike a snippet it's a command line, and which
        // one ran in which window is exactly what's worth being able to look back at.
        _log.Add(LogKind.Action, $"sent \"{command}\" to {focus?.ProcessName ?? "the focused window"}"
                                 + (source.Submit ? " and pressed Enter" : ""));
    }

    /// <summary>
    /// Explains a rejected injection instead of leaving it looking like nothing
    /// happened, which is exactly how UIPI presents itself.
    /// </summary>
    private void WarnInputBlocked(string what)
    {
        const int ERROR_ACCESS_DENIED = 5;
        int error = NativeMethods.LastSendError;

        _log.Add(LogKind.Warning, $"Windows blocked the input — couldn't {what} (error {error})");

        string reason = error == ERROR_ACCESS_DENIED
            ? "Windows blocked the keystrokes because the focused window runs at a "
              + "higher integrity level than the dashboard — typically it is elevated "
              + "and the dashboard is not.\n\nRun the dashboard as administrator too."
            : $"Windows rejected the input (error {error}).";

        MessageBox.Show($"Couldn't {what}.\n\n{reason}",
                        "Deckhand", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // {id} comes out empty for a folder the split captured nothing from, so a command
    // written around it belongs on the matching side of the switch. {branch} and {wi}
    // are the same two values under the names they had when this feature was
    // branch-only — kept as aliases so an old config keeps working as written.
    private static string Substitute(string? template, FolderInfo folder) =>
        (template ?? "")
            .Replace("{dir}", folder.FullPath)
            .Replace("{name}", folder.Name)
            .Replace("{id}", folder.Id)
            .Replace("{branch}", folder.Name)
            .Replace("{wi}", folder.Id);
}
