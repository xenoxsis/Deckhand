using System.Runtime.InteropServices;
using System.Text;

namespace Deckhand;

/// <summary>
/// Win32 interop: window styles that prevent focus stealing, and SendInput
/// for injecting keystrokes into whatever window currently has focus.
/// </summary>
internal static class NativeMethods
{
    // ---- Window styles ----------------------------------------------------

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000; // window never receives focus
    public const int WS_EX_TOOLWINDOW = 0x00000080; // keeps it out of the taskbar/alt-tab
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;

    /// <summary>Sent when the system's move/size loop ends — the end of a drag or resize.</summary>
    public const int WM_EXITSIZEMOVE = 0x0232;

    /// <summary>
    /// Asks which part of the window a point is over. AllowsTransparency leaves this
    /// window with no non-client area at all, so the system finds no border to grab
    /// and nothing is resizable by its edges until we answer this ourselves.
    /// </summary>
    public const int WM_NCHITTEST = 0x0084;

    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;

    // ---- Dark title bar ---------------------------------------------------

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
                                                    ref int value, int size);

    /// <summary>
    /// Asks Windows to draw this window's title bar dark. Only the ordinary windows need
    /// it — the panel draws its own chrome — and without it a light system title bar sits
    /// on top of a near-black window. Best effort: on a build that doesn't know the
    /// attribute the call simply fails and the frame stays light.
    /// </summary>
    public static void UseDarkTitleBar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        int on = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }

    /// <summary>Used to hand focus back when the panel is locked again.</summary>
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Displays a window at the size and position it had, without activating it.</summary>
    private const int SW_SHOWNOACTIVATE = 4;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>
    /// Puts a minimized window back where it was without giving it focus.
    ///
    /// Needed because hiding a minimized window leaves it minimized: WPF only applies
    /// WindowState to a window that is on screen, so the handle keeps its minimized flag
    /// while it's away and Show() brings it back as an icon — visible, by every measure
    /// except being on the screen. Restoring it through WPF instead would work, and would
    /// also steal focus, which the windows that need this have gone out of their way not
    /// to do.
    /// </summary>
    public static void RestoreWithoutFocus(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // ---- Monitor geometry -------------------------------------------------

    public const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor; // full monitor bounds
        public RECT rcWork;    // bounds minus taskbar and other appbars
        public uint dwFlags;
    }

    /// <summary>
    /// Asks how large the window may become. Answering it is what stops a maximized
    /// borderless window from spilling over the taskbar and off the screen edges.
    /// </summary>
    public const int WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    /// <summary>
    /// The window's DPI, for converting WPF's device-independent units into the pixels
    /// MINMAXINFO is answered in. Per window rather than per screen, so a panel dragged
    /// to another monitor answers for the one it's on.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    public const int ATTACH_PARENT_PROCESS = -1;

    /// <summary>
    /// Joins the console this process was started from, if there was one — a WinExe
    /// never gets a console of its own, and --check has text to show.
    /// </summary>
    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int dwProcessId);

    /// <summary>
    /// The window's true bounds in physical pixels. Needed because WPF's
    /// Left/Top/Width/Height describe the restore bounds while maximized, not where
    /// the window actually is.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // ---- Cursor tracking --------------------------------------------------

    /// <summary>Logical left mouse button, whatever the buttons are swapped to.</summary>
    public const int VK_LBUTTON = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    /// <summary>
    /// Used to follow a resize drag. WPF's mouse capture is no help here: Win32 only
    /// lets the foreground window capture the mouse, and this window is never allowed
    /// to become foreground, so events stop the moment the pointer leaves the panel.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// Whether a button is still held. The high bit is the current state. Needed
    /// because a release outside the panel produces no WPF MouseUp to hook.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    // ---- Foreground window tracking --------------------------------------

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
                                      int idObject, int idChild, uint dwEventThread,
                                      uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    public static string GetWindowTitle(IntPtr hwnd)
    {
        var buffer = new StringBuilder(512);
        int length = GetWindowText(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : string.Empty;
    }

    // ---- SendInput --------------------------------------------------------

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_TAB = 0x09;
    private const ushort VK_ESCAPE = 0x1B;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi; // unused, but sizes the union correctly
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } }
    };

    /// <summary>
    /// Win32 error from the last failed send, or 0 after a success. ERROR_ACCESS_DENIED
    /// (5) means UIPI blocked it — the target window is at a higher integrity level.
    /// </summary>
    public static int LastSendError { get; private set; }

    /// <summary>
    /// SendInput silently discards everything when the focused window outranks us,
    /// so the count it returns is the only signal that injection failed.
    /// </summary>
    private static bool Send(INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == (uint)inputs.Length)
        {
            LastSendError = 0;
            return true;
        }

        LastSendError = Marshal.GetLastWin32Error();
        return false;
    }

    /// <summary>Sends Ctrl+V to the focused window (paste).</summary>
    public static bool SendCtrlV() => Send(new[]
    {
        Key(VK_CONTROL, up: false),
        Key(VK_V, up: false),
        Key(VK_V, up: true),
        Key(VK_CONTROL, up: true),
    });

    /// <summary>Presses Enter in the focused window, to submit a typed command.</summary>
    public static bool SendEnter() => SendKey(KeyPress.Enter);

    /// <summary>Presses one key in the focused window and lets it go again.</summary>
    public static bool SendKey(KeyPress key)
    {
        ushort vk = key switch
        {
            KeyPress.Tab => VK_TAB,
            KeyPress.Enter => VK_RETURN,
            KeyPress.Escape => VK_ESCAPE,
            _ => 0,
        };

        return vk != 0 && Send(new[] { Key(vk, up: false), Key(vk, up: true) });
    }

    /// <summary>
    /// Types text character-by-character as Unicode key events, without touching
    /// the clipboard. Slower than paste, but works in fields that block Ctrl+V,
    /// and is independent of keyboard layout.
    /// </summary>
    public static bool TypeText(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE } }
            });
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } }
            });
        }
        return Send(inputs.ToArray());
    }
}

/// <summary>
/// The keys a snippet can ask for by name once its text is in. Deliberately short:
/// these are the ones that mean something to an editor waiting on a snippet prefix,
/// not a general remote keyboard.
/// </summary>
internal enum KeyPress { Tab, Enter, Escape }
