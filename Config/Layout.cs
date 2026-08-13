using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deckhand;

// How much of the panel a section gets: the grid the window is divided into, and the
// ranges a section pins itself to within it.

public class LayoutConfig
{
    /// <summary>How many columns the dashboard width is divided into.</summary>
    public int Columns { get; set; } = 12;

    /// <summary>
    /// How many rows the dashboard height is divided into. Rows share the height
    /// equally, which is what gives every group a size fixed by the grid rather
    /// than by how many tiles happen to be in it.
    ///
    /// A finer grid buys placement precision at the cost of the smallest useful
    /// group: at 12 rows a row is roughly 45pt of a default-height panel, so a
    /// one-row group is shorter than a single tile and scrolls from the start.
    /// </summary>
    public int Rows { get; set; } = 12;
}

/// <summary>
/// A run of grid tracks, written either as a width (<c>3</c> — pack the section
/// into the first gap that fits) or as a pinned 1-based range (<c>"1-3"</c> —
/// tracks 1 through 3, wherever the other sections end up). Pinning is what keeps
/// a group in the same place as sections come and go with the focused app.
/// </summary>
[JsonConverter(typeof(GridRangeConverter))]
public readonly record struct GridRange(int? Start, int Length)
{
    private static readonly char[] Separators = { '-', ':' };

    public static bool TryParse(string? text, out GridRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Empty entries are kept so a half-written "1-" is rejected and reported
        // rather than passing as a width of 1.
        var parts = text.Split(Separators, StringSplitOptions.TrimEntries);

        if (parts.Length == 1)
        {
            if (!int.TryParse(parts[0], out int width)) return false;
            range = new GridRange(null, Math.Max(1, width));
            return true;
        }

        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out int from) || !int.TryParse(parts[1], out int to)) return false;

        // "3-1" is a slip, not an error; read it the way it was clearly meant.
        int start = Math.Max(1, Math.Min(from, to));
        int end = Math.Max(start, Math.Max(from, to));
        range = new GridRange(start, end - start + 1);
        return true;
    }

    public override string ToString() =>
        Start is null ? Length.ToString() : $"{Start}-{Start + Length - 1}";
}

public class GridRangeConverter : JsonConverter<GridRange>
{
    /// <summary>
    /// The values a converter couldn't read, kept for
    /// <see cref="DashboardConfig.Warnings"/> — <see cref="SourceSettingsConverter"/>
    /// reports here too, it being the one channel of this kind. Throwing instead would
    /// count as the file failing to parse, and one half-written "1-" would drop every
    /// tile to defaults; a warning and an unpinned section costs only the pin. Static
    /// because a converter has no path to the config it's building — loading happens
    /// on one thread, and Load clears it before and drains it after.
    /// </summary>
    internal static readonly List<string> Problems = new();

    public override GridRange Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int width))
        {
            return new GridRange(null, Math.Max(1, width));
        }

        if (reader.TokenType == JsonTokenType.String && GridRange.TryParse(reader.GetString(), out var range))
        {
            return range;
        }

        string raw = reader.TokenType == JsonTokenType.String
            ? $"\"{reader.GetString()}\""
            : "that value";
        Problems.Add($"{raw} isn't a track count (3) or a range (\"1-3\") for "
                     + "\"columns\"/\"rows\" — the section is packed into the first "
                     + "gap that fits instead.");

        // Length 0 can't come out of a parse that succeeded, so it's the marker Ready
        // turns back into "no pin at all".
        return default;
    }

    public override void Write(Utf8JsonWriter writer, GridRange value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
