using System.IO;
using System.Text.Json;

namespace Deckhand;

/// <summary>
/// Where the token file has been put, when that was chosen in the status window rather
/// than written in the config.
///
/// It is kept in %LOCALAPPDATA% for the reason <see cref="PanelPlacement"/> is: the
/// config is hand-edited and full of comments, and a program that rewrites it to record
/// one path strips every one of them. It is also the right shape of fact for that
/// folder — which drive this machine keeps its key on is this machine's business, and
/// dashboard.json travels.
///
/// A choice made here wins over "tokenFile" in the config, because it is the later and
/// more deliberate of the two acts: someone who opens a file dialog and picks a file
/// means it. Choosing the path the config or the default already names records nothing
/// and clears any earlier choice, so there is a way back to being config-driven that
/// doesn't involve knowing this file exists.
/// </summary>
internal static class TokenLocation
{
    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Deckhand", "token-file.json");

    private sealed record Stored(string Path);

    /// <summary>The path chosen here, or null when nothing has been.</summary>
    public static string? Chosen
    {
        get
        {
            try
            {
                if (!File.Exists(StorePath)) return null;

                string? path = JsonSerializer.Deserialize<Stored>(File.ReadAllText(StorePath))?.Path;
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // An unreadable note about where the key is kept is not a reason to
                // refuse to serve. Fall back to the config, and then to the default.
                return null;
            }
        }
    }

    /// <summary>
    /// Records the choice. Null forgets it, which is what picking the path the config or
    /// the default already names amounts to — there is nothing to remember, and leaving a
    /// stale note here would go on overriding a config the user later edits.
    /// </summary>
    public static void Choose(string? path)
    {
        string store = StorePath;
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);

        if (string.IsNullOrWhiteSpace(path))
        {
            if (File.Exists(store)) File.Delete(store);
            return;
        }

        File.WriteAllText(store, JsonSerializer.Serialize(new Stored(path)));
    }
}
