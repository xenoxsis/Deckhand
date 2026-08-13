using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Deckhand;

// Everything that decides whether a request is allowed to do anything: the constant-time
// token compare, the host allowlist, the one-device session, and the per-address
// rationing that makes guessing a token pointless. Kept in one file because "is this
// request ours" is a question that should be answerable in one sitting.
internal sealed partial class RemoteServer
{
    /// <summary>
    /// True if <paramref name="device"/> holds this session, claiming it if nobody does.
    /// </summary>
    private bool Claim(string? device, string? peer)
    {
        // A page too old to send an id can use a session nobody holds, but it can't take
        // one and can't be recognised as holding it — as far as we can go on nothing.
        if (device is null) return Volatile.Read(ref _pinned) is null;

        string? held = Interlocked.CompareExchange(ref _pinned, device, null);
        if (held is null)
        {
            _pinnedFrom = peer;

            // Exactly one caller comes back from that with null, so this is said once.
            _log.Add(LogKind.Panel, $"paired with {Describe(device, peer)} — other "
                                    + "devices are refused until ↻ or Unpair");
            return true;
        }

        if (held != device) return false;

        // The same tablet, wherever it's calling from today — a new lease or a new
        // network changes the address without changing the device.
        _pinnedFrom = peer;
        return true;
    }

    /// <summary>Whether <paramref name="peer"/> has guessed itself into the minute box.</summary>
    private bool LockedOut(string? peer)
    {
        lock (_failures)
        {
            return _failures.TryGetValue(peer ?? "?", out var slot)
                   && DateTime.UtcNow.Ticks < slot.LockedUntilTicks;
        }
    }

    /// <summary>
    /// A wrong token. Past enough of them from one address in one window, that address is
    /// shut out for a minute, which is what turns guessing from a matter of bandwidth into
    /// a matter of years.
    ///
    /// Counted over a window rather than as a run of consecutive misses, which is the
    /// obvious way to do this and wrong: the tablet is succeeding constantly, so a count
    /// reset by success would never reach any limit at all.
    /// </summary>
    private void NoteFailure(string? peer)
    {
        string key = peer ?? "?";
        long now = DateTime.UtcNow.Ticks;
        bool shut = false;

        lock (_failures)
        {
            if (!_failures.TryGetValue(key, out var slot))
            {
                if (_failures.Count >= MaximumTrackedAddresses) Prune(now);
                _failures[key] = slot = new FailureSlot();
            }

            if (now - slot.WindowStartTicks > FailureWindow.Ticks)
            {
                slot.WindowStartTicks = now;
                slot.Count = 0;
            }

            if (++slot.Count >= MaximumFailures)
            {
                slot.Count = 0;
                slot.WindowStartTicks = now;
                slot.LockedUntilTicks = now + Lockout.Ticks;
                shut = true;
            }
        }

        if (shut)
        {
            _log.Add(LogKind.Warning,
                     $"{MaximumFailures} wrong tokens from {key} in under a minute — "
                     + $"refusing that address for {Lockout.TotalSeconds:0} seconds");
        }
    }

    /// <summary>
    /// Drops the slots that no longer say anything — window over, lockout served. Run
    /// with the lock held, when the table is full; clearing outright is the backstop
    /// against something cycling through addresses faster than its slots expire.
    /// </summary>
    private void Prune(long now)
    {
        foreach (var (address, slot) in _failures.ToList())
        {
            if (now - slot.WindowStartTicks > FailureWindow.Ticks
                && now >= slot.LockedUntilTicks)
            {
                _failures.Remove(address);
            }
        }

        if (_failures.Count >= MaximumTrackedAddresses) _failures.Clear();
    }

    /// <summary>
    /// A rejected token, at most one line every few seconds: something scanning the port
    /// shouldn't be able to push the real traffic out of a 500-line log.
    /// </summary>
    private void LogRejection(HttpListenerRequest request)
    {
        if (!DueAgain(ref _lastRejectionTicks, TimeSpan.FromSeconds(3))) return;

        string offered = request.Headers[TokenHeader] ?? "";
        _log.Add(LogKind.Warning,
                 offered.Length == 0
                     ? $"a request from {request.RemoteEndPoint?.Address} arrived with no token"
                     : $"a wrong token from {request.RemoteEndPoint?.Address} "
                       + $"({offered.Length} characters)");
    }

    private long _lastRejectionTicks;

    // ---- The token, and who is allowed to offer one ------------------------

    private bool IsAuthorized(HttpListenerRequest request)
    {
        string? offered = request.Headers[TokenHeader];
        if (offered is null) return false;

        // Hashed before comparing, so what's compared is always two 32-byte digests.
        // FixedTimeEquals is constant time across equal lengths but returns at once when
        // they differ — so comparing the tokens themselves would hand back the length of
        // the real one, which is most of what guessing it needs to know. Constant time
        // over the digests, so a wrong token can't be narrowed down a character at a time
        // either.
        Span<byte> offeredHash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(offered), offeredHash);

        return CryptographicOperations.FixedTimeEquals(offeredHash, _tokenHash);
    }

    /// <summary>
    /// Whether the name the request used for this machine is one of ours.
    ///
    /// A browser sends our token to whatever host resolves to this address, and what a
    /// name resolves to is not ours to decide — so a page on evil.example can point that
    /// name here, become same-origin with us, and be inside the token header's protection
    /// without ever having crossed the network we thought we were on. An address can't be
    /// re-pointed that way: whatever DNS says a moment later, a Host of 192.168.0.20 meant
    /// this machine when the request was made.
    ///
    /// So: addresses and localhost always, the configured host when the prefix names one,
    /// and anything in remote.allowedHosts — which is there for a real name worth reaching
    /// this by, a Tailscale one most likely.
    /// </summary>
    private bool HostAllowed(HttpListenerRequest request)
    {
        string host = request.UserHostName ?? "";

        // The port, and the brackets an IPv6 literal wears. Compared against the last "]"
        // so the colons inside "[::1]:8787" aren't mistaken for the one before the port.
        int colon = host.LastIndexOf(':');
        if (colon > host.LastIndexOf(']')) host = host[..colon];
        host = host.Trim('[', ']');

        if (host.Length == 0) return false;
        if (IPAddress.TryParse(host, out _)) return true;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;

        if (_configuredHost is not null
            && host.Equals(_configuredHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _allowedHosts.Any(
            allowed => host.Equals(allowed.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The host out of an HttpListener prefix, or null for the wildcards — which stand for
    /// every interface and so name nothing in particular to compare against.
    /// </summary>
    private static string? HostOf(string prefix)
    {
        int schemeEnd = prefix.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return null;

        int hostStart = schemeEnd + 3;
        int hostEnd = prefix.IndexOfAny(new[] { ':', '/' }, hostStart);
        if (hostEnd < 0) return null;

        string host = prefix[hostStart..hostEnd];
        return host is "+" or "*" ? null : host;
    }
}
