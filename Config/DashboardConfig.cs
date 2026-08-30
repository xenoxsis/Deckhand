using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Deckhand;

public class DashboardConfig
{
    public LayoutConfig Layout { get; set; } = new();

    /// <summary>
    /// Process names that count as a browser. A tile whose "path" is a URL opens a
    /// tab in the focused one of these instead of in whatever browser Windows calls
    /// default; from anything else it falls back to the default browser. Add yours
    /// here if it isn't listed.
    /// </summary>
    public List<string> Browsers { get; set; } =
        new() { "chrome", "msedge", "firefox", "brave", "vivaldi", "opera", "opera_gx" };

    /// <summary>Serving the panel to a tablet on the network. Off unless asked for.</summary>
    public RemoteSettings Remote { get; set; } = new();


    /// <summary>Sections laid out on the grid, always visible.</summary>
    public List<SectionEntry> Sections { get; set; } = new();

    /// <summary>Shorthand for a single always-visible "Apps" section.</summary>
    public List<AppEntry> Apps { get; set; } = new();

    /// <summary>Shorthand for a single always-visible "Snippets" section.</summary>
    public List<SnippetEntry> Snippets { get; set; } = new();

    /// <summary>
    /// Names for the apps worth recognising. A profile holds no tiles of its own — it
    /// says which processes count as "Git Bash", and sections written for that name are
    /// shown while one is focused. First match wins, so order them most-specific first.
    /// </summary>
    public List<ProfileEntry> Profiles { get; set; } = new();

    public ProfileEntry? MatchProfile(string processName, string windowTitle) =>
        Profiles.FirstOrDefault(p => p.Matches(processName, windowTitle));

    /// <summary>
    /// Config that parsed cleanly but will quietly cost tiles — a section written for a
    /// profile name that nothing is labelled, a profile that can never be the first
    /// match. Both are invisible at runtime: the tiles simply never appear, with no
    /// error to look for, so they're collected here and said out loud once per load.
    /// </summary>
    [JsonIgnore]
    public List<string> Warnings { get; } = new();

    private void Check()
    {
        // A grid with no tracks isn't a layout, it's a crash in the placer's clamp.
        if (Layout.Columns < 1 || Layout.Rows < 1)
        {
            Warnings.Add($"layout needs at least one column and one row — "
                         + $"{Layout.Columns}x{Layout.Rows} was read as "
                         + $"{Math.Max(1, Layout.Columns)}x{Math.Max(1, Layout.Rows)}.");
            Layout.Columns = Math.Max(1, Layout.Columns);
            Layout.Rows = Math.Max(1, Layout.Rows);
        }

        // A titleMatch that doesn't compile is otherwise invisible: the profile just
        // stops matching (see ProfileEntry.Matches for why nothing is the right answer),
        // and its sections look exactly like sections that weren't meant to appear.
        foreach (var profile in Profiles)
        {
            if (RegexProblem(profile.TitleMatch) is { } problem)
            {
                Warnings.Add($"Profile \"{profile.Label}\" has a titleMatch that isn't a "
                             + $"valid regex ({problem}) — until it's fixed, the profile "
                             + "matches nothing and its sections won't appear.");
            }
        }

        // The same for a section source's "split", which fails closed too: a pattern
        // that doesn't compile captures nothing, so every folder lands on the "other"
        // side of the switch. And a source with no folder to scan can only ever say so.
        foreach (var section in Sections)
        {
            if (section.Source is not { } source) continue;

            if (string.IsNullOrWhiteSpace(source.Folders))
            {
                Warnings.Add($"Section \"{section.Label}\" has a \"source\" with no "
                             + "\"folders\" — there is nothing to scan, so the section "
                             + "will only show a warning tile.");
            }

            if (RegexProblem(source.Split) is { } splitProblem)
            {
                Warnings.Add($"Section \"{section.Label}\" has a \"split\" that isn't a "
                             + $"valid regex ({splitProblem}) — until it's fixed, every "
                             + $"folder counts as \"{source.OtherLabel}\".");
            }
        }

        // Tile colours, before they reach a brush: an unparseable one is silently
        // unpainted at render time, which looks like the config being ignored.
        foreach (var section in EffectiveSections())
        {
            var tiles = section.Apps.Select(a => (a.Label, a.Color))
                        .Concat(section.Snippets.Select(s => (s.Label, s.Color)));
            foreach (var (label, color) in tiles)
            {
                if (color is not null && !ValidColor(color))
                {
                    Warnings.Add($"Tile \"{label}\" has the color \"{color}\", which "
                                 + "isn't #RGB or #RRGGBB — it won't be painted.");
                }
            }
        }

        var labels = Profiles.Select(p => p.Label)
                             .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var section in Sections)
        {
            foreach (string name in section.Profiles.Where(n => !labels.Contains(n)))
            {
                Warnings.Add($"Section \"{section.Label}\" is written for a profile "
                             + $"called \"{name}\", and no profile has that label. Its "
                             + "tiles will never be shown.");
            }
        }

        // Which profile has already taken a process name outright. A profile with a
        // titleMatch doesn't take one — it can decline a window, leaving a later
        // profile for the same process reachable, which is how one app is split in two.
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in Profiles)
        {
            foreach (string process in profile.Match.Select(ProcessNames.Strip))
            {
                if (claimed.TryGetValue(process, out string? owner))
                {
                    Warnings.Add($"Profile \"{profile.Label}\" matches {process}, which "
                                 + $"\"{owner}\" above it already matches unconditionally. "
                                 + $"The first match wins, so this one is never used — move "
                                 + $"it above \"{owner}\", or narrow \"{owner}\" with a "
                                 + "\"titleMatch\".");
                }
                else if (string.IsNullOrWhiteSpace(profile.TitleMatch))
                {
                    claimed[process] = profile.Label;
                }
            }
        }
    }

    /// <summary>
    /// The two hex forms that mean the same colour to WPF and to a browser. Longer WPF
    /// forms like #AARRGGBB are refused on purpose: a browser reads eight digits as
    /// RGBA, so the panel and the tablet would disagree about what was written.
    /// </summary>
    private static bool ValidColor(string color) =>
        Regex.IsMatch(color.Trim(), "^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$");

    /// <summary>The reason a pattern won't compile, or null for one that does (or none at all).</summary>
    private static string? RegexProblem(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        try
        {
            _ = new Regex(pattern);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Explicit sections, then the "apps"/"snippets" shorthand as sections.</summary>
    public IEnumerable<SectionEntry> EffectiveSections()
    {
        foreach (var section in Sections) yield return section;

        if (Apps.Count > 0)
            yield return new SectionEntry { Label = "Apps", Apps = Apps };

        if (Snippets.Count > 0)
            yield return new SectionEntry { Label = "Snippets", Snippets = Snippets };
    }

    private const string FileName = "dashboard.json";
    private const string LocalFileName = "dashboard.local.json";
    private const string ProjectFileName = "Deckhand.csproj";

    /// <summary>Which file this was read from, so the ↻ button can name it.</summary>
    [JsonIgnore]
    public string SourcePath { get; private set; } = "";

    /// <summary>
    /// Reads dashboard.json, with dashboard.local.json merged over it when one sits
    /// beside it — the shared file travels with the project, the local one holds this
    /// machine's overrides. <paramref name="error"/> comes back non-null when the
    /// shared file couldn't be read, in which case this returns defaults — a typo in
    /// the config shouldn't take the dashboard down with it. A local file that won't
    /// parse costs only itself: the shared file still loads, and a warning says so.
    /// </summary>
    public static DashboardConfig Load(out string? error)
    {
        error = null;

        string path = ResolvePath();
        if (!File.Exists(path)) return Ready(new DashboardConfig(), path);

        var options = ReadOptions();

        GridRangeConverter.Problems.Clear();

        try
        {
            string text = File.ReadAllText(path);
            string overlayPath = Path.Combine(Path.GetDirectoryName(path) ?? "", LocalFileName);

            string? overlayText = null;
            string? overlayProblem = null;
            if (File.Exists(overlayPath))
            {
                try { overlayText = File.ReadAllText(overlayPath); }
                catch (IOException ex) { overlayProblem = ex.Message; }
            }

            JsonObject? merged = null;
            if (overlayText is not null && overlayProblem is null)
            {
                try { merged = MergeOverlay(text, overlayText); }
                catch (JsonException ex) { overlayProblem = ex.Message; }
            }

            DashboardConfig config;
            if (merged is not null)
            {
                try
                {
                    config = merged.Deserialize<DashboardConfig>(options)
                             ?? new DashboardConfig();
                }
                catch (JsonException ex)
                {
                    // The merged whole not deserializing when the shared file alone
                    // does means the overlay put a wrong-shaped value somewhere. The
                    // overlay is what gets dropped; a broken local file mustn't cost
                    // the shared one. (If the shared file is the broken one, the
                    // re-read below throws too, into the outer catch where it belongs.)
                    overlayProblem = ex.Message;
                    merged = null;
                    GridRangeConverter.Problems.Clear();
                    config = JsonSerializer.Deserialize<DashboardConfig>(text, options)
                             ?? new DashboardConfig();
                }
            }
            else
            {
                config = JsonSerializer.Deserialize<DashboardConfig>(text, options)
                         ?? new DashboardConfig();
            }

            if (overlayProblem is not null)
            {
                config.Warnings.Add($"{overlayPath} couldn't be used — {overlayProblem} "
                                    + "None of its overrides are applied.");
            }

            NoteUnknownKeys(text, config, source: "");
            if (merged is not null) NoteUnknownKeys(overlayText!, config, source: LocalFileName);

            var ready = Ready(config, path);
            if (merged is not null) ready.SourcePath = $"{path} + {LocalFileName}";
            return ready;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            error = $"{path}\n\n{ex.Message}";
            return Ready(new DashboardConfig(), path);
        }
    }

    /// <summary>
    /// Reads one file by itself — no dashboard.local.json laid over it. This is the
    /// designer's load: it rewrites the file it read, and a merged read would bake this
    /// machine's overrides into the shared file the moment it was saved.
    /// </summary>
    public static DashboardConfig LoadFile(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path)) return Ready(new DashboardConfig(), path);

        GridRangeConverter.Problems.Clear();

        try
        {
            string text = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<DashboardConfig>(text, ReadOptions())
                         ?? new DashboardConfig();
            NoteUnknownKeys(text, config, source: "");
            return Ready(config, path);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            error = $"{path}\n\n{ex.Message}";
            return Ready(new DashboardConfig(), path);
        }
    }

    /// <summary>How the file is read everywhere: comments and trailing commas are part
    /// of the format, and keys match however they're capitalised.</summary>
    private static JsonSerializerOptions ReadOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The shared file with the local file's keys laid over it. Objects merge a level
    /// at a time; anything else — a value, a list — replaces outright, because half a
    /// list from each file would be impossible to reason about. Keys are matched
    /// case-insensitively, the same way the deserializer reads them.
    /// </summary>
    private static JsonObject? MergeOverlay(string baseText, string overlayText)
    {
        var nodeOptions = new JsonNodeOptions { PropertyNameCaseInsensitive = true };
        var docOptions = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        // A shared file that isn't an object will fail the plain read too, where the
        // error is attributed to the right file; nothing to merge over here.
        if (JsonNode.Parse(baseText, nodeOptions, docOptions) is not JsonObject merged)
        {
            return null;
        }

        if (JsonNode.Parse(overlayText, nodeOptions, docOptions) is not JsonObject overlay)
        {
            throw new JsonException("the file isn't a JSON object.");
        }

        MergeInto(merged, overlay);
        return merged;
    }

    private static void MergeInto(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay)
        {
            if (value is JsonObject inner && target[key] is JsonObject existing)
            {
                MergeInto(existing, inner);
            }
            else
            {
                // Cloned because a node can only hang from one parent, and this one
                // still hangs from the overlay being walked.
                target[key] = value?.DeepClone();
            }
        }
    }

    /// <summary>
    /// The last of loading, on every path out of it — including the ones that came back
    /// with defaults, since the token lives outside this file and a config that failed to
    /// parse shouldn't also lose track of where it was read from.
    /// </summary>
    private static DashboardConfig Ready(DashboardConfig config, string path)
    {
        config.SourcePath = path;
        config.Remote.ResolveToken();

        // What the converter couldn't read, and what it left behind: a range that
        // didn't parse came through as a zero-length pin, which here becomes "no pin"
        // — the section is packed like one that never asked — plus a warning, instead
        // of one typo dropping the whole file to defaults.
        config.Warnings.AddRange(GridRangeConverter.Problems);
        GridRangeConverter.Problems.Clear();
        foreach (var section in config.Sections)
        {
            if (section.Columns is { Length: 0 }) section.Columns = null;
            if (section.Rows is { Length: 0 }) section.Rows = null;
        }

        config.Check();
        return config;
    }

    /// <summary>
    /// Misspelled settings are this file's quietest failure: the serializer skips what
    /// it doesn't recognise, and a "snipets" list simply never appears, with no error to
    /// go looking for. So the document is walked once more against the model, and any
    /// key the model has no property for is named out loud.
    /// </summary>
    private static void NoteUnknownKeys(string json, DashboardConfig config, string source)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            NoteUnknownKeys(document.RootElement, typeof(DashboardConfig), source, config.Warnings);
        }
        catch (JsonException)
        {
            // Deserialize already accepted this text; disagreeing now helps nobody.
        }
    }

    private static void NoteUnknownKeys(JsonElement element, Type type, string path,
                                        List<string> warnings)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            if (!type.IsGenericType) return;
            Type item = type.GetGenericArguments()[0];

            int index = 0;
            foreach (var entry in element.EnumerateArray())
            {
                NoteUnknownKeys(entry, item, $"{path}[{index++}]", warnings);
            }
            return;
        }

        // Only our own model types have named settings to check; a string, a number, or
        // a GridRange read by its converter has nothing to walk.
        if (element.ValueKind != JsonValueKind.Object) return;
        if (type.Assembly != typeof(DashboardConfig).Assembly) return;

        var known = type.GetProperties()
                        .Where(p => p.SetMethod?.IsPublic == true
                                    && !p.IsDefined(typeof(JsonIgnoreAttribute), inherit: true))
                        .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var property in element.EnumerateObject())
        {
            // The editor's business, not ours: it names the schema file that gives
            // completion and squiggles while the config is being typed.
            if (property.NameEquals("$schema")) continue;

            if (!known.TryGetValue(property.Name, out var match))
            {
                string where = path.Length == 0 ? "" : $" in {path}";
                warnings.Add($"\"{property.Name}\"{where} isn't a setting this dashboard "
                             + "knows — check the spelling. It was read and ignored.");
                continue;
            }

            var inner = Nullable.GetUnderlyingType(match.PropertyType) ?? match.PropertyType;
            NoteUnknownKeys(property.Value, inner,
                            path.Length == 0 ? property.Name : $"{path}.{property.Name}",
                            warnings);
        }
    }

    /// <summary>
    /// Normally the copy beside the exe. In a source checkout that copy is a build
    /// artifact — the build overwrites it from the project root — so editing the
    /// project's own dashboard.json and tapping ↻ would silently re-read the stale
    /// copy. Walking up to the .csproj finds the file actually being edited; a
    /// deployed copy has no .csproj next to it and falls back to the local file.
    /// </summary>
    public static string ResolvePath(string? baseDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        string local = Path.Combine(baseDirectory, FileName);

        for (var dir = new DirectoryInfo(baseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, ProjectFileName))) continue;

            string source = Path.Combine(dir.FullName, FileName);
            return File.Exists(source) ? source : local;
        }

        return local;
    }
}
