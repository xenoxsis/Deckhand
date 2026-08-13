using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace Deckhand;

/// <summary>
/// Serves the panel to a tablet on the same network. The tablet's browser draws
/// whatever the panel is currently showing and sends back the id of the tile that was
/// tapped; the tap is then raised on that very <see cref="System.Windows.Controls.Button"/>
/// back here, so a remote tap and a finger on the panel run the same code by
/// construction. Nothing about what a tile *does* is reimplemented over HTTP.
///
/// That the action happens on this machine is the whole point: typing into the focused
/// window only works from inside the session that owns it, at an integrity level that
/// UIPI will let through. A tablet can't do either, and doesn't have to.
///
/// Three rules keep an "it types and launches things as administrator" endpoint from
/// being a remote shell:
///
///  - **Ids only.** A request can name a tile that is already configured and on screen.
///    There is no field anywhere in the protocol that carries a path, a command or text
///    to type, so the worst a request can do is fire one of your own tiles.
///  - **A token on every call**, compared in constant time. Without a long one the server
///    refuses to start rather than listening open. It's kept outside the project — see
///    <see cref="RemoteSettings.TokenFile"/> — so a copy of the code isn't a copy of the
///    key to it.
///  - **Off unless asked for**, and bound to whatever <see cref="RemoteSettings.Listen"/>
///    says, so it can be narrowed to one interface or to loopback for a tunnel.
///
/// It speaks plain HTTP: the token crosses the LAN in the clear. That's the trade-off
/// for something that has to work from a browser on a tablet with no certificate to
/// trust — put it on a tunnel (Tailscale, or loopback plus port forwarding) if the
/// network isn't yours.
/// </summary>
internal sealed partial class RemoteServer : IDisposable
{
    /// <summary>What came of a tap, so the tablet can say something true about it.</summary>
    public enum TapResult
    {
        Fired,

        /// <summary>The id isn't on screen — a page left open across a config change.</summary>
        UnknownTile,

        /// <summary>The panel is unlocked, when even a local tap does nothing.</summary>
        PanelUnlocked,

        /// <summary>
        /// One of the dashboard's own windows has focus here, so a snippet would type
        /// into it instead of into the app being worked in.
        /// </summary>
        DashboardFocused,

        /// <summary>
        /// The picker is open over the panel, and this tap named something behind it. A
        /// finger on the panel can't reach those tiles either — the modal covers them —
        /// so the tablet doesn't get to do what the panel can't.
        /// </summary>
        BehindOverlay,
    }

    private const string TokenHeader = "X-Dashboard-Token";
    private const string ScreenHeader = "X-Dashboard-Screen";

    /// <summary>
    /// The page's own id for the device it's running on — a random value it makes once
    /// and keeps in its storage. This is what the session is pinned to; see
    /// <see cref="_pinned"/> for why it isn't the address.
    /// </summary>
    private const string DeviceHeader = "X-Dashboard-Device";

    private const int MaximumBodyBytes = 1024;

    private readonly HttpListener _listener;

    /// <summary>
    /// SHA-256 of the token, not the token: it makes every comparison one between two
    /// 32-byte digests, which is what keeps the length of a wrong guess from coming back
    /// out of the timing. See <see cref="IsAuthorized"/>.
    /// </summary>
    private readonly byte[] _tokenHash;

    /// <summary>Names, besides an address, that this machine answers to — see <see
    /// cref="HostAllowed"/>.</summary>
    private readonly IReadOnlyList<string> _allowedHosts;

    /// <summary>The host in the prefix, or null when it's the "+" wildcard.</summary>
    private readonly string? _configuredHost;

    private readonly Dispatcher _dispatcher;
    private readonly Func<string, TapResult> _onTap;
    private readonly string _page;

    /// <summary>
    /// The files a browser fetches for itself, by the path it asks for: the manifest, the
    /// icons, the service worker. Unlike the panel they're static, so they're read from
    /// the assembly once.
    /// </summary>
    private readonly IReadOnlyDictionary<string, StaticFile> _files;

    /// <summary>
    /// Which build of the browser's half of this app it's talking to, sent with every
    /// snapshot. A page the tablet opened before the app was rebuilt goes on running its
    /// old code against new snapshots — and since the two are designed together, that
    /// draws the panel wrong in ways that look like a fault in the panel. The page
    /// compares this with what it was serving when it loaded and replaces itself when
    /// they differ.
    ///
    /// It fingerprints everything served, not just the page: the service worker keeps a
    /// cache named after this, and the manifest and the page have to agree about what the
    /// app is called and where it starts. One hash over all of it means any change to any
    /// of them is one update, and none of them can be left a build behind.
    /// </summary>
    private readonly string _pageBuild;

    private readonly ActivityLog _log;

    /// <summary>
    /// The current snapshot, already serialized: it changes far less often than it's
    /// asked for. Replaced wholesale, never mutated, so a request can't see it half
    /// written.
    /// </summary>
    private volatile string _snapshot =
        "{\"revision\":0,\"context\":\"\",\"ready\":false,\"columns\":1,\"rows\":1,\"groups\":[]}";
    private int _revision;

    // Set from the listener thread, read from the UI thread by the status window: in
    // remote mode nothing is on screen here, so "is the tablet actually talking to me"
    // is otherwise unanswerable without reaching for a packet capture.
    private long _lastSeenTicks;
    private volatile string? _peer;
    private int _taps;

    /// <summary>
    /// The one device this session belongs to: the id of the first to get the token
    /// right, and after that the only one allowed to. One tablet is the whole design —
    /// nothing about two makes sense here — so there is no reason to accept a second,
    /// and one very good reason not to: the token crosses the LAN in the clear, so
    /// anyone who listened has it, and this is what makes having it insufficient.
    ///
    /// The id is the page's own — see <see cref="DeviceHeader"/> — rather than the
    /// address it called from, because an address stops identifying anything the moment
    /// there's a tunnel or a new lease in the way: every client of a port-forward
    /// arrives from one address, and yesterday's tablet arrives from a new one. The
    /// page's id survives both. Not volatile, because it's read and claimed through
    /// <see cref="Interlocked"/>.
    ///
    /// Released by <see cref="Unpair"/>, or by tapping ↻, which restarts the server —
    /// that being the existing "I've changed something, pick it up" gesture.
    /// </summary>
    private string? _pinned;

    /// <summary>Where the pinned device last called from, for the status window: an id
    /// on its own answers "who" but not "where", and "where" is what a human checks.</summary>
    private volatile string? _pinnedFrom;

    /// <summary>
    /// Wrong tokens by the address they came from. One counter for the whole port meant
    /// anything on the LAN could spray wrong tokens and keep the real tablet locked out
    /// before it had ever paired; counted per address, a stranger only ever locks itself
    /// out. Bounded, so something cycling addresses grows a table and nothing else, and
    /// behind a plain lock — the traffic is one tablet and whatever is knocking.
    /// </summary>
    private readonly Dictionary<string, FailureSlot> _failures = new();

    private sealed class FailureSlot
    {
        public long WindowStartTicks;
        public int Count;
        public long LockedUntilTicks;
    }

    private const int MaximumTrackedAddresses = 128;

    /// <summary>
    /// How many wrong tokens inside <see cref="FailureWindow"/> shut the door, and for how
    /// long. Ten a minute leaves even a bad sixteen-character token out of reach, and
    /// leaves a mistyped one enough room to be mistyped a few times.
    /// </summary>
    private const int MaximumFailures = 10;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Completed and replaced by every <see cref="Publish"/>, which is what lets a tiles
    /// request wait for "something changed" instead of asking once a second.
    /// RunContinuationsAsynchronously so nothing waiting runs on the UI thread that
    /// published.
    /// </summary>
    private TaskCompletionSource _changed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Tiles requests currently held open waiting for a change. A parked
    /// request is a live tablet, which is what <see cref="Activity"/> reads this for.</summary>
    private int _parked;

    /// <summary>
    /// How long a tiles request with nothing new is held before answering 204 anyway.
    /// Under what the page waits before giving up on the fetch, and under anything a
    /// proxy or http.sys would kill quietly.
    /// </summary>
    private static readonly TimeSpan LongPollHold = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Whether the tablet is managing to keep its own screen on, as it reports on every
    /// request: "lock", "video" or "none". Held here so the status window can say so, and
    /// logged when it changes — a tablet that has given up and started sleeping mid-task
    /// is worth knowing about before it's blamed on the dashboard.
    /// </summary>
    private volatile string? _screen;

    private RemoteServer(HttpListener listener, string token,
                         IReadOnlyList<string> allowedHosts, Dispatcher dispatcher,
                         Func<string, TapResult> onTap, Assets assets, ActivityLog log)
    {
        _listener = listener;
        _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        _allowedHosts = allowedHosts;
        _configuredHost = HostOf(listener.Prefixes.FirstOrDefault() ?? "");
        _dispatcher = dispatcher;
        _onTap = onTap;
        _page = assets.Page;
        _files = assets.Files;
        _pageBuild = assets.Build;
        _log = log;
    }

    /// <summary>
    /// Starts listening, or returns null with <paramref name="error"/> set. Failing to
    /// serve the panel remotely is never a reason to take the panel itself down.
    /// </summary>
    public static RemoteServer? TryStart(RemoteSettings settings, Dispatcher dispatcher,
                                        Func<string, TapResult> onTap, ActivityLog log,
                                        out string? error)
    {
        error = null;

        if (settings.Token.Length < RemoteSettings.MinimumTokenLength)
        {
            error = $"the remote token must be at least {RemoteSettings.MinimumTokenLength} "
                    + "characters. The remote panel types and launches things as "
                    + $"administrator, so it won't listen without one.\n\nPut it in "
                    + $"{settings.ResolveTokenFile()} — one line, nothing else. "
                    + $"({settings.TokenSource})";
            return null;
        }

        string prefix = settings.Listen.EndsWith('/') ? settings.Listen : settings.Listen + "/";

        var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add(prefix);

            // A body that stops arriving mid-request mustn't hold a handler for ever:
            // ReadTileId blocks on that stream, so http.sys giving up on the connection is
            // what turns a client that went quiet into the IOException it already treats
            // as a malformed body. Without this the default is two minutes.
            try { listener.TimeoutManager.EntityBody = TimeSpan.FromSeconds(10); }
            catch (Exception ex) when (ex is PlatformNotSupportedException
                                          or NotSupportedException)
            {
                // Not every platform exposes http.sys's timeouts. The handler being off
                // the accept loop is what actually keeps the listener answering.
            }

            listener.Start();
        }
        catch (Exception ex) when (ex is HttpListenerException or ArgumentException
                                      or ObjectDisposedException)
        {
            // Port already taken, or a prefix Windows won't grant. Both are worth
            // saying out loud: the tablet would otherwise just never connect.
            error = $"couldn't listen on {prefix} — {ex.Message}";
            listener.Close();
            return null;
        }

        var server = new RemoteServer(listener, settings.Token, settings.AllowedHosts,
                                      dispatcher, onTap, LoadAssets(), log);
        _ = server.Loop();
        return server;
    }

    /// <summary>
    /// Replaces what the tablet sees. Called whenever the panel is rebuilt, so the
    /// remote view follows the focused app the same way the panel does.
    /// </summary>
    public void Publish(string context, bool ready, int columns, int rows,
                        IReadOnlyList<RemoteGroup> groups, RemoteOverlay? overlay)
    {
        var snapshot = new RemoteSnapshot(++_revision, context, ready, columns, rows,
                                          groups, _pageBuild, overlay);
        _snapshot = JsonSerializer.Serialize(snapshot, JsonOptions);

        // Wake every parked tiles request. The fresh source goes in before the old one
        // completes, so a request arriving mid-publish parks against the next change
        // rather than a spent one.
        Interlocked.Exchange(ref _changed,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult();
    }

    /// <summary>The address to open on the tablet, for the ↻ tooltip to show.</summary>
    public string Address => _listener.Prefixes.FirstOrDefault() ?? "";

    /// <summary>
    /// The device this session is paired with, or null before one has connected. Shown by
    /// the status window, because a second device being refused is otherwise unexplained.
    /// A short stub of the id plus where it last called from — the id alone answers "who"
    /// in a way no human can check against anything.
    /// </summary>
    public string? Pinned
    {
        get
        {
            string? device = Volatile.Read(ref _pinned);
            return device is null ? null : Describe(device, _pinnedFrom);
        }
    }

    /// <summary>
    /// Lets the session go without restarting anything: the next device with the right
    /// token takes it. This is the half of ↻ that was only reachable bundled with the
    /// other half — re-reading the config — when all anyone wanted was to swap tablets.
    /// </summary>
    public void Unpair()
    {
        if (Interlocked.Exchange(ref _pinned, null) is null) return;

        _pinnedFrom = null;
        _log.Add(LogKind.Panel,
                 "unpaired — the next device with the right token takes the session");
    }

    private static string Describe(string device, string? peer)
    {
        string stub = device.Length > 8 ? device[..8] : device;
        return peer is null ? $"device {stub}" : $"{peer} (device {stub})";
    }

    /// <summary>
    /// Who has been talking to us and when, counted only for requests that got past the
    /// token — so a port scanner doesn't show up as "the tablet connected".
    /// </summary>
    public (DateTime? LastSeenUtc, string? Peer, int Taps, string? Screen) Activity
    {
        get
        {
            // A parked tiles request is a live tablet: it hasn't *sent* anything for up
            // to the length of the hold, but the connection is open and waiting — and
            // reporting the silence would flip the status dot on every quiet stretch.
            if (Volatile.Read(ref _parked) > 0)
            {
                return (DateTime.UtcNow, _peer, Volatile.Read(ref _taps), _screen);
            }

            long ticks = Interlocked.Read(ref _lastSeenTicks);
            return (ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc),
                    _peer, Volatile.Read(ref _taps), _screen);
        }
    }

    /// <summary>
    /// The addresses to type on the tablet. "+" means every interface, which is not
    /// something anyone can type, so it's expanded into this machine's own addresses;
    /// a prefix that already names a host is shown as configured.
    ///
    /// Interfaces with a default gateway come first: that's the closest available guess
    /// at "the network the tablet is also on". The others — WSL, Hyper-V, a VPN stub —
    /// are still listed, because guessing wrong shouldn't hide the one that works.
    /// </summary>
    public IReadOnlyList<string> Addresses()
    {
        string prefix = Address;

        int schemeEnd = prefix.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return new[] { prefix };

        int hostStart = schemeEnd + 3;
        int hostEnd = prefix.IndexOfAny(new[] { ':', '/' }, hostStart);
        if (hostEnd < 0) return new[] { prefix };

        if (prefix[hostStart..hostEnd] is not ("+" or "*")) return new[] { prefix };

        string head = prefix[..hostStart], tail = prefix[hostEnd..];
        var addresses = LocalAddresses().Select(a => head + a + tail).ToList();

        // Nothing plugged in and no Wi-Fi: show the prefix as it stands rather than an
        // empty list, which would read as "there is no address".
        return addresses.Count > 0 ? addresses : new List<string> { prefix };
    }

    private static List<string> LocalAddresses()
    {
        var found = new List<(bool Routed, string Address)>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var properties = nic.GetIPProperties();
                bool routed = properties.GatewayAddresses.Any(
                    g => g.Address is { AddressFamily: AddressFamily.InterNetwork } gateway
                         && !gateway.Equals(IPAddress.Any));

                foreach (var unicast in properties.UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(address)) continue;

                    // 169.254.x.x is what an adapter gives itself when there was no DHCP
                    // answer — an address nothing else on the network can reach.
                    string text = address.ToString();
                    if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;

                    found.Add((routed, text));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No list of interfaces is a reason to show the prefix unexpanded, not to
            // take the server down.
        }

        return found.OrderByDescending(f => f.Routed).Select(f => f.Address).Distinct().ToList();
    }

    public void Dispose()
    {
        // Close() aborts the pending GetContextAsync, which is what ends the loop.
        try { _listener.Close(); } catch (ObjectDisposedException) { }

        // Anything parked on "something changed" is let go now rather than left to sit
        // out the hold against a listener that's gone.
        _changed.TrySetResult();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
