using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Deckhand;

// Drawing the grid and the tiles on it. The sections are placed on the fixed layout
// grid first — pinned rectangles honoured as written, the rest packed into the gaps —
// and then each section's tiles are laid inside its own scrolling body. The two halves
// of an app tile and the snippet tile are here too: they are the smallest things the
// panel draws, and nothing about what they do is decided until one is tapped.
public partial class MainWindow
{
    /// <summary>
    /// Lays the sections out on a fixed grid of layout.columns × layout.rows.
    /// Both axes are star-sized and the grid fills the window, so a section pinned
    /// to columns 1-3 of 12 gets a quarter of the width whatever else is on screen,
    /// and every group has a definite height to scroll its tiles inside.
    /// </summary>
    private UIElement BuildSectionGrid(List<SectionEntry> sections)
    {
        int totalColumns = Math.Clamp(_config.Layout.Columns, 1, 48);
        int totalRows = Math.Clamp(_config.Layout.Rows, 1, 48);

        var grid = NewStarGrid(totalColumns);
        var placer = new GridPlacer(totalColumns, totalRows);

        foreach (var section in sections)
        {
            if (BuildSection(section) is not { } content) continue;

            var cell = placer.Place(section.Columns, section.Rows);
            Place(grid, content, cell.Row, cell.Column, cell.ColumnSpan, cell.RowSpan, starRows: true);
        }

        // Rows the placer reserved but nothing landed in still hold their share of
        // the height, so a pinned section keeps its size when a neighbour is hidden.
        AddStarRows(grid, placer.RowCount);
        return grid;
    }

    /// <summary>
    /// One section as a bordered, titled box. The tiles live in a ScrollViewer, so
    /// when more of them are configured than the box can show, the box scrolls
    /// instead of the section growing and pushing the grid out of shape.
    /// </summary>
    private FrameworkElement? BuildSection(SectionEntry section)
    {
        // A section written for a profile is off screen unless that profile is the
        // active one: null drops it before the placer,
        // so its cells are left free rather than reserved for something invisible.
        if (!section.AppliesTo(_activeProfile)) return null;

        var tiles = new List<(FrameworkElement Tile, int Span)>();
        string header = section.Label;

        // Worked out before the tiles rather than with the layout below, because a
        // source section's ⇄ switch is as wide as the row: it decides what's underneath
        // it instead of being one of them, whatever width the section was given.
        int tilesPerRow = section.Columns is null
            ? 1
            : Math.Clamp(section.TilesPerRow ?? section.Columns.Value.Length, 1, 24);

        if (section.Source is { } source)
        {
            // No gate of its own: when these tiles appear is the profiles check above,
            // the same as any section. One that types commands should be written for
            // the windows they're typed into — that list is also what the tap
            // re-checks — while one whose tiles open their folders can go anywhere.
            tiles.AddRange(FolderTiles(section, source, tilesPerRow, out string? note));
            if (note is not null) header = $"{header} ({note})";
        }

        tiles.AddRange(section.Apps.Select(a => ((FrameworkElement)AppTile(a), a.Span)));
        tiles.AddRange(section.Snippets.Select(s => ((FrameworkElement)SnippetTile(s), s.Span)));
        if (tiles.Count == 0) return null;

        // A section without an explicit width keeps the original behaviour: tiles
        // size to their content and flow. With a width, they snap to the grid.
        var body = section.Columns is null ? FlowTiles(tiles) : GridTiles(tiles, tilesPerRow);

        var dock = new DockPanel();

        if (!string.IsNullOrWhiteSpace(header))
        {
            var title = new TextBlock
            {
                Text = header.ToUpperInvariant(),
                Style = (Style)Application.Current.Resources["SectionHeader"],
            };
            DockPanel.SetDock(title, Dock.Top);
            dock.Children.Add(title);
        }

        // Fills what the header leaves. Horizontal scrolling is off so tiles wrap
        // rather than hide sideways, and PanningMode makes a finger-drag scroll.
        dock.Children.Add(new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
            Focusable = false,
        });

        return new Border
        {
            Style = (Style)Application.Current.Resources["SectionGroup"],
            Child = dock,
        };
    }

    private static UIElement FlowTiles(List<(FrameworkElement Tile, int Span)> tiles)
    {
        var panel = new WrapPanel();
        foreach (var (tile, _) in tiles) panel.Children.Add(tile);
        return panel;
    }

    private static Grid GridTiles(List<(FrameworkElement Tile, int Span)> tiles, int tilesPerRow)
    {
        var grid = new Grid();
        FillTiles(grid, tiles, tilesPerRow);
        return grid;
    }

    /// <summary>
    /// Lays tiles out on a grid that already exists, replacing whatever was in it.
    /// Separate from <see cref="GridTiles"/> because an app tile's window list is built
    /// when its ▾ is tapped rather than when the panel is drawn — the windows open now
    /// aren't the windows that were open then — and the grid it has to fill is the one
    /// that expander already points at.
    /// </summary>
    private static void FillTiles(Grid grid, List<(FrameworkElement Tile, int Span)> tiles,
                                 int tilesPerRow)
    {
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();
        for (int i = 0; i < tilesPerRow; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        int column = 0, row = 0;
        foreach (var (tile, rawSpan) in tiles)
        {
            int span = Math.Clamp(rawSpan, 1, tilesPerRow);
            if (column + span > tilesPerRow) { column = 0; row++; }

            // The style's MinWidth is for the free-flowing layout; on the grid the
            // cell decides the width, so drop it and let the tile fill the cell.
            tile.MinWidth = 0;
            tile.HorizontalAlignment = HorizontalAlignment.Stretch;

            Place(grid, tile, row, column, span);
            column += span;
        }
    }

    private static Grid NewStarGrid(int columns)
    {
        var grid = new Grid();
        for (int i = 0; i < columns; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        return grid;
    }

    /// <summary>
    /// <paramref name="starRows"/> distinguishes the two grids in play: the dashboard
    /// grid divides a fixed height between star-sized rows, while the tile grid inside
    /// a section sizes its rows to the tiles and lets the section scroll.
    /// </summary>
    private static void Place(Grid grid, FrameworkElement element,
                             int row, int column, int columnSpan,
                             int rowSpan = 1, bool starRows = false)
    {
        while (grid.RowDefinitions.Count < row + rowSpan)
        {
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = starRows ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            });
        }

        Grid.SetRow(element, row);
        Grid.SetRowSpan(element, rowSpan);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
        grid.Children.Add(element);
    }

    private static void AddStarRows(Grid grid, int rows)
    {
        while (grid.RowDefinitions.Count < rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }
    }

    // ---- The tiles themselves ----------------------------------------------

    /// <summary>
    /// An app tile is one control with two sides, divided by a line: the wide side goes to
    /// the program — the window it already has, asking which one when there is more than
    /// one — and the narrow + starts another copy.
    ///
    /// Both sides are always there. Nothing is read here about what the program has open:
    /// that is decided when the tile is tapped, so the tile can't be one focus change out
    /// of date, and the + can never be missing at the moment it's the only thing that
    /// would work — with nothing open, it's what the wide side falls back to anyway.
    /// </summary>
    private FrameworkElement AppTile(AppEntry app)
    {
        var main = Tile(app.Label, () => ActivateApp(app), "SplitTileLeft",
                        app.Color, app.Icon, app.IconMode);

        // The + never wears the picture, whatever the tile asked for: it's the half that
        // opens another window, and on a pair whose wide side has become a logo it is the
        // only thing left with a shape to look for.
        var add = Tile(NewWindow, () => LaunchApp(app), "SplitTileRight", app.Color);

        return SideBySide(main, add, joined: true);
    }

    private Button SnippetTile(SnippetEntry snippet) =>
        Tile(snippet.Label, () => InsertSnippet(snippet), "TileButton",
             snippet.Color, snippet.Icon, snippet.IconMode);

    /// <summary>
    /// One tile wearing what its config asked for. The picture is resolved here, once,
    /// into the two things that are wanted from it — the image this screen draws, and the
    /// hash the tablet will ask for it by — and both go onto the button, because
    /// <see cref="RemoteTiles"/> builds the tablet's copy by walking these buttons rather
    /// than the config they came from.
    /// </summary>
    private Button Tile(string label, Action onClick, string style, string? color,
                        string? icon = null, IconMode mode = IconMode.Left)
    {
        // Null for a tile that asked for no picture and for one whose file couldn't be
        // used. Either way it draws its label, and Check() has already said which.
        var picture = TileImages.Of(_config.SourceFolder, icon);
        var image = picture?.Image;

        var button = MakeTile(label, onClick, style, image, mode);
        Wear(button, new TileFace(Hex(color), image is null ? null : picture!.Hash, mode));
        return button;
    }

    /// <summary>
    /// Paints one tile's outline and remembers its face. On a panel of identical tiles a
    /// colour is faster to hit than a label is to read, which on a touch surface is the
    /// whole game. The face goes into Tag as well as the brush because the tablet draws
    /// this tile too, and Tag is where <see cref="RemoteTiles"/> reads it back without
    /// having to unparse a brush or find the config entry again.
    /// </summary>
    private static void Wear(Button tile, TileFace face)
    {
        tile.Tag = face;
        if (face.Color is null) return;

        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(face.Color));
            brush.Freeze();
            tile.BorderBrush = brush;
        }
        catch (FormatException)
        {
            // Already reported by Check() at load; a colour that won't parse just
            // isn't painted.
        }
    }

    private static string? Hex(string? color) =>
        string.IsNullOrWhiteSpace(color) ? null : color.Trim();

    private Button MakeTile(string label, Action onClick, string style = "TileButton",
                            ImageSource? image = null, IconMode mode = IconMode.Left)
    {
        var button = new Button
        {
            Content = TileFaces.Draw(label, image, mode),
            Style = (Style)Application.Current.Resources[style],
        };

        // The label, kept sayable even when it isn't drawn. This is what the log calls
        // the tile, what a tap from the tablet names it by and what a screen reader
        // reads — none of which a picture can answer for — so it's set on every tile
        // rather than only on the ones that turned out to need it.
        AutomationProperties.SetName(button, label);
        if (image is not null && mode is IconMode.Fill)
        {
            button.ToolTip = label.Replace("\n", " — ");
        }

        button.Click += (_, _) => onClick();
        return button;
    }
}
