namespace Deckhand;

/// <summary>What a line is about, which is all the colouring in the log needs.</summary>
internal enum LogKind
{
    /// <summary>The panel or the link changed — what's on screen, who connected.</summary>
    Panel,

    /// <summary>Something was done to this machine: typed, pasted, launched, sent.</summary>
    Action,

    /// <summary>Refused, blocked or failed.</summary>
    Warning,
}

internal sealed record LogEntry(long Sequence, DateTime At, LogKind Kind, string Text);

/// <summary>
/// Everything that passes between the tablet and this machine, in order: the page being
/// opened, the tablet connecting, each tap as it arrives, what that tap actually did, and
/// every time the panel changed underneath it. In remote mode the panel isn't on screen,
/// so without this there is nothing to look at when a tap seems to do nothing.
///
/// Written from both the listener thread and the UI thread, and read by the status window
/// asking for everything since the last sequence number it saw — so a line can't be
/// missed between two polls, and can't be shown twice.
/// </summary>
internal sealed class ActivityLog
{
    /// <summary>
    /// Enough to cover a working session's worth of taps without growing without end.
    /// This is a status display, not an audit trail; nothing is written to disk.
    /// </summary>
    private const int Capacity = 500;

    private readonly object _gate = new();
    private readonly LinkedList<LogEntry> _entries = new();
    private long _sequence;

    public void Add(LogKind kind, string text)
    {
        lock (_gate)
        {
            _entries.AddLast(new LogEntry(++_sequence, DateTime.Now, kind, text));
            while (_entries.Count > Capacity) _entries.Remove(Evictable());
        }
    }

    /// <summary>
    /// Which line goes when the log is full: the oldest one that isn't a record of
    /// something done to this machine.
    ///
    /// Anything that can reach the port can produce the other kinds at will — the page
    /// being served, a token being refused — and dropping oldest-first would let it push
    /// every tap out of a 500-line window, so that what was actually done here is gone by
    /// the time anyone looks. Taps are what the window is for; they leave last.
    /// </summary>
    private LinkedListNode<LogEntry> Evictable()
    {
        for (var node = _entries.First; node is not null; node = node.Next)
        {
            if (node.Value.Kind != LogKind.Action) return node;
        }

        // Nothing but taps, which is a busy session rather than a flood. The oldest goes.
        return _entries.First!;
    }

    /// <summary>Everything newer than <paramref name="sequence"/>, oldest first.</summary>
    public List<LogEntry> Since(long sequence)
    {
        lock (_gate)
        {
            return _entries.Where(e => e.Sequence > sequence).ToList();
        }
    }
}
