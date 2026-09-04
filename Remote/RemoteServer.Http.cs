using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace Deckhand;

// Reading requests and answering them: the accept loop, the routing table, the tap
// endpoint, and the two helpers every reply goes out through. The long poll is here too —
// a request parked for twenty-five seconds is what makes the tablet's panel change at the
// same moment the laptop's does, with neither end polling for it.
internal sealed partial class RemoteServer
{
    private async Task Loop()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException
                                          or InvalidOperationException)
            {
                return; // disposed, or the listener died — either way we're done
            }

            // Off the accept loop, because handling a request blocks: on the body being
            // read, and on the UI thread for a tap. Handled here, one client that stopped
            // mid-request would stop this loop accepting for as long as it cared to — and
            // a dashboard that has silently stopped answering is worse than one that says
            // it can't, because there's nothing on screen here to notice it by.
            //
            // Only one tablet ever talks to this, so nothing below is racing for
            // throughput; the point is that a stalled request can't take the rest down
            // with it. What the two threads share is written for it: interlocked counters,
            // a volatile snapshot swapped whole, a log behind a lock, and a Dispatcher
            // that serializes the taps.
            _ = Task.Run(() =>
            {
                // One bad request must never end the loop, or the tablet goes dark for
                // the rest of the session.
                try { Handle(context); }
                catch (Exception) { TryFail(context); }
            });
        }
    }

    /// <summary>Runs on a thread pool thread, one per request — see <see cref="Loop"/>.</summary>
    private void Handle(HttpListenerContext context)
    {
        var request = context.Request;
        string path = request.Url?.AbsolutePath ?? "/";

        // Before anything is served or done, because this is the check that says which
        // *page* is talking to us rather than which machine — see HostAllowed.
        if (!HostAllowed(request))
        {
            if (DueAgain(ref _lastBadHostTicks, PageAgain))
            {
                _log.Add(LogKind.Warning, $"a request from {request.RemoteEndPoint?.Address} "
                                          + $"called this machine \"{request.UserHostName}\", "
                                          + "which isn't a name it answers to — refused");
            }

            Send(context, HttpStatusCode.Forbidden, "text/plain; charset=utf-8",
                 "that isn't a name this dashboard answers to. Open it by address — or, for "
                 + "a name worth using, add it to remote.allowedHosts in dashboard.json.");
            return;
        }

        // The page itself carries no data and no token — it's the thing that asks for
        // one. Everything that reads or does anything is behind the token below.
        if (request.HttpMethod == "GET" && path is "/" or "/index.html")
        {
            // The first sign of life from a tablet, and the one that proves the address
            // and the network are right even when the token turns out to be wrong.
            //
            // Rationed, because this is written before anything has been proved about who
            // asked: fetching the page needs no token, so anything that can reach the port
            // could otherwise write a line per request and scroll a session's worth of log
            // out of a 500-line window. One tablet opens the page a handful of times a
            // day, so a line every half minute loses nothing real.
            if (DueAgain(ref _lastPageTicks, PageAgain))
            {
                _log.Add(LogKind.Panel,
                         $"the page was opened by {request.RemoteEndPoint?.Address}");
            }

            Send(context, HttpStatusCode.OK, "text/html; charset=utf-8", _page);
            return;
        }

        // The manifest, the icons and the service worker. Also no token, and for a reason
        // that isn't a choice: a browser fetches these by itself, from a <link> or a
        // register() call, and there's no way to make it send a header of ours with them.
        // They carry the app's name, its picture and its caching rules — nothing about the
        // panel, and nothing that does anything.
        if (request.HttpMethod == "GET" && _files.TryGetValue(path, out var file))
        {
            SendBytes(context, HttpStatusCode.OK, file.ContentType, file.Bytes);
            return;
        }

        string? peer = request.RemoteEndPoint?.Address.ToString();
        string? device = request.Headers[DeviceHeader];
        bool paired = device is not null && device == Volatile.Read(ref _pinned);

        // Guessing is rationed by the address doing the guessing, and the paired tablet
        // is exempt even from its own: a stranger getting the token wrong on purpose only
        // ever locks itself out, and can no longer deny the tablet its first connection
        // by spraying the port before it pairs.
        if (!paired && LockedOut(peer))
        {
            Send(context, HttpStatusCode.TooManyRequests, "text/plain; charset=utf-8",
                 "too many wrong tokens have been tried from this address. Wait a minute, "
                 + "then try again.");
            return;
        }

        if (!IsAuthorized(request))
        {
            NoteFailure(peer);
            LogRejection(request);

            // The character count of what was *offered* — never of the real token — so a
            // mistyped or stale token identifies itself instead of silently bouncing
            // the tablet back to the token box with nothing to go on.
            string offered = request.Headers[TokenHeader] ?? "";
            Send(context, HttpStatusCode.Unauthorized, "text/plain; charset=utf-8",
                 offered.Length == 0
                     ? "no token sent"
                     : $"that token was rejected ({offered.Length} characters sent). If you "
                       + "changed the token, tap ↻ on the panel so it's re-read.");
            return;
        }

        // The right token, but from a device that isn't the one this session belongs to.
        // Refused rather than quietly taking over, which is what a token copied off the
        // wire would otherwise buy: it's no longer enough to have heard the token, you have
        // to be the tablet that was already using it.
        if (!Claim(device, peer))
        {
            if (DueAgain(ref _lastPinRefusalTicks, PageAgain))
            {
                _log.Add(LogKind.Warning, $"{peer} had the right token but this session is "
                                          + $"paired with {Pinned} — refused");
            }

            Send(context, HttpStatusCode.Forbidden, "text/plain; charset=utf-8",
                 "this dashboard is already paired with another device. Tap Unpair (or ↻) "
                 + "on the laptop to let this one take over.");
            return;
        }

        // Past the token, so this really is the tablet rather than something knocking.
        NoteSeen(request);

        if (request.HttpMethod == "GET" && path == "/api/tiles")
        {
            // The tablet asks and the answer is held until there's one worth giving: a
            // request that finds nothing new parks until the panel next changes or the
            // hold runs out, so a change reaches the tablet the moment it happens rather
            // than on the next tick of a timer — and the radio over there wakes for one
            // response instead of twenty that all said "nothing".
            //
            // "Nothing new" has to include which build this is, not just the revision.
            // Restarting puts the revision count back to the beginning, so a page that
            // reconnects to a rebuilt app on the same number it left — one or two, for a
            // panel nobody has touched — would be told it was up to date and go on running
            // code the snapshot no longer matches, with nothing to notice it by until the
            // panel next happened to change. Installed to a home screen, that page could
            // stay wrong for days with no address bar to reload it from.
            //
            // The change signal is read before the revision is compared, so a publish
            // landing between the two is a wait that returns at once, not one missed.
            var changed = Volatile.Read(ref _changed).Task;
            if (int.TryParse(request.QueryString["since"], out int since)
                && since == Volatile.Read(ref _revision)
                && request.QueryString["page"] == _pageBuild)
            {
                Interlocked.Increment(ref _parked);
                try { changed.Wait(LongPollHold); }
                finally { Interlocked.Decrement(ref _parked); }

                // Nothing changed while parked — or only the wait ran out. 204 either
                // way; the page turns straight around and parks again.
                if (since == Volatile.Read(ref _revision))
                {
                    Send(context, HttpStatusCode.NoContent, null, null);
                    return;
                }
            }

            Send(context, HttpStatusCode.OK, "application/json; charset=utf-8", _snapshot);
            return;
        }

        // A tile's picture, named by the hash of its own bytes. Behind the token and the
        // pairing like everything else here: the pictures are this machine's files, and
        // which ones a panel wears is as much about it as the tiles are.
        if (request.HttpMethod == "GET" && path.StartsWith(IconPath, StringComparison.Ordinal))
        {
            SendIcon(context, path[IconPath.Length..]);
            return;
        }

        if (request.HttpMethod == "POST" && path == "/api/tap")
        {
            Tap(context);
            return;
        }

        Send(context, HttpStatusCode.NotFound, "text/plain; charset=utf-8", "no such thing");
    }

    /// <summary>
    /// Marks the link alive, and says so in the log when it's news: a different device,
    /// or the same one back after long enough that the last line has scrolled out of
    /// mind. A poll every second is not worth a line each.
    /// </summary>
    private void NoteSeen(HttpListenerRequest request)
    {
        string? peer = request.RemoteEndPoint?.Address.ToString();

        long now = DateTime.UtcNow.Ticks;
        long previous = Interlocked.Exchange(ref _lastSeenTicks, now);
        bool quiet = previous == 0 || now - previous > QuietAgain.Ticks;
        bool different = peer != _peer;

        _peer = peer;

        if (different) _log.Add(LogKind.Panel, $"tablet connected — {peer}");
        else if (quiet) _log.Add(LogKind.Panel, $"tablet back — {peer}");

        NoteScreen(request.Headers[ScreenHeader], different);
        NoteViewport(request.Headers[ViewportHeader], different);
    }

    /// <summary>
    /// Records what the tablet says about its own screen, and logs it when the answer
    /// changes — or when a new tablet arrives, since then it's news either way. The page
    /// sends this on every request, so silence here means an older page.
    /// </summary>
    private void NoteScreen(string? screen, bool newTablet)
    {
        if (screen is not ("lock" or "video" or "none")) return;

        bool changed = screen != _screen;
        _screen = screen;
        if (!changed && !newTablet) return;

        _log.Add(screen == "none" ? LogKind.Warning : LogKind.Panel, screen switch
        {
            "lock" => "the tablet is holding its screen awake",
            "video" => "the tablet is holding its screen awake as best it can",
            _ => "the tablet can't keep its screen awake — it may sleep mid-task",
        });
    }

    /// <summary>
    /// How much screen the page says it has, and a line in the log when that changes —
    /// which is the point of asking at all: the panel is being laid out for a screen
    /// nothing on this end can measure, and the answer is a number to put into the
    /// designer. Rotating the tablet or dropping the browser's bars changes it, so it's
    /// a line per change rather than a line per request.
    ///
    /// Junk is dropped rather than reported. Anything that gets past the token can send
    /// this header, and the log is the only account of what happened here — so nothing
    /// but numbers a screen could plausibly have ever reaches it, and never the text as
    /// it arrived.
    /// </summary>
    private void NoteViewport(string? header, bool newTablet)
    {
        if (ReadViewport(header) is not { } tablet) return;

        var before = _tablet;
        _tablet = tablet;

        // Records compare by value, so this is "the same measurement", not "the same
        // object" — the page sends a fresh one of these every second.
        if (tablet == before && !newTablet) return;

        string ratio = tablet.Ratio > 1 ? $" at {tablet.Ratio:0.##}× pixels" : "";
        string tiles = tablet.TilesWidth is { } width && tablet.TilesHeight is { } height
            ? $", tiles {width}×{height} — that pair is what the designer's screen size wants"
            : "";

        _log.Add(LogKind.Panel, $"the tablet's screen is {tablet.Width}×{tablet.Height}"
                                + $"{ratio}{tiles}");
    }

    /// <summary>
    /// The one shape accepted: "1280x800@2", optionally followed by " tiles=1264x744".
    /// Null for anything else — a header is exactly as trustworthy as whoever sent it,
    /// and this one ends up on screen.
    /// </summary>
    private static TabletScreen? ReadViewport(string? header)
    {
        if (header is null || header.Length is 0 or > 64) return null;

        var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 2) return null;

        var box = parts[0].Split('@');
        if (box.Length > 2 || ReadBox(box[0]) is not { } viewport) return null;

        double ratio = 1;
        if (box.Length == 2
            && (!double.TryParse(box[1], NumberStyles.AllowDecimalPoint,
                                 CultureInfo.InvariantCulture, out ratio)
                || ratio is < 0.25 or > 16))
        {
            return null;
        }

        (int W, int H)? tiles = null;
        if (parts.Length == 2)
        {
            const string prefix = "tiles=";
            if (!parts[1].StartsWith(prefix, StringComparison.Ordinal)) return null;
            if (ReadBox(parts[1][prefix.Length..]) is not { } measured) return null;
            tiles = measured;
        }

        return new TabletScreen(viewport.W, viewport.H, ratio, tiles?.W, tiles?.H);
    }

    /// <summary>
    /// "1280x800", and only when both halves are a plain count of pixels a screen could
    /// have. NumberStyles.None on purpose: no sign, no spaces, no thousands separator —
    /// the page sends digits, so anything else is not the page.
    /// </summary>
    private static (int W, int H)? ReadBox(string text)
    {
        int cross = text.IndexOf('x');
        if (cross < 1) return null;

        return Pixels(text[..cross]) is { } width && Pixels(text[(cross + 1)..]) is { } height
            ? (width, height)
            : null;
    }

    private static int? Pixels(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
        && value is >= 64 and <= 20000
            ? value
            : null;

    /// <summary>After this much silence, the next request is worth a line again.</summary>
    private static readonly TimeSpan QuietAgain = TimeSpan.FromSeconds(30);

    /// <summary>How often serving the page is worth a line, however often it's asked for.</summary>
    private static readonly TimeSpan PageAgain = TimeSpan.FromSeconds(30);

    /// <summary>
    /// True at most once per <paramref name="window"/>, for the lines anything that can
    /// reach the port can produce at will. The log is 500 lines deep and is the only
    /// account of what happened here, so those lines are rationed rather than written each
    /// time — and <see cref="ActivityLog"/> drops them before it drops a tap.
    /// </summary>
    private static bool DueAgain(ref long last, TimeSpan window)
    {
        long now = DateTime.UtcNow.Ticks;
        long seen = Interlocked.Read(ref last);
        if (now - seen < window.Ticks) return false;

        // CompareExchange rather than Exchange: requests are handled off the accept loop,
        // so two arriving together must not both come away thinking they were the one due.
        return Interlocked.CompareExchange(ref last, now, seen) == seen;
    }

    private long _lastPageTicks;
    private long _lastBadHostTicks;
    private long _lastPinRefusalTicks;

    // ---- Taps --------------------------------------------------------------

    private void Tap(HttpListenerContext context)
    {
        string? id = ReadTileId(context.Request);
        if (id is null)
        {
            Send(context, HttpStatusCode.BadRequest, "text/plain; charset=utf-8", "expected {\"id\":\"…\"}");
            return;
        }

        TapResult result;
        try
        {
            // The tiles belong to the UI thread. A timeout rather than a wait without
            // end: the UI thread can be sitting on a modal error dialog from a launch
            // that failed, and the tablet deserves an answer either way.
            result = _dispatcher.Invoke(() => _onTap(id), DispatcherPriority.Normal,
                                        CancellationToken.None, TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            Send(context, HttpStatusCode.ServiceUnavailable, "text/plain; charset=utf-8",
                 "the panel is busy — check it for a dialog");
            return;
        }

        if (result == TapResult.Fired) Interlocked.Increment(ref _taps);

        var (status, message) = result switch
        {
            TapResult.Fired => (HttpStatusCode.OK, "done"),
            TapResult.PanelUnlocked => (HttpStatusCode.Conflict, "the panel is unlocked, so taps do nothing"),
            TapResult.DashboardFocused => (HttpStatusCode.Conflict,
                "the dashboard's own window has focus on the laptop — click back into your app first"),
            TapResult.BehindOverlay => (HttpStatusCode.Conflict,
                "the picker is open — choose one of its windows, or cancel"),
            _ => (HttpStatusCode.NotFound, "that tile isn't on screen any more"),
        };
        Send(context, status, "application/json; charset=utf-8",
             JsonSerializer.Serialize(new { ok = result == TapResult.Fired, message }, JsonOptions));
    }

    /// <summary>The tapped id, or null if the body isn't the one shape we accept.</summary>
    private static string? ReadTileId(HttpListenerRequest request)
    {
        try
        {
            // Capped, and read to the end of what fits: a body this small can still
            // arrive in more than one piece.
            var buffer = new byte[MaximumBodyBytes];
            int read = 0, chunk;
            while (read < buffer.Length
                   && (chunk = request.InputStream.Read(buffer, read, buffer.Length - read)) > 0)
            {
                read += chunk;
            }

            using var document = JsonDocument.Parse(buffer.AsMemory(0, read));

            return document.RootElement.TryGetProperty("id", out var id)
                   && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException)
        {
            return null;
        }
    }

    // ---- Replies -----------------------------------------------------------

    private static void Send(HttpListenerContext context, HttpStatusCode status,
                             string? contentType, string? body) =>
        SendBytes(context, status, contentType, body is null ? null : Encoding.UTF8.GetBytes(body));

    /// <summary>
    /// One tile's picture. Nothing here reads the disk: the set was read when the panel
    /// was drawn and published with it, so a hash that no tile names is a 404 and a hash
    /// that one does can only be a file some tile asked for by name. That is what makes an
    /// arbitrary string in this URL safe — it is a key into a table, never a path — and it
    /// is why the answer says "not on the panel" rather than "no such file", which would
    /// be a way to ask this machine what it has.
    /// </summary>
    private void SendIcon(HttpListenerContext context, string hash)
    {
        if (Picture(hash) is not { } picture)
        {
            Send(context, HttpStatusCode.NotFound, "text/plain; charset=utf-8",
                 "no picture by that name is on the panel");
            return;
        }

        SendBytes(context, HttpStatusCode.OK, picture.ContentType, picture.Bytes);
    }

    /// <summary>
    /// As above, for the things that aren't text — the app's own icons, and the pictures
    /// on the tiles. Named rather than overloaded so that `Send(context, status, null,
    /// null)`, which is how a 204 is sent, still says one thing.
    /// </summary>
    private static void SendBytes(HttpListenerContext context, HttpStatusCode status,
                                  string? contentType, byte[]? body)
    {
        var response = context.Response;
        response.StatusCode = (int)status;

        // Including the icons and the manifest, which are cheap to re-fetch over a LAN and
        // must never be the reason a rebuilt app keeps serving an old name or picture. The
        // service worker's cache is what makes an installed copy launch quickly, and that
        // one is versioned by build rather than left to the browser to guess about.
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";

        // frame-ancestors is the one that matters most here. Framed by another page, this
        // one would draw the live panel and an overlay could line its own targets up over
        // the tiles — so a click meant for something else lands on a tile, and a tile
        // launches things as administrator. Storage partitioning stops that on current
        // browsers by keeping the token out of a framed copy; this stops it outright, and
        // X-Frame-Options says the same thing to a browser too old to read the CSP.
        //
        // base-uri and form-action close the other two ways a page can be made to talk
        // somewhere it wasn't built to. 'unsafe-inline' is a knowing compromise: the page's
        // script and style are inline by design, being one embedded file served from
        // memory, and nothing in it writes markup — every value from a snapshot goes in
        // through textContent or a style property — so there is no sink for the directive
        // to protect. It's here for what it does allow to be locked down, not as a claim
        // to have shut inline script out.
        //
        // blob: on img-src is the tile pictures. They're behind the token like everything
        // else, and an <img src> can't be made to send a header of ours, so the page
        // fetches them itself and draws them from object URLs — data the page already
        // holds, given a name it can put in an attribute.
        response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self' 'unsafe-inline'; "
            + "style-src 'self' 'unsafe-inline'; img-src 'self' blob:; connect-src 'self'; "
            + "media-src 'self' blob:; frame-ancestors 'none'; base-uri 'none'; "
            + "form-action 'none'";
        response.Headers["X-Frame-Options"] = "DENY";
        response.Headers["Referrer-Policy"] = "no-referrer";

        if (contentType is not null) response.ContentType = contentType;

        if (body is not null)
        {
            response.ContentLength64 = body.Length;
            response.OutputStream.Write(body, 0, body.Length);
        }

        response.Close();
    }

    private static void TryFail(HttpListenerContext context)
    {
        try { Send(context, HttpStatusCode.InternalServerError, "text/plain; charset=utf-8", "sorry"); }
        catch (Exception) { /* the client is gone; nothing left to tell it */ }
    }
}
