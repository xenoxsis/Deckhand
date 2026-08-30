using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace Deckhand;

public partial class ModeWindow : Window
{
    /// <summary>
    /// What was picked, or null if the window was closed without picking — which the
    /// caller treats as "don't start".
    /// </summary>
    internal DashboardMode? Mode { get; private set; }

    internal ModeWindow(RemoteSettings remote)
    {
        InitializeComponent();

        // A token too short to serve with is a dead end, so say so here rather than
        // letting the choice be made and then fail behind a status window.
        if (remote.Token.Length < RemoteSettings.MinimumTokenLength)
        {
            RemoteButton.IsEnabled = false;
            RemoteButton.Opacity = 0.45;
            RemoteNote.Text = $"For a tablet, the token needs at least "
                              + $"{RemoteSettings.MinimumTokenLength} characters — what's on the "
                              + "other side of it types and launches things as administrator. "
                              + $"Put one in {remote.ResolveTokenFile()}, on a line of its own.";
            RemoteNote.Visibility = Visibility.Visible;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private void Local_Click(object sender, RoutedEventArgs e) => Pick(DashboardMode.Local);

    private void Remote_Click(object sender, RoutedEventArgs e) => Pick(DashboardMode.Remote);

    private void Designer_Click(object sender, RoutedEventArgs e) => Pick(DashboardMode.Designer);

    private void Pick(DashboardMode mode)
    {
        Mode = mode;
        Close();
    }

    /// <summary>
    /// Keys as well as taps: this window can be reached before a tablet is anywhere
    /// near, and Enter is what a keyboard expects to do with a question whose usual
    /// answer — the local panel — it already knows.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();       // Mode stays null
                break;

            case Key.Enter:
                // Enter belongs to whichever button was Tabbed to — a WPF button clicks
                // itself on Enter, so it's left alone rather than answered here, where it
                // would pick Local out from under a focused Remote. The default answer is
                // only for when no button has focus.
                if (Keyboard.FocusedElement is Button) return;
                Pick(DashboardMode.Local);
                break;

            case Key.D1:
            case Key.NumPad1:
                Pick(DashboardMode.Local);
                break;

            case Key.D2:
            case Key.NumPad2:
                if (RemoteButton.IsEnabled) Pick(DashboardMode.Remote);
                break;

            case Key.D3:
            case Key.NumPad3:
                Pick(DashboardMode.Designer);
                break;

            default:
                return;
        }

        e.Handled = true;
    }
}
