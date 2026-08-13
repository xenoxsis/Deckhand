using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Deckhand;

public record FolderInfo(
    string Name,
    string FullPath,
    bool Matched,
    string Id,
    string? Subtitle,
    long LastUsed,
    DateTime Modified);

/// <summary>
/// Scans a section's "source" folder: one entry per subdirectory, dealt onto the two
/// sides of the section's switch by its "split" regex when it has one, subtitled from
/// its "subtitles" file when it names one, and ordered by when each folder's tile was
/// last tapped, then by when the folder changed.
///
/// Nothing here knows what the folders are. One config points this at a git root and
/// types "goto {name}" into a shell; pointed at any projects directory with no command
/// at all, the same scan is a launcher whose tiles open the folders themselves.
/// </summary>
public static class FolderCatalog
{
    public static IReadOnlyList<FolderInfo> Discover(SourceSettings source, out string? error)
    {
        error = null;

        string root = Environment.ExpandEnvironmentVariables(source.Folders ?? "").Trim();
        if (root.Length == 0)
        {
            error = "This section's \"source\" names no \"folders\" — there is nothing "
                  + "to scan.";
            return Array.Empty<FolderInfo>();
        }

        if (!Directory.Exists(root))
        {
            error = $"The folder does not exist:\n{root}";
            return Array.Empty<FolderInfo>();
        }

        // Case-insensitive to match the filesystem the names came from. A pattern that
        // doesn't compile fails closed, the same way a profile's titleMatch does: it
        // matches nothing, every folder lands on the "other" side, and Check() already
        // said why at load — guessing at what a broken regex meant would be worse.
        Regex? split = null;
        bool splitBroken = false;
        if (!string.IsNullOrWhiteSpace(source.Split))
        {
            try { split = new Regex(source.Split, RegexOptions.IgnoreCase); }
            catch (ArgumentException) { splitBroken = true; }
        }

        var subtitles = ReadSubtitles(source.Subtitles);
        var history = TapHistory.Read();
        var excluded = new HashSet<string>(source.Exclude, StringComparer.OrdinalIgnoreCase);

        var folders = new List<FolderInfo>();
        foreach (string path in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(path);
            if (excluded.Contains(name)) continue;

            // With no split there is no switch, so every folder is "matched" — the one
            // flat list. With one, an unmatched folder is kept rather than skipped: it's
            // the other side of the switch, and dropping anything is "exclude"'s job.
            bool matched = split is null ? !splitBroken : split.IsMatch(name);
            string id = "";
            if (split is not null && split.Match(name) is { Success: true } hit
                && hit.Groups.Count > 1)
            {
                id = hit.Groups[1].Value;
            }

            // The id is the better key — it's what the subtitle file is usually written
            // in terms of — with the bare name as the fallback for files keyed that way.
            string? subtitle = null;
            if (id.Length > 0) subtitles.TryGetValue(id, out subtitle);
            if (subtitle is null) subtitles.TryGetValue(name, out subtitle);

            // The root can be written with forward slashes ("C:/git"), which would
            // otherwise reach a shell command's {dir} as a mixed-separator path.
            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch { fullPath = path; }

            history.TryGetValue(fullPath, out long lastUsed);

            DateTime modified;
            try { modified = Directory.GetLastWriteTimeUtc(path); }
            catch { modified = DateTime.MinValue; }

            folders.Add(new FolderInfo(name, fullPath, matched, id, subtitle,
                                       lastUsed, modified));
        }

        if (folders.Count == 0)
        {
            error = $"No folders found in:\n{root}";
        }

        // The tiles tapped recently first, then the folders touched most recently —
        // the second is the only ordering a folder never tapped can have, and a fresh
        // history file starts everything there.
        return folders
            .OrderByDescending(f => f.LastUsed)
            .ThenByDescending(f => f.Modified)
            .ToList();
    }

    /// <summary>
    /// Reads the "key|text" lines out of the subtitles file — or files: a glob is
    /// allowed ("…/cache/titles_*") so a tool can write dated files without
    /// coordinating with anyone. Oldest first, so newer files win on conflict. The
    /// format is the entire contract; whatever wrote the files is not this code's
    /// business, and a file that can't be read costs its lines and nothing else.
    /// </summary>
    private static Dictionary<string, string> ReadSubtitles(string? pattern)
    {
        var subtitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(pattern)) return subtitles;

        string expanded = Environment.ExpandEnvironmentVariables(pattern.Trim());

        IEnumerable<string> files;
        try
        {
            if (expanded.IndexOfAny(new[] { '*', '?' }) >= 0)
            {
                string dir = Path.GetDirectoryName(expanded) ?? "";
                if (dir.Length == 0 || !Directory.Exists(dir)) return subtitles;

                files = new DirectoryInfo(dir)
                    .EnumerateFiles(Path.GetFileName(expanded))
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .Select(f => f.FullName);
            }
            else
            {
                files = File.Exists(expanded) ? new[] { expanded } : Array.Empty<string>();
            }

            foreach (string file in files)
            {
                string[] lines;
                try { lines = File.ReadAllLines(file, Encoding.UTF8); }
                catch { continue; }

                foreach (string line in lines)
                {
                    int bar = line.IndexOf('|');
                    if (bar <= 0) continue;

                    string key = line[..bar].Trim();
                    string text = line[(bar + 1)..].Trim();
                    if (key.Length > 0 && text.Length > 0) subtitles[key] = text;
                }
            }
        }
        catch
        {
            // A malformed glob or an unreadable directory costs the subtitles, which
            // are a nice-to-have on top of names that work without them.
        }

        return subtitles;
    }
}

/// <summary>
/// The dashboard's own record of which folder tiles were tapped when, which is what
/// keeps a source section in recently-used order. It has to be the dashboard's own:
/// a folder's timestamp only moves when its direct children change, so a working
/// copy edited daily for a month looks untouched, and every use of these folders
/// goes through a tile anyway.
///
/// One "&lt;unix seconds&gt;\t&lt;full path&gt;" line per tap, appended as they happen and
/// compacted to the latest line per path once the file is big enough to care about.
/// Purely a nice-to-have: a file that can't be read or written costs the ordering,
/// never the tiles, so every failure here is swallowed.
/// </summary>
public static class TapHistory
{
    // Settable so tests can point it somewhere disposable instead of writing tap
    // records into the real profile.
    internal static string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".deckhand_history");

    /// <summary>Past that, Note() rewrites the file down to one line per path.</summary>
    private const long CompactBytes = 256 * 1024;

    /// <summary>Each path's latest tap, as unix seconds.</summary>
    public static Dictionary<string, long> Read()
    {
        var lastUsed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!File.Exists(FilePath)) return lastUsed;

            foreach (string line in File.ReadAllLines(FilePath))
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                if (!long.TryParse(line[..tab], out long stamp)) continue;

                string path = line[(tab + 1)..].Trim();
                if (path.Length == 0) continue;

                if (!lastUsed.TryGetValue(path, out long existing) || stamp > existing)
                {
                    lastUsed[path] = stamp;
                }
            }
        }
        catch
        {
            // A locked or garbled file reads as an empty history.
        }

        return lastUsed;
    }

    public static void Note(string path)
    {
        try
        {
            File.AppendAllText(FilePath,
                $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\t{path}{Environment.NewLine}");

            // Compaction is checked after the append so the trigger can't be starved
            // by its own failures, and rewrites through Read() so it keeps exactly
            // what ordering uses: the latest line per path.
            if (new FileInfo(FilePath).Length > CompactBytes)
            {
                var lines = Read()
                    .OrderBy(entry => entry.Value)
                    .Select(entry => $"{entry.Value}\t{entry.Key}");
                File.WriteAllLines(FilePath, lines);
            }
        }
        catch
        {
            // Losing one tap's record is invisible; a dialog about it would not be.
        }
    }
}
