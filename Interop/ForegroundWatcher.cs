using System.Diagnostics;

namespace Deckhand;

/// <summary>
/// Reports which app the user is actually working in. Raises <see cref="Changed"/>
/// whenever a different window becomes foreground.
///
/// Because the dashboard uses WS_EX_NOACTIVATE it never becomes foreground itself,
/// so the reported window is always the one that will receive our synthesized input.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    /// <summary>Process name is without the ".exe" suffix, e.g. "Code" or "OUTLOOK".</summary>
    public record Context(string ProcessName, string WindowTitle);

    public event Action<Context>? Changed;

    // Held in a field so the GC can't collect the delegate while Windows holds the hook.
    private readonly NativeMethods.WinEventProc _callback;
    private IntPtr _hook;

    public ForegroundWatcher()
    {
        _callback = OnWinEvent;

        // WINEVENT_OUTOFCONTEXT delivers callbacks through this thread's message
        // queue, so Changed is raised on the UI thread — no marshalling needed.
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _callback, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    /// <summary>
    /// Reads the focused window on demand. Preferred over a cached value when acting
    /// on a tap, so the decision can't be made against a stale foreground.
    /// </summary>
    public Context? Current() => Describe(NativeMethods.GetForegroundWindow());

    /// <summary>Raises <see cref="Changed"/> for whatever is focused right now.</summary>
    public void Poll() => Report(NativeMethods.GetForegroundWindow());

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
                            int idObject, int idChild, uint thread, uint time)
    {
        // idObject == OBJID_WINDOW (0) filters out caret/menu noise.
        if (idObject == 0) Report(hwnd);
    }

    private void Report(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (Describe(hwnd) is { } context) Changed?.Invoke(context);
    }

    private static Context? Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return new Context(process.ProcessName, NativeMethods.GetWindowTitle(hwnd));
        }
        catch
        {
            // Process exited between the event and our query, or access denied.
            return null;
        }
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }
}
