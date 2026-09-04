using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deckhand;

/// <summary>
/// dashboard.json back out of the objects, for the designer's save. The file can't be
/// round-tripped — it's JSONC, and a serializer strips the comments — so this writes a
/// fresh document instead, and writes it sparsely: a setting appears only when it says
/// something the loader wouldn't assume anyway. The defaults here are the model's own
/// (see <see cref="AppEntry"/> etc.), spelled out per property rather than left to the
/// serializer, whose idea of a default is the CLR's — it would drop "deElevate": false,
/// which is the one value of that setting worth writing.
/// </summary>
internal static class ConfigWriter
{
    public static string Write(DashboardConfig config)
    {
        var root = new JsonObject
        {
            // So the file the designer writes gets the same editor completion and
            // squiggles as one written by hand.
            ["$schema"] = "./dashboard.schema.json",
            ["layout"] = new JsonObject
            {
                ["columns"] = config.Layout.Columns,
                ["rows"] = config.Layout.Rows,
            },
        };

        var stockBrowsers = new DashboardConfig().Browsers;
        if (!config.Browsers.SequenceEqual(stockBrowsers, StringComparer.OrdinalIgnoreCase))
        {
            root["browsers"] = Strings(config.Browsers);
        }

        if (RemoteObject(config.Remote) is { } remote) root["remote"] = remote;

        if (config.Profiles.Count > 0)
        {
            root["profiles"] = new JsonArray(
                config.Profiles.Select(ProfileObject).ToArray<JsonNode?>());
        }

        if (config.Sections.Count > 0)
        {
            root["sections"] = new JsonArray(
                config.Sections.Select(SectionObject).ToArray<JsonNode?>());
        }

        if (config.Apps.Count > 0)
        {
            root["apps"] = new JsonArray(config.Apps.Select(AppObject).ToArray<JsonNode?>());
        }

        if (config.Snippets.Count > 0)
        {
            root["snippets"] = new JsonArray(
                config.Snippets.Select(SnippetObject).ToArray<JsonNode?>());
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
               + Environment.NewLine;
    }

    /// <summary>The remote block, or null when every setting in it is the default —
    /// an all-default block says nothing the loader wouldn't assume.</summary>
    private static JsonObject? RemoteObject(RemoteSettings remote)
    {
        var stock = new RemoteSettings();
        var block = new JsonObject();

        if (remote.Enabled) block["enabled"] = true;
        if (remote.Listen != stock.Listen) block["listen"] = remote.Listen;
        if (!string.IsNullOrWhiteSpace(remote.TokenFile)) block["tokenFile"] = remote.TokenFile;
        if (remote.AllowedHosts.Count > 0) block["allowedHosts"] = Strings(remote.AllowedHosts);

        // Never remote.Token: after ResolveToken it holds the token in use, whichever
        // file that came from, and this file is exactly where a token must not live.

        return block.Count > 0 ? block : null;
    }

    private static JsonObject ProfileObject(ProfileEntry profile)
    {
        var block = new JsonObject
        {
            ["label"] = profile.Label,
            ["match"] = Strings(profile.Match),
        };
        if (!string.IsNullOrWhiteSpace(profile.TitleMatch)) block["titleMatch"] = profile.TitleMatch;
        return block;
    }

    private static JsonObject SectionObject(SectionEntry section)
    {
        var block = new JsonObject { ["label"] = section.Label };

        if (section.Profiles.Count > 0) block["profiles"] = Strings(section.Profiles);
        if (section.Columns is { } columns) block["columns"] = Range(columns);
        if (section.Rows is { } rows) block["rows"] = Range(rows);
        if (section.TilesPerRow is { } perRow) block["tilesPerRow"] = perRow;
        if (section.Source is { } source) block["source"] = SourceObject(source);

        if (section.Apps.Count > 0)
        {
            block["apps"] = new JsonArray(section.Apps.Select(AppObject).ToArray<JsonNode?>());
        }

        if (section.Snippets.Count > 0)
        {
            block["snippets"] = new JsonArray(
                section.Snippets.Select(SnippetObject).ToArray<JsonNode?>());
        }

        return block;
    }

    private static JsonObject SourceObject(SourceSettings source)
    {
        var stock = new SourceSettings();
        var block = new JsonObject { ["folders"] = source.Folders };

        if (source.Exclude.Count > 0) block["exclude"] = Strings(source.Exclude);
        if (!string.IsNullOrWhiteSpace(source.Split)) block["split"] = source.Split;
        if (source.MatchLabel != stock.MatchLabel) block["matchLabel"] = source.MatchLabel;
        if (source.OtherLabel != stock.OtherLabel) block["otherLabel"] = source.OtherLabel;
        if (!string.IsNullOrWhiteSpace(source.Command)) block["command"] = source.Command;
        if (!source.Submit) block["submit"] = false;

        if (source.Commands.Count > 0)
        {
            block["commands"] = new JsonArray(source.Commands
                .Select(c => (JsonNode?)new JsonObject
                {
                    ["label"] = c.Label,
                    ["command"] = c.Command,
                })
                .ToArray());
        }

        if (source.CommandColumns != stock.CommandColumns) block["commandColumns"] = source.CommandColumns;
        if (source.Max != stock.Max) block["max"] = source.Max;
        if (!string.IsNullOrWhiteSpace(source.Subtitles)) block["subtitles"] = source.Subtitles;

        return block;
    }

    private static JsonObject AppObject(AppEntry app)
    {
        var block = new JsonObject
        {
            ["label"] = app.Label,
            ["path"] = app.Path,
        };
        if (!string.IsNullOrWhiteSpace(app.Args)) block["args"] = app.Args;
        if (!string.IsNullOrWhiteSpace(app.Browser)) block["browser"] = app.Browser;
        if (!app.DeElevate) block["deElevate"] = false;
        if (app.Windows.Count > 0) block["windows"] = Strings(app.Windows);
        if (app.Span != 1) block["span"] = app.Span;
        if (!string.IsNullOrWhiteSpace(app.Color)) block["color"] = app.Color;
        Picture(block, app.Icon, app.IconMode);
        return block;
    }

    private static JsonObject SnippetObject(SnippetEntry snippet)
    {
        var block = new JsonObject
        {
            ["label"] = snippet.Label,
            ["text"] = snippet.Text,
        };
        if (snippet.Method == InsertMethod.Type) block["method"] = "type";
        if (!string.IsNullOrWhiteSpace(snippet.Keys)) block["keys"] = snippet.Keys;
        if (snippet.Span != 1) block["span"] = snippet.Span;
        if (!string.IsNullOrWhiteSpace(snippet.Color)) block["color"] = snippet.Color;
        Picture(block, snippet.Icon, snippet.IconMode);
        return block;
    }

    /// <summary>
    /// A tile's picture, on the two kinds of tile that can wear one. The placement is
    /// only written when there is a picture to place: "iconMode" on a tile with no
    /// "icon" is a line that does nothing, and this file is read by people.
    /// </summary>
    private static void Picture(JsonObject block, string? icon, IconMode mode)
    {
        if (string.IsNullOrWhiteSpace(icon)) return;

        block["icon"] = icon;
        if (mode != IconMode.Left) block["iconMode"] = mode.ToString().ToLowerInvariant();
    }

    /// <summary>The two spellings the loader reads back: a width as the number it is,
    /// a pinned range as "1-3".</summary>
    private static JsonNode Range(GridRange range) =>
        range.Start is null ? JsonValue.Create(range.Length) : JsonValue.Create(range.ToString());

    private static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}
