using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Deckhand;

/// <summary>
/// The designer's picture of the panel: the same grid, the same placer, the same
/// styles — and none of the behaviour. Tiles here launch nothing and type nothing;
/// they can't even be pressed, so a click anywhere in a group lands on the group and
/// selects it for editing. It mirrors MainWindow.Tiles on purpose and deliberately
/// doesn't call it: that code is entangled with taps, window lists and folder
/// history, all of which are exactly what a preview must not do.
/// </summary>
internal static class DesignerPreview
{
    /// <summary>
    /// The whole panel body as the panel would draw it, with <paramref name="selected"/>
    /// outlined and every group clickable. Empty groups, which the panel drops, are kept
    /// as something to click on while their buttons are still being written.
    ///
    /// <paramref name="shows"/> decides which groups are in it — the designer asks either
    /// for all of them, which is the view for building, or for the ones the panel would
    /// draw with a given app in front. A group it refuses is dropped before the placer, as
    /// the panel drops it: its cells go back to the pool and what came after it moves up,
    /// which is the whole reason for looking at one profile at a time.
    ///
    /// <paramref name="folder"/> is what a tile's relative icon path is measured from —
    /// the folder of the file being saved, so a picture is resolved here the way the panel
    /// will resolve it when it reads that file.
    /// </summary>
    public static UIElement Build(DashboardConfig config, string folder,
                                  SectionEntry? selected,
                                  Func<SectionEntry, bool> shows,
                                  Action<SectionEntry> select)
    {
        int totalColumns = Math.Clamp(config.Layout.Columns, 1, 48);
        int totalRows = Math.Clamp(config.Layout.Rows, 1, 48);

        var grid = new Grid();
        for (int i = 0; i < totalColumns; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var placer = new GridPlacer(totalColumns, totalRows);

        foreach (var section in config.EffectiveSections())
        {
            if (!shows(section)) continue;

            var content = BuildSection(section, folder,
                                       section == selected, select);
            var cell = placer.Place(section.Columns, section.Rows);

            while (grid.RowDefinitions.Count < cell.Row + cell.RowSpan)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            }

            Grid.SetRow(content, cell.Row);
            Grid.SetRowSpan(content, cell.RowSpan);
            Grid.SetColumn(content, cell.Column);
            Grid.SetColumnSpan(content, cell.ColumnSpan);
            grid.Children.Add(content);
        }

        while (grid.RowDefinitions.Count < placer.RowCount)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        return grid;
    }

    private static FrameworkElement BuildSection(SectionEntry section, string folder,
                                                 bool isSelected,
                                                 Action<SectionEntry> select)
    {
        var tiles = new List<(FrameworkElement Tile, int Span)>();

        int tilesPerRow = section.Columns is null
            ? 1
            : Math.Clamp(section.TilesPerRow ?? section.Columns.Value.Length, 1, 24);

        if (section.Source is { } source) tiles.AddRange(FolderGhosts(source));
        tiles.AddRange(section.Apps.Select(a => ((FrameworkElement)AppTile(a, folder), a.Span)));
        tiles.AddRange(section.Snippets.Select(s => ((FrameworkElement)SnippetTile(s, folder), s.Span)));

        FrameworkElement body;
        if (tiles.Count == 0)
        {
            // The panel returns null here and the group never exists. The designer is
            // where the group is on its way to existing, so it's drawn — with a line
            // saying what the panel will do about it.
            body = new TextBlock
            {
                Text = "empty — the panel won't draw this group until it has buttons",
                Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x70)),
                FontSize = 12,
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(8),
            };
        }
        else if (section.Columns is null)
        {
            var panel = new WrapPanel();
            foreach (var (tile, _) in tiles) panel.Children.Add(tile);
            body = panel;
        }
        else
        {
            body = GridTiles(tiles, tilesPerRow);
        }

        var dock = new DockPanel();

        if (!string.IsNullOrWhiteSpace(section.Label))
        {
            var title = new TextBlock
            {
                Text = section.Label.ToUpperInvariant(),
                Style = (Style)Application.Current.Resources["SectionHeader"],
            };
            DockPanel.SetDock(title, Dock.Top);
            dock.Children.Add(title);
        }

        dock.Children.Add(new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        });

        var box = new Border
        {
            Style = (Style)Application.Current.Resources["SectionGroup"],
            Child = dock,
            Cursor = Cursors.Hand,
            // A Border only hit-tests where it paints, and SectionGroup paints
            // everywhere, so the whole rectangle is the click target.
            ToolTip = Describe(section),
        };

        if (isSelected)
        {
            box.BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
            box.BorderThickness = new Thickness(2);
            // Thicker border, same rectangle — or selecting a group would nudge
            // its tiles.
            box.Padding = new Thickness(1, 1, 1, 3);
        }

        box.MouseLeftButtonDown += (_, e) =>
        {
            select(section);
            e.Handled = true;
        };

        return box;
    }

    /// <summary>What a click is selecting, said where the pointer already is.</summary>
    private static string Describe(SectionEntry section)
    {
        var parts = new List<string>
        {
            string.IsNullOrWhiteSpace(section.Label) ? "(unnamed group)" : section.Label,
        };

        parts.Add(section.Columns is { } c
            ? c.Start is null ? $"width {c.Length}, packed" : $"columns {c}"
            : "full width, tiles flow");
        if (section.Rows is { } r) parts.Add(r.Start is null ? $"height {r.Length}" : $"rows {r}");
        if (section.Profiles.Count > 0) parts.Add($"only with: {string.Join(", ", section.Profiles)}");
        if (section.Source is not null) parts.Add("folder tiles — edit the source in dashboard.json");

        return string.Join("  ·  ", parts);
    }

    private static Grid GridTiles(List<(FrameworkElement Tile, int Span)> tiles, int tilesPerRow)
    {
        var grid = new Grid();
        for (int i = 0; i < tilesPerRow; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        int column = 0, row = 0;
        foreach (var (tile, rawSpan) in tiles)
        {
            int span = Math.Clamp(rawSpan, 1, tilesPerRow);
            if (column + span > tilesPerRow) { column = 0; row++; }

            tile.MinWidth = 0;
            tile.HorizontalAlignment = HorizontalAlignment.Stretch;

            while (grid.RowDefinitions.Count < row + 1)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            Grid.SetRow(tile, row);
            Grid.SetColumn(tile, column);
            Grid.SetColumnSpan(tile, span);
            grid.Children.Add(tile);

            column += span;
        }

        return grid;
    }

    /// <summary>
    /// A folder section's tiles, scanned the cheap way: names only, excluded and capped
    /// like the panel, the split's matching side first the way the ⇄ switch starts. No
    /// history ordering and no command expanders — those belong to taps, and nothing
    /// here taps. A folder that can't be read becomes one tile saying so.
    /// </summary>
    private static IEnumerable<(FrameworkElement Tile, int Span)> FolderGhosts(SourceSettings source)
    {
        if (ScanFolders(source) is not { } names)
        {
            yield return (Ghost($"⚠ {source.Folders}"), 1);
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(source.Split))
        {
            try
            {
                var split = new Regex(source.Split, RegexOptions.IgnoreCase);
                var matching = names.Where(n => split.IsMatch(n)).ToList();
                if (matching.Count > 0) names = matching;
            }
            catch (ArgumentException)
            {
                // Reported by Check() at load; the preview just shows the flat list.
            }
        }

        int max = Math.Max(1, source.Max);
        foreach (string name in names.Take(max)) yield return (Ghost(name), 1);
    }

    /// <summary>The folder's subdirectory names, minus the excluded — or null when the
    /// folder can't be read, which the caller turns into a tile that says so.</summary>
    private static List<string>? ScanFolders(SourceSettings source)
    {
        try
        {
            string root = Environment.ExpandEnvironmentVariables(source.Folders ?? "");
            return Directory.EnumerateDirectories(root)
                .Select(d => System.IO.Path.GetFileName(d) ?? "")
                .Where(n => !source.Exclude.Contains(n, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A folder tile as a shape: real name, dimmed, because which folders
    /// exist is the machine's answer and not this config's.</summary>
    private static Button Ghost(string label)
    {
        var tile = MakeTile(label);
        tile.Opacity = 0.55;
        return tile;
    }

    private static FrameworkElement AppTile(AppEntry app, string folder)
    {
        var main = MakeTile(app.Label, "SplitTileLeft", Image(folder, app.Icon), app.IconMode);

        // Never the picture, exactly as on the panel: it's the half that opens another
        // window, and a preview that showed it as a logo would be showing a panel that
        // can't happen.
        var add = MakeTile("+", "SplitTileRight");

        Accent(main, app.Color);
        Accent(add, app.Color);

        var pair = new Grid();
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        main.MinWidth = 0;
        pair.Children.Add(main);

        add.MinWidth = 0;
        add.Width = 44;
        Grid.SetColumn(add, 1);
        pair.Children.Add(add);

        return pair;
    }

    private static Button SnippetTile(SnippetEntry snippet, string folder)
    {
        var tile = MakeTile(snippet.Label, "TileButton",
                            Image(folder, snippet.Icon), snippet.IconMode);
        Accent(tile, snippet.Color);
        return tile;
    }

    /// <summary>
    /// The tile's picture, read from the folder of the file this window has open — which
    /// is the point of resolving it here rather than reusing whatever the panel loaded: a
    /// relative icon path in a config being designed means a file beside *that* config,
    /// and the designer may well have a different one open than the panel is running.
    ///
    /// Null for a file that's missing or won't decode, and the tile falls back to its
    /// label — the same thing the panel does with it, so a picture that isn't going to
    /// work looks the same here as it will there.
    /// </summary>
    private static ImageSource? Image(string folder, string? icon) =>
        TileImages.Of(folder, icon)?.Image;

    private static void Accent(Button tile, string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return;

        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color.Trim()));
            brush.Freeze();
            tile.BorderBrush = brush;
        }
        catch (FormatException)
        {
            // The editor beside this preview marks the bad value; here it just
            // isn't painted, same as the panel.
        }
    }

    private static Button MakeTile(string label, string style = "TileButton",
                                   ImageSource? image = null, IconMode mode = IconMode.Left)
    {
        return new Button
        {
            Content = TileFaces.Draw(label, image, mode),
            Style = (Style)Application.Current.Resources[style],
            // Presses go through to the group behind, which is what a click in the
            // designer means.
            IsHitTestVisible = false,
        };
    }
}
