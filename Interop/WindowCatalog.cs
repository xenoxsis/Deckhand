using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Deckhand;

/// <summary>
/// The windows a program already has open, and a way to bring one of them to the front.
///
/// This is the wide half of an app tile: it goes to the copy of the program that is
/// already running — listing them when there is more than one — where the + beside it
/// starts another.
///
/// The filter is the task switcher's: visible, unowned, not a tool window, not cloaked,
/// and titled. Without it the list would hold every HWND a process happens to own —
/// Chrome alone keeps a handful of invisible helpers — rather than the windows a person
/// would recognise.
/// </summary>
internal static class WindowCatalog
{
    internal readonly record struct OpenWindow(IntPtr Handle, string Title);

    /// <summary>
    /// The matching windows in Z-order, frontmost first, which is close enough to
    /// most-recently-used that the window someone means is usually near the top.
    /// </summary>
    public static List<OpenWindow> Of(IReadOnlyCollection<string> processNames)
    {
        var found = new List<OpenWindow>();

        // Windows are matched by owning process id rather than by asking each window
        // which process it belongs to: one lookup per name beats a Process.GetProcessById
        // for every top-level window on the desktop.
        var pids = new HashSet<uint>();
        foreach (string name in processNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            foreach (var process in Process.GetProcessesByName(name))
            {
                pids.Add((uint)process.Id);
                process.Dispose();
            }
        }

        if (pids.Count == 0) return found;

        EnumWindows((hwnd, _) =>
        {
            if (Listable(hwnd) && pids.Contains(Owner(hwnd)))
            {
                found.Add(new OpenWindow(hwnd, NativeMethods.GetWindowTitle(hwnd)));
            }
            return true; // keep enumerating
        }, IntPtr.Zero);

        return found;
    }

    private static uint Owner(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    /// <summary>
    /// Brings <paramref name="hwnd"/> to the foreground, restoring it first if it was
    /// minimized. False means every attempt was refused — most likely the window closed
    /// between the list being drawn and the tap arriving.
    /// </summary>
    public static bool Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;

        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        if (NativeMethods.SetForegroundWindow(hwnd)) return true;

        // Windows only lets the process that owns the foreground window, or the one that
        // received the last input event, change the foreground. A tap on the panel counts
        // as that input, but a tap from the tablet is an HTTP request and counts as
        // nothing — so on that path the call above is refused and the taskbar button
        // flashes instead. Sharing the foreground thread's input queue for the length of
        // one call satisfies the rule without injecting any input.
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        uint theirs = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        uint ours = GetCurrentThreadId();

        if (theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true))
        {
            try
            {
                if (NativeMethods.SetForegroundWindow(hwnd)) return true;
            }
            finally
            {
                AttachThreadInput(ours, theirs, false);
            }
        }

        // Last resort: the call the shell's own task switcher makes. Documented as not
        // for general use, but this is exactly the case it exists for, and it is the only
        // thing that still works when the panel has no claim on the foreground at all.
        SwitchToThisWindow(hwnd, true);
        return NativeMethods.GetForegroundWindow() == hwnd;
    }

    /// <summary>
    /// Whether a window is one a person would recognise as a window, whoever owns it.
    /// </summary>
    private static bool Listable(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;

        // An owned window is a dialog or a palette belonging to another window, and it
        // isn't what "pick a window" means.
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;

        // The desktop passes every other test here — Explorer owns it, it's visible, it's
        // titled "Program Manager" — so a folder tile would count it as a window to go to,
        // and "open Downloads" would raise the desktop with no Explorer window open at all.
        if (hwnd == GetShellWindow()) return false;

        // Frameworks keep titled message-only windows around that are technically visible
        // and have no size. Minimized windows keep a real size (parked off-screen), so
        // this doesn't cost them.
        if (!NativeMethods.GetWindowRect(hwnd, out var bounds)
            || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
        {
            return false;
        }

        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return false;

        // Cloaked windows are still "visible" as far as Win32 is concerned: a suspended
        // store app, or a window living on another virtual desktop. Activating one of
        // those does nothing anyone can see.
        if (Cloaked(hwnd)) return false;

        return NativeMethods.GetWindowTitle(hwnd).Length > 0;
    }

    private static bool Cloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
        && cloaked != 0;

    // ---- Interop -----------------------------------------------------------

    private const uint GW_OWNER = 4;
    private const int SW_RESTORE = 9;
    private const int DWMWA_CLOAKED = 14;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute,
                                                    out int value, int size);
}
