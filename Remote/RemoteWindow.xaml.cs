using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;

namespace Deckhand;

/// <summary>
/// What remote mode looks like on this machine: where to reach the panel, and whether
/// the tablet is getting through. It holds no tiles of its own — the panel behind it
/// still owns all of those — so nothing here can act on the machine except ↻ and Quit.
///
/// It can be put away without stopping any of it: ✕ hides it to the notification area,
/// where the icon carries the same status and the same two buttons. Quit is the only way
/// out, and RemoteWindow.Tray.cs is the rest of that story.
/// </summary>
public partial class RemoteWindow : Window
{
    /// <summary>
    /// How long since the last request still counts as connected. The page keeps a
    /// request parked on the laptop whenever it has nothing better to do — and a parked
    /// request reports as "seen now" — so a few seconds of real silence means it's gone
    /// rather than slow.
    /// </summary>
    private static readonly TimeSpan LinkTimeout = TimeSpan.FromSeconds(5);

    private static readonly Brush LiveDot = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
    private static readonly Brush IdleDot = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
    private static readonly Brush BrokenDot = new SolidColorBrush(Color.FromRgb(0xD1, 0x24, 0x2F));

    private readonly MainWindow _panel;
    private readonly DispatcherTimer _timer;

    /// <summary>The addresses currently listed, so the list is only rebuilt when it
    /// changes — resetting ItemsSource every second would break a selection mid-drag.</summary>
    private IReadOnlyList<string> _listed = Array.Empty<string>();

    private bool _tokenShown;

    /// <summary>The last log line drawn, so each poll asks only for what's new.</summary>
    private long _drawn;

    /// <summary>The same lines as text, for the copy button.</summary>
    private readonly List<string> _lines = new();

    /// <summary>
    /// How many lines stay on screen. The log itself is bounded too; this keeps the
    /// window's own element count from growing with it over a long session.
    /// </summary>
    private const int VisibleLines = 500;

    private static readonly FontFamily LogFont = new("Consolas");

    internal RemoteWindow(MainWindow panel)
    {
        _panel = panel;
        InitializeComponent();

        if (PanelPlacement.Load(PanelPlacement.RemoteFile) is { } saved)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = saved.Left;
            Top = saved.Top;
            Width = Math.Max(MinWidth, saved.Width);
            Height = Math.Max(MinHeight, saved.Height);
        }

        // Once a second, which is roughly how often the tablet asks for the panel:
        // anything finer would only render the gaps between its polls. This constructor
        // starts the timer.
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
                                     (_, _) => Refresh(), Dispatcher);
        Refresh();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private void Refresh()
    {
        DrawLog();

        var status = _panel.Status();

        if (!_listed.SequenceEqual(status.Addresses))
        {
            _listed = status.Addresses;
            AddressList.ItemsSource = status.Addresses;
            AddressNote.Text = status.Addresses.Count > 1
                ? "More than one network on this machine. The tablet can only reach the "
                  + "address on the network it's joined to — the first is the likeliest."
                : "";
        }

        TokenText.Text = _tokenShown
            ? status.Token
            : new string('•', Math.Clamp(status.Token.Length, 0, 24));

        // Which file it came from. A masked field can't otherwise answer "is it reading the
        // one I edited?", which is the question after changing it and tapping ↻.
        TokenText.ToolTip = status.TokenSource;

        DrawQr(status);

        var (line, state) = Link(status);

        StatusDot.Fill = Dot(state);
        StatusText.Text = line;

        // While this window is hidden the icon is all there is, and hovering it is then
        // the only way to read this line without bringing anything back.
        _tray?.Describe(Tip(line));

        if (!status.Serving)
        {
            DetailText.Text = status.Error ?? "";
            WarningText.Visibility = Visibility.Collapsed;
            return;
        }

        // Which device the session belongs to. In the tooltip rather than the line, since
        // while things are working it's the same address the line already shows — it earns
        // its place only when the tablet's address has changed under it and being refused
        // needs explaining.
        StatusText.ToolTip = status.Pinned is { } pinned
            ? $"Paired with {pinned}. Another device with the same token is refused; tap ↻ "
              + "to let one take over."
            : "Not paired yet — the first device to send the right token claims the session.";

        DetailText.Text = $"showing {status.Context} · {status.Tiles} tiles · "
                          + $"{status.Taps} tap{(status.Taps == 1 ? "" : "s")} so far"
                          + Screen(status.Screen);

        // Ready is false here only because this window has focus: the panel's lock is
        // the other cause, and in this mode it's not on screen to unlock.
        WarningText.Text = "This window has focus, so taps are refused — a snippet would "
                           + "be typed into it. Click back into your app.";
        WarningText.Visibility = status.Ready ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Whether the tablet is getting through, in one line. Shared with the tray icon's
    /// tooltip and the top of its menu, so the two can't drift into describing the same
    /// link differently.
    /// </summary>
    private static (string Line, LinkState State) Link(RemoteStatus status)
    {
        if (!status.Serving) return ("not serving", LinkState.Broken);

        var silence = status.LastSeenUtc is { } seen ? DateTime.UtcNow - seen : (TimeSpan?)null;
        if (silence < LinkTimeout) return ($"tablet connected — {status.Peer}", LinkState.Live);

        return (silence is null
            ? "waiting for the tablet…"
            : $"nothing from the tablet for {Ago(silence.Value)}", LinkState.Idle);
    }

    private static Brush Dot(LinkState state) => state switch
    {
        LinkState.Live => LiveDot,
        LinkState.Broken => BrokenDot,
        _ => IdleDot,
    };

    /// <summary>
    /// What hovering the tray icon says. It names the app, because an icon in a row of
    /// icons has to, and then says exactly what the window's status line says.
    /// </summary>
    private static string Tip(string line) => $"Deckhand\n{line}";

    /// <summary>
    /// Appends whatever has happened since the last pass. The scroll only follows the
    /// end when it was already there, so reading back through the log isn't yanked away
    /// by the next line arriving.
    /// </summary>
    private void DrawLog()
    {
        var entries = _panel.LogSince(_drawn);
        if (entries.Count == 0) return;

        bool following = LogScroll.VerticalOffset >= LogScroll.ScrollableHeight - 4;

        foreach (var entry in entries)
        {
            _drawn = entry.Sequence;

            string line = $"{entry.At:HH:mm:ss}  {entry.Text}";
            _lines.Add(line);

            LogLines.Children.Add(new TextBlock
            {
                Text = line,
                Foreground = Ink(entry.Kind),
                FontFamily = LogFont,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 2),
            });
        }

        while (LogLines.Children.Count > VisibleLines) LogLines.Children.RemoveAt(0);
        while (_lines.Count > VisibleLines) _lines.RemoveAt(0);

        if (following) LogScroll.ScrollToEnd();
    }

    private static Brush Ink(LogKind kind) => kind switch
    {
        LogKind.Action => Brushes.White,
        LogKind.Warning => WarningInk,
        _ => IdleDot,
    };

    private static readonly Brush WarningInk = new SolidColorBrush(Color.FromRgb(0xF0, 0xD6, 0x8A));

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (_lines.Count == 0) return;

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, _lines));
        }
        catch (ExternalException)
        {
            MessageBox.Show("Something else is using the clipboard — the log wasn't copied.",
                            "Deckhand", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// What the tablet says about its own screen. Nothing at all for a page that doesn't
    /// report it, rather than a guess: an empty detail reads better than "unknown".
    /// </summary>
    private static string Screen(string? screen) => screen switch
    {
        "lock" => " · screen kept awake",
        "video" => " · screen kept awake, best effort",
        "none" => " · screen may sleep",
        _ => "",
    };

    private static string Ago(TimeSpan span) =>
        span.TotalMinutes < 1 ? $"{span.TotalSeconds:0} seconds"
        : span.TotalHours < 1 ? $"{span.TotalMinutes:0} minutes"
        : $"{span.TotalHours:0} hours";

    private void ShowToken_Click(object sender, RoutedEventArgs e)
    {
        _tokenShown = !_tokenShown;
        ShowTokenButton.Content = _tokenShown ? "Hide" : "Show";
        ShowTokenButton.ToolTip = _tokenShown
            ? "Hide the token again"
            : "Show the token on this screen";
        Refresh();
    }

    private void CopyToken_Click(object sender, RoutedEventArgs e)
    {
        string token = _panel.Status().Token;
        if (token.Length == 0) return;

        try
        {
            Clipboard.SetText(token);
        }
        catch (ExternalException)
        {
            // Another process had the clipboard open. Worth saying, since the paste that
            // follows would otherwise put something else entirely into the tablet.
            MessageBox.Show("Something else is using the clipboard — the token wasn't copied.",
                            "Deckhand", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>What the QR currently encodes, so it's redrawn only when that changes —
    /// this runs from the once-a-second Refresh.</summary>
    private string? _qrShows;

    /// <summary>
    /// The first address as a QR, so setup is a scan instead of typing an IP address on
    /// a tablet keyboard. While Show has the token revealed, the token rides along in
    /// the URL fragment and scanning is the whole setup: a fragment never leaves the
    /// browser — it isn't sent with any request — and the page stores it and scrubs it
    /// from the address bar on arrival. Tying that to Show keeps Show the one switch
    /// that reveals the secret; masked, the QR is only the address.
    ///
    /// Which of the two you are looking at is written under the code, because it is not
    /// visible in the code itself: scanning a QR that turns out to be address-only ends
    /// at a page asking for a token, and nothing on screen would have said why.
    /// </summary>
    private void DrawQr(RemoteStatus status)
    {
        string address = status.Addresses.FirstOrDefault() ?? "";
        string content = address.Length == 0 || !address.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? ""
            : _tokenShown && status.Token.Length > 0
                ? $"{address}#token={Uri.EscapeDataString(status.Token)}"
                : address;

        if (content == _qrShows) return;
        _qrShows = content;

        if (content.Length == 0)
        {
            QrImage.Source = null;
            QrPanel.Visibility = Visibility.Collapsed;
            return;
        }

        QrImage.Source = QrBitmap(content);
        QrPanel.Visibility = Visibility.Visible;

        // The same condition the content is built from, so the caption cannot claim
        // something the code doesn't carry — including the case where there is no token
        // yet, where Show reveals nothing and the QR stays the address either way.
        QrCaption.Text = _tokenShown && status.Token.Length > 0
            ? "Carries the token — scan and you're in."
            : "Address only — tap Show to include the token.";
    }

    /// <summary>
    /// One pixel per module, 1 bit per pixel; the Image scales it up and its
    /// NearestNeighbor setting is what keeps the modules square.
    /// </summary>
    private static BitmapSource QrBitmap(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.L);

        var modules = data.ModuleMatrix;
        int size = modules.Count;

        // BlackWhite is 1bpp with 0 as black, so only the light pixels are set.
        int stride = (size + 7) / 8;
        var pixels = new byte[stride * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (!modules[y][x]) pixels[y * stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        }

        return BitmapSource.Create(size, size, 96, 96, PixelFormats.BlackWhite, null,
                                   pixels, stride);
    }

    private void Unpair_Click(object sender, RoutedEventArgs e)
    {
        _panel.UnpairRemote();
        Refresh();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Reload();

    /// <summary>The window's ↻ and the tray menu's, which are the same thing.</summary>
    private void Reload()
    {
        _panel.ReloadConfig();

        // The address and the token can both have changed, and the list is only rebuilt
        // when it differs — so forget what was listed and let Refresh decide again.
        _listed = Array.Empty<string>();
        Refresh();
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => Quit();

    /// <summary>
    /// The way out, from this window's Quit button or from the icon's menu. ✕ isn't one of
    /// them, so closing has to be told apart from being asked to stop.
    /// </summary>
    private void Quit()
    {
        _quitting = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();

        // Taken away here rather than left to the shell to notice, which it only does when
        // something next hovers over it — an icon for a process that has quit, in a row
        // where every other icon still works.
        _tray?.Dispose();
        _tray = null;

        // Maximized — or quit from the tray, where the window is both minimized and hidden
        // — the plain properties describe a geometry the window isn't using.
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        new PanelPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height)
            .Save(PanelPlacement.RemoteFile);

        // Getting this far is quitting — ✕ never does, it hides — and there is nothing else
        // on screen, so leaving the process running would be a server with no way to see or
        // stop it. Closing the panel is what lets go of the focus hook and the port.
        _panel.Close();
        App.RequestShutdown();

        base.OnClosed(e);
    }
}

/// <summary>
/// How the link is doing, as the dot beside the status line colours it: nothing being
/// served at all, nothing heard from the tablet lately, or working.
/// </summary>
internal enum LinkState { Broken, Idle, Live }
