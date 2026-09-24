using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Deckhand;

// The browser's half of the app as the server sees it: the page and the files a browser
// fetches for itself, read out of the assembly once at startup and answered from memory.
// They are embedded rather than copied beside the exe because none of it is configuration
// and a deployed copy shouldn't be able to lose any of it.
internal sealed partial class RemoteServer
{
    /// <summary>One of the files a browser fetches for itself, ready to send.</summary>
    private readonly record struct StaticFile(string ContentType, byte[] Bytes);

    /// <summary>The browser's half of the app: the page, its files, and their fingerprint.</summary>
    private readonly record struct Assets(string Page,
                                          IReadOnlyDictionary<string, StaticFile> Files,
                                          string Build);

    /// <summary>
    /// What is served, and where. The manifest and the icons are what let the page be
    /// installed to a home screen instead of opened as a tab; the worker is what lets an
    /// installed copy launch without the laptop and what carries the build forward.
    /// </summary>
    private static readonly (string Path, string Resource, string Type)[] Served =
    {
        ("/manifest.webmanifest", "Deckhand.manifest.webmanifest",
         "application/manifest+json; charset=utf-8"),
        ("/sw.js", "Deckhand.sw.js", "text/javascript; charset=utf-8"),
        ("/icon-192.png", "Deckhand.icon-192.png", "image/png"),
        ("/icon-512.png", "Deckhand.icon-512.png", "image/png"),
        ("/icon-180.png", "Deckhand.icon-180.png", "image/png"),
    };

    /// <summary>
    /// Everything the browser is given, embedded in the assembly rather than read from
    /// disk: none of it is configuration, and a deployed copy shouldn't be able to lose
    /// any of it.
    /// </summary>
    private static Assets LoadAssets()
    {
        byte[]? page = Styled(Embedded("Deckhand.remote.html"));

        var files = new Dictionary<string, StaticFile>(StringComparer.Ordinal);
        foreach (var (path, resource, type) in Served)
        {
            // A missing icon is a broken build, not a reason not to serve the panel: the
            // page still works, it just won't install prettily.
            if (Embedded(resource) is byte[] bytes) files[path] = new StaticFile(type, bytes);
        }

        string build = Fingerprint(page, files);

        // The worker is told which build it is. It can't be asked at runtime — a worker
        // outlives the page that registered it — and it matters that its own bytes change
        // with the build, because a byte-for-byte comparison of this script is how the
        // browser decides there's an update to install at all.
        if (files.TryGetValue("/sw.js", out var worker))
        {
            string source = Encoding.UTF8.GetString(worker.Bytes)
                                    .Replace("__BUILD__", build, StringComparison.Ordinal);
            files["/sw.js"] = worker with { Bytes = Encoding.UTF8.GetBytes(source) };
        }

        return new Assets(
            page is null
                ? "<!doctype html><p>remote.html is missing from the build."
                : Encoding.UTF8.GetString(page),
            files, build);
    }

    /// <summary>
    /// The page with the tile measurements dropped into its :root block, so the browser
    /// lays a tile out with the same numbers the panel's own styles are built from — see
    /// <see cref="TileMetrics"/>, which is the one copy of them.
    ///
    /// Done here, before the fingerprint, rather than on the way out: the values are part
    /// of what's served, so changing one has to count as a new build. Otherwise the file
    /// on disk would be unchanged, the fingerprint with it, and a page already open on the
    /// tablet would go on drawing yesterday's tiles.
    /// </summary>
    private static byte[]? Styled(byte[]? page)
    {
        if (page is null) return null;

        string html = Encoding.UTF8.GetString(page)
                              .Replace("__TILES__", TileMetrics.Css(), StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(html);
    }

    /// <summary>
    /// Four bytes over everything served, in a fixed order so the same build always
    /// fingerprints the same way and a restart doesn't look like an update.
    /// </summary>
    private static string Fingerprint(byte[]? page,
                                      IReadOnlyDictionary<string, StaticFile> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        if (page is not null) hash.AppendData(page);
        foreach (var path in files.Keys.OrderBy(p => p, StringComparer.Ordinal))
        {
            // The path as well as the content: adding a file changes what's served even if
            // it happens to be a copy of one already there.
            hash.AppendData(Encoding.UTF8.GetBytes(path));
            hash.AppendData(files[path].Bytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset(), 0, 4).ToLowerInvariant();
    }

    private static byte[]? Embedded(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (stream is null) return null;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
