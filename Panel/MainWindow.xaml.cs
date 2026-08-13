using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Deckhand;

/// <summary>
/// The panel itself: the window, what it knows about the app in front of it, and the
/// decision to redraw. Everything a tile does — building them, tapping them, typing,
/// launching, serving them to a tablet, and the chrome around the edge — is in one of
/// the MainWindow.*.cs parts beside this file. The class is one class by necessity, since
/// a tap is a routed event on a control this window owns, and several subjects by nature.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Where the panel sits when not filling the screen; null while filled.</summary>
    private (double Left, double Top, double Width, double Height)? _panelBounds;

    private DashboardConfig _config = new();
    private ForegroundWatcher? _watcher;
    private ProfileEntry? _activeProfile;

    /// <summary>
    /// Local or remote, fixed for the run. In remote mode this window is built and kept
    /// up to date but never shown: the tiles, the focus watching and the typing all
    /// still happen here, they're just drawn in the tablet's browser instead. Nothing
    /// below needs the window to be visible — the snapshot is read from the elements
    /// this code builds, and a tap is a routed event, neither of which waits on layout.
    /// </summary>
    private readonly DashboardMode _mode;

    private const string ChevronClosed = "▾";
    private const string ChevronOpen = "▴";

    /// <summary>The narrow side of an app tile: one more copy of the program.</summary>
    private const string NewWindow = "+";

    /// <summary>The width of a tile's ▾ — a tappable target that leaves the label its room.</summary>
    private const double ExpanderWidth = 44;

    /// <summary>The one expanded list under a tile, so opening another can close it.</summary>
    private (Grid List, Button Expander)? _openList;

    // Each source section's folder scan, done once and reused across profile switches;
    // cleared by the reload button and the file watcher. Keyed by the section object,
    // which lives exactly as long as the config it was read from.
    private readonly Dictionary<SectionEntry, (IReadOnlyList<FolderInfo> Folders, string? Error)>
        _folderScans = new();

    /// <summary>
    /// The source sections showing the "other" side of their split, by label. Both
    /// sides come out of the one scan, so the switch costs no disk — and it survives a
    /// render and a reload, so alt-tabbing away and back, or saving the config, comes
    /// back to the set that was being used.
    /// </summary>
    private readonly HashSet<string> _showOtherSide = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Serves the panel to a tablet, when the config asks for it.</summary>
    private RemoteServer? _remote;

    /// <summary>
    /// The tiles the remote panel currently knows about, by id. Rebuilt with every
    /// snapshot, so an id can only ever name a tile that was on screen when the
    /// tablet last drew — there is no path from a request to anything else.
    /// </summary>
    private readonly Dictionary<string, Button> _remoteTiles = new();

    /// <summary>
    /// The ids on the picker's card while it's open, and empty otherwise. A tap for
    /// anything else is refused while it isn't empty — the modal covers those tiles on
    /// the panel, and the tablet gets the same answer.
    /// </summary>
    private readonly HashSet<string> _overlayTiles = new();

    /// <summary>Why the remote panel isn't running, for the status window to show.</summary>
    private string? _remoteError;

    /// <summary>
    /// What has passed between the tablet and this machine. Kept whatever the mode,
    /// because it's written from the places that do the work rather than from the server,
    /// and only read by the status window.
    /// </summary>
    private readonly ActivityLog _log = new();

    /// <summary>How many groups the last snapshot carried, for the log line.</summary>
    private int _remoteGroups;

    /// <summary>
    /// The addresses to open on the tablet, worked out when the server starts rather
    /// than every time the status window redraws — that walk asks Windows for every
    /// network interface, and the answer only changes when the network does. Which is
    /// when it's re-read: Windows says so through <see cref="OnNetworkAddressChanged"/>,
    /// and ↻ still re-reads it by hand.
    /// </summary>
    private IReadOnlyList<string> _remoteAddresses = Array.Empty<string>();

    internal MainWindow(DashboardMode mode)
    {
        _mode = mode;
        InitializeComponent();

        _config = DashboardConfig.Load(out string? configError);
        ShowConfigSource();

        // Come back where it was last left — including a position locked into a
        // FancyZones zone. The XAML size and the right-edge dock are the first-run
        // fallback, also used when a saved placement is no longer on any screen.
        // Skipped in remote mode: nothing is placed anywhere, and reading a placement
        // this run would never use only risks writing it back on the way out.
        if (mode == DashboardMode.Local)
        {
            if (PanelPlacement.Load() is { } saved)
            {
                Width = Math.Max(MinWidth, saved.Width);
                Height = Math.Max(MinHeight, saved.Height);
                Left = saved.Left;
                Top = saved.Top;
            }
            else
            {
                Left = SystemParameters.WorkArea.Right - Width - 16;
                Top = SystemParameters.WorkArea.Top + 60;
            }
        }

        ApplyLockChrome(); // starts locked, so the geometry controls start hidden
        Render(null);
        ReportConfigProblems(configError);

        StartRemote();

        // Only in the mode that has addresses to keep fresh — and for the window's
        // lifetime rather than the server's, since ↻ replaces the server.
        if (mode == DashboardMode.Remote)
        {
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        }

        _watcher = new ForegroundWatcher();
        _watcher.Changed += OnForegroundChanged;
        _watcher.Poll(); // adopt the already-focused app's profile immediately

        WatchConfig(); // edits to the config show up on save; ↻ stays the manual override
    }

    /// <summary>
    /// The no-focus trick: once the HWND exists, add WS_EX_NOACTIVATE so the
    /// window can never be activated, and answer WM_MOUSEACTIVATE with
    /// MA_NOACTIVATE so clicks/taps don't activate it either.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    /// <summary>
    /// Windows' snap-to-top maximizes the panel without going through the ⛶ button,
    /// so the chrome has to follow the state rather than only being set alongside it.
    /// </summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        ApplyLockChrome();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Covers shutting down while still unlocked, which would otherwise lose an
        // arrangement that was never locked in. Not in remote mode, where the window was
        // never on screen and its geometry is whatever the XAML happened to say — saving
        // that would overwrite the arrangement the panel is actually kept in.
        if (_mode == DashboardMode.Local) SavePlacement();
        if (_mode == DashboardMode.Remote) NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _configWatcher?.Dispose();
        _reloadDebounce?.Stop();
        _watcher?.Dispose();
        _remote?.Dispose();
        base.OnClosed(e);

        // Shutdown is explicit for the whole app — the mode chooser closing, and a panel
        // that is never shown, would both confuse "the last window closed". So closing
        // the panel has to end the process itself, or Alt+F4 while unlocked would leave
        // it running with nothing on screen. In remote mode the remote window owns this,
        // and it's the one that closed the panel to get here.
        if (_mode == DashboardMode.Local) App.RequestShutdown();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Refusing activation is what keeps taps from stealing focus — but while
        // unlocked the window is meant to behave normally, including being clicked
        // into, so the refusal is lifted for as long as that lasts.
        if (msg == NativeMethods.WM_MOUSEACTIVATE && _unlocked is null)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }

        if (msg == NativeMethods.WM_GETMINMAXINFO && ConstrainMaximizedSize(hwnd, lParam))
        {
            handled = true;
            return IntPtr.Zero;
        }

        // Claim the outer few pixels as resize borders while unlocked, so the panel
        // can be dragged by its edges like any other window.
        if (msg == NativeMethods.WM_NCHITTEST && _unlocked is not null
            && EdgeHitTest(lParam) is { } edge)
        {
            handled = true;
            return new IntPtr(edge);
        }

        // End of a move or resize — the system's loop owns the mouse throughout, so
        // no WPF mouse event marks the drop. Covers a FancyZones snap too.
        if (msg == NativeMethods.WM_EXITSIZEMOVE && _unlocked is not null) SavePlacement();

        return IntPtr.Zero;
    }

    /// <summary>
    /// Confines a maximized panel to the monitor's work area. Left alone, Windows
    /// maximizes a borderless window to the whole monitor plus its invisible resize
    /// border — 2574x1454 on a 2560x1440 screen — which covers the taskbar and pushes
    /// the panel's own edges off screen. Snapping to the top of the screen is the way
    /// in; it's the same problem FillCurrentScreen sidesteps by sizing by hand.
    /// </summary>
    private bool ConstrainMaximizedSize(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return false;

        var limits = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);

        // Both are relative to the monitor, not the desktop.
        limits.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        limits.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        limits.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        limits.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;

        // Answering the message means answering all of it: marking it handled stops
        // WPF filling this half in from MinWidth/MinHeight, and an edge drag would
        // then shrink the panel below the minimum the corner grip enforces.
        double scale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
        limits.ptMinTrackSize.X = (int)Math.Ceiling(MinWidth * scale);
        limits.ptMinTrackSize.Y = (int)Math.Ceiling(MinHeight * scale);

        // ptMaxTrackSize is deliberately left alone: it would also cap manual
        // resizing, which has no reason to stop at one monitor's edge.
        Marshal.StructureToPtr(limits, lParam, false);
        return true;
    }

    // ---- Focus-driven layout -----------------------------------------------

    private void OnForegroundChanged(ForegroundWatcher.Context context)
    {
        // Poll() reads the real foreground, which can be one of our own windows: the
        // startup chooser on the way in, or the remote status window being moved. The
        // hook itself never reports those (WINEVENT_SKIPOWNPROCESS), so match it here
        // rather than relabelling the panel after the dashboard itself and dropping the
        // profile of the app the user was actually in.
        if (context.ProcessName.Equals(OwnProcessName, StringComparison.OrdinalIgnoreCase)) return;

        var profile = _config.MatchProfile(context.ProcessName, context.WindowTitle);

        // Held separately so unlocking can show its own message without losing track
        // of which app the label should go back to. Set before rendering, because
        // Render publishes the remote snapshot and that snapshot carries this label —
        // updating it afterwards left the tablet naming the previously focused app
        // above the new one's tiles until something else happened to republish.
        _contextText = profile?.Label ?? context.ProcessName;
        if (_unlocked is null) ContextLabel.Text = _contextText;

        // Rebuilding tiles on every window switch would be wasteful and would interrupt a
        // tap in progress, so only redraw when the layout would differ. Nothing about a
        // tile depends on which windows are open any more — an app tile reads those when
        // it's tapped rather than when it was drawn — so a switch within one profile is
        // never a redraw.
        if (!ReferenceEquals(profile, _activeProfile))
        {
            Render(profile);
        }
    }

    private void Render(ProfileEntry? profile)
    {
        _activeProfile = profile;
        _openList = null; // the tiles it pointed at are about to be discarded

        // The picker belongs to the tile that opened it, and that tile is being replaced.
        // Not published here: this method publishes once at the end anyway, and doing it
        // now would send the panel that is about to be thrown away.
        CloseWindowPicker(publish: false);

        ContentRoot.Children.Clear();

        // Every section, in the order they're written; the ones belonging to a profile
        // other than this one are dropped as they're built.
        ContentRoot.Children.Add(BuildSectionGrid(_config.EffectiveSections().ToList()));
        PublishRemote();

        // The other half of "what is the tablet looking at": this is the line that
        // explains a set of tiles changing under someone's finger.
        if (_remote is not null)
        {
            _log.Add(LogKind.Panel, $"showing {_contextText} — {_remoteGroups} groups, "
                                    + $"{_remoteTiles.Count} tiles");
        }
    }
}
