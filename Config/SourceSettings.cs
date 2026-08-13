using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deckhand;

/// <summary>
/// Fills a section with one tile per subdirectory of a folder. This is the general
/// machinery behind what used to be a hard-coded "branches" block: the dashboard
/// scans a directory, splits the entries in two if asked, and either types a command
/// template into the focused window or opens the folder itself. Everything that made
/// it about branches — which folder, which regex, which commands — is content
/// written here, not knowledge built in, so a section pointed at a projects
/// directory with no command at all is a self-maintaining launcher, and the same
/// section pointed at a git root with "goto {name}" is the original feature.
/// </summary>
public class SourceSettings
{
    /// <summary>
    /// The directory whose subdirectories become tiles. %VARIABLES% are expanded, so
    /// a path under the profile can be written without a user name in it.
    /// </summary>
    public string Folders { get; set; } = "";

    /// <summary>Directory names to leave out of the scan.</summary>
    public List<string> Exclude { get; set; } = new();

    /// <summary>
    /// A regex (matched case-insensitively) that deals the folders onto the two sides
    /// of the section's ⇄ switch: names it matches on one, the rest on the other. Its
    /// first capture group becomes "{id}" in the commands — ".*wi(\d+)" puts work item
    /// directories on the matching side and makes the number available, and the greedy
    /// prefix means the last such marker in a name is the one captured. Omitted, there
    /// is no switch and the list is flat.
    /// </summary>
    public string? Split { get; set; }

    /// <summary>What the ⇄ switch calls the folders "split" matches.</summary>
    public string MatchLabel { get; set; } = "matching";

    /// <summary>What the ⇄ switch calls the rest.</summary>
    public string OtherLabel { get; set; } = "others";

    /// <summary>
    /// What tapping a tile types into the focused window. "{name}" is the folder's
    /// name, "{id}" what "split" captured from it, "{dir}" the full path. Omitted,
    /// tapping opens the folder instead, like an app tile pointed at it.
    /// </summary>
    public string? Command { get; set; }

    /// <summary>Press Enter after typing, so the command runs rather than just sitting there.</summary>
    public bool Submit { get; set; } = true;

    /// <summary>
    /// Extra commands reached by expanding a tile with its ▾, with the same
    /// placeholders as <see cref="Command"/>. None means no ▾.
    /// </summary>
    public List<SourceCommand> Commands { get; set; } = new();

    /// <summary>Columns in a tile's expanded command list.</summary>
    public int CommandColumns { get; set; } = 2;

    /// <summary>Cap on folder tiles. The section header says when tiles were dropped.</summary>
    public int Max { get; set; } = 24;

    /// <summary>
    /// A file — or a glob like "%USERPROFILE%/notes/titles_*" — of "key|text" lines.
    /// A folder whose "{id}" (or, failing that, whose name) appears as a key gets the
    /// text as a second line on its tile. The format is the whole contract: anything
    /// able to write two fields around a pipe can put subtitles on these tiles, and
    /// no file at all just means plain names.
    /// </summary>
    public string? Subtitles { get; set; }
}

/// <summary>One entry in a folder tile's expanded command list.</summary>
public class SourceCommand
{
    public string Label { get; set; } = "";

    /// <summary>Typed into the focused window. "{name}", "{id}" and "{dir}" are substituted.</summary>
    public string Command { get; set; } = "";
}

/// <summary>
/// Reads a section's "source". It's an object now, but it spent its first life as the
/// string "branches" naming a top-level block that no longer exists — this converter
/// is what keeps that old spelling from throwing the whole file away, turning it into
/// a warning that says what to write instead.
/// </summary>
public class SourceSettingsConverter : JsonConverter<SourceSettings?>
{
    public override SourceSettings? Read(ref Utf8JsonReader reader, Type typeToConvert,
                                         JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            GridRangeConverter.Problems.Add(
                $"\"source\": \"{reader.GetString()}\" is the old form — \"source\" is "
                + "now an object written in the section itself (\"folders\", \"command\", "
                + "\"split\", …; see the README). The section shows only its own apps "
                + "and snippets until it's rewritten.");
            return null;
        }

        return JsonSerializer.Deserialize<SourceSettings>(ref reader, options);
    }

    public override void Write(Utf8JsonWriter writer, SourceSettings? value,
                               JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}
