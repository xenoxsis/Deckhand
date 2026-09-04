using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Deckhand;

/// <summary>
/// What goes on the face of a tile: its label, its picture, or both in one of two
/// arrangements. Its own class because three views draw the same tile — the panel, the
/// designer's preview of the panel, and the page on the tablet — and the first two are
/// WPF and must not drift. The preview mirrors <see cref="MainWindow"/> rather than
/// calling it, deliberately, because that code is tangled up with taps and window lists;
/// none of this is, so here they share.
///
/// The page draws its own version of these three arrangements in CSS. That one can't be
/// shared, only kept honest — see the .faced rules in remote.html.
/// </summary>
internal static class TileFaces
{
    /// <summary>
    /// The content for one tile. The label alone is a TextBlock rather than a bare string
    /// so that narrow grid cells wrap it instead of overflowing it; a picture joins it
    /// beside, above, or instead of it. Instead still leaves the label on the tooltip and
    /// on the tile's automation name — see <see cref="AppEntry.IconMode"/>.
    ///
    /// Both arrangements are DockPanels rather than StackPanels, and that isn't a
    /// preference: a horizontal StackPanel measures its children against infinite width,
    /// so a label beside an icon would refuse to wrap and run out of its cell.
    ///
    /// The picture sits inside the template's own padding instead of bleeding to the
    /// tile's rounded edge. That's a compromise — the alternative is a control template
    /// per mode — and a picture that stops 12px short still reads as a tile that is a
    /// picture.
    /// </summary>
    public static object Draw(string label, ImageSource? image, IconMode mode)
    {
        var text = new TextBlock
        {
            Text = label,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        if (image is null) return text;

        if (mode is IconMode.Fill) return Picture(image, 88, 480);

        var dock = new DockPanel { LastChildFill = true };

        if (mode is IconMode.Above)
        {
            text.Margin = new Thickness(0, 6, 0, 0);
            DockPanel.SetDock(text, Dock.Bottom);
            dock.Children.Add(text);
            dock.Children.Add(Picture(image, 64, 480));
            return dock;
        }

        // Beside the label: the picture at about text height, so it can't decide how tall
        // a row of buttons is, and no wider than the + on an app tile, so a banner used as
        // an icon can't push the label it belongs to out of the cell.
        var icon = Picture(image, 22, 44);
        icon.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);

        text.VerticalAlignment = VerticalAlignment.Center;
        dock.Children.Add(text);
        return dock;
    }

    /// <summary>
    /// What a tile is called. The automation name is where <c>MakeTile</c> puts it, and is
    /// the only answer for a tile whose face is a picture — there's no text on it to read.
    /// The content is the fallback, for a button declared in XAML rather than built here:
    /// the picker's Cancel is one, and reading only the name left it published to the
    /// tablet as a blank button.
    ///
    /// Both, rather than the name alone with a note to remember to set it, because the
    /// thing that goes wrong is silent on this screen: a XAML tile keeps its text here and
    /// loses its label only over there.
    /// </summary>
    public static string LabelOf(Button tile) =>
        AutomationProperties.GetName(tile) is { Length: > 0 } name
            ? name
            : (tile.Content as TextBlock)?.Text ?? "";

    /// <summary>A picture at its own aspect ratio, bounded. The bounds are what keep a
    /// 512px export from setting the height of the row it landed in.</summary>
    private static Image Picture(ImageSource source, double maxHeight, double maxWidth) =>
        new()
        {
            Source = source,
            Stretch = Stretch.Uniform,
            MaxHeight = maxHeight,
            MaxWidth = maxWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
}
