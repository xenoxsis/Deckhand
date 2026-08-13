using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Deckhand;

// The tiles, the sections that hold them and the profiles that decide which sections are
// on screen — as the config file spells them. Everything here is filled in by the
// deserializer and is otherwise inert: what the panel does with any of it is MainWindow's
// business, which is what keeps this file readable next to dashboard.json.

internal static class ProcessNames
{
    public static string Strip(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}

/// <summary>
/// A titled, bordered block of tiles occupying a rectangle of the dashboard grid.
/// Give it a pinned <see cref="Columns"/>/<see cref="Rows"/> range and it always
/// sits there; give it plain widths and it's packed into the first gap that fits.
/// </summary>
public class SectionEntry
{
    public string Label { get; set; } = "";

    /// <summary>
    /// Profile labels this section belongs to — it's on screen while one of them is the
    /// focused app's profile, and nowhere else. Omit for a section that's always shown.
    ///
    /// This is the whole of what makes a group conditional, and it's written here rather
    /// than in the profile so that everything about a group of buttons — what they do,
    /// where they sit, and when they appear — is in one place to go looking in.
    /// </summary>
    public List<string> Profiles { get; set; } = new();

    /// <summary>
    /// Fills this section with one tile per subdirectory of a folder — see
    /// <see cref="SourceSettings"/>. Any "apps"/"snippets" listed here are appended
    /// after them. When the tiles type commands, gate the section with
    /// <see cref="Profiles"/> naming the windows they're typed into: the profile list
    /// is both what shows the section and what the tap re-checks before sending.
    /// </summary>
    [JsonConverter(typeof(SourceSettingsConverter))]
    public SourceSettings? Source { get; set; }

    /// <summary>
    /// Dashboard columns this section occupies — a width (<c>3</c>) or a pinned
    /// range (<c>"1-3"</c>). Omit for a full-width section whose tiles size
    /// themselves and flow naturally instead of snapping to the grid.
    /// </summary>
    public GridRange? Columns { get; set; }

    /// <summary>
    /// Dashboard rows this section occupies, in the same two forms. Defaults to a
    /// single row.
    /// </summary>
    public GridRange? Rows { get; set; }

    /// <summary>
    /// Tiles per row inside this section. Defaults to the section's width in
    /// columns, so a 4-column section fits four 1-column tiles before wrapping.
    /// </summary>
    public int? TilesPerRow { get; set; }

    public List<AppEntry> Apps { get; set; } = new();
    public List<SnippetEntry> Snippets { get; set; } = new();

    /// <summary>
    /// Whether this section is on screen with <paramref name="profile"/> active. One
    /// that names no profiles always is.
    /// </summary>
    public bool AppliesTo(ProfileEntry? profile) =>
        Profiles.Count == 0
        || (profile is not null
            && Profiles.Any(p => string.Equals(p, profile.Label,
                                               StringComparison.OrdinalIgnoreCase)));
}

/// <summary>
/// A name for an app, and how to recognise it. Nothing else: a profile holds no tiles
/// and no placement, it only decides which of "VS Code", "Git Bash" or nothing at all
/// the focused window counts as. Sections naming it (see
/// <see cref="SectionEntry.Profiles"/>) are what actually appear.
/// </summary>
public class ProfileEntry
{
    public string Label { get; set; } = "";

    /// <summary>
    /// Process names that activate this profile, with or without ".exe"
    /// (e.g. "Code", "OUTLOOK"). Case-insensitive.
    /// </summary>
    public List<string> Match { get; set; } = new();

    /// <summary>
    /// Optional regex the window title must also match, for splitting one app into
    /// several profiles (e.g. only when a particular repo is open in VS Code).
    /// </summary>
    public string? TitleMatch { get; set; }

    private Regex? _titleRegex;
    private bool _titleRegexBuilt;

    public bool Matches(string processName, string windowTitle)
    {
        bool processHit = Match.Any(m =>
            string.Equals(ProcessNames.Strip(m), processName, StringComparison.OrdinalIgnoreCase));
        if (!processHit) return false;

        if (!_titleRegexBuilt)
        {
            _titleRegexBuilt = true;
            if (!string.IsNullOrWhiteSpace(TitleMatch))
            {
                // A malformed pattern is reported by Check() at load, not thrown on
                // every focus change.
                try { _titleRegex = new Regex(TitleMatch, RegexOptions.IgnoreCase); }
                catch (ArgumentException) { _titleRegex = null; }
            }
        }

        if (_titleRegex is not null) return _titleRegex.IsMatch(windowTitle);

        // A pattern that was written but didn't compile matches nothing: matching
        // everything would be broader than what was asked for, and would shadow the
        // profiles below this one for as long as the typo lasted.
        return string.IsNullOrWhiteSpace(TitleMatch);
    }
}

public class AppEntry
{
    public string Label { get; set; } = "";

    /// <summary>
    /// A program, a folder, or an http(s) URL — a URL opens as a new tab rather than
    /// launching anything (see <see cref="Browser"/>).
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// Command line for the program. On a URL tile these are browser switches, so
    /// they only apply when a browser is actually named — see <see cref="Browser"/>.
    /// </summary>
    public string? Args { get; set; }

    /// <summary>
    /// URL tiles only: which browser to open in. Omit to use the focused browser and
    /// fall back to the default one; "default" to always use the default browser; or
    /// a process name like "msedge" to pin the tile to one browser.
    /// </summary>
    public string? Browser { get; set; }

    /// <summary>
    /// Launch at normal user integrity instead of inheriting the dashboard's
    /// elevation. On unless the tile says otherwise: the dashboard is only elevated
    /// so its keystrokes can reach elevated windows, and an editor or folder that
    /// silently inherits administrator leaves elevated files behind (a GVFS working
    /// tree is the canonical casualty). Set "deElevate": false on the rare tile
    /// that should run elevated on purpose.
    /// </summary>
    public bool DeElevate { get; set; } = true;

    /// <summary>
    /// Process names whose windows the tile goes to. Defaults to the program in
    /// <see cref="Path"/>, which is right until the thing that gets launched isn't the
    /// thing that ends up on screen — "git-bash.exe" starts a mintty and exits, "wt.exe"
    /// starts WindowsTerminal — so set it explicitly for a tile that starts another copy
    /// when its windows are plainly open.
    /// </summary>
    public List<string> Windows { get; set; } = new();

    /// <summary>How many of the section's tile columns this tile occupies.</summary>
    public int Span { get; set; } = 1;

    /// <summary>
    /// Accent for the tile's outline, "#RGB" or "#RRGGBB" — the two forms that mean the
    /// same thing to the panel and to a browser, since the tablet paints it too. On a
    /// panel of identical tiles a colour is faster to hit than a label is to read.
    /// </summary>
    public string? Color { get; set; }
}

public class SnippetEntry
{
    public string Label { get; set; } = "";

    /// <summary>
    /// Dropped at the caret. {date} and {time} become the local short date and time at
    /// the moment of the tap, and {clipboard} the text currently on the clipboard —
    /// resolved then rather than when the config was written, like {name} on a folder
    /// tile's command.
    /// </summary>
    public string Text { get; set; } = "";

    /// <summary>"paste" (default: clipboard + Ctrl+V) or "type" (per-character Unicode input).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public InsertMethod Method { get; set; } = InsertMethod.Paste;

    /// <summary>
    /// Keys pressed after the text lands, in order: "tab", "enter", "escape", separated
    /// by spaces or commas. What this is for is the editor's own snippets — insert
    /// "rafce", press Tab, and VS Code expands it with its placeholders selected, which
    /// is something inserted text can't do for itself.
    /// </summary>
    public string? Keys { get; set; }

    /// <summary>How many of the section's tile columns this tile occupies.</summary>
    public int Span { get; set; } = 1;

    /// <summary>Accent for the tile's outline — see <see cref="AppEntry.Color"/>.</summary>
    public string? Color { get; set; }
}

public enum InsertMethod { Paste, Type }
