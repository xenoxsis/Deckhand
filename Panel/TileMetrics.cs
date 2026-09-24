using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace Deckhand;

/// <summary>
/// What a tile looks like — the numbers and the palette — in the one place both screens
/// read them from.
///
/// The panel and the tablet draw the same dashboard with different machinery: WPF styles
/// in App.xaml on this end, CSS in remote.html on the other. Nothing stops those two from
/// describing slightly different tiles, and for a while they did — the tablet's were four
/// pixels taller and two roomier than the panel's, which nobody wrote on purpose and
/// nobody noticed either. Numbers written twice drift, so these are written once: App.xaml
/// reaches them with x:Static, <see cref="TileFaces"/> uses them directly, and
/// <see cref="Css"/> hands the same values to the page as custom properties. Change one
/// here and both screens change together.
///
/// Only what both screens draw belongs here. A bound that exists on one side alone —
/// <see cref="PictureMaxWidth"/>, which is WPF needing a finite width where the browser
/// says 100% — stays a plain constant next to the code that uses it.
/// </summary>
public static class TileMetrics
{
    // ---- Palette -----------------------------------------------------------
    // Hex strings are the source: they are what the page needs, and what the config
    // spells a tile's own accent in. The brushes below are the same colours for XAML.

    /// <summary>Behind everything — the panel's own backdrop.</summary>
    public const string BackgroundHex = "#1E1E1E";

    /// <summary>The box around one section.</summary>
    public const string GroupHex = "#252526";

    /// <summary>A tile's face.</summary>
    public const string TileHex = "#2D2D30";

    /// <summary>Every outline: tiles, groups and the panel's own border.</summary>
    public const string LineHex = "#3F3F46";

    /// <summary>Label text.</summary>
    public const string TextHex = "#EAEAEA";

    /// <summary>Section headers and other text that isn't being read so much as scanned.</summary>
    public const string DimHex = "#8A8A8A";

    /// <summary>A tile being pressed, and the outline under the pointer.</summary>
    public const string AccentHex = "#007ACC";

    public static readonly SolidColorBrush Background = Brush(BackgroundHex);
    public static readonly SolidColorBrush Group = Brush(GroupHex);
    public static readonly SolidColorBrush Tile = Brush(TileHex);
    public static readonly SolidColorBrush Line = Brush(LineHex);
    public static readonly SolidColorBrush Text = Brush(TextHex);
    public static readonly SolidColorBrush Dim = Brush(DimHex);
    public static readonly SolidColorBrush Accent = Brush(AccentHex);

    // ---- The tile ----------------------------------------------------------

    /// <summary>How short a tile may be. A finger, not a pointer, is what lands on it.</summary>
    public const double MinHeight = 56;

    /// <summary>Label inset, left and right.</summary>
    public const double PaddingX = 12;

    /// <summary>Label inset, top and bottom.</summary>
    public const double PaddingY = 8;

    public const double Radius = 8;
    public const double BorderWidth = 1;
    public const double FontSize = 16;

    /// <summary>
    /// Clear space around a tile. WPF puts this on each tile as a margin, so the space
    /// between two of them is twice it and the page's grid gap is <see cref="Gap"/> —
    /// the same distance, counted the way each side counts it.
    /// </summary>
    public const double Margin = 4;

    /// <summary>The space between two tiles: see <see cref="Margin"/>.</summary>
    public const double Gap = Margin * 2;

    /// <summary>The section box's corners, a little rounder than the tiles inside it.</summary>
    public const double GroupRadius = 10;

    // ---- A tile's picture --------------------------------------------------

    /// <summary>Beside the label: short enough to sit on one line of text.</summary>
    public const double IconLeftHeight = 22;

    /// <summary>And no wider than that, so a banner can't crowd the label out of a narrow tile.</summary>
    public const double IconLeftWidth = 44;

    /// <summary>Above the label, with room left for it underneath.</summary>
    public const double IconAboveHeight = 64;

    /// <summary>The whole face, the label kept as the tooltip.</summary>
    public const double IconFillHeight = 88;

    /// <summary>Between a picture and the label beside it.</summary>
    public const double IconGap = 8;

    /// <summary>
    /// And between a picture and the label under it, which is tighter: stacked, the two
    /// already read as one thing, and the space that separates them side by side only
    /// makes the tile taller.
    /// </summary>
    public const double IconStackGap = 6;

    /// <summary>
    /// How wide a picture may be drawn. WPF wants a finite bound where the browser can
    /// say 100%, and this is only ever that bound: it is deliberately wider than a tile,
    /// so what actually limits the picture is the tile it's in.
    /// </summary>
    public const double PictureMaxWidth = 480;

    // ---- XAML ---------------------------------------------------------------
    // Setters and templates need these as the types the properties take. Built here
    // rather than written in the markup so the numbers above stay the only copy.

    public static readonly Thickness TileMargin = new(Margin);
    public static readonly Thickness SplitLeftMargin = new(Margin, Margin, 0, Margin);
    public static readonly Thickness SplitRightMargin = new(0, Margin, Margin, Margin);
    public static readonly Thickness LabelPadding = new(PaddingX, PaddingY, PaddingX, PaddingY);

    /// <summary>The narrow half of a split tile: no side inset, the ▾ is centred in it.</summary>
    public static readonly Thickness NarrowLabelPadding = new(0, PaddingY, 0, PaddingY);

    public static readonly Thickness Border = new(BorderWidth);

    /// <summary>The left half of a split tile, whose right border is the pair's divider.</summary>
    public static readonly Thickness SplitRightBorder = new(0, BorderWidth, BorderWidth, BorderWidth);

    public static readonly CornerRadius Corner = new(Radius);
    public static readonly CornerRadius SplitLeftCorner = new(Radius, 0, 0, Radius);
    public static readonly CornerRadius SplitRightCorner = new(0, Radius, Radius, 0);
    public static readonly CornerRadius GroupCorner = new(GroupRadius);

    // ---- The page ----------------------------------------------------------

    /// <summary>
    /// The same values as CSS custom properties, for the <c>:root</c> block of the served
    /// page. Declarations only — the page's own stylesheet decides what to do with them,
    /// and keeps the properties it holds alone (the viewport height it measures for
    /// itself is not ours to set).
    ///
    /// Lengths carry their unit, colours are lower-cased because that is how the rest of
    /// the page's CSS is written, and everything is formatted invariantly: a machine set
    /// to a comma decimal separator must not serve a stylesheet the browser can't parse.
    /// </summary>
    public static string Css()
    {
        var css = new StringBuilder();

        Color("bg", BackgroundHex);
        Color("group", GroupHex);
        Color("tile", TileHex);
        Color("line", LineHex);
        Color("text", TextHex);
        Color("dim", DimHex);
        Color("accent", AccentHex);
        css.AppendLine();

        Length("tile-min-height", MinHeight);
        Length("tile-pad-x", PaddingX);
        Length("tile-pad-y", PaddingY);
        Length("tile-radius", Radius);
        Length("tile-border", BorderWidth);
        Length("tile-font", FontSize);
        Length("tile-gap", Gap);
        Length("group-radius", GroupRadius);
        css.AppendLine();

        Length("icon-left-height", IconLeftHeight);
        Length("icon-left-width", IconLeftWidth);
        Length("icon-above-height", IconAboveHeight);
        Length("icon-fill-height", IconFillHeight);
        Length("icon-gap", IconGap);
        Length("icon-stack-gap", IconStackGap);

        return css.ToString().TrimEnd();

        void Color(string name, string hex) =>
            css.Append("    --").Append(name).Append(": ")
               .Append(hex.ToLowerInvariant()).AppendLine(";");

        void Length(string name, double value) =>
            css.Append("    --").Append(name).Append(": ")
               .Append(value.ToString("0.###", CultureInfo.InvariantCulture))
               .AppendLine("px;");
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        // Frozen so one brush can be shared by every tile on the panel: an unfrozen one
        // would be cloned per element and would keep a change notification alive for each.
        brush.Freeze();
        return brush;
    }
}
