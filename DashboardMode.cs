namespace Deckhand;

/// <summary>
/// Which of the two ways the dashboard is being used, decided once at startup because
/// the two want opposite things from a window.
/// </summary>
public enum DashboardMode
{
    /// <summary>
    /// The panel on this screen: always on top, never activatable, tiles under your
    /// finger. Nothing listens on the network.
    /// </summary>
    Local,

    /// <summary>
    /// The panel in a browser on another device. Here there is only a small ordinary
    /// window saying where to reach it — the tiles still live in this process, they
    /// just aren't drawn on this screen.
    /// </summary>
    Remote,
}
