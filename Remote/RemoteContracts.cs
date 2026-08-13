namespace Deckhand;

// What the tablet is told, in the shape it is told it. These records are serialized
// straight to JSON and read by remote.html, so a change here is a change to the protocol:
// the page draws whatever this describes and knows nothing else about the panel.
/// <summary>
/// One tile as the tablet sees it: a label, an id to send back, and its cell in the
/// group's tile grid (1-based). The cell is spelt out rather than left to the page to
/// flow, because the panel's tiles aren't all a row wide — a folder tile's ▾ sits
/// beside its label, and flowing put it underneath.
/// </summary>
/// <param name="Trailing">True for a narrow button sharing the previous tile's cell —
/// the ▾ — which the page draws at a fixed width beside it. Its row and columns are the
/// leader's, so a page that ignores this still draws something sensible.</param>
/// <param name="Indent">Pixels in from its cell, as the panel sets a folder's command
/// list in from the header it belongs to.</param>
/// <param name="Joined">On a trailing button, that the pair is one split control divided
/// by a line rather than two tiles with a gap — an app tile, which goes to the program on
/// the wide side and starts another copy on the narrow one.</param>
/// <param name="Color">The tile's accent, as the config wrote it (#RGB or #RRGGBB), or
/// null for the stock outline. The page paints its border with it, as the panel does.</param>
internal sealed record RemoteTile(string Id, string Label, int Row, int Column, int Span,
                                 bool Trailing, int Indent, bool Joined,
                                 string? Color = null);

/// <summary>
/// The picker, while it's open: a modal over the panel, listing one choice per row.
/// The panel can't put this in a window of its own without taking focus, so on both
/// screens it's a layer over everything, and everything behind it is unavailable —
/// including to a tap that names it.
/// </summary>
/// <param name="Dismiss">The id of the choice that closes without picking anything, so
/// tapping the backdrop can do the same thing as tapping Cancel.</param>
internal sealed record RemoteOverlay(string Title, int Columns,
                                     IReadOnlyList<RemoteTile> Tiles, string Dismiss);

/// <summary>
/// One group: the rectangle of the dashboard grid it occupies (1-based, as the config
/// writes it), the columns its tiles are placed on, and those tiles. The rectangle is
/// the one the placer actually gave it rather than the one the config asked for, so
/// packed sections come out where they really are. <paramref name="TileColumns"/> is
/// the section's tilesPerRow for plain tiles, but finer where a group nests them.
/// </summary>
internal sealed record RemoteGroup(string Label, int TileColumns,
                                   int Column, int ColumnSpan, int Row, int RowSpan,
                                   IReadOnlyList<RemoteTile> Tiles);

/// <summary>
/// Everything on the panel right now. <paramref name="Revision"/> is what lets the
/// tablet poll cheaply, <paramref name="Ready"/> is false while the panel is unlocked
/// (when taps do nothing here either), and Columns/Rows are the grid the rectangles
/// above are cut from, so the tablet can lay itself out the same way.
/// </summary>
/// <param name="Page">Which build of the browser's half of the app this snapshot was made
/// for, so a page still open from an older one can notice and replace itself. The page
/// sends it back on every poll, which is what stops "nothing new" from covering a
/// rebuild.</param>
/// <param name="Overlay">The modal picker, or null when there isn't one. The groups are
/// still published underneath it — the page draws them behind the modal, as the panel
/// does — but only the overlay's own tiles will answer a tap.</param>
internal sealed record RemoteSnapshot(int Revision, string Context, bool Ready,
                                      int Columns, int Rows,
                                      IReadOnlyList<RemoteGroup> Groups, string Page,
                                      RemoteOverlay? Overlay);

/// <summary>
/// What the status window shows in remote mode, where the panel isn't on screen to
/// look at. Enough to answer the two questions that mode raises — where do I reach it,
/// and is the tablet actually getting through — without that window needing to know
/// anything about how either side works.
/// </summary>
/// <param name="Error">Why nothing is being served, when that's the case.</param>
/// <param name="Screen">What the tablet last said about keeping its own screen on:
/// "lock", "video", "none", or null from a page too old to say.</param>
/// <param name="TokenSource">Which file the token in use was read from, for the tooltip
/// on it — the one question a masked field otherwise can't answer.</param>
/// <param name="Pinned">The device this session is paired with, or null before one has
/// connected.</param>
internal sealed record RemoteStatus(bool Serving, string? Error,
                                    IReadOnlyList<string> Addresses, string Token,
                                    string TokenSource,
                                    string Context, int Tiles, bool Ready,
                                    DateTime? LastSeenUtc, string? Peer, int Taps,
                                    string? Screen, string? Pinned);
