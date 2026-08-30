using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Deckhand;

/// <summary>
/// Builds dashboard.json by arrangement instead of typing: the grid, the groups on it
/// and the buttons in them on the left, and on the right the panel as it will be
/// drawn, rebuilt on every change. Saving writes a fresh file through
/// <see cref="ConfigWriter"/> — the one thing this window never does is rewrite a
/// hand-edited file unannounced, because a serializer strips the comments those files
/// are full of, so overwriting an existing file is asked about first.
///
/// It edits the whole model but only part of it visually: profiles, the remote block
/// and a section's folder scan are carried through a load and a save untouched, so
/// opening a working config here and saving it back costs none of them.
/// </summary>
public partial class DesignerWindow : Window
{
    private DashboardConfig _config = new();

    /// <summary>Where Save writes without asking where. Starts as the file the panel
    /// itself would read, which is almost always the file being designed.</summary>
    private string _path;

    private SectionEntry? _section;

    /// <summary>The selected button: an <see cref="AppEntry"/> or a
    /// <see cref="SnippetEntry"/>, the two having no common base to name.</summary>
    private object? _tile;

    private bool _dirty;

    /// <summary>True while code is filling the fields, so TextChanged knows the
    /// difference between the user typing and itself talking.</summary>
    private bool _updating;

    /// <summary>Files whose "comments will be lost" question was already answered yes,
    /// so saving twice in a row doesn't nag.</summary>
    private readonly HashSet<string> _confirmedOverwrites = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The screen the preview is pretending to be, in pixels — a tablet's resolution,
    /// usually. Both set, the preview is laid out at exactly that size and shrunk to
    /// fit the pane; either blank, it just fills the pane. Not part of the config:
    /// it's about this window, so it lives beside the window placement instead.
    /// </summary>
    private int? _screenWidth;
    private int? _screenHeight;

    private static readonly Brush LineBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
    private static readonly Brush BadBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0x24, 0x2F));
    private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
    private static readonly Brush SelectedRow = new SolidColorBrush(Color.FromRgb(0x09, 0x47, 0x71));
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
    private static readonly Brush TileBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));

    public DesignerWindow()
    {
        InitializeComponent();

        if (PanelPlacement.Load(PanelPlacement.DesignerFile) is { } saved)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = saved.Left;
            Top = saved.Top;
            Width = Math.Max(MinWidth, saved.Width);
            Height = Math.Max(MinHeight, saved.Height);
        }

        (_screenWidth, _screenHeight) = LoadPreviewSize();
        _updating = true;
        ScreenWidthBox.Text = _screenWidth?.ToString() ?? "";
        ScreenHeightBox.Text = _screenHeight?.ToString() ?? "";
        _updating = false;
        UpdatePreviewSizing();

        _path = DashboardConfig.ResolvePath();
        LoadFrom(_path);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_dirty)
        {
            var answer = MessageBox.Show(
                "Save the layout before closing?", "Deckhand — designer",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel
                || (answer == MessageBoxResult.Yes && !SaveTo(_path)))
            {
                e.Cancel = true;
                return;
            }
        }

        new PanelPlacement(Left, Top, Width, Height).Save(PanelPlacement.DesignerFile);
        SavePreviewSize();
        base.OnClosing(e);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SaveTo(_path);
            e.Handled = true;
        }
    }

    // ---- Loading and saving -------------------------------------------------

    private void LoadFrom(string path)
    {
        var config = DashboardConfig.LoadFile(path, out string? error);
        if (error is not null)
        {
            MessageBox.Show($"The file couldn't be read — starting from a blank layout "
                            + $"instead.\n\n{error}",
                            "Deckhand — designer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // The "apps"/"snippets" shorthand becomes the sections it already means
        // (see EffectiveSections), so those tiles are editable here like any others.
        if (config.Apps.Count > 0)
        {
            config.Sections.Add(new SectionEntry { Label = "Apps", Apps = config.Apps });
            config.Apps = new List<AppEntry>();
        }
        if (config.Snippets.Count > 0)
        {
            config.Sections.Add(new SectionEntry { Label = "Snippets", Snippets = config.Snippets });
            config.Snippets = new List<SnippetEntry>();
        }

        _config = config;
        _path = path;
        _section = config.Sections.FirstOrDefault();
        _tile = null;
        _dirty = false;

        RefreshAll();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_dirty)
        {
            var keep = MessageBox.Show(
                "There are unsaved changes — opening another file loses them. Open anyway?",
                "Deckhand — designer", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (keep != MessageBoxResult.Yes) return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Dashboard config (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = Path.GetDirectoryName(_path) ?? "",
            FileName = Path.GetFileName(_path),
        };
        if (dialog.ShowDialog(this) != true) return;

        LoadFrom(dialog.FileName);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveTo(_path);

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Dashboard config (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = Path.GetDirectoryName(_path) ?? "",
            FileName = Path.GetFileName(_path),
            // The overwrite question is ours: it has more to say than "replace?" —
            // see SaveTo.
            OverwritePrompt = false,
        };
        if (dialog.ShowDialog(this) != true) return;

        SaveTo(dialog.FileName);
    }

    /// <summary>Writes the layout, true when it was actually written. The one question
    /// on the way is the comments one: this rewrites the whole file, and a hand-edited
    /// dashboard.json is usually full of comments a serializer can't keep.</summary>
    private bool SaveTo(string path)
    {
        if (File.Exists(path) && !_confirmedOverwrites.Contains(path))
        {
            var answer = MessageBox.Show(
                $"{path} already exists.\n\nThe designer writes the whole file fresh, so "
                + "any comments and formatting in the existing one are lost — the "
                + "settings themselves are kept, including the parts the designer "
                + "doesn't edit. Overwrite it?",
                "Deckhand — designer", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return false;
            _confirmedOverwrites.Add(path);
        }

        try
        {
            File.WriteAllText(path, ConfigWriter.Write(_config));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"The file couldn't be written.\n\n{ex.Message}",
                            "Deckhand — designer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _path = path;
        _dirty = false;
        UpdateStatus();
        return true;
    }

    private void Touch()
    {
        _dirty = true;
        RefreshPreview();
        UpdateStatus();
    }

    private void UpdateStatus() =>
        StatusText.Text = _dirty ? $"{_path}  —  unsaved changes" : _path;

    // ---- The grid -----------------------------------------------------------

    private void GridColumnsDown_Click(object sender, RoutedEventArgs e) => Bump(GridColumnsBox, -1);
    private void GridColumnsUp_Click(object sender, RoutedEventArgs e) => Bump(GridColumnsBox, +1);
    private void GridRowsDown_Click(object sender, RoutedEventArgs e) => Bump(GridRowsBox, -1);
    private void GridRowsUp_Click(object sender, RoutedEventArgs e) => Bump(GridRowsBox, +1);

    private static void Bump(TextBox box, int by)
    {
        int value = int.TryParse(box.Text, out int current) ? current : 12;
        box.Text = Math.Clamp(value + by, 1, 48).ToString();
    }

    private void GridColumns_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        bool ok = int.TryParse(GridColumnsBox.Text, out int value) && value is >= 1 and <= 48;
        Mark(GridColumnsBox, ok);
        if (!ok) return;
        _config.Layout.Columns = value;
        PreviewSizeText.Text = GridCaption();
        Touch();
    }

    private void GridRows_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        bool ok = int.TryParse(GridRowsBox.Text, out int value) && value is >= 1 and <= 48;
        Mark(GridRowsBox, ok);
        if (!ok) return;
        _config.Layout.Rows = value;
        PreviewSizeText.Text = GridCaption();
        Touch();
    }

    private string GridCaption() => $"{_config.Layout.Columns} × {_config.Layout.Rows} grid";

    // ---- The group list -----------------------------------------------------

    private void AddSection_Click(object sender, RoutedEventArgs e)
    {
        // A width and a height rather than a pin, so the new group lands in the first
        // gap and is on screen immediately — pinning is a decision for once it has
        // somewhere it belongs.
        var section = new SectionEntry
        {
            Label = "New group",
            Columns = new GridRange(null, Math.Min(4, _config.Layout.Columns)),
            Rows = new GridRange(null, Math.Min(3, _config.Layout.Rows)),
        };
        _config.Sections.Add(section);
        Select(section);
        Touch();
    }

    /// <summary>Selecting a group, from the list or from a click in the preview.</summary>
    private void Select(SectionEntry section)
    {
        _section = section;
        _tile = null;
        RefreshSectionList();
        RefreshSectionEditor();
        RefreshPreview();
    }

    private void RefreshSectionList()
    {
        SectionList.Children.Clear();

        for (int i = 0; i < _config.Sections.Count; i++)
        {
            var section = _config.Sections[i];
            SectionList.Children.Add(ListRow(
                title: string.IsNullOrWhiteSpace(section.Label) ? "(unnamed)" : section.Label,
                detail: PlacementSummary(section),
                selected: section == _section,
                onSelect: () => Select(section),
                onUp: i == 0 ? null : () => { Swap(_config.Sections, i); Touch(); RefreshSectionList(); },
                onDown: i == _config.Sections.Count - 1
                    ? null
                    : () => { Swap(_config.Sections, i + 1); Touch(); RefreshSectionList(); },
                onDelete: () =>
                {
                    _config.Sections.Remove(section);
                    if (_section == section) { _section = _config.Sections.FirstOrDefault(); _tile = null; }
                    RefreshSectionList();
                    RefreshSectionEditor();
                    Touch();
                }));
        }
    }

    private static string PlacementSummary(SectionEntry section)
    {
        string columns = section.Columns is { } c
            ? c.Start is null ? $"w{c.Length}" : c.ToString()
            : "flow";
        string extra = section.Source is null ? "" : " · 📁";
        return section.Rows is { } r ? $"{columns} × {r}{extra}" : $"{columns}{extra}";
    }

    /// <summary>One row of either list: a wide select button, then ▲ ▼ ✕. A null
    /// move handler is an end of the list, shown as a disabled button so the row
    /// keeps its shape.</summary>
    private FrameworkElement ListRow(string title, string detail, bool selected,
                                     Action onSelect, Action? onUp, Action? onDown,
                                     Action onDelete)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };

        var delete = MiniButton("✕", "Remove", onDelete);
        DockPanel.SetDock(delete, Dock.Right);
        row.Children.Add(delete);

        var down = MiniButton("▼", "Move down", onDown);
        DockPanel.SetDock(down, Dock.Right);
        row.Children.Add(down);

        var up = MiniButton("▲", "Move up", onUp);
        DockPanel.SetDock(up, Dock.Right);
        row.Children.Add(up);

        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        text.Inlines.Add(new Run(title));
        if (detail.Length > 0)
        {
            text.Inlines.Add(new Run($"   {detail}") { Foreground = DimBrush, FontSize = 11 });
        }

        var select = new Button
        {
            Style = (Style)Resources["RowButton"],
            Content = text,
        };
        if (selected) select.Background = SelectedRow;
        select.Click += (_, _) => onSelect();
        row.Children.Add(select);

        return row;
    }

    private Button MiniButton(string glyph, string tip, Action? onClick)
    {
        var button = new Button
        {
            Style = (Style)Resources["MiniButton"],
            Content = glyph,
            ToolTip = tip,
            FontSize = 11,
            IsEnabled = onClick is not null,
        };
        if (onClick is not null) button.Click += (_, _) => onClick();
        return button;
    }

    private static void Swap<T>(List<T> list, int upper)
    {
        (list[upper - 1], list[upper]) = (list[upper], list[upper - 1]);
    }

    // ---- The selected group -------------------------------------------------

    private void RefreshSectionEditor()
    {
        if (_section is not { } section)
        {
            SectionEditor.Visibility = Visibility.Collapsed;
            TileEditor.Visibility = Visibility.Collapsed;
            return;
        }

        SectionEditor.Visibility = Visibility.Visible;

        _updating = true;
        SectionLabelBox.Text = section.Label;
        SectionColumnsBox.Text = section.Columns?.ToString() ?? "";
        SectionRowsBox.Text = section.Rows?.ToString() ?? "";
        SectionPerRowBox.Text = section.TilesPerRow?.ToString() ?? "";
        SectionProfilesBox.Text = string.Join(", ", section.Profiles);
        Mark(SectionColumnsBox, ok: true);
        Mark(SectionRowsBox, ok: true);
        Mark(SectionPerRowBox, ok: true);
        _updating = false;

        SourceNote.Visibility = section.Source is null ? Visibility.Collapsed : Visibility.Visible;

        RefreshTileList();
        RefreshTileEditor();
    }

    private void SectionLabel_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updating || _section is not { } section) return;
        section.Label = SectionLabelBox.Text;
        RefreshSectionList();
        Touch();
    }

    private void SectionColumns_Changed(object sender, TextChangedEventArgs e) =>
        ApplyRange(SectionColumnsBox, range => { if (_section is { } s) s.Columns = range; });

    private void SectionRows_Changed(object sender, TextChangedEventArgs e) =>
        ApplyRange(SectionRowsBox, range => { if (_section is { } s) s.Rows = range; });

    /// <summary>Both range fields read the config's own two spellings — a width, or a
    /// pinned "1-3" — with blank meaning the setting isn't written at all.</summary>
    private void ApplyRange(TextBox box, Action<GridRange?> apply)
    {
        if (_updating || _section is null) return;

        string text = box.Text.Trim();
        if (text.Length == 0)
        {
            Mark(box, ok: true);
            apply(null);
            Touch();
            return;
        }

        bool ok = GridRange.TryParse(text, out var range);
        Mark(box, ok);
        if (!ok) return;

        apply(range);
        Touch();
    }

    private void SectionPerRow_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updating || _section is not { } section) return;

        string text = SectionPerRowBox.Text.Trim();
        if (text.Length == 0)
        {
            Mark(SectionPerRowBox, ok: true);
            section.TilesPerRow = null;
            Touch();
            return;
        }

        bool ok = int.TryParse(text, out int value) && value is >= 1 and <= 24;
        Mark(SectionPerRowBox, ok);
        if (!ok) return;

        section.TilesPerRow = value;
        Touch();
    }

    private void SectionProfiles_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updating || _section is not { } section) return;
        section.Profiles = SectionProfilesBox.Text
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        Touch();
    }

    // ---- The button list ----------------------------------------------------

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        if (_section is not { } section) return;
        var app = new AppEntry { Label = "New app" };
        section.Apps.Add(app);
        _tile = app;
        RefreshTileList();
        RefreshTileEditor();
        Touch();
    }

    private void AddSnippet_Click(object sender, RoutedEventArgs e)
    {
        if (_section is not { } section) return;
        var snippet = new SnippetEntry { Label = "New snippet" };
        section.Snippets.Add(snippet);
        _tile = snippet;
        RefreshTileList();
        RefreshTileEditor();
        Touch();
    }

    private void RefreshTileList()
    {
        TileList.Children.Clear();
        if (_section is not { } section) return;

        if (section.Source is not null)
        {
            TileList.Children.Add(new TextBlock
            {
                Text = "📁 folder tiles come first, from the scan",
                Foreground = DimBrush,
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(8, 4, 0, 2),
            });
        }

        for (int i = 0; i < section.Apps.Count; i++)
        {
            var app = section.Apps[i];
            int index = i;
            TileList.Children.Add(ListRow(
                title: string.IsNullOrWhiteSpace(app.Label) ? "(unnamed)" : app.Label,
                detail: "app",
                selected: _tile == app,
                onSelect: () => { _tile = app; RefreshTileList(); RefreshTileEditor(); },
                onUp: index == 0 ? null : () => { Swap(section.Apps, index); Touch(); RefreshTileList(); },
                onDown: index == section.Apps.Count - 1
                    ? null
                    : () => { Swap(section.Apps, index + 1); Touch(); RefreshTileList(); },
                onDelete: () =>
                {
                    section.Apps.Remove(app);
                    if (_tile == app) _tile = null;
                    RefreshTileList();
                    RefreshTileEditor();
                    Touch();
                }));
        }

        for (int i = 0; i < section.Snippets.Count; i++)
        {
            var snippet = section.Snippets[i];
            int index = i;
            TileList.Children.Add(ListRow(
                title: string.IsNullOrWhiteSpace(snippet.Label) ? "(unnamed)" : snippet.Label,
                detail: "snippet",
                selected: _tile == snippet,
                onSelect: () => { _tile = snippet; RefreshTileList(); RefreshTileEditor(); },
                onUp: index == 0 ? null : () => { Swap(section.Snippets, index); Touch(); RefreshTileList(); },
                onDown: index == section.Snippets.Count - 1
                    ? null
                    : () => { Swap(section.Snippets, index + 1); Touch(); RefreshTileList(); },
                onDelete: () =>
                {
                    section.Snippets.Remove(snippet);
                    if (_tile == snippet) _tile = null;
                    RefreshTileList();
                    RefreshTileEditor();
                    Touch();
                }));
        }
    }

    // ---- The selected button ------------------------------------------------

    private void RefreshTileEditor()
    {
        if (_tile is null)
        {
            TileEditor.Visibility = Visibility.Collapsed;
            return;
        }

        TileEditor.Visibility = Visibility.Visible;

        _updating = true;
        switch (_tile)
        {
            case AppEntry app:
                TileKindLabel.Text = "APP BUTTON — LABEL";
                AppFields.Visibility = Visibility.Visible;
                SnippetFields.Visibility = Visibility.Collapsed;
                TileLabelBox.Text = app.Label;
                TilePathBox.Text = app.Path;
                TileArgsBox.Text = app.Args ?? "";
                TileSpanBox.Text = app.Span.ToString();
                TileColorBox.Text = app.Color ?? "";
                break;

            case SnippetEntry snippet:
                TileKindLabel.Text = "SNIPPET BUTTON — LABEL";
                AppFields.Visibility = Visibility.Collapsed;
                SnippetFields.Visibility = Visibility.Visible;
                TileLabelBox.Text = snippet.Label;
                // The box shows real newlines; the config file's \n is the
                // serializer's business.
                TileTextBox.Text = snippet.Text;
                TileKeysBox.Text = snippet.Keys ?? "";
                TileSpanBox.Text = snippet.Span.ToString();
                TileColorBox.Text = snippet.Color ?? "";
                UpdateMethodButtons(snippet.Method);
                break;
        }
        Mark(TileSpanBox, ok: true);
        Mark(TileColorBox, ok: true);
        _updating = false;
    }

    private void TileLabel_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        switch (_tile)
        {
            case AppEntry app: app.Label = TileLabelBox.Text; break;
            case SnippetEntry snippet: snippet.Label = TileLabelBox.Text; break;
            default: return;
        }
        RefreshTileList();
        Touch();
    }

    private void TilePath_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updating || _tile is not AppEntry app) return;
        app.Path = TilePathBox.Text;
        Touch();
    }

    private void TileArgs_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updating || _tile is not AppEntry app) return;
        app.Args = Blank(TileArgsBox.Text);
        Touch();
    }

    private void BrowsePath_Click(object sender, RoutedEventArgs e)
    {
        if (_tile is not AppEntry) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            Title = "What should this button launch?",
        };
        if (dialog.ShowDialog(this) != true) return;

        // Through the box rather than the model, so the one TextChanged path applies
        // it, marks the window dirty and redraws the preview.
        TilePathBox.Text = dialog.FileName;
    }

    private void TileText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updating || _tile is not SnippetEntry snippet) return;
        // The box's line breaks arrive as \r\n; the panel types \n.
        snippet.Text = TileTextBox.Text.Replace("\r\n", "\n");
        Touch();
    }

    private void TileKeys_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updating || _tile is not SnippetEntry snippet) return;
        snippet.Keys = Blank(TileKeysBox.Text);
        Touch();
    }

    private void MethodPaste_Click(object sender, RoutedEventArgs e) => SetMethod(InsertMethod.Paste);

    private void MethodType_Click(object sender, RoutedEventArgs e) => SetMethod(InsertMethod.Type);

    private void SetMethod(InsertMethod method)
    {
        if (_tile is not SnippetEntry snippet) return;
        snippet.Method = method;
        UpdateMethodButtons(method);
        Touch();
    }

    private void UpdateMethodButtons(InsertMethod method)
    {
        MethodPasteButton.Background = method == InsertMethod.Paste ? AccentBrush : TileBrush;
        MethodTypeButton.Background = method == InsertMethod.Type ? AccentBrush : TileBrush;
    }

    private void SpanDown_Click(object sender, RoutedEventArgs e) => BumpSpan(-1);
    private void SpanUp_Click(object sender, RoutedEventArgs e) => BumpSpan(+1);

    private void BumpSpan(int by)
    {
        int value = int.TryParse(TileSpanBox.Text, out int current) ? current : 1;
        TileSpanBox.Text = Math.Clamp(value + by, 1, 24).ToString();
    }

    private void TileSpan_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updating || _tile is null) return;

        bool ok = int.TryParse(TileSpanBox.Text, out int value) && value is >= 1 and <= 24;
        Mark(TileSpanBox, ok);
        if (!ok) return;

        switch (_tile)
        {
            case AppEntry app: app.Span = value; break;
            case SnippetEntry snippet: snippet.Span = value; break;
        }
        Touch();
    }

    private void TileColor_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updating || _tile is null) return;

        string? color = Blank(TileColorBox.Text);
        // The same two forms the loader accepts — see DashboardConfig.ValidColor.
        bool ok = color is null
                  || Regex.IsMatch(color, "^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$");
        Mark(TileColorBox, ok);
        if (!ok) return;

        switch (_tile)
        {
            case AppEntry app: app.Color = color; break;
            case SnippetEntry snippet: snippet.Color = color; break;
        }
        Touch();
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        // Through the box, so the one TextChanged path validates and applies it.
        if (sender is Button { Tag: string hex }) TileColorBox.Text = hex;
    }

    private void SwatchNone_Click(object sender, RoutedEventArgs e) => TileColorBox.Text = "";

    // ---- Shared field plumbing ----------------------------------------------

    /// <summary>A field the model reader couldn't take is outlined red and left
    /// unapplied, so the preview never shows something the file wouldn't mean.</summary>
    private static void Mark(TextBox box, bool ok) => box.BorderBrush = ok ? LineBrush : BadBrush;

    private static string? Blank(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    // ---- The pretend screen -----------------------------------------------------

    private void ScreenSize_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        _screenWidth = ParseScreenSize(ScreenWidthBox);
        _screenHeight = ParseScreenSize(ScreenHeightBox);
        UpdatePreviewSizing();
    }

    /// <summary>Blank is "no pretend screen" and fine; anything else must be a pixel
    /// count a screen could have, or the box goes red and the size isn't used.</summary>
    private static int? ParseScreenSize(TextBox box)
    {
        string text = box.Text.Trim();
        if (text.Length == 0)
        {
            Mark(box, ok: true);
            return null;
        }

        bool ok = int.TryParse(text, out int value) && value is >= 100 and <= 10000;
        Mark(box, ok);
        return ok ? value : null;
    }

    private void PreviewArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Only the pane-filling mode follows the pane; a pretend screen holds its
        // size and lets the Viewbox do the shrinking.
        if (_screenWidth is null || _screenHeight is null) UpdatePreviewSizing();
    }

    private void UpdatePreviewSizing()
    {
        if (_screenWidth is { } width && _screenHeight is { } height)
        {
            PreviewShell.Width = width;
            PreviewShell.Height = height;
        }
        else
        {
            // Exactly the pane, so the DownOnly Viewbox has nothing to scale and the
            // preview behaves as if the Viewbox weren't there.
            PreviewShell.Width = Math.Max(1, PreviewArea.ActualWidth);
            PreviewShell.Height = Math.Max(1, PreviewArea.ActualHeight);
        }
    }

    /// <summary>The remembered screen size — designer furniture, not config, so it
    /// lives in %LOCALAPPDATA% beside the window placements.</summary>
    private sealed record PreviewSize(int? Width, int? Height);

    private static string PreviewSizePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Deckhand", "designer-preview.json");

    private static (int? Width, int? Height) LoadPreviewSize()
    {
        try
        {
            if (!File.Exists(PreviewSizePath)) return (null, null);
            var size = System.Text.Json.JsonSerializer.Deserialize<PreviewSize>(
                File.ReadAllText(PreviewSizePath));
            return (size?.Width, size?.Height);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException
                                       or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    private void SavePreviewSize()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreviewSizePath)!);
            File.WriteAllText(PreviewSizePath, System.Text.Json.JsonSerializer.Serialize(
                new PreviewSize(_screenWidth, _screenHeight)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Forgetting a preview size isn't worth interrupting a close for.
        }
    }

    // ---- The preview ----------------------------------------------------------

    private void RefreshAll()
    {
        _updating = true;
        GridColumnsBox.Text = _config.Layout.Columns.ToString();
        GridRowsBox.Text = _config.Layout.Rows.ToString();
        Mark(GridColumnsBox, ok: true);
        Mark(GridRowsBox, ok: true);
        _updating = false;

        PreviewSizeText.Text = GridCaption();
        RefreshSectionList();
        RefreshSectionEditor();
        RefreshPreview();
        UpdateStatus();
    }

    private void RefreshPreview() =>
        PreviewHost.Content = DesignerPreview.Build(_config, _section, section =>
        {
            // Only groups the designer holds are selectable — belt and braces; after
            // the shorthand fold there are no synthetic ones left to click.
            if (_config.Sections.Contains(section)) Select(section);
        });
}
