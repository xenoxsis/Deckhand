using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Deckhand;

/// <summary>
/// One icon in the notification area — the bit of the taskbar by the clock — standing in
/// for a window that is hidden. It carries the status window's whole surface while that
/// window is away: the link status in its tooltip, and everything worth doing in the menu
/// behind a right-click.
///
/// It hangs off a window that already exists rather than making one of its own: the shell
/// needs a window to send the icon's mouse events to, and a hidden window still has its
/// handle and still pumps messages. Which is also why this must be built after
/// OnSourceInitialized — before that there is no handle to give away.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    /// <summary>
    /// The message the shell sends back for every mouse event on the icon. Anything from
    /// WM_APP up belongs to the application: the shell doesn't interpret it, it only
    /// repeats what was registered here.
    /// </summary>
    private const int WM_TRAY = 0x8000 + 0x100; // WM_APP + 0x100

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10;

    /// <summary>What the icon is always describing: its window, its picture, its tooltip.</summary>
    private const uint Fields = NIF_MESSAGE | NIF_ICON | NIF_TIP;

    /// <summary>No glyph of its own on the notification — the icon is picture enough.</summary>
    private const uint NIIF_NONE = 0x00;

    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    /// <summary>
    /// What the menu key and Shift+F10 produce once the icon has keyboard focus. Only
    /// newer icon versions send it, and this one doesn't ask to be one — it costs a line
    /// to accept it anyway, and refusing it would make the icon mouse-only on a machine
    /// where the shell decides otherwise.
    /// </summary>
    private const int WM_CONTEXTMENU = 0x007B;

    private const int SM_CXSMICON = 49, SM_CYSMICON = 50;
    private const int IDI_APPLICATION = 32512;

    /// <summary>
    /// What the fixed-width text fields hold, terminator included. The struct below
    /// declares the same numbers, and the marshaller throws rather than truncating when a
    /// string doesn't fit one.
    /// </summary>
    private const int TipLength = 128, InfoLength = 256, InfoTitleLength = 64;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = TipLength)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = InfoLength)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = InfoTitleLength)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATAW data);

    /// <summary>
    /// One icon out of a file, at a size we choose. Undocumented in the sense that the
    /// header warns it may change, but it is what the shell itself calls, and it is the
    /// only one of these that picks the closest image out of an icon group and scales it:
    /// LoadIcon and ExtractIconEx hand back a fixed 32 or 16 to be squeezed into whatever
    /// the tray is actually asking for.
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "PrivateExtractIconsW", CharSet = CharSet.Unicode)]
    private static extern int PrivateExtractIcons(string file, int index, int cx, int cy,
                                                  IntPtr[] icons, int[] ids, int count, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>
    /// Broadcast to every top-level window when Explorer starts, including after it has
    /// crashed and come back. The notification area it drew is gone with it, so an icon
    /// that doesn't take this as its cue to re-add itself simply disappears — and with the
    /// window hidden behind it, so does every way of quitting the app.
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    private readonly HwndSource _source;
    private readonly Action _open;
    private readonly Action<Point> _menu;
    private readonly uint _shellRestarted;

    /// <summary>
    /// Whether the icon handle is ours to destroy. The fallback is a shared system icon,
    /// and destroying one of those is not this class's business.
    /// </summary>
    private readonly bool _ownIcon;

    private NOTIFYICONDATAW _data;
    private bool _added;
    private bool _disposed;

    /// <summary>
    /// Adds the icon, or returns null if the shell wouldn't take it. Null matters: the
    /// caller is about to hide the only window it has, and doing that with no icon to come
    /// back through would leave the app running with nothing on screen at all.
    /// </summary>
    /// <param name="owner">A window that has been shown at least once — it supplies the
    /// handle the shell sends the icon's events to.</param>
    /// <param name="tip">What hovering the icon says.</param>
    /// <param name="open">A double-click.</param>
    /// <param name="menu">A right-click, given the cursor in device-independent pixels —
    /// what WPF places popups in, where the shell deals in screen pixels.</param>
    internal static TrayIcon? Add(Window owner, string tip, Action open, Action<Point> menu)
    {
        if (PresentationSource.FromVisual(owner) is not HwndSource source) return null;

        var tray = new TrayIcon(source, tip, open, menu);
        if (tray._added) return tray;

        // Whatever it got as far as — the hook, the icon handle — goes back.
        tray.Dispose();
        return null;
    }

    private TrayIcon(HwndSource source, string tip, Action open, Action<Point> menu)
    {
        _source = source;
        _open = open;
        _menu = menu;
        _shellRestarted = RegisterWindowMessage("TaskbarCreated");

        (IntPtr icon, _ownIcon) = LoadTrayIcon();

        _data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = source.Handle,
            uID = 1,
            uFlags = Fields,
            uCallbackMessage = WM_TRAY,
            hIcon = icon,
            szTip = Trim(tip, TipLength),

            // Not blown up by the marshaller: a ByValTStr field can't be copied out of a
            // null string, and none of the balloon fields are in use here.
            szInfo = "",
            szInfoTitle = "",
        };

        _source.AddHook(WndProc);
        _added = Shell_NotifyIcon(NIM_ADD, ref _data);
    }

    /// <summary>
    /// The exe's own icon at the size this machine's tray asks for — 16 pixels at 100%,
    /// 20 at 125%, and so on, which is why it isn't a constant. Falls back to the generic
    /// application icon: a plain-looking icon still opens its menu, where no icon at all
    /// is a gap in the tray with the only way back into the app hidden behind it.
    /// </summary>
    private static (IntPtr Handle, bool Own) LoadTrayIcon()
    {
        var icons = new IntPtr[1];
        var ids = new int[1];

        if (Environment.ProcessPath is { } exe
            && PrivateExtractIcons(exe, 0, GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON),
                                   icons, ids, 1, 0) > 0
            && icons[0] != IntPtr.Zero)
        {
            return (icons[0], true);
        }

        return (LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION), false);
    }

    /// <summary>
    /// Changes what hovering the icon says. Ignored while it reads that already: this is
    /// called on a timer, and there is no reason to talk to the shell every second to tell
    /// it nothing has changed.
    /// </summary>
    internal void Describe(string tip)
    {
        string trimmed = Trim(tip, TipLength);
        if (_disposed || !_added || trimmed == _data.szTip) return;

        _data.szTip = trimmed;
        Modify(NIF_TIP);
    }

    /// <summary>
    /// Says something from the icon: a balloon tip, which Windows 10 and 11 turn into an
    /// ordinary toast. Best effort in a way nothing here can check — with notifications
    /// switched off for this app, or focus assist on, the call still succeeds and nothing
    /// is shown. So it's for confirming something that just happened, never for the only
    /// copy of something that matters.
    /// </summary>
    internal void Notify(string title, string text)
    {
        if (_disposed || !_added) return;

        _data.szInfoTitle = Trim(title, InfoTitleLength);
        _data.szInfo = Trim(text, InfoLength);
        _data.dwInfoFlags = NIIF_NONE;
        Modify(NIF_INFO);

        // Cleared so this reads as what it is — a balloon that has been raised — rather
        // than as one still waiting to be.
        _data.szInfoTitle = "";
        _data.szInfo = "";
    }

    /// <summary>
    /// Changes the fields named and no others: uFlags is what the shell reads to decide
    /// what it's being told about, so it goes back to the icon's standing description
    /// afterwards rather than being left describing one update.
    /// </summary>
    private void Modify(uint fields)
    {
        _data.uFlags = fields;
        Shell_NotifyIcon(NIM_MODIFY, ref _data);
        _data.uFlags = Fields;
    }

    /// <summary>
    /// Fits a string to one of the fixed-width fields, leaving room for the terminator —
    /// the marshaller throws rather than truncating.
    /// </summary>
    private static string Trim(string text, int length) =>
        text.Length < length ? text : text[..(length - 1)];

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _shellRestarted && _added)
        {
            // A fresh notification area knows nothing about the old icon, so this is an
            // add rather than a modify.
            Shell_NotifyIcon(NIM_ADD, ref _data);
            return IntPtr.Zero;
        }

        if (msg != WM_TRAY) return IntPtr.Zero;

        // The mouse message is in the low half of lParam. Masking rather than casting
        // whole: a newer icon version packs the icon's id into the high half, and this
        // reads the same either way.
        switch ((int)(lParam.ToInt64() & 0xFFFF))
        {
            case WM_LBUTTONDBLCLK:
                Post(_open);
                break;

            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                // Read now, queued after: by the time the menu opens the pointer may have
                // moved, and the menu belongs where the click was.
                var at = Cursor();
                Post(() => _menu(at));
                break;
        }

        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>
    /// Hands a click back once this message has been dealt with, rather than during it.
    /// What the owner does with a double-click is put its window back on screen, which
    /// takes this icon away with it — and disposing of the hook that is currently running
    /// is not something to do from inside it.
    /// </summary>
    private void Post(Action action) => _source.Dispatcher.BeginInvoke(action);

    /// <summary>
    /// Where the pointer is, in the units WPF places popups in. The transform belongs to
    /// the owner window's monitor, so a menu opened on a second screen at a different
    /// scale can land a little off the pointer — the alternative is asking the shell which
    /// monitor the tray is on, for a menu that appears next to the cursor either way.
    /// </summary>
    private Point Cursor()
    {
        if (!NativeMethods.GetCursorPos(out var point)) return new Point(0, 0);

        return _source.CompositionTarget is { } target
            ? target.TransformFromDevice.Transform(new Point(point.X, point.Y))
            : new Point(point.X, point.Y);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_added)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _data);
            _added = false;
        }

        _source.RemoveHook(WndProc);

        if (_ownIcon && _data.hIcon != IntPtr.Zero) DestroyIcon(_data.hIcon);
    }
}
