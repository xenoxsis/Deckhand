using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Deckhand;

// Putting the status window away without stopping anything it does. ✕ hides it to the
// notification area, where the icon carries the same dot, the same line of status and the
// same two buttons. The window has to keep existing while it is hidden: it is the thing
// the panel behind it is being watched through.
public partial class RemoteWindow
{
    /// <summary>
    /// The icon by the clock, for as long as this window is hidden — and nothing while it
    /// isn't. On screen the window has a taskbar button of its own, and the same window
    /// listed in two places at once is one more than is any use.
    /// </summary>
    private TrayIcon? _tray;

    /// <summary>
    /// The icon's menu, built the first time it's asked for. Its top line is rewritten
    /// each time it opens, so these two are kept: the rest of it never changes.
    /// </summary>
    private ContextMenu? _trayMenu;
    private Ellipse? _trayDot;
    private TextBlock? _trayLine;

    /// <summary>
    /// Set by <see cref="Quit"/>, and the only thing that lets this window close. Without
    /// it ✕ would stop the server, and ✕ on a window that isn't the thing being used is
    /// too easy to reach for that.
    /// </summary>
    private bool _quitting;

    /// <summary>
    /// ✕ hides this window to the notification area instead of closing it. The panel
    /// behind it is what the tablet is using, and it goes on working whether this is on
    /// screen or not — so the button that puts a window away shouldn't be the button that
    /// stops the machine answering. Quit is for that, from here or from the icon's menu.
    ///
    /// Minimizing is left alone: it means what it means, and the taskbar button is a
    /// perfectly good place for a window to wait.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_quitting) return;

        e.Cancel = true;
        ToTray();
    }

    private void ToTray()
    {
        _tray ??= TrayIcon.Add(this, Tip(Link(_panel.Status()).Line), FromTray, ShowTrayMenu);

        if (_tray is null)
        {
            // Hiding takes the taskbar button with it, so with no icon there would be no
            // way back at all. Minimizing instead keeps both halves of what ✕ was asked to
            // mean — still running, still findable — and Quit still stops it.
            WindowState = WindowState.Minimized;
            return;
        }

        Hide();

        // Said out loud, because ✕ everywhere else means gone: without this the panel would
        // be answering a tablet from a process with no window and no sign of life.
        _tray.Notify("Deckhand is still running",
                     "The panel is still answering the tablet. Double-click the icon by the "
                     + "clock to bring this window back, or right-click it to quit.");
    }

    /// <summary>Back on screen, and the icon goes with the hiding that put it there.</summary>
    private void FromTray()
    {
        _tray?.Dispose();
        _tray = null;

        Show();

        // A window can be minimized and then closed — from its taskbar button's own menu,
        // or the preview's ✕ — and hiding it left it that way. Show() brings a minimized
        // window back as a minimized window, which is to say nothing that can be seen:
        // WPF only applies WindowState to a window that's on screen, so the flag stayed on
        // the handle while it was away. Doing this through WPF would take focus too, which
        // this window avoids. Maximized is left alone — SW_SHOWNOACTIVATE would undo it.
        if (WindowState == WindowState.Minimized)
        {
            NativeMethods.RestoreWithoutFocus(new WindowInteropHelper(this).Handle);
        }
    }

    /// <summary>
    /// The icon's menu: the one thing this mode is watched for, and the two buttons from
    /// the bottom of the window — so reloading or quitting doesn't need the window back
    /// first.
    /// </summary>
    private void ShowTrayMenu(Point at)
    {
        _trayMenu ??= BuildTrayMenu();

        var (line, state) = Link(_panel.Status());
        _trayDot!.Fill = Dot(state);
        _trayLine!.Text = line;

        // No placement target: a popup placed against this window would have to appear
        // beside it, and it's hidden. An absolute point puts it at the cursor instead, in
        // the device-independent pixels WPF measures popups in — which is what TrayIcon
        // converted the shell's screen pixels into.
        _trayMenu.Placement = PlacementMode.AbsolutePoint;
        _trayMenu.HorizontalOffset = at.X;
        _trayMenu.VerticalOffset = at.Y;
        _trayMenu.IsOpen = true;

        // The menu is a top-level window of its own, and with everything else of ours
        // hidden this process has no focus to lend it. Without this it would take neither
        // a keypress nor notice a click elsewhere: a menu that won't go away. Best effort
        // — the click that opened it went to the shell, not to us, and Windows only grants
        // the foreground to whoever got the last input.
        if (PresentationSource.FromVisual(_trayMenu) is HwndSource popup)
        {
            NativeMethods.SetForegroundWindow(popup.Handle);
        }
    }

    private ContextMenu BuildTrayMenu()
    {
        // The same dot and line as the top of the window, drawn the same way — a shape
        // rather than a glyph, which can't come out as a tofu box in a font that hasn't
        // got the character.
        _trayDot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = IdleDot,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _trayLine = new TextBlock { VerticalAlignment = VerticalAlignment.Center };

        var status = new StackPanel { Orientation = Orientation.Horizontal };
        status.Children.Add(_trayDot);
        status.Children.Add(_trayLine);

        var menu = new ContextMenu { Style = (Style)FindResource("TrayMenu") };

        // Not a command — the top line of the menu. Hit testing and focus off, so it
        // neither lights up under the pointer nor takes an arrow key on the way past.
        menu.Items.Add(new MenuItem
        {
            Header = status,
            Style = (Style)FindResource("TrayMenuItem"),
            IsHitTestVisible = false,
            Focusable = false,
        });

        menu.Items.Add(new Separator { Style = (Style)FindResource("TrayMenuLine") });
        menu.Items.Add(TrayItem("Open the dashboard", FromTray));
        menu.Items.Add(TrayItem("↻ Reload", Reload));
        menu.Items.Add(TrayItem("Quit", Quit));

        return menu;
    }

    private MenuItem TrayItem(string label, Action click)
    {
        var item = new MenuItem { Header = label, Style = (Style)FindResource("TrayMenuItem") };
        item.Click += (_, _) => click();
        return item;
    }
}
