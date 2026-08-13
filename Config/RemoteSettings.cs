using System.IO;
using System.Text.Json.Serialization;

namespace Deckhand;

/// <summary>
/// Serving the panel to a browser on another device — see <see cref="RemoteServer"/>
/// for what is and isn't exposed.
/// </summary>
public class RemoteSettings
{
    /// <summary>
    /// A token this short would be worth guessing, and what's on the other side of it
    /// types and launches things as administrator.
    /// </summary>
    public const int MinimumTokenLength = 16;

    /// <summary>
    /// Off by default. This is the only switch: with it false nothing listens, no
    /// port is opened, and the token is never read.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The HttpListener prefix to serve on. "+" is every interface; narrow it to one
    /// address ("http://192.168.1.20:8787/") to keep it off the others, or to
    /// "http://127.0.0.1:8787/" when reaching it through a tunnel.
    /// </summary>
    public string Listen { get; set; } = "http://+:8787/";

    /// <summary>
    /// Required on every request, and the one thing here that must not be written in
    /// dashboard.json: that file travels with the project, so a token in it is in every
    /// copy, backup and build output too — and what's on the other side of it types and
    /// launches things as administrator. Keep it in <see cref="TokenFile"/> instead,
    /// which wins over this whenever it has anything in it.
    ///
    /// After <see cref="ResolveToken"/> this holds the token actually in use, whichever
    /// of the two it came from, so nothing downstream has to know about the difference.
    /// </summary>
    public string Token { get; set; } = "";

    /// <summary>
    /// The file the token is really kept in: one line, nothing else. Defaults to
    /// %USERPROFILE%\.deckhand_token — outside the project, so copying the code
    /// somewhere doesn't copy the key to it with it.
    /// </summary>
    public string? TokenFile { get; set; }

    /// <summary>
    /// Where the token in use came from, so "is it reading my file?" is answerable from
    /// the status window instead of by experiment. Never the token itself.
    /// </summary>
    [JsonIgnore]
    public string TokenSource { get; private set; } = "";

    /// <summary>
    /// Reads the token file over the top of whatever the config said. Called once as the
    /// config is loaded rather than at each use: the status window asks for the token on
    /// a timer, and that is no reason to touch the disk.
    /// </summary>
    public void ResolveToken()
    {
        string path = ResolveTokenFile();

        string? fromFile;
        try
        {
            // The first line only, so a file with a note under it still works, and
            // trimmed because any editor will have left a newline behind.
            fromFile = File.Exists(path) ? File.ReadLines(path).FirstOrDefault()?.Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable is worth saying out loud. Falling back silently would serve
            // with a token that isn't the one the file was meant to be holding.
            TokenSource = $"{path} couldn't be read — {ex.Message}";
            return;
        }

        if (!string.IsNullOrEmpty(fromFile))
        {
            Token = fromFile;
            TokenSource = path;
            return;
        }

        TokenSource = Token.Length > 0
            ? $"dashboard.json — move it to {path}"
            : $"nothing in {path}";
    }

    /// <summary>
    /// Names, besides an address, that the panel may be reached by — a Tailscale name, or
    /// whatever a tunnel presents this as. Empty is the normal case: the tablet opens one
    /// of the addresses the status window lists, and an address needs no permission here.
    ///
    /// The reason a name does is that what a name points at isn't ours to decide. A page
    /// anywhere can point one at this machine and be talking to the panel from inside its
    /// own origin; an address can't be moved like that. So names are listed rather than
    /// accepted, and a request calling this machine anything else is refused.
    /// </summary>
    public List<string> AllowedHosts { get; set; } = new();

    /// <summary>The token file's path, configured or default.</summary>
    public string ResolveTokenFile() =>
        string.IsNullOrWhiteSpace(TokenFile)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                           ".deckhand_token")
            : TokenFile;
}
