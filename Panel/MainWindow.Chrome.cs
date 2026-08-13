using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Deckhand;

// The window around the tiles: dragging it, locking it in place, resizing from the grip,
// and filling the screen. Locked is the normal state — a panel meant to be touched can't
// afford to be dragged out of place by a stray finger — so unlocking is a deliberate mode
// with a border colour of its own, and everything that only makes sense unlocked is
// switched on and off in one place here.
public partial class MainWindow
{
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Locked, the panel is anchored — the top bar is just a label and its buttons.
        if (_unlocked is null || e.ButtonState != MouseButtonState.Pressed) return;

        DragMove();
    }

    // ---- Unlock / lock -----------------------------------------------------

    /// <summary>Ex-styles and the window to re-focus when locking; null while locked.</summary>
    private (IntPtr ExStyle, IntPtr Foreground)? _unlocked;

    /// <summary>The focused app's label, kept while the unlocked message is displayed.</summary>
    private string _contextText = "Deckhand";

    private static readonly Brush ChromeBorder = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
    private static readonly Brush UnlockedBorder = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));

    /// <summary>
    /// Shows or hides everything that only makes sense while unlocked. The geometry
    /// controls are hidden rather than disabled when locked, because a locked panel
    /// can't be moved, resized or filled at all — a greyed-out button would imply the
    /// state was temporary rather than deliberate.
    /// </summary>
    /// <summary>
    /// Whether the panel currently covers a screen — by the ⛶ button, or by Windows'
    /// own snap-to-top, which maximizes it without asking us.
    /// </summary>
    private bool IsFilled => _panelBounds is not null || WindowState == WindowState.Maximized;

    private void ApplyLockChrome()
    {
        bool unlocked = _unlocked is not null;

        MaximizeButton.Visibility = unlocked ? Visibility.Visible : Visibility.Collapsed;
        MaximizeButton.Content = IsFilled ? "❐" : "⛶";
        MaximizeButton.ToolTip = IsFilled ? "Shrink back to a panel" : "Fill this screen";

        // Nothing to drag while the monitor dictates the size either.
        ResizeGrip.Visibility = unlocked && !IsFilled
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Room for three buttons locked, four unlocked.
        ContextLabel.Margin = new Thickness(8, 0, unlocked ? 205 : 156, 0);

        LockButton.Content = unlocked ? "🔓" : "🔒";
        LockButton.ToolTip = unlocked
            ? "Lock: back to a panel that never takes focus"
            : "Unlock: move, resize or snap the panel";

        RootBorder.BorderBrush = unlocked ? UnlockedBorder : ChromeBorder;

        // Tiles go inert while unlocked: the panel can hold focus then, so a tapped
        // snippet would type into the dashboard itself.
        ContentRoot.IsHitTestVisible = !unlocked;
        ContentRoot.Opacity = unlocked ? 0.55 : 1;

        // The ⠿ handle advertises a title-bar drag, which only works unlocked.
        ContextLabel.Text = unlocked
            ? "⠿  Unlocked — move, resize or snap, then tap 🔓"
            : _contextText;

        // A tablet can't see the panel go dim, so tell it the tiles are inert.
        PublishRemote();
    }

    private void ToggleLock_Click(object sender, RoutedEventArgs e)
    {
        if (_unlocked is null) Unlock();
        else Lock();
    }

    /// <summary>
    /// Turns the panel into an ordinary window: activatable, resizable by its edges,
    /// present in Alt-Tab, and — the point of the exercise — something FancyZones is
    /// willing to snap, since it skips tool windows and windows that can't be
    /// activated. Everything goes back on <see cref="Lock"/>.
    /// </summary>
    private void Unlock()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        _unlocked = (exStyle, NativeMethods.GetForegroundWindow());

        long ordinary = exStyle.ToInt64()
                        & ~(long)(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ordinary));

        // WS_THICKFRAME alone changes nothing here — AllowsTransparency leaves the
        // window with no non-client area to hit-test — but FancyZones looks for it,
        // and WM_NCHITTEST in WndProc supplies the borders it doesn't get.
        ResizeMode = ResizeMode.CanResize;

        ApplyLockChrome();
        Activate(); // so it can be snapped with FancyZones or Win+arrow straight away
    }

    private void Lock()
    {
        if (_unlocked is not { } saved) return;
        _unlocked = null;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        ResizeMode = ResizeMode.NoResize;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, saved.ExStyle);

        ApplyLockChrome();
        SavePlacement();

        // Leaving the dashboard foreground would point the next tile's keystrokes at
        // itself, which is the one failure this app must never have.
        if (saved.Foreground != IntPtr.Zero && NativeMethods.GetForegroundWindow() == hwnd)
        {
            NativeMethods.SetForegroundWindow(saved.Foreground);
        }

        _watcher?.Poll(); // restore the label and profile for whatever is focused now
    }

    /// <summary>How far in from an edge counts as a grab for resizing, in DIPs.</summary>
    private const double EdgeMargin = 10;

    /// <summary>
    /// Which edge or corner a point is on, or null for "not an edge — treat it as
    /// ordinary content". Answering WM_NCHITTEST with an edge is what lets the system
    /// run its normal resize loop on a window that has no real border to grab.
    /// </summary>
    private int? EdgeHitTest(IntPtr lParam)
    {
        // A maximized window isn't edge-resized, and reporting borders while it is
        // would be actively harmful: WPF's Left/Top/Width/Height still describe the
        // restore bounds, so most of the window would test as a resize border and
        // every click on the top bar would be swallowed before reaching a button.
        if (WindowState != WindowState.Normal) return null;

        // Compared in physical pixels against the real window rect. WPF's properties
        // can't be trusted here for the same reason, and Windows' own snap moves the
        // window without going through them.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect)) return null;

        // Screen coordinates, packed as two signed 16-bit values (negative on a
        // monitor left of or above the primary one).
        long packed = lParam.ToInt64();
        double x = (short)(packed & 0xFFFF);
        double y = (short)((packed >> 16) & 0xFFFF);

        double scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        double margin = EdgeMargin * scale;

        bool left = x < rect.Left + margin;
        bool right = x > rect.Right - margin;
        bool top = y < rect.Top + margin;
        bool bottom = y > rect.Bottom - margin;

        if (top && left) return NativeMethods.HTTOPLEFT;
        if (top && right) return NativeMethods.HTTOPRIGHT;
        if (bottom && left) return NativeMethods.HTBOTTOMLEFT;
        if (bottom && right) return NativeMethods.HTBOTTOMRIGHT;
        if (left) return NativeMethods.HTLEFT;
        if (right) return NativeMethods.HTRIGHT;
        if (top) return NativeMethods.HTTOP;
        if (bottom) return NativeMethods.HTBOTTOM;
        return null;
    }

    /// <summary>
    /// Remembers where the panel is. Skipped while filling a screen, where ⛶ owns the
    /// geometry and ShrinkToPanel restores the real placement afterwards.
    /// </summary>
    private void SavePlacement()
    {
        if (_panelBounds is not null) return;

        // Maximized by Windows' snap, RestoreBounds is the panel's real size — the
        // plain properties describe a geometry it isn't currently using. Saving the
        // restore bounds means it comes back as a panel rather than full screen.
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        new PanelPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height).Save();
    }

    // ---- Resize ------------------------------------------------------------

    /// <summary>Polls the cursor while the grip is held; null when not dragging.</summary>
    private DispatcherTimer? _resizeTimer;

    /// <summary>
    /// Cursor-to-corner offset in DIPs, captured at mouse-down so the corner doesn't
    /// jump to the pointer when the drag starts.
    /// </summary>
    private Vector _resizeGrab;

    /// <summary>
    /// Resizing is done by hand, and by polling the cursor rather than capturing it.
    ///
    /// Two Win32 rules force this. Windows' own resize loop starts from a non-client
    /// hit, and this window's whole design rests on never being activated. And only
    /// the foreground window may capture the mouse — which this window can never be —
    /// so WPF's CaptureMouse would deliver events only while the pointer is over the
    /// panel, dropping the drag exactly when it leaves, which is the direction you
    /// drag to make the panel bigger. GetCursorPos has neither restriction.
    /// </summary>
    private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryGetCursorDip(out var cursor)) return;

        _resizeGrab = new Vector(Left + Width - cursor.X, Top + Height - cursor.Y);

        _resizeTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15),
                                            DispatcherPriority.Input, ResizeTick, Dispatcher);
        _resizeTimer.Start();
        e.Handled = true;
    }

    private void ResizeTick(object? sender, EventArgs e)
    {
        // A release anywhere — including off the panel, where no MouseUp reaches us —
        // ends the drag and is the only reliable way to notice it.
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) == 0)
        {
            _resizeTimer?.Stop();
            SavePlacement();
            return;
        }

        if (!TryGetCursorDip(out var cursor)) return;

        // Left/Top stay put while the bottom-right corner is dragged.
        Width = Math.Max(MinWidth, cursor.X + _resizeGrab.X - Left);
        Height = Math.Max(MinHeight, cursor.Y + _resizeGrab.Y - Top);
    }

    /// <summary>The cursor in DIPs, so the drag stays 1:1 at any display scaling.</summary>
    private bool TryGetCursorDip(out Point point)
    {
        point = default;
        if (!NativeMethods.GetCursorPos(out var native)) return false;

        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                    ?? Matrix.Identity;
        point = toDip.Transform(new Point(native.X, native.Y));
        return true;
    }

    // ---- Filling the screen ------------------------------------------------

    private void ToggleMaximize_Click(object sender, RoutedEventArgs e)
    {
        // Snapping to the top of the screen maximizes us behind our back. Undo that
        // rather than stacking our own fill on top of it, or the two mechanisms end up
        // disagreeing about what "shrink back" means.
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            return;
        }

        if (_panelBounds is null) FillCurrentScreen();
        else ShrinkToPanel();
    }

    /// <summary>
    /// Resizes to the work area of whichever monitor the window is currently on.
    /// We size by hand instead of using WindowState.Maximized because a borderless
    /// topmost window maximizes over the taskbar; rcWork excludes it.
    /// </summary>
    private void FillCurrentScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;

        _panelBounds = (Left, Top, Width, Height);

        // rcWork is in physical pixels; WPF positions in DIPs, so convert.
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                    ?? Matrix.Identity;
        var topLeft = toDip.Transform(new Point(info.rcWork.Left, info.rcWork.Top));
        var bottomRight = toDip.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom));

        Left = topLeft.X;
        Top = topLeft.Y;
        Width = bottomRight.X - topLeft.X;
        Height = bottomRight.Y - topLeft.Y;

        RootBorder.CornerRadius = new CornerRadius(0);
        ApplyLockChrome(); // hides the grip and flips ⛶ to ❐: the monitor owns the size now
    }

    private void ShrinkToPanel()
    {
        if (_panelBounds is not { } bounds) return;

        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;

        RootBorder.CornerRadius = new CornerRadius(12);
        _panelBounds = null;
        ApplyLockChrome(); // brings the grip back now that the panel owns its size
    }

    private void Close_Click(object sender, RoutedEventArgs e) => App.RequestShutdown();
}
