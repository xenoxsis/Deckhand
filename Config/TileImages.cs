using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace Deckhand;

/// <summary>
/// What a tile looks like beyond its label: the accent on its outline and the picture on
/// its face. Carried on the button itself (its Tag) rather than looked up again from the
/// config, because by the time the tablet's copy is built the config is behind — the
/// panel walks the buttons it drew, and this is how what the config asked for travels
/// with them.
/// </summary>
/// <param name="Icon">The picture's content hash, not its path: where the file is on
/// this machine is nobody else's business, and the hash is what the tablet asks for it
/// by. Null for a tile with no picture, or one whose file couldn't be read.</param>
internal sealed record TileFace(string? Color = null, string? Icon = null,
                                IconMode Mode = IconMode.Left);

/// <summary>
/// The pictures the tiles are wearing, read once and kept: the bytes as they are on disk,
/// their content hash, and the decoded image the panel draws. One place, because three
/// views want the same file — the panel draws it, the designer's preview draws it, and
/// the tablet is sent it — and reading it three times would let them disagree.
///
/// Nothing here is looked up, extracted or fetched. A tile draws the file its config
/// names and nothing else: no icon pulled out of an exe, no favicon off the web. That
/// keeps loading a config down to reading files that were asked for by name, which is the
/// only version of this that can't surprise anyone.
/// </summary>
internal static class TileImages
{
    /// <summary>
    /// What both screens can draw. The rule is the intersection and not the union: a
    /// picture only one of the two views can show is a tile that looks different on the
    /// tablet, which is the one thing the preview and the publish exist to prevent. tiff
    /// is the format that misses out — WPF reads it and a browser doesn't.
    ///
    /// Two of these needed something before they qualified. webp's decoder is optional on
    /// Windows (Web Media Extensions), which a stock Windows 10 or 11 has and some Server
    /// and LTSC builds don't; on a machine without it the decode below fails and the tile
    /// falls back to its label — on both screens, because it's the panel that publishes
    /// the hash, so a picture this machine can't draw is never one the tablet is offered.
    /// svg has no WPF decoder at all and is drawn by <see cref="Vector"/> through a
    /// package, which is the whole reason that package is referenced.
    /// </summary>
    private static readonly Dictionary<string, string> Types =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".bmp"] = "image/bmp",
            [".ico"] = "image/x-icon",
            [".webp"] = Webp,
            [".svg"] = Svg,
        };

    /// <summary>Named because the decoder for it is optional on Windows, which the line
    /// about a webp that wouldn't decode has to know — see <see cref="Read"/>.</summary>
    private const string Webp = "image/webp";

    /// <summary>Named because it takes a different road out of <see cref="Read"/>: shapes
    /// to be drawn rather than pixels to be decoded.</summary>
    private const string Svg = "image/svg+xml";

    /// <summary>
    /// A tile's picture is sent to the tablet whole, over a link that also has a panel to
    /// keep up to date. Anything this size is a photograph that was meant to be an icon,
    /// so it's refused with a line saying so rather than quietly costing the link a
    /// megabyte the first time the page draws that tile.
    /// </summary>
    private const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>One picture, as far as it got. <see cref="Problem"/> set and no
    /// <see cref="Image"/> is a file that was asked for and can't be used.</summary>
    internal sealed record Picture(string Path, string Hash, string ContentType,
                                   byte[] Bytes, ImageSource? Image, string? Problem);

    private static readonly object Gate = new();

    /// <summary>Keyed by path and the file's stamp, so editing a picture and reloading
    /// draws the new one instead of what was read at startup.</summary>
    private static readonly Dictionary<string, Picture> Cache = new(StringComparer.Ordinal);

    /// <summary>The same pictures by hash, which is how the tablet asks for one: it is
    /// given the hash and knows nothing about where the file was.</summary>
    private static readonly Dictionary<string, Picture> ByHash = new(StringComparer.Ordinal);

    /// <summary>
    /// Where <paramref name="icon"/> points, for a config that lives in
    /// <paramref name="folder"/>. Relative is relative to the config file and not to the
    /// working directory: the panel is started from a shortcut, a tray icon and a build
    /// folder, and the config's own folder is the only one of those that means the same
    /// thing every time.
    /// </summary>
    public static string Resolve(string folder, string icon)
    {
        string expanded = Environment.ExpandEnvironmentVariables(icon.Trim());
        return Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(Path.Combine(folder, expanded));
    }

    /// <summary>
    /// The picture a tile wears, or null when it asked for none. A file that can't be
    /// used comes back with <see cref="Picture.Problem"/> set and no image, so the tile
    /// can fall back to its label and <see cref="DashboardConfig"/> can say why once at
    /// load — rather than the panel drawing nothing and never mentioning it.
    /// </summary>
    public static Picture? Of(string folder, string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;

        string path;
        try { path = Resolve(folder, icon); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException
                                      or NotSupportedException)
        {
            return Bad(icon, $"the icon \"{icon}\" isn't a path this machine can read — "
                             + ex.Message);
        }

        var file = new FileInfo(path);
        if (!file.Exists) return Bad(path, $"there is no file at {path}");

        if (!Types.TryGetValue(file.Extension, out string? type))
        {
            return Bad(path, $"{path} isn't an image type both the panel and the tablet "
                             + "can draw — png, jpg, gif, bmp, ico, webp or svg");
        }

        if (file.Length > MaxBytes)
        {
            return Bad(path, $"{path} is {file.Length / 1024}KB, which is too much to send "
                             + $"to a tablet — keep a tile's picture under {MaxBytes / 1024}KB");
        }

        // The stamp is part of the key rather than checked against a cached one, so a
        // picture edited while the panel is running is simply a different entry: the next
        // render reads it, and the old bytes stay answerable for as long as a page on the
        // tablet is still asking for them by their hash.
        string key = $"{path}\0{file.LastWriteTimeUtc.Ticks}\0{file.Length}";

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }

        var picture = Read(path, type);

        lock (Gate)
        {
            Cache[key] = picture;
            if (picture.Problem is null) ByHash[picture.Hash] = picture;
        }

        return picture;
    }

    /// <summary>
    /// The bytes behind a hash the panel has published, for the request that comes back
    /// asking for it. This is the whole of what a request can reach: the tablet names a
    /// picture by its content and never by a path, so there is no filename in a request
    /// to sanitise and nothing outside this table to name.
    /// </summary>
    public static Picture? Find(string hash)
    {
        lock (Gate) return ByHash.GetValueOrDefault(hash);
    }

    /// <summary>Why a tile's picture can't be used, or null when it can — what
    /// <c>Check()</c> reports at load.</summary>
    public static string? Problem(string folder, string? icon) => Of(folder, icon)?.Problem;

    private static Picture Read(string path, string type)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Bad(path, $"{path} couldn't be read — {ex.Message}");
        }

        // Eight bytes of SHA-256, spelt the way a tile id is: enough that two pictures on
        // one panel won't collide, short enough to read in a log line.
        string hash = Convert.ToHexString(SHA256.HashData(bytes), 0, 8).ToLowerInvariant();

        try
        {
            if (type == Svg) return Vector(path, hash, bytes);

            // Decoded from the bytes already in hand rather than from the file, so the
            // panel never holds a handle on a picture someone is editing — OnLoad for the
            // same reason: the frame is read now, and the stream can go.
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None,
                                               BitmapCacheOption.OnLoad);

            // The biggest frame, which is what an .ico needs: several sizes of one picture
            // in a single file, and a tile would far rather scale 256px down than 16px up.
            var frame = decoder.Frames
                               .OrderByDescending(f => (long)f.PixelWidth * f.PixelHeight)
                               .FirstOrDefault();

            if (frame is null) return Bad(path, $"{path} holds no picture");

            var image = Transparency(frame, type);
            if (image.CanFreeze) image.Freeze();
            return new Picture(path, hash, type, bytes, image, null);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException
                                      or ArgumentException or OverflowException
                                      or IOException or XmlException)
        {
            // The name said png and the bytes didn't. Worth a line of its own: the tile
            // draws its label and otherwise looks exactly like an icon nobody wrote.
            //
            // On a webp it's worth more than that, because the likely cause isn't the file:
            // the format is fine and this Windows is missing the component that reads it.
            string hint = type == Webp
                ? " A webp needs a decoder Windows keeps optional — the Web Media "
                  + "Extensions component — so if the file opens elsewhere, that's the "
                  + "thing to install, or save it as png."
                : "";

            return new Picture(path, hash, type, bytes, null,
                               $"{path} couldn't be decoded — {ex.Message}{hint}");
        }
    }

    /// <summary>
    /// An svg, as shapes rather than pixels. It stays a drawing all the way to the tile,
    /// so it's sharp at whatever size the tile turns out to be — which is the reason to
    /// use one at all, given that every other format here is a fixed grid of pixels being
    /// scaled to fit.
    ///
    /// The tablet is sent the same file and draws it with its browser's own renderer, so
    /// the two are two renderings of one document rather than one picture and a copy. That
    /// holds for shapes; text set in a font one side has and the other doesn't will differ,
    /// which is a reason to prefer an svg whose lettering was converted to outlines.
    /// </summary>
    private static Picture Vector(string path, string hash, byte[] bytes)
    {
        if (Enclosed(bytes) is { } refusal) return Bad(path, $"{path} {refusal}");

        // IncludeRuntime off because the runtime bits are for the package's own controls,
        // which nothing here uses; text as geometry so a drawing doesn't depend on a font
        // still being installed when it's next drawn.
        var settings = new WpfDrawingSettings
        {
            IncludeRuntime = false,
            TextAsGeometry = true,
            OptimizePath = true,
        };

        using var stream = new MemoryStream(bytes);
        if (new FileSvgReader(settings).Read(stream) is not { } drawing)
        {
            return Bad(path, $"{path} is an svg this can't draw — nothing came back from "
                             + "reading it");
        }

        var image = new DrawingImage(drawing);
        if (image.CanFreeze) image.Freeze();
        return new Picture(path, hash, Svg, bytes, image, null);
    }

    /// <summary>
    /// Whether an svg is only itself, and why not when it isn't. An svg is a document
    /// rather than a picture: it can pull in another file, declare entities that expand as
    /// they're read, and carry script. None of that belongs on a button, and all of it
    /// would break the promise the rest of this file keeps — that loading a config reads
    /// the files it was told to read and nothing else.
    ///
    /// So the markup is walked once before anything draws it, with the DTD prohibited (a
    /// DOCTYPE throws here rather than being expanded) and no resolver, and anything that
    /// points anywhere but at itself is refused. A reference within the document
    /// (<c>#gradient</c>) is what an svg legitimately needs; a path or a URL is a file this
    /// would have to fetch, and would fail on the tablet in any case, where the file
    /// arrives with nothing around it.
    ///
    /// A picture carried inline (<c>data:</c>) is refused for a duller reason: the drawing
    /// package quietly draws nothing for it while a browser draws it, so the tile would
    /// differ between the two screens. Point the tile at the picture instead — that's what
    /// the other six formats are for.
    /// </summary>
    private static string? Enclosed(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
            });

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;

                if (reader.LocalName is "script" or "foreignObject")
                {
                    return $"has a <{reader.LocalName}> in it. A button's picture is a "
                           + "picture; neither screen would draw that, and it has no "
                           + "business being read as anything else.";
                }

                for (int i = 0; i < reader.AttributeCount; i++)
                {
                    reader.MoveToAttribute(i);
                    if (Outside(reader.LocalName, reader.Value) is { } outside) return outside;
                }
            }

            return null;
        }
        catch (XmlException ex)
        {
            // Includes the DOCTYPE case, which is the one worth catching: prohibited above
            // rather than expanded, so a file that grows as it's read never gets read.
            return $"isn't xml this will read — {ex.Message}";
        }
    }

    /// <summary>One attribute, and whether it reaches out of the file.</summary>
    private static string? Outside(string name, string value)
    {
        if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) && name.Length > 2)
        {
            return $"has an {name} handler on it. See above: a picture.";
        }

        bool points = name is "href" or "src"
                      || value.Contains("url(", StringComparison.OrdinalIgnoreCase);

        if (!points) return null;

        // Inside the document, and nothing else. A relative path would be read from beside
        // the file here and from beside a blob URL on the tablet, which is to say drawn on
        // one screen and not the other.
        string target = Trimmed(value);
        if (target.Length == 0 || target.StartsWith('#')) return null;

        // Said separately because it's the one that looks like it ought to work: the bytes
        // really are in the file, and a browser really does draw them.
        if (target.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return "carries a picture inline (data:…), which the tablet's browser would draw "
                   + "and this wouldn't — so the tile would differ between the two screens. "
                   + "Point the tile straight at that picture instead; png, jpg, gif, bmp, "
                   + "ico and webp are all accepted.";
        }

        return $"points at {Shortened(target)}, which is outside the file. An svg on a tile "
               + "may point within itself (#…) and nowhere else — anything else is a file "
               + "this would have to go and fetch, and the tablet couldn't.";
    }

    /// <summary>A url() value with the wrapper taken off, so what's reported is the thing
    /// pointed at rather than the css around it.</summary>
    private static string Trimmed(string value)
    {
        int open = value.IndexOf("url(", StringComparison.OrdinalIgnoreCase);
        if (open < 0) return value.Trim();

        int close = value.IndexOf(')', open);
        string inside = close < 0 ? value[(open + 4)..] : value[(open + 4)..close];
        return inside.Trim().Trim('"', '\'');
    }

    private static string Shortened(string target) =>
        target.Length <= 60 ? $"\"{target}\"" : $"\"{target[..60]}…\"";

    /// <summary>
    /// Windows' webp decoder hands back a frame labelled Bgr32 — "no alpha in here" —
    /// while the fourth byte of every pixel is in fact the alpha, premultiplied. Taken at
    /// its word, a logo saved with a transparent background draws as a logo in a solid
    /// black box, which on a dark tile is the one thing a picture must not do. So a webp
    /// that came back that way is relabelled rather than converted: the same bytes, read
    /// as the Pbgra32 they are. Premultiplied is not a guess — an anti-aliased edge comes
    /// back with every colour channel below its alpha, which straight alpha wouldn't do.
    ///
    /// Only webp, and only that one format: png, gif and ico arrive as Bgra32 already and
    /// have nothing to put right. A webp with no transparency carries 255 in that byte
    /// throughout, so this costs it one copy and changes nothing about it.
    /// </summary>
    private static BitmapSource Transparency(BitmapSource frame, string type)
    {
        if (type != Webp || frame.Format != PixelFormats.Bgr32) return frame;

        // Past this, a second copy of the pixels is a worse thing than a black box: it's
        // 64MB at the limit, and a tile's picture has no business being that big anyway.
        if (frame.PixelWidth > 4096 || frame.PixelHeight > 4096) return frame;

        int stride = frame.PixelWidth * 4;
        var pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);

        return BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, frame.DpiX, frame.DpiY,
                                   PixelFormats.Pbgra32, null, pixels, stride);
    }
    private static Picture Bad(string path, string problem) =>
        new(path, "", "", Array.Empty<byte>(), null, problem);
}
