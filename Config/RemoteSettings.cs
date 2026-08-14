using System.IO;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// The token file's path: the one chosen in the status window if there is one, then
    /// the config's, then the default beside the user profile. See
    /// <see cref="TokenLocation"/> for why a choice made in the window wins.
    /// </summary>
    public string ResolveTokenFile() =>
        TokenLocation.Chosen
        ?? (string.IsNullOrWhiteSpace(TokenFile)
                ? DefaultTokenFile
                : TokenFile);

    /// <summary>Where the token lives when nobody has said otherwise.</summary>
    public static string DefaultTokenFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".deckhand_token");

    /// <summary>
    /// A fresh token, in the shape the README's PowerShell recipe makes: 25 characters
    /// drawn from a 32-symbol alphabet with the pairs that are misread off a screen left
    /// out — no O beside 0, no l or I beside 1, no u beside v — in five hyphenated
    /// groups, so it can be read aloud and typed on a tablet without a mistake.
    ///
    /// 256 is a whole number of 32s, so taking each byte modulo the alphabet is unbiased
    /// and there is nothing to reject and redraw.
    /// </summary>
    public static string NewToken()
    {
        const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        byte[] bytes = RandomNumberGenerator.GetBytes(25);

        var symbols = bytes.Select(b => alphabet[b % alphabet.Length]).ToArray();
        return string.Join('-', Enumerable.Range(0, 5)
                                          .Select(g => new string(symbols, g * 5, 5)));
    }

    /// <summary>
    /// Writes a token as the file is expected to hold it: the token, a newline, nothing
    /// else, and no byte order mark — <see cref="ResolveToken"/> takes the first line and
    /// trims it, and a BOM would be part of the first character it read.
    /// </summary>
    public static void WriteTokenFile(string path, string token)
    {
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        File.WriteAllText(path, token + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
