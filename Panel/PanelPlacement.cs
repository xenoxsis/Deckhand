using System.IO;
using System.Text.Json;
using System.Windows;

namespace Deckhand;

/// <summary>
/// Where the panel sits and how big it is, remembered between runs so arranging it
/// once is enough — including after locking it into a FancyZones zone.
///
/// Deliberately not in dashboard.json: that file is hand-edited and full of comments,
/// and rewriting it every time the window moves would strip them. %LOCALAPPDATA% also
/// survives a rebuild, which the bin directory does not.
/// </summary>
internal sealed record PanelPlacement(double Left, double Top, double Width, double Height)
{
    /// <summary>How much of the title bar must be on a screen to be grabbable.</summary>
    private const double MinReachable = 120;

    /// <summary>The panel's own placement.</summary>
    public const string PanelFile = "panel.json";

    /// <summary>
    /// The remote status window's placement, kept separately: the two windows are never
    /// on screen in the same run, but they're different shapes and each should come back
    /// where it was rather than where the other one was left.
    /// </summary>
    public const string RemoteFile = "remote-window.json";

    /// <summary>The designer window's placement — a third shape again, kept apart for
    /// the same reason as the other two.</summary>
    public const string DesignerFile = "designer-window.json";

    private static string PathOf(string file) =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Deckhand", file);

    /// <summary>The remembered placement, or null to fall back to the default dock position.</summary>
    public static PanelPlacement? Load(string file = PanelFile)
    {
        try
        {
            string path = PathOf(file);
            if (!File.Exists(path)) return null;

            var placement = JsonSerializer.Deserialize<PanelPlacement>(File.ReadAllText(path));
            return placement?.IsReachable() == true ? placement : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Best effort — failing to remember a placement isn't worth interrupting for.</summary>
    public void Save(string file = PanelFile)
    {
        try
        {
            string path = PathOf(file);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignored.
        }
    }

    /// <summary>
    /// Whether the panel would come back somewhere it can actually be reached. Guards
    /// against a zero size, and against a placement saved on a monitor that has since
    /// been unplugged putting the panel off every screen with no title bar to grab.
    /// </summary>
    private bool IsReachable()
    {
        if (Width <= 0 || Height <= 0) return false;

        var screens = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                               SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        // Only the title bar matters: that's what a stranded panel is dragged back by.
        var titleBar = Rect.Intersect(screens, new Rect(Left, Top, Width, 40));
        return !titleBar.IsEmpty && titleBar.Width >= MinReachable && titleBar.Height >= 10;
    }
}
