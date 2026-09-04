# Deckhand

An always-on-top, touch-friendly WPF panel that **never takes focus**. Tap a
tile to launch an app or insert a text snippet into whatever window is focused
(VSCode, a browser, anything).

**Setting one up?** The [documentation site][docs] covers how to run and configure the
panel, with a worked example of every configuration key and five complete config files
to copy. This README is the design document behind it: why each decision went the way
it did, and what was tested.

[Getting started][docs] · [Configuration reference][config] · [Example configs][examples] · [Tablet remote][tablet]

[docs]: https://xenoxsis.github.io/Deckhand/docs/
[config]: https://xenoxsis.github.io/Deckhand/docs/configuration.html
[examples]: https://xenoxsis.github.io/Deckhand/docs/examples.html
[tablet]: https://xenoxsis.github.io/Deckhand/docs/tablet.html

## Contents

- [Run](#run)
- [Where the code lives](#where-the-code-lives)
- [Where the panel runs](#where-the-panel-runs)
- [Elevation](#elevation)
- [Configure](#configure)
- [URL tiles and the "respective browser"](#url-tiles-and-the-respective-browser)
- [The panel on a tablet](#the-panel-on-a-tablet)
- [Resizing](#resizing)
- [Unlocking, and FancyZones](#unlocking-and-fancyzones)
- [How the no-focus trick works](#how-the-no-focus-trick-works)
- [The example Work Items section, key by key](#the-example-work-items-section-key-by-key)
- [Known limitations](#known-limitations)
- [License](#license)

## Run

Start with the config, because there isn't one until you make it:

```
copy dashboard.example.json dashboard.json
```

`dashboard.json` and `dashboard.local.json` are in `.gitignore`. A config is a list
of the paths on one machine, the URLs it opens and the text it types into other
windows — and since a snippet is only text, "the text it types" is a place people
put things they would not publish. So the repository holds
`dashboard.example.json`, the same file with the personal parts taken out, and the
one you edit is yours. It is a working config on its own; the tiles it names are
worth replacing, not the shape of it.

The app requires administrator (see [Elevation](#elevation)), so `dotnet run` no
longer works — it fails with *"The requested operation requires elevation"*
because it starts the exe without going through ShellExecute. Build, then launch:

```
dotnet build Deckhand.csproj
Start-Process bin\Debug\net9.0-windows\Deckhand.exe
```

Double-clicking the exe works too. Either way you get a UAC prompt; see
[Elevation](#elevation) for how to avoid it.

Open `Deckhand.sln` to work on it in Visual Studio or Rider. Visual Studio
must itself run elevated to debug with F5.

## Where the code lives

One project, five folders, and a root that holds only what has to be there:

| | |
|---|---|
| `Config/` | `dashboard.json` as objects, and the reading, merging, checking and (for the designer) writing of it — plus the folder scan a `source` section is built from, and the tile pictures. Free of WPF and Win32 but for `TileImages.cs`, which reads and decodes those pictures and so needs both a decoder and, for an svg, the drawing package: a picture is checked as part of loading a config, because a missing one belongs in the same list of warnings as a missing profile. |
| `Panel/` | The panel window: placing sections on the grid, building tiles, what a tap does, and the chrome around the edge. |
| `Designer/` | The layout designer: the editing window, and a preview that draws a config with the panel's own placer and styles but none of its behaviour. |
| `Remote/` | The tablet's side: the HTTP server, the records it serializes, the status window and its notification-area icon, and `Remote/Web/` — the page a browser is served, which the csproj embeds from there. |
| `Interop/` | Everything said to Windows directly: the P/Invokes, the foreground hook, the window list, and the URL and de-elevated launchers. |
| the root | Startup (`App`, `ModeWindow`), the two small types both halves share, and the files the build and the config are addressed by name: `dashboard.example.json`, the schema, `icon.ico`, `icon.ps1`. |

`MainWindow` and `RemoteServer` are each one class in several files. They have to be
one class — a remote tap is raised on a control this window owns, and a request is
answered from the state the accept loop keeps — but drawing tiles and de-elevating a
launch have nothing to say to each other, and neither do the long poll and the token
compare. The parts are named for their subject (`MainWindow.Snippets.cs`,
`RemoteServer.Access.cs`) and the one without a suffix is the window or the server
itself: its fields, its lifetime, and nothing that could be read on its own.

## Where the panel runs

On start the dashboard asks where you want it:

- **On this laptop** — the panel this README is mostly about: always on top, never
  activatable, following the focused app. Nothing listens on the network in this mode,
  whatever `remote` says in the config.
- **On a tablet** — nothing on this screen but a small ordinary window, with the panel
  itself served to the tablet's browser. See
  [The panel on a tablet](#the-panel-on-a-tablet).
- **Design the layout** — no panel at all: a window for building `dashboard.json`
  without typing it. Pick the grid, add groups and buttons, and a live preview draws
  the panel as you go — the same placer and the same styles as the real thing, minus
  the tapping. Set a screen size (a tablet's, say) above the preview and it's laid out
  at exactly those pixels, shrunk to fit the window — and opened from the tablet-mode
  window, both boxes are already filled in with what that tablet reported. **FOCUSED**
  beside them draws the panel as it would look with a given app in front: every group at
  once while you build, or one profile's worth, or only the groups that are always there.
  A button can wear a picture too: **PICTURE** takes a path, or picks one with `…` — which
  copies the file in beside the config, so a layout and its artwork stay together — and
  the line under it says which file the path actually found and what was done with it.
  Worth saying, because a relative path is relative to the file being saved rather than to
  anything on screen. The three placements sit under it, and the preview draws the picture
  where the panel will. Saving writes the file fresh
  through the same model the panel loads, keeping the parts the designer doesn't edit
  — profiles, the `remote` block, a section's folder `source` — but not comments,
  which it asks about before overwriting a file that has any. A running panel notices
  the save and redraws itself — including a panel being served to a tablet, which is
  why **Design** is also a button on
  [the tablet-mode window](#what-remote-mode-looks-like-on-the-laptop) rather than only
  a mode of its own.

Enter answers the question the usual way — the local panel — so the everyday start is
still one keypress. Closing it without answering quits, rather than falling back to
the panel — that would put a window on the screen you'd just decided against, in the
corner it was last left in.

`--local`, `--remote` and `--designer` answer it in advance, which is what a pinned
shortcut wants:

```
Start-Process bin\Debug\net9.0-windows\Deckhand.exe -ArgumentList --remote
```

A switch beats the config: `--remote` serves even with `remote.enabled: false`, on the
grounds that a shortcut saying `--remote` means it. `enabled` decides whether you're
*asked*; the mode decides what actually runs.

### What remote mode looks like on the laptop

The window is deliberately ordinary — movable, resizable, in the taskbar, and its ✕
[doesn't quit](#closing-it-the-notification-area) — because it isn't the control surface;
the tablet is. It shows the addresses to open there — and the first of them as a QR, so
setup is a scan rather than typing an IP on a tablet keyboard — the
token (masked until **Show**), whether the tablet is getting through and who it is,
whether that tablet is managing to keep its own screen on, how big that tablet's screen
turned out to be, and a log of everything that passed between the two sides.

The QR follows **Show**: masked, it encodes only the address; with the token on screen it
folds the token into the URL fragment too, and scanning is the whole setup — the page
stores it and scrubs it from the address bar, and a fragment is never sent with any
request. Tying it to Show keeps Show the one switch that reveals the secret.

**Unpair**, next to ↻, lets go of the paired device so another one with the right token
can take the session. ↻ still unpairs too — restarting the server is what it does — but it
also re-reads the config, and swapping tablets shouldn't have to mean that. Its size and position are
remembered in `%LOCALAPPDATA%\Deckhand\remote-window.json`, separately from the
panel's own.

**Design** opens the layout designer beside this window — the same one `--designer`
starts, only without it having to be the whole app. It's reachable from here because in
this mode the panel being designed isn't on any screen to right-click; this window is
what's on screen. Nothing stops while it's open: the tablet stays paired, and a save
reaches the panel through the config watcher as an ordinary reload, so the layout appears
on the tablet as you arrange it. One designer at a time, and closing it asks about
unsaved work — **Quit** asks first too, and answering Cancel there cancels the quit
rather than losing the layout to a button in another window. The same item is in the
tray icon's menu, so putting this window away isn't a reason to have it back before
reaching the layout.

Opening it that way also fills in the designer's **SCREEN** boxes: the tiles pair from
the tablet's last measurement, so the preview is at the size the tablet actually draws,
without anyone reading a number off one window and typing it into another. The boxes say
where the numbers came from if you hover them, and they're ordinary boxes — change them,
or clear both to fill the pane. Nothing reported yet and the designer opens at the size
it was last left at: a viewport figure in place of the tiles one would be wrong by the
page's own header, and a number that looks authoritative and quietly isn't is worse than
a remembered one.

While the designer has focus, taps are refused for the reason they're refused while this
window has focus: a snippet would be typed into whatever field is focused, which in the
designer's case is part of the config being written. The tablet says so, and the line
under the status dot says which of the two windows it is.

**How big the tablet is, which is the one thing this end can't work out.** The panel is
laid out for a screen nobody at the laptop can see, so the page measures its own and sends
the answer with every request. The window shows it under the status line and the log writes
a line each time it changes:

```
14:22:07  the tablet's screen is 1280×800 at 2× pixels, tiles 1264×744 — that pair is what the designer's screen size wants
```

Two boxes, because they answer different questions. **tablet screen** is the visible
viewport: what the browser leaves the page once its own bars are counted, measured with
`visualViewport` rather than guessed from a unit. **tiles** is what's left for the grid
after the page's own header — and that is the pair to type into the designer's screen
size, whose preview is the tile grid and nothing around it.

All of it in CSS pixels, which is the unit the page lays out in and the unit the designer
wants. The `2×` is how many device pixels each of those is worth: it's why a tablet sold
as 2560×1600 reports 1280×800 and is right to. Rotating the tablet, or its browser bars
sliding away, changes the numbers and writes another line.

A page too old to send it leaves the line off rather than guessing, and a header that
isn't two plausible pixel counts is dropped without being echoed anywhere — the same rule
as the screen-wake header, and for the same reason: anything that gets past the token can
send one, and the log is the only account of what happened here.

`+` in the listen prefix isn't something anyone can type into a tablet, so it's expanded
into this machine's addresses, interfaces with a default gateway first: a WSL, Hyper-V
or VPN-stub address is one nothing else on the network can reach. They're all listed
anyway, because guessing wrong shouldn't hide the one that works. ↻ re-reads them, which
is also what to tap after moving to another Wi-Fi.

Behind it the panel is still built, still watches focus and still does all the work — it
just isn't drawn on this screen. The window appears *without* taking focus, because
while one of the dashboard's own windows has focus a tap is refused (see
[What stops it being a remote shell](#what-stops-it-being-a-remote-shell)), and starting
up in that state would make the first tap from the tablet fail for no visible reason.

### Closing it: the notification area

**✕ doesn't quit.** It hides the window to the notification area — the icons by the clock
— and takes its taskbar button with it. The panel behind it goes on serving the tablet
whether this window is on screen or not, so the button that puts a window away shouldn't
be the button that stops the machine answering. **Quit** is for that, either the one at the
bottom of the window or the one in the icon's menu; both stop the server and end the
process. Closing it raises a notification saying so, because ✕ everywhere else means gone.

Minimizing is left alone. It means what it means, and the taskbar button is a perfectly
good place for a window to wait.

What ✕ leaves behind is the exe's own icon, at whatever size the tray asks for on this
machine — 16 pixels at 100%, 20 at 125%:

- **Hover** it for the same status line the window shows: `tablet connected —
  192.168.1.42`, `waiting for the tablet…`, `nothing from the tablet for 4 minutes`, `not
  serving`. Both come out of one method, so the icon and the window can't drift into
  describing the same link differently.
- **Double-click** it to put the window back — without focus, same as when it first
  appeared, so the tablet keeps working while you read it.
- **Right-click** it for that status line again with its coloured dot, then **Open the
  dashboard**, **Design the layout**, **↻ Reload** and **Quit** — the same buttons as the
  bottom of the window, so re-reading the config, building a layout or stopping the
  server doesn't need the window back first. The designer opens on its own; the status
  window stays where you put it.

The icon exists only while the window is hidden; on screen the window has a taskbar
button, and the same window listed twice is one listing more than is useful.

**The first time you close it, expect the icon to be in the overflow.** Windows 11
hides tray icons it hasn't been told to show, so it lands behind the `^` chevron rather
than on the taskbar. Drag it out of that flyout onto the taskbar to keep it there, or turn
it on under Settings → Personalization → Taskbar → Other system tray icons — which lists
it only while it exists, so close the window first. Nothing can ask for promotion from this
side: `Shell_NotifyIcon` can hide an icon, not un-hide it. The notification appears either
way, which is the one thing worth having when the icon itself is behind a chevron — unless
notifications are off for this app or focus assist is on, and a balloon that goes nowhere
still reports success, so nothing here can tell you it didn't arrive.

Two things are being worked around in `Remote/TrayIcon.cs` and `RestoreWithoutFocus`:

- **The icon needs a window to send its clicks to**, and a hidden window still has its
  handle and still pumps messages — so it hangs off this one rather than making a
  message-only window of its own. That's also why it can't be built before
  `OnSourceInitialized`. Clicks are handed back through the dispatcher rather than called
  from inside the message hook: what a double-click does is put the window back, which
  disposes the very hook that is running.
- **Hiding a minimized window leaves it minimized.** A window can be minimized and then
  closed — from the taskbar button's own menu, or its preview's ✕ — and WPF only applies
  `WindowState` to a window that's on screen, so the flag stays on the handle while it's
  away and `Show()` brings it back as an icon: visible by every measure except being on
  the screen. `SW_SHOWNOACTIVATE` restores it in place, and unlike restoring through WPF it
  doesn't take focus. Only when it really was minimized, mind — the same call would undo a
  maximized window.

If the shell refuses the icon, ✕ minimizes the window instead of hiding it: still running
and still findable, which is both halves of what ✕ was asked to mean, rather than a server
left running with nothing on screen to stop it. Quitting saves the window's position from
`RestoreBounds` when it isn't `Normal`, since a hidden or minimized window's plain
`Left`/`Top` describe a geometry it isn't using.

The menu is a WPF `ContextMenu` templated dark in `App.xaml` (`DarkMenu`, `DarkMenuItem`,
`DarkMenuLine`, shared with the designer) rather than a Win32 or WinForms one, so it
matches the window it belongs to; a stock `MenuItem` paints its highlight from a trigger
inside its own template, which
no style out here can reach, so a plain dark style would give a dark menu with a light
blue hover in it. It's placed at the cursor as an absolute point — a popup placed against
this window would have to appear beside it, and it's hidden — and handed the foreground
once it's open, because a menu belonging to a process with no focus would neither take a
keypress nor notice a click elsewhere. That last part is best effort: the click that
opened it went to the shell, not to us, and Windows only grants the foreground to whoever
received the last input.

### The log

Everything the two sides said to each other, and everything it caused, oldest first:

```
11.41.28  serving on http://+:8787/
11.41.31  the page was opened by 192.168.0.14
11.41.31  tablet connected — 192.168.0.14
11.41.31  the tablet is holding its screen awake as best it can
11.41.33  showing VS Code — 3 groups, 12 tiles
11.41.45  tap — "Email"
11.41.45  typed "Email" into VS Code
11.41.52  tap — "VS Code"
11.41.52  launched code as you, not as administrator
11.42.02  a wrong token from 192.168.0.9 (7 characters)
```

Dim lines are the panel and the link — what the tablet is looking at, who connected,
the config being re-read. White lines are things done to this machine: typed, pasted,
launched, sent. Amber lines are refusals: a blocked injection, a tap that arrived while
this window had focus, a wrong token.

In remote mode this is the only place any of it is visible, which is the point — a tap
that appears to do nothing has nowhere else to explain itself. Two deliberate limits:
**snippet text is never logged** (a snippet can be an address or a password-reset link;
which tile fired and where it went is what's useful), while a **shell command is logged
in full**, since which command ran in which shell is exactly what's worth being able to
look back at. Nothing is written to disk — it's a 500-line window, not an audit trail —
and **Copy** puts the whole of it on the clipboard.

The tablet keeps one request parked here whenever nothing is changing — that's what makes
a change land on it instantly — and none of that traffic gets a line each: the tablet's connection is logged when it's
news, meaning a different device or the same one back after half a minute of silence.
The screen line is logged the same way — on arrival, and after that only when the answer
changes. Rejected tokens are logged at most once every three seconds, so something
scanning the port can't push the real traffic out of the window.

## Elevation

The dashboard runs elevated on purpose. Windows' UIPI only allows synthesized
input to travel to windows at the same or a lower integrity level, and Git Bash
here is elevated (`HKCU\...\AppCompatFlags\Layers` marks `git-bash.exe` as
`RUNASADMIN`). From a Medium-integrity process, `SendInput` into that window is
discarded silently — no error, nothing typed. Running at High integrity covers
both cases: High into elevated Git Bash, and High into ordinary Medium apps like
VS Code and Outlook.

No injection technique gets around this. Clipboard-plus-Ctrl+V fails identically,
because the Ctrl+V itself is synthesized input.

Two consequences:

- **Launched apps would inherit administrator.** Tiles therefore de-elevate by
  default, borrowing Explorer's token to start the process at normal user
  integrity — an elevated editor in a GVFS working tree can leave files that later
  trip up non-elevated git operations, and that shouldn't be the price of a tile
  that never asked. Set `"deElevate": false` on the rare tile you *want* elevated.

  The borrowed token can't be handed to `ShellExecute`, and `CreateProcessW` does
  none of what makes a tile work — no PATH lookup, no App Paths, no file
  associations, no notion of a folder or a URL. So the target is resolved first and
  each shape started as itself: an `.exe` is named to `CreateProcess` directly; a
  folder, a URL or a document goes to `explorer.exe` as a single argument; and a
  batch shim — `code` on PATH is `code.cmd` — goes through `cmd /c` with the window
  created hidden, so the VS Code tile doesn't leave a stray terminal on the desktop.
  A tile that wants a visible console should point at a terminal; `wt.exe` opens its
  own window either way.

  This used to be one `cmd /c start /b "" "<target>" <args>`, which did all of the
  above and re-read the whole line as shell syntax while it was at it. A path is not
  shell syntax: a folder honestly named `R&D` was split at the ampersand and never
  opened, and anything that could write a tile could hide a second command in one.
  Only the batch shim reaches a shell now, and its arguments are refused rather than
  escaped if they contain characters `cmd` would read as commands — `LastFailure`
  says so, and the tile falls back to an ordinary (elevated) launch.
- **UAC prompts at every start.** To avoid the prompt, register a scheduled task
  with "Run with highest privileges" that launches the exe, and start the
  dashboard by triggering that task (`schtasks /run /tn Deckhand`) from a
  shortcut. Task Scheduler elevates without prompting.

If injection is ever refused, the dashboard now says so in a dialog with the
Win32 error rather than appearing to do nothing.

## Configure

Edit `dashboard.json` and save — the dashboard watches both config files and reloads
itself a moment later, rescanning folder sections and relaying out, so no rebuild, restart or
even a tap is needed. ↻ is still there as the manual override, and it's the loud one:
a reload you asked for reports config problems in a dialog, where the automatic one
only logs them — a half-saved file is a normal thing for a watcher to see. The
automatic reload also leaves the remote server (and so the tablet's pairing) alone
unless the `remote` block itself changed; ↻ restarts it on purpose.

Which file that is matters, because the build copies `dashboard.json` next to the
exe and would otherwise leave ↻ re-reading a stale copy. So the dashboard walks up
from the exe looking for `Deckhand.csproj`: in a source checkout the
project's own `dashboard.json` wins, and a deployed copy (no `.csproj` beside it)
uses the file next to the exe. The ↻ tooltip names the exact file.

It's two files, not one, when a `dashboard.local.json` sits beside it. That one is
merged over the shared file on load — keys in it win, objects merge a level at a
time, and a list or a plain value replaces the shared one outright, because half a
list from each file would be impossible to reason about. The point of the split is
that `dashboard.json` travels — it is the one the build copies, and the one worth
handing to a second machine — and the local file doesn't: machine-specific paths
and per-machine choices live locally, and the shared file works anywhere.
`remote.enabled` is the example — off in the file that travels, on in the file that
stays. Neither is in git (see [Run](#run)); `dashboard.example.json` is what the
repository ships, and it is the shared file with the personal parts removed. A local file that won't parse costs only
itself: the shared file still loads, and the warning says exactly what was dropped.
When both were read, the ↻ tooltip and the log name both.

`dashboard.schema.json` describes the whole format, and both files point at it with
`"$schema"`, so an editor that reads JSON schemas (VS Code does) completes key
names and flags typos while you type. The dashboard checks for misspelled keys on
load as well — a `"snipets"` list would otherwise just silently never appear — and
`--check` runs every one of those checks without starting anything:

```
Deckhand.exe --check
```

reads the config, prints the same warnings ↻ would show to the console it was
started from (or a message box when there isn't one), and exits — code 0 when
clean, 1 when not, so a script can gate on it.

One change ↻ can't show you: a section written for profiles is hidden unless one of
them is the focused app, so editing it looks like nothing happened until you focus a
window it's for. The designer's **FOCUSED** button is the way to look at one without
arranging for the right window to be in front — it draws the panel as it would be with
that app focused, which is also how to see what a gated group does to the ones packed
around it.

The file itself is plain JSON with no comments in it — this section is the
documentation. The loader does skip `//` comments and trailing commas, though, so
notes can be left inline where they'd help.

- **Layout** — the dashboard is a fixed grid, `layout.columns` × `layout.rows`
  (default 12 × 12), dividing the window evenly, so ranges read the same way on
  either axis. There is no size in the config: the grid is proportional, and the
  panel's own size comes from dragging it (see [Resizing](#resizing)).
- **Choosing a row count** — rows are the sharper trade-off of the two axes,
  because tiles have a minimum height. At 12 rows on a 620pt-tall panel a row is
  only ~45pt, less than a tile's 56pt, so a one-row group scrolls from its very
  first tile. Budget roughly 2 rows per tile row you want visible, or drag the
  panel taller. Fewer rows (3–4) make every group comfortable but coarse to place.
- **Sections** — each one is a bordered group occupying a rectangle of that grid,
  given as 1-based ranges: `"columns": "1-3", "rows": "1-3"`. A plain number is a
  width instead of a range — `"columns": 3` means "3 wide, wherever it fits" — and
  those sections are packed into the first free gap, scanning left to right and
  top to bottom. Pinned and unpinned sections mix freely; the unpinned ones flow
  around the pinned ones.
- **Group size and scrolling** — a group's size comes from the grid, not from how
  many tiles are in it. Configure more tiles than fit and the group grows a
  vertical scrollbar (drag with a finger to scroll) rather than stretching and
  pushing the rest of the layout around. If groups are too cramped, drag the panel
  bigger or use fewer rows.
- **Overlapping rectangles are honoured, not resolved.** Two sections pinned on
  both axes keep the rectangles they were given even where those rectangles
  intersect — moving one would second-guess a config that was explicit. The pair
  then draws on top of each other, and the group underneath shows through around
  the edges of the one on top. That reads as a bug in the wrong group: a scrollbar
  a hand's width from the tiles it belongs to looks like it belongs to the small
  group covering them. If a group seems to have a scrollbar it has no need for,
  compare its rectangle with its neighbours' before anything else. Two groups that
  can be on screen together — including two written for the same profile — can only
  share columns if their rows don't overlap.
- **Tiles in a group** — tiles snap to their own grid inside it. `tilesPerRow`
  defaults to the group's width in columns, so a 3-column group fits three tiles
  before wrapping. A tile can straddle several with `"span": 2`. Omit `columns`
  on a section entirely and its tiles size themselves and flow instead of
  aligning to a grid.
- **Snippet placeholders** — a snippet's `text` may carry `{date}`, `{time}` and
  `{clipboard}`, resolved at the moment of the tap rather than when the config was
  written: the local short date and time, and whatever text is on the clipboard (empty
  when what's there isn't text). The same idea as `{name}` on a folder tile's command.
- **Tile colour** — an app or snippet tile takes `"color": "#RRGGBB"` (or `#RGB`) and
  wears it on its outline, on the panel and on the tablet alike. On a wall of identical
  tiles a colour is faster to hit than a label is to read. Longer WPF forms like
  `#AARRGGBB` are refused because a browser reads eight digits as RGBA — the two screens
  would disagree about what was written.
- **Tile pictures** — an app or snippet tile takes `"icon"`, the path to a picture, and
  `"iconMode"` saying where it goes on the tile: `"left"` of the label (the default — a
  small one, at text height), `"above"` it (the picture takes the tile and the label sits
  under it), or `"fill"`, where the tile *is* the picture and the label is kept as its
  tooltip. The label never goes away whichever mode it is: it stays what the tile is
  called in the log, in a tap from the tablet and to a screen reader. On an app tile the
  `+` is never a picture — it's the half that opens another window, and on a pair whose
  wide side has become a logo it's the only thing left with a shape to look for.

  The path is absolute, or relative to `dashboard.json`'s own folder, with `%ENVVARS%`
  expanded. Pick one with the designer's `…` and the file is **copied in** beside the
  config — into a `pictures\` folder next to it — and what gets written is the relative
  path, so a layout and its pictures stay one thing you can move. A pointer into a
  downloads folder is a tile that breaks the day that folder is tidied, and it breaks
  quietly.

  Around that: a picture already under the config's folder is left exactly where it is,
  wherever that is; one identical to a file already in `pictures\` is pointed at rather
  than copied twice; a name taken by different content becomes `logo-2.png`; and a file
  that can't be used isn't copied at all, so that folder never collects anything that
  doesn't draw. A path typed by hand is left alone, which is right — sometimes a shared
  folder is exactly what you meant.

  png, jpg, gif, bmp, ico, webp and svg — the intersection of what this can draw and what
  a browser can, because a picture only one of the two screens can show is a tile that
  looks different on the tablet, and the preview exists to stop exactly that. tiff is the
  one left out, and nothing over 2MB, since every picture is also sent over the air.

  **svg** stays a drawing all the way to the tile rather than being flattened to pixels,
  so it's sharp at whatever size the tile turns out to be — which is the reason to use one,
  since everything else here is a fixed grid being scaled to fit. WPF has no svg decoder,
  so this is the one format that needs a package to draw
  ([SharpVectors](https://github.com/ElinamLLC/SharpVectors), BSD-3): a picture only the
  tablet's browser could draw would have been a tile that looked different there, so it
  was either the dependency or no svg at all.

  An svg has to be **self-contained**, because an svg is a document and not a picture. One
  that points at another file, declares entities, or carries `<script>`, a `<foreignObject>`
  or an `on…` handler is refused at load with a line naming which of those it did. It may
  point within itself (`#gradient`) and nowhere else: a relative path would be read from
  beside the file here and from beside a blob URL on the tablet, which is to say drawn on
  one screen and not the other. That is what keeps "nothing is fetched" true of a format
  with its own opinions about it.

  A picture carried inline (`data:…`) is refused too, for a duller reason: the drawing
  package draws nothing for one while a browser draws it, so that tile would differ between
  the two screens. Point the tile at the picture itself instead — that is what the six
  raster formats are for. And one caveat survives all of it: text set in a font, since the
  panel draws it with the font installed here and the tablet with whatever it has, so an
  svg whose lettering was converted to outlines is the one that's the same picture on both.

  webp is the one of those whose decoder Windows keeps optional — the Web Media Extensions
  component, which a stock Windows 10 or 11 has and some Server and LTSC builds don't. A
  machine without it says so in the load warning and the tile draws its label, on the
  tablet as well as here: the panel is what publishes a picture at all, so one it can't
  decode is never offered to the tablet, and the two screens still agree.

  Windows' webp decoder also reports its frames as carrying no transparency when they
  plainly do — the alpha is in the bytes, just not in the label — so a logo exported with
  a transparent background would draw as a logo in a black box. The panel relabels those
  frames rather than believing them, which is why a transparent webp looks like a
  transparent png here. An opaque one is unaffected.

  Nothing is extracted, guessed or fetched: a tile draws the file its config names and no
  other. That keeps loading a config down to reading files that were asked for by name,
  rather than "go and look inside that exe" or "ask that website for its favicon". A file
  that is missing, too big, of the wrong type or simply not decodable is named at load
  with the other config warnings, and its tile draws the label instead — on the panel, in
  the designer's preview and on the tablet alike.

  Pictures are read once and kept, keyed by the file and its timestamp: editing one in
  place is picked up on the next redraw — a focus change, or ↻ — since the watcher watches
  `dashboard*.json` and not the pictures beside it. The tablet follows for free, because
  new bytes are a new hash and a hash it hasn't got is a hash it fetches.
- **Folder sections** — a section with a `"source"` object gets one tile per
  subdirectory of `source.folders`, most recently used first. What a tile does is
  the `command` decision: with one, tapping types it into the focused window and
  presses Enter (`submit`), with `{name}`, `{id}` and `{dir}` substituted — the
  folder's name, what `split` captured from it, its full path (`{branch}` and
  `{wi}` still work as aliases from when this feature was branch-only). With no
  `command`, tapping opens the folder itself, exactly like an app tile pointed at
  it — window matching included — which makes a source section with nothing but a
  `folders` line a self-maintaining launcher for a projects directory.

  A section that types belongs behind `"profiles"` naming the windows it types
  into. That one list does double duty: it's what shows and hides the section —
  with a pinned layout its rectangle sits empty until a matching window is
  focused — and it's re-checked at tap time, because focus can change between the
  render and the tap and a command typed into an editor is junk. A section with no
  profiles is always on screen and sends without checking; that's right for
  open-the-folder tiles and wrong for command tiles, so give the latter profiles.

  The `▾` beside a tile expands `source.commands` — extra commands with the same
  placeholders — laid out `commandColumns` wide; no commands, no `▾`. Only one
  tile stays expanded at a time. `max` caps the list (the header says so when it
  does, so a short list is never mistaken for the whole list) and `exclude` names
  directories to leave out of the scan entirely.

  `split` is a regex (case-insensitive) that deals the folders onto two sides of a
  switch: names it matches on one, everything else on the other, and its first
  capture group becomes `{id}`. With one set, the top of the section — spanning
  the row — is that switch. It reads `⇄ Non-WI branches (1)`, or whatever
  `matchLabel`/`otherLabel` call the two sides: the side it switches *to* and how
  many are over there, the way a `▾` says what tapping does rather than what is
  already open. That is deliberate — the tablet draws every tile the same and has
  no way to show one pressed in, so the label has to carry the state on its own.
  The header names the set you're looking at, the switch is left out when the
  other side is empty, and the choice survives alt-tabbing away and back — and a
  reload. Both sides come out of one scan, so switching touches no disk. No
  `split`, no switch: one flat list.

  `subtitles` names a file — or a glob, so a tool can write dated files without
  coordinating with anyone — of `key|text` lines; a folder whose `{id}` (or,
  failing that, whose name) appears as a key gets the text as a second line on its
  tile. The format is the entire contract: anything that can write two fields
  around a pipe can put subtitles on these tiles, and no file means plain names.

  The recently-used ordering is the dashboard's own: every tap on a folder tile is
  recorded in `%USERPROFILE%\.deckhand_history` (one `seconds<TAB>path` line
  per tap, compacted once it grows), and the scan sorts by that, then by each
  folder's change time. It has to be the dashboard's own record because a folder's
  timestamp only moves when its direct children change — a working copy edited
  daily for a month looks untouched — so the ordering starts from change times and
  learns from use.
- **Profiles** — a name for an app, and how to recognise it. That is all a profile
  is: it holds no tiles and no placement. `match` is a list of process names (with
  or without `.exe`, case-insensitive) — what Task Manager's Details tab shows,
  minus the `.exe`. The optional `titleMatch` is a regex the window title must also
  match, for splitting one app in two:

  ```json
  "profiles": [
    { "label": "VS Code — API repo", "match": [ "Code" ], "titleMatch": "backend-api" },
    { "label": "VS Code", "match": [ "Code" ] }
  ]
  ```

  First match wins, so list narrower profiles first — as above, where the general
  one would otherwise answer for every VS Code window and the specific one would
  never be reached. The top bar shows which profile is live.
- **Sections belong to profiles, not the other way round.** A section with
  `"profiles": [ "Git Bash" ]` is on screen while that profile is the focused app's,
  and nowhere else; a section that lists none is always on screen. Every button in
  the dashboard is therefore in one list, `sections`, with what it does, where it
  sits and when it appears all written in the same place:

  ```json
  { "label": "Git", "profiles": [ "Git Bash" ], "columns": "3-6", "rows": "9-12",
    "snippets": [ { "label": "Diff stat", "text": "git diff --stat", "method": "type" } ] }
  ```

  List several names to share one group between profiles. Since only one profile is
  ever active, groups for different profiles can all claim the same rectangle — but
  two groups for the *same* profile can't, and that's the one overlap worth
  checking, since nothing warns about it (see the note on overlapping rectangles
  above).
- **Two config mistakes are reported rather than left to be noticed.** Both are
  invisible at runtime — tiles that simply never appear, which looks exactly like a
  group that isn't meant to be there — so they get a dialog on load and on `↻`: a
  section written for a profile label nothing carries, and a profile that can never
  be the first match because an unconditional one above it already claims the
  process.
- **Apps** — `path` can be an exe name on PATH (`wt.exe`), a full path, a
  folder, or a URL; anything ShellExecute understands. Optional `args`.
- **An app tile has two sides** — one control divided by a line, not two buttons.
  The wide side is *go to the program*: it brings the window it already has to the
  front, so you land back in the Chrome you were using rather than starting a
  seventh. With several windows open it puts up a picker, newest in front, and with
  none open it starts the program — that being the only thing "go to Chrome" can
  mean when Chrome isn't running. The narrow `+` is the other half: always another
  copy, whatever is already open.

  **Both sides are always there**, and nothing about which is read when the tile is
  drawn — the windows are looked up the moment you tap, never earlier. So a tile
  can't name a window that has since closed, can't miss one opened since, and the
  `+` can't be missing at the moment it's the only thing that would work. That also
  means a tile never changes shape while you look at it, and the panel does no
  desktop walk per focus change.

  The picker holds the windows a task switcher would show: visible, titled, not a
  tool window, not on another virtual desktop. Twelve at most, and it says how many
  it left out. It only ever appears for a real choice — one window is raised
  outright, and there is no such thing as an empty card.

  The picker is a modal over the panel, not a dropdown under the tile: a list of
  window titles needs the width, and the panel is the wrong shape to grow one
  downwards. It can't be a real dialog either — that would be its own window and
  would take focus, which this panel must never do — so it's a layer inside the
  panel that covers everything behind it. Tap the dim backdrop or Cancel to leave
  without picking. (Folder tiles keep their dropdown: those lists are short, and
  they belong visually under the folder they came from.)

  Which process's windows a tile goes to is usually obvious from `path`, and where
  it isn't, `"windows": [ "mintty" ]` says so outright. The gap is launchers: a
  program that starts something else and exits owns no windows by the time you
  look. `git-bash.exe` (mintty) and `wt.exe` (WindowsTerminal) are handled
  already; anything else that starts a fresh copy when its windows are plainly open
  wants `windows` set.

  Two kinds of tile the wide side can't serve well, and there is no longer a flag to
  turn it off for them:

  - **A folder** opens in Explorer, and Explorer is one process for every window it
    has. So the wide side goes to whichever Explorer window is frontmost, not to
    that folder's. The `+` always opens the folder itself.
  - **A URL** is a tab in a window that already exists, so it has no window of its
    own to go back to: both sides open the page. Add `"windows": [ "msedge" ]` to
    have the wide side raise that browser instead.

  ```json
  { "label": "Chrome",   "path": "chrome" },
  { "label": "Our App", "path": "C:/tools/start-ourapp.cmd",
    "windows": [ "OurApp" ] }
  ```
- **URLs** — a `path` of `http://…` or `https://…` isn't launched, it's opened as
  a new tab **in the browser you're currently looking at**, which is not
  necessarily the default browser (see below). `browser` overrides the choice:
  `"default"` always uses the default browser, a process name (`"msedge"`) pins
  the tile to one browser. Here `args` means browser switches (`--incognito`,
  Firefox's `-private-window`), so they apply only when a browser is actually
  named — not on the default-browser fallback. The top-level `browsers` list is
  what counts as a browser; it already covers Chrome, Edge, Firefox, Brave,
  Vivaldi and Opera.
- **Remote** — `remote.enabled` serves the panel to a tablet's browser; see
  [The panel on a tablet](#the-panel-on-a-tablet).
- **Snippets** — `text` is what gets inserted. `method` chooses how:
  - `paste` (default): puts the text on the clipboard, sends Ctrl+V, then
    restores your previous clipboard text. Best for long/multi-line snippets.
  - `type`: synthesizes per-character Unicode key events. Slower, but never
    touches the clipboard and works where paste is blocked.

  `keys` presses keys afterwards — `tab`, `enter` or `escape`, several of them
  separated by spaces or commas. This is the way to a tile that fills in a
  template rather than a fixed string: inserted text can't select any part of
  itself, but an editor expanding its own snippet can. Type `rafce`, press Tab,
  and VS Code writes the component with its name selected in all three places,
  ready to be typed over once. Anything the editor offers on Tab works the same
  way; the tile only supplies the prefix.

  VS Code needs `"editor.tabCompletion": "onlySnippets"` for that Tab to expand
  a prefix — at its default of `off` it just indents, which looks exactly like
  the tile having done nothing. Worth sending `escape tab` rather than `tab`:
  typing the prefix opens the suggestion list, and a Tab aimed at that list
  accepts whatever it happens to have highlighted instead.

### The shipped layout

Both axes are twelfths of the panel, so these ranges are the whole of it:

```
  cols 1-2   cols 3-6          cols 7-12
  +-------+  +--------------+  +-------------------------+  rows 1
  |       |  |  the group   |  |                         |    ...
  |       |  |  for the app |  |                         |     8
  | Apps  |  |  in front    |  |       Work Items        |  rows 9
  |       |  +--------------+  |                         |    ...
  |       |  |  a second    |  |                         |    12
  +-------+  +--------------+  +-------------------------+
```

Columns 3-6 are the whole of the conditional strip: there isn't room for two of
them plus Apps and Work Items side by side in 12 columns, so groups for different
profiles all claim rows 1-8 there and only one is ever drawn. Git Bash is the one
app with two groups, and they stack — rows 1-8 and 9-12 — because they *are* on
screen together and would otherwise overlap. The lower one gets 4 of the 12 rows,
which on a 620pt-tall panel is two rows of tiles; more than that scrolls.

Work Items is a folder section written for the Git Bash profiles, so cols 7-12 sit
empty until a Git Bash window is focused.

## URL tiles and the "respective browser"

Handing a URL to ShellExecute opens it in the *default* browser. That's the wrong
one whenever the browser in front of you isn't the default: the tab appears in a
window you aren't looking at, in a browser you may not even have open.

So when the focused process is one of the `browsers`, the URL goes to **that
browser's own exe** with the URL as its argument. Chrome, Edge and Firefox all pass
a command-line URL to their already-running instance, which opens a tab — no second
copy of the browser. The exe path comes from the running process itself rather than
being guessed at, with the bare name (`chrome.exe`) as a fallback for a browser
that isn't running yet, which ShellExecute resolves through the registry's
`App Paths`. Brave, Vivaldi and Opera register no `App Paths` entry, so pinning a
tile to one of those with `browser` works only while it's running — following the
focus is unaffected, since a focused browser is by definition running.

Two details worth knowing:

- **URL tiles always de-elevate**, whatever `deElevate` says. The dashboard runs as
  administrator, and an elevated browser process can't join your ordinary browser
  session — it either refuses to start on a profile already in use or opens its own
  separate elevated window. Neither is a tab.
- **Firefox is told `-new-tab` explicitly.** A bare URL on its command line honours
  its own "open links in" preference and can produce a window instead. Chrome and
  Edge always open a tab and ignore switches they don't recognise.

The foreground window is read at the moment the tile is tapped rather than taken
from the cached profile context — a tap never changes focus, so the live answer is
always the right one.

## The panel on a tablet

`remote` is what makes the tablet an option at all. It's off until you turn it on:

```json
"remote": { "enabled": true, "listen": "http://+:8787/" }
```

The token is deliberately not in there. `dashboard.json` is copied next to the exe by the
build and travels with the project, so a token written in it ends up in every copy, backup
and build output too — and what's on the other side of it types and launches things as
administrator. It's read from `%USERPROFILE%\.deckhand_token` instead, one line and
nothing else; set `remote.tokenFile` to look somewhere else. A token still written in
`remote.token` is honoured so an old config keeps working, but the status window's tooltip
on the token says where the one in use came from, and says to move it.

**Both of those are also buttons.** The status window shows the file under the token,
with **Change…** and **New** beside it — there is no settings page, because two things
about the token belong next to the token rather than behind a door.

**New** writes a fresh token to whatever file is in use and restarts the server, which
locks out every device paired with the old one. That is the point of it rather than a
side effect: a rotation nobody is disconnected by wouldn't be one. It asks first.

**Change…** picks the file. A file that already holds a token is *adopted*, not
overwritten — pointing the panel at a key you already have is the reason to change this,
and writing over it would lock out whatever else reads it. A missing or empty one is
written with the token in use, so the change never leaves the panel without a key. The
old file is left where it is, still holding a copy; deleting a file you didn't name is a
bigger thing than moving a setting, so the confirmation says so and leaves it to you.

The choice is remembered in `%LOCALAPPDATA%\Deckhand\token-file.json`, not written back
into the config — that file is hand-edited and full of comments, and a program that
rewrites it to record one path strips all of them. So a path chosen here **wins over
`remote.tokenFile`**, being the later and more deliberate of the two acts. Choosing the
path the config or the default already names clears the override rather than recording
it, which is the way back to being config-driven.

If you'd rather mint one yourself, the button does exactly this:

```powershell
# 29 characters of the alphabet that survives being read off a screen
$a='0123456789abcdefghjkmnpqrstvwxyz'; $b=[byte[]]::new(25)
([System.Security.Cryptography.RNGCryptoServiceProvider]::Create()).GetBytes($b)
$c=foreach($x in $b){$a[$x%32]}
[IO.File]::WriteAllText("$env:USERPROFILE\.deckhand_token",
  (0..4|%{-join $c[($_*5)..($_*5+4)]}) -join '-', (New-Object Text.UTF8Encoding($false)))
```

With it on, [the startup question](#where-the-panel-runs) offers **On a tablet**;
choosing that is what opens the port. Open `http://<laptop>:8787/` on the tablet, enter
the token once (it's kept in that browser's local storage), and the page draws whatever
the panel is showing — following the focused app, because it's reporting the live panel
rather than reading the config. Tap a tile and it fires here. The address is in the
remote window on the laptop, and in the ↻ tooltip.

**It mirrors the grid, not just the tiles.** Each group carries the rectangle the
placer actually gave it, along with the grid it was cut from, so on a screen 600px or
wider the page lays out exactly like the panel — an `Apps` section pinned to columns
1-2 takes two twelfths of the tablet's width, not all of it, and each group scrolls its
own tiles inside a fixed cell. Below 600px twelve columns would leave tiles too small
to hit, so the groups stack full-width and the page scrolls as a whole instead.

**Every tile is placed, not flowed.** The page has one grid per group where the panel has
nested panels — a branch tile is a label with its `▾` beside it, above a command list of
its own, and an app tile is the same two-button shape with a `+` instead — so the snapshot
spells out a row, a column and a width for each button, and the
group's grid divides finely enough for every level of that nesting to line up: a group of
branch tiles is one tile per row until a list opens, then two, so `commandColumns: 2` puts
the commands two to a row there as it does on the panel. Sending the buttons in order and
letting the page flow them is what used to put a branch's `▾` on the row *below* the
button it belongs to, since a group one tile wide has no room beside it.

The narrow button is the exception to placing things: it's marked as *trailing*, meaning it
shares the cell of the tile before it, and the page gives it a fixed 44px there and the
label the rest — the same 44px the panel uses. A share of the row would instead be a fat
`▾` in a wide group and a sliver in a narrow one, when what it wants is to be the same size
in both. A trailing button is also marked *joined* or not: an app tile is drawn as one
control split by a line, a branch tile as a label with an arrow beside it, and the page
needs telling which because both arrive as the same two buttons in one cell. Each tile also
carries the indent of the containers it sits inside, which is how a command list arrives
set in under the branch it belongs to: the panel indents it with a 12px margin, and that
margin is what travels.

**The picker is published, not reimplemented.** When the window picker is open on the
laptop, the snapshot carries its card — a title, one choice per row, and which of them is
the way out — and the page draws it as a modal over the groups, exactly as the panel does
over its own tiles. So opening it with a finger on the laptop opens it on the tablet, the
choices are ordinary tiles that answer to an ordinary tap, and the backdrop dismisses by
tapping the same Cancel the panel is showing. Nothing behind it will answer while it's up
(see [What stops it being a remote shell](#what-stops-it-being-a-remote-shell)).

**A redraw keeps its place.** Every tap publishes a new panel, and the page redraws from
scratch — so it carries each group's scroll position across. Without that, tapping a
command in a branch halfway down the list threw the list back to the top, away from both
the tile just used and the ones beside it.

**The tiles go dead when the laptop stops answering.** The dot in the header turns red,
and every tile dims and stops responding to a tap — a tile that looks live while nothing
is listening is one you tap twice and then harder. The banner says it in words as well,
because a dimmed tile alone doesn't distinguish a laptop asleep from one off the network.
A refusal counts as not answering, since the tap would be refused too, so what's on screen
stays one rule: red dot, dead tiles, and the banner for the reason. The groups still
scroll, so the panel can still be read while it's out of touch, and the first answer that
arrives brings everything back — the page knocks every three seconds until one does.

**A changed token puts the tablet back at its token box.** However it changed — **New**, a
hand-edited file, a different file chosen — the server is restarted, which drops the parked
long poll, and the next request is answered *401*. The page throws away the token it was
holding rather than retrying with it, and shows what the laptop said, so a tablet stops
being a live control surface within seconds of the token it knows ceasing to be the token.
Scanning the QR again with **Show** on pairs it back.

**It replaces itself when the laptop app is rebuilt.** The page and the snapshot are one
design, so a page the tablet opened before a rebuild goes on drawing new snapshots with
old code — which looks like a fault in the panel, not a stale tab. Every snapshot carries
a short fingerprint of everything the browser is served — the page, the service worker and
the manifest together, so no one of them can be left a build behind — and a page whose own
fingerprint no longer matches fetches itself again. Restarting the same build changes
nothing, so a restart alone doesn't disturb the tablet. The token is in local storage, so
a reload costs only the redraw. Upgrading *to* a page that does this still needs one
manual refresh — the old one has no idea to look.

The page sends its fingerprint back on every poll, and that's load-bearing rather than
decorative: the laptop answers "nothing new" only when the revision *and* the build still
match. Restarting puts the revision count back to the beginning, so a page reconnecting to
a rebuilt app on the same number it left — one or two, for a panel nobody has touched —
was otherwise told it was up to date, and stayed on the old build until the panel next
happened to change. Installed to a home screen that page could be wrong for days, with no
address bar to reload it from.

If a reload somehow lands on the same old page — a browser holding on harder than
`no-store` should allow — the page says so in the banner and asks to be closed and
reopened, rather than reloading in a loop. It goes on drawing in the meantime: a panel one
build behind still launches things.

**It fits the screen the tablet actually has.** In grid mode the page is exactly as tall
as the viewport and doesn't scroll, so its height has to be right or the bottom row goes
somewhere you can't reach. `100vh` is the wrong number on a tablet: it's the height with
the browser's bars retracted, which put the last row of tiles behind the bottom bar. The
page asks `visualViewport` how much it can actually see, falling back to `100dvh` and
then `100vh` where it can't, and re-measures when a bar slides away or the tablet is
turned. Edge to edge under Android's navigation bar is separate and handled separately,
by `viewport-fit=cover` plus `env(safe-area-inset-*)` padding.

**It keeps the tablet's screen on.** A dashboard you have to wake up first costs an extra
tap on every tile, so the page holds the screen for as long as it's the page in front,
and lets go when you leave it — a wake lock held by a tab you've moved on from is just a
flat battery. The header says which of the two ways is holding it, because they aren't
equally sure:

- `awake` — a real [Screen Wake Lock](https://developer.mozilla.org/docs/Web/API/Screen_Wake_Lock_API).
  The browser only offers this on a secure page, so **over plain `http` to the laptop you
  won't see it**; it's what you'd get through an https tunnel.
- `awake (best effort)` — what the tablet will normally show. No wake lock, so the page
  falls back to the older lever: a silent 8px video, drawn from a canvas in the header's
  own grey, playing at a frame a second. A browser that keeps the screen on for a film
  keeps it on for this one. Nothing to see, and not guaranteed — hence the wording.
- `may sleep` — neither worked. Set the tablet's screen timeout instead.

Touch the words for the long version. The page reports which one it managed on every
request, so [the log](#the-log) and the status window say it too, and a tablet that
quietly gave up shows up there rather than being blamed on the dashboard.

**A remote tap is the same tap.** The page sends back only the tapped tile's id, and
the server raises `Click` on that very `Button` in the panel. There is no second
implementation of what a tile does, so nothing can drift: URL tiles still pick the
focused browser, `deElevate` still de-elevates, folder tiles still type into the
focused Git Bash, a folder's `▾` still expands, and an app tile's wide side still goes to
the window you choose while its `+` still starts a copy — the page just redraws with what
appeared.

This is why the work has to stay on the laptop. Typing into the focused window only
works from inside the session that owns it, at an integrity level UIPI will let
through; a tablet has neither. It only ever supplies the tap.

### Installing it on the tablet

The page can be put on the tablet's home screen and opened as an app rather than found in
a browser: its own icon, no address bar, and the whole screen given to the tiles. Nothing
about how it works changes — it's the same page talking to the same laptop — but a control
surface you reach through a browser's tab list is a control surface with two taps in front
of it, and an address bar is 40 wasted pixels across the top of a panel sized to fit
exactly.

Alongside the page the laptop serves the four files a browser fetches for itself:

| | |
|---|---|
| `/manifest.webmanifest` | the name, the colours, `display: standalone`, and the icons |
| `/icon-192.png`, `/icon-512.png` | what Android installs from, both marked `maskable` |
| `/icon-180.png` | `apple-touch-icon`, what an iPad puts on its home screen |
| `/sw.js` | the service worker — see below |

**On an iPad** it works as it stands: Share → *Add to Home Screen*. iOS has asked in its
own way since long before the manifest existed, and it doesn't mind the connection being
plain `http`, so you get the icon and a standalone window with nothing else to do.

**On Android** the same menu offers *Add to Home screen*, and over plain `http` that gets
you a shortcut with the right icon that still opens inside Chrome. A real install — its own
window, no address bar — needs a **secure origin**, which `http://192.168.x.x:8787` is not.
Two ways to have one:

- Reach the laptop through an https tunnel — `listen` on `http://127.0.0.1:8787/` and let
  Tailscale (or an SSH tunnel) present it as https. Also the answer if the network isn't
  yours, and it's what turns the tablet's screen wake lock from `awake (best effort)` into
  `awake`.
- Or tell Chrome to trust the address: `chrome://flags/#unsafely-treat-insecure-origin-as-secure`,
  with the origin typed in exactly — `http://192.168.1.23:8787`, no trailing slash. Per
  origin, so it needs retyping if the laptop's address moves; give the laptop a fixed
  address if you go this way.

**The service worker is thin on purpose.** This dashboard is a window onto another machine:
every tile it draws and every tap it sends only mean anything while that machine is
answering, so there's nothing here pretending to work offline. It caches the shell — the
page, the manifest, the icons — which is enough to open instantly and say *the laptop isn't
answering* with a **Try again** button, in a standalone window that has no address bar to
retry from. Two rules keep it out of the way:

- **The network wins.** Everything is fetched live and the cache is only the fallback. A
  cache-first shell is the usual way an installed web app gets stuck on an old build, and
  a panel showing what the laptop looked like yesterday is worse than no panel.
- **`/api/` is never touched.** Snapshots and taps go straight past, so nothing in the
  worker can answer for the laptop or replay a tap.

It also carries the update forward. The worker's script is served with the build
fingerprint substituted into it, so a rebuilt app serves a script whose bytes differ —
which is exactly what the browser's own update check looks for — and each build gets its
own cache, so activating a new worker throws the old shell away. The page asks the worker
to re-check itself before reloading, or the reload could be answered out of the shell it
already had.

A worker only exists on a secure page, so over plain `http` to the laptop there is none.
The page checks and carries on without it: no offline shell, and the update comes from the
snapshot fingerprint alone, which is the part that matters.

**The icon** is drawn by [`icon.ps1`](https://github.com/xenoxsis/Deckhand/blob/main/icon.ps1) — a dark panel of four tiles with one of
them lit, which is what the panel looks like from across a desk. One script draws every
size, including `icon.ico` for the exe, so the taskbar, Explorer, an iPad's home screen and
an Android launcher all show the same thing. The tile colours are lightened from the
panel's real `#2d2d30`-on-`#1e1e1e`, which at 16px reads as a single dark blob: an icon is
looked at from further away than the thing it stands for. Run it after changing anything
there — the files it writes are committed, because the build needs them and a build
shouldn't depend on PowerShell:

```
powershell -ExecutionPolicy Bypass -File icon.ps1
```

### What stops it being a remote shell

The endpoint types and launches things as administrator, so the protocol is built to
have nothing else in it:

- **Ids only.** No field anywhere carries a path, a command, or text to type. Ids are
  a hash of group, label and repeat, and only resolve against tiles that were on
  screen when the tablet last drew — `{"path":"…\\calc.exe"}` is a 400, not a launch.
- **Pictures by content, never by path.** A tile's picture crosses as the hash of its own
  bytes, and the request that comes back for it is answered out of the set the panel
  published with those tiles — so there is no filename in it to sanitise, and nothing
  outside that set to name. `/api/icon/../../dashboard.json` is a 404 for the same reason
  `/api/icon/beef` is: neither is a key in the table, and the answer says "not on the
  panel" rather than "no such file", which would be a way to ask this machine what it has.
  The bytes sit behind the token like everything else, which is why the page fetches them
  itself rather than leaving it to an `<img src>` — a browser won't put our header on one.
- **A token on every call**, compared in constant time, minimum 16 characters. Without
  one the server refuses to start rather than listening open — and the startup question
  disables the tablet option and says why, rather than letting the choice be made and
  then fail. It's kept in `%USERPROFILE%\.deckhand_token`, outside the project, so
  a copy of the code isn't a copy of the key to it.
- **One tablet, one session.** The first device to send the right token holds it, and
  another with the same token is refused rather than quietly taking over. This is what
  makes having overheard the token insufficient — you also have to be the device that was
  already using it. What holds the session is an id the page makes for itself once and
  keeps, not the address it calls from: an address stops identifying anything behind a
  tunnel, where every client arrives from one address, and a new DHCP lease used to bounce
  the tablet's own session. The status window's tooltip says who holds it; **Unpair** lets
  it go so another device can take over, and ↻ still does the same on its way through the
  config.
- **Ten guesses a minute, per address.** Past that, that address is shut out for a minute.
  The paired tablet is exempt even from its own count, and a stranger only ever locks
  itself out — one counter for the whole port would have let it spray wrong tokens and
  deny the tablet its first connection. Counted over a window rather than as a run,
  because the tablet is succeeding constantly and a count reset by success would never
  reach a limit.
- **Compared as digests.** The token is hashed before comparing, so what's compared is
  always two 32-byte values. Comparing the tokens themselves is constant-time only across
  equal lengths — it returns at once when they differ, which hands back the length of the
  real one, and that is most of what guessing it needs to know.
- **By address, not by name.** A browser will send our token to whatever name resolves to
  this machine, and what a name resolves to isn't ours to decide — so a page anywhere can
  point one here and be talking to the panel from inside its own origin. Addresses and
  `localhost` are accepted; a name has to be listed in `remote.allowedHosts` first.
- **Not framable.** `frame-ancestors 'none'` and `X-Frame-Options: DENY`, so the panel
  can't be drawn inside another page with something laid over the tiles to catch a click
  meant for elsewhere. `base-uri` and `form-action` are shut for the same reason.
- **The log can't be flushed by a stranger.** Serving the page needs no token, so anything
  that can reach the port could otherwise write a line per request and scroll a session out
  of a 500-line window. Those lines are rationed to one every thirty seconds, and when the
  log is full it drops them before it drops the record of a tap.
- **A stalled client can't take the listener down.** Requests are handled off the accept
  loop and http.sys drops a body that stops arriving, so one connection that goes quiet
  mid-request can't stop the panel answering the tablet.
- **Except the page and its furniture**, which is not a choice: the page, the manifest, the
  icons and the service worker are fetched by the browser itself — from a `<link>`, or a
  `register()` call — and there is no way to make it send a header of ours along with them.
  What they carry is a name, a picture and some caching rules. Everything that reads the
  panel or does anything to it is behind the token.
- **Off unless it's the mode you chose.** In local mode nothing listens at all, whatever
  the config says. With `remote.enabled: false` no port is opened and the token is never
  read unless you pass `--remote` explicitly.
- **Bound where you say.** `+` is every interface; narrow it to one address, or to
  `http://127.0.0.1:8787/` when reaching it through a tunnel.
- **Nothing while unlocked.** An unlocked panel can hold focus, so its tiles are inert
  locally; a remote tap gets a 409 and the page says so.
- **Nothing behind a modal.** While the window picker is open it covers the panel, so a
  finger can't reach the tiles underneath it — and neither may a tap. Anything but the
  picker's own choices gets a 409 saying so. What the laptop can't do, the tablet can't
  ask for.
- **Nothing while the dashboard itself has focus.** In remote mode the status window is
  an ordinary window you can click into, and synthesized input goes wherever focus is —
  so a snippet would be typed into it, and a shell command ending in Enter could press
  whichever of its buttons had focus. Also a 409, also said out loud: on the page, in
  the window, and in the log.

It speaks plain HTTP, so **the token crosses the network in the clear** — that's the
price of working in a tablet browser with no certificate to trust. Pairing is what takes
most of the sting out of that: overhearing the token gets you nothing while the tablet
holds the session, and a device trying it is refused and named in the log. It is not a
substitute for a tunnel, though — on a network that isn't yours, listen on loopback and
reach it over Tailscale or an SSH tunnel instead, adding the name it presents to
`remote.allowedHosts`.

The wildcard prefix needs administrator, which the dashboard already has. Windows
Firewall will want to allow the port — and the rule's profile has to match how Windows
has classified the network you're actually on, which is the easiest thing to get wrong
here. Check first:

```
Get-NetConnectionProfile | Select-Object Name, NetworkCategory
```

`Private` is the one to want for a network with your own tablet on it; a phone hotspot
often comes up `Public`, in which case a `-Profile Private` rule is silently
irrelevant. Either mark the network private (`Set-NetConnectionProfile -InterfaceAlias
Wi-Fi -NetworkCategory Private`, elevated) and then add the rule, or scope the rule to
the profile you're on:

```
New-NetFirewallRule -DisplayName "Deckhand remote" -Direction Inbound `
  -Action Allow -Protocol TCP -LocalPort 8787 -Profile Private
```

### Tested

Five end-to-end harnesses, all non-elevated copies of the real project on loopback with
a throwaway config whose app tiles run a marker script and open Character Map.

**The protocol.** The page served without a token; `/api/tiles` refused with no token,
with a wrong token of the same length, and served with the right one; `?since=` **and**
`?page=` together returning 204 when both still hold and 200 when either is stale, missing
or junk — a build that no longer matches is never "nothing new", whatever the revision says;
unknown route 404; tap with an
unknown id 404, malformed body 400, a `path` field instead of an id 400, wrong token
401; and a real tap returning 200 **and the marker script actually running** — so the
whole chain through `LaunchApp` is proved, not assumed. Two tiles sharing a label come
back with distinct ids.

**The modes**, driven through the real startup path with the real switches, asserting on
the windows Win32 reports and on what the status window says (read back through UI
Automation, not assumed):

- `--remote`: exactly one window on screen, and it's the status one, *without*
  `WS_EX_NOACTIVATE` — an ordinary window that can be moved and clicked into. The panel
  publishes its groups and its grid while never being shown, a tap still fires the marker
  script, and the window shows the address, the connection, the tap count and a masked
  token it never reveals by itself.
- The log: serving, the page being opened, the tablet connecting, the panel's contents,
  the tap, what it launched, and a wrong token — with the token itself absent from all
  of it.
- The focus guard: with the status window really foreground (`SetForegroundWindow` after
  releasing the foreground lock, and verified rather than assumed), a tap comes back 409
  saying focus, the window shows its warning, and the log records the refusal.
- Closing the window quits and gives the port back — which also proves closing a
  never-shown panel doesn't throw. That run predates ✕ hiding to the notification area:
  what it settled is the teardown, which is unchanged and now reached by **Quit** instead.
  The ✕ path was not re-run.
- `--local`: the panel comes up *with* `WS_EX_NOACTIVATE`, and nothing is listening.
- No switch: the question comes up, offers both places, and nothing is listening behind
  it.
- `--remote` with a 4-character token: the window still comes up, nothing listens, and
  it says why.
- `remote.enabled: false` with no switch: straight to the panel, no question.

**The tablet's screen**, reported header by header the way the page does it, with the
status window read back through UI Automation: a page that says nothing gets no claim
either way rather than a guess; `video` puts "screen kept awake, best effort" on the
detail line and one line in the log; repeating the same answer doesn't add more lines;
`none` warns in both places; `lock` says so plainly; and a header full of junk is ignored
without being echoed anywhere.

**Where each tile is put**, over a throwaway git root of three fake `wi<number>`
directories and a config that counts Character Map as its shell, so branch tiles appear at
all: every branch comes back as a button spanning its row with its `▾` marked as trailing
**in the same cell**, and no two placed tiles sharing one. Tapping a `▾` through the real
endpoint then divides the group in two, puts the four commands two to a row beneath it
indented by the panel's own 12px, leaves the label and the flipped `▴` where they were and
unindented, and starts the next branch below the list rather than in it. Other groups are
checked in the same snapshot: one tile per row is still one column, two per row still sit
side by side, an app tile with windows open shares its cell with an arrow the same way a
branch tile does, one with nothing to switch to has none, and snippet tiles never do.

  That last pair of clauses records the older app tile, whose narrow side appeared only
  while the program had windows open. Every app tile now carries its `+` unconditionally,
  so the half about it disappearing no longer applies — the flattening those runs
  exercised, a joined trailing button sharing its leader's cell, is unchanged.

  The runs also predate folder sections: they drove the branches-era config, where the
  shell gate and the commands lived in a top-level `branches` block, where now the same
  tiles are any section's `source`, gated by its profiles. What they exercised is the
  shape — a label with a trailing `▾` above an indented command list — and the shape is
  unchanged.

**Raising a window**, which is the part no amount of reading the source can settle: a tap
from the tablet is an HTTP request, so the panel has no claim on the foreground when it
tries to change it. With Character Map open and winver deliberately holding the
foreground, tapping an app tile's `▾` through the real endpoint publishes the picker with
that window in it, one per row, and a named way out; tapping the window **really brings
Character Map to the front** (asserted against `GetForegroundWindow`, not assumed) and
closes the picker behind it. Then the same again from minimized: the window is still
listed, and the tap both restores and raises it. While the picker is up, a tap for the
tile behind it — the one that would have run the marker script — comes back 409 and the
picker stays put. Cancel closes it and the tiles answer again afterwards. The arrow itself
is checked to arrive marked *joined* on an app tile and not on a branch tile, which is the
difference between one split control and a label with an arrow next to it — and to not
arrive at all for a tile whose program has no windows: closing the last Character Map
window takes the arrow away, and opening one brings it back.

  Read that against the older app tile too: it was the `▾` that opened the picker, where
  now it's the wide side, and a single window is raised without a card at all. What those
  runs actually settled is unchanged code — the raise itself, the 409 behind the modal, and
  Cancel — but the taps that reach it were not re-run in this shape.

**Installing and updating** was driven in a browser against the real endpoint, on
`127.0.0.1` — which browsers count as secure, so the whole service-worker path runs there
even though the tablet's own connection can't. The manifest, both PNG icons, the
apple-touch icon and the worker are all served with the right content types; the worker
registers, activates, takes control, and precaches the shell under a name carrying the
build; the build the worker was served matches the one in the snapshot. Then the app was
rebuilt with the page changed and restarted, and the open tab **replaced itself
unprompted** — new fingerprint, new cache, the old one deleted, and nothing left of the
old page. That last part is what found the bug this needed: the tab had been sitting
happily on the stale build, because a restarted app was back on the revision the page
already had and its poll was answered "nothing new". With the laptop stopped, a launch
still comes up from cache with the connection dot red, and a launch with nothing cached
gets *the laptop isn't answering* and a **Try again** button rather than the browser's own
error page — which in a standalone window would have no address bar to retry from.

The page itself was driven in a browser: branch tiles really draw with the arrow beside
the label at both tablet and phone width with no overflow, an app tile really draws as one
rounded control split by a line with full-width snippet tiles beside it, the
picker really draws as a modal over dimmed groups with its way out spanning the card, the
fallback video plays and
reports `video`, the granted-wake-lock branch stops that video and reports `lock`, and
with a 120px bottom bar simulated the grid ends 16px above it where the old rule left
104px of it underneath. The panel's own copy of that group was screenshotted alongside for
comparison — it always drew the arrows beside the labels; only the page didn't.

**Partly covered.** Both configurations compile clean, and three of the newer pieces were
checked without a tablet in the room:

- **The screen-size header.** The parser was driven through reflection on the built
  assembly — the shipped method, not a copy — over 34 inputs: what the page sends with and
  without the tiles box, a missing ratio, a comma decimal, a sign, an interior space, a
  thousands separator, a capital `X`, pixel counts under 64 and over 20000, a ratio of 0
  and of 99, a third field, a repeated one, `<script>` in place of a number, and 70 digits.
  Every one of those was rejected — surrounding whitespace is the one thing tolerated — so
  nothing but parsed integers can reach the log. The page's own `measured()` was then run
  in a browser against the real `remote.html` — `532x682@1.25` at the token box,
  `532x682@1.25 tiles=532x400` with the panel up — and both strings fed back through that
  parser, which accepted them and rendered the window's line. What has *not* been run is a
  real tablet reporting to a real server, or the log line's once-per-change rationing.
- **FOCUSED.** `DesignerPreview.Build` was driven, again by reflection on the built
  assembly, over a four-group throwaway config — two always on, one for a "VS Code"
  profile, one for "Outlook" — with each of the four lenses, and the group headers read
  back out of the visual tree it returned: every group, then the two ungated ones for
  "nothing focused", then each profile's own three. Each was also rendered to a PNG and
  looked at, which is what confirms the part worth having: the remaining groups really do
  grow and move up into the dropped one's cells, exactly as the panel packs them.
- **The Design button** and its tray-menu twin: the mode-switch runs above exercise the
  designer window itself, from `--designer`, but not opening it from the remote window —
  the one designer at a time, the unsaved-work question in front of **Quit**, and the
  taps-refused line naming the designer. The window it opens was built in a harness and
  measured, which settles the two things a picture can settle: the tablet's size arrives
  in the SCREEN boxes with the tooltip saying where it came from, and the FOCUSED button
  and its caption fit beside them at the window's narrowest. The three menu styles it
  looks up by name are asserted present, since `FindResource` throws when they aren't.
  Nobody has yet clicked the menu itself open.
- **Tile pictures.** The loader — `TileImages`, by reflection on the built assembly, so
  the shipped code and not a copy — over twelve icon paths: three real pictures, a 3MB
  file, a png that isn't one, an svg, a missing file, a directory, an absolute path, one
  with `%WINDIR%` in it, one padded with spaces, and none at all. Each came back either
  accepted with its hash, type and pixel size, or refused with the line the config
  warnings then carry. `DesignerPreview.Build` was run over a config using all three
  placements, rendered to a PNG and looked at: the pictures land where they should, a wide
  one is bounded rather than squeezing its label out of the cell, and all three unusable
  files fall back to their labels.

  The tablet's half was run for real, without a tablet: a server on loopback, a published
  snapshot carrying the three placements plus a joined `+` pair, and the page itself in a
  browser with the token typed in. It drew all three, fetched each picture exactly once —
  three requests for four tiles wearing three pictures — and the tile whose hash the
  server didn't have showed its label rather than sitting there blank, which is what the
  first look at it caught. Against that same server: `/api/icon/<hash>` 200 with the right
  content type and byte count, an unknown hash 404, no token 401, and five shapes of
  traversal — `../`, `..%2f`, a hash with `/../` after it, a relative path and an absolute
  Windows one — all 404, none of them reaching a file. What has *not* been run is a real
  tablet, or a picture being edited on disk while the panel is up.

  The designer was driven as well: the window built, a config with pictures loaded into
  it, a button selected, and then the **PICTURE** box, the line under it and the three
  placement buttons read back — a path that finds nothing turns that line into the reason
  — with the editing column rendered and looked at to see that it all fits. `ConfigWriter`
  was made to write that config back out and the file read in again, thirteen tiles and
  three placements out and back unchanged, because a save that quietly dropped the
  pictures is the expensive kind of bug.

  webp was added afterwards and checked the same way, over two real files — a lossless
  Chrome logo with transparency and an opaque photo, both straight out of a Downloads
  folder. Both decode here, this machine having the optional component, and rendering them
  in all three placements is what caught the alpha: the logo came out in a black box until
  the frames were relabelled, and the file's own header says `alpha_is_used`, so the box
  was ours and not the picture's. An anti-aliased edge pixel settled premultiplied versus
  straight. What has *not* been run is a machine lacking the decoder — that path is the
  ordinary "couldn't be decoded" one, with a line naming the component to install.

  svg came last and needed its own set, since an svg is a document: ten files through the
  real loader — a gradient icon, one with text, one carrying an inline `data:` picture, one
  pointing at `http://`, one at a relative path, one with `url(https://…)` inside a style
  attribute, one with `<script>`, one with an `onload`, an entity-expanding DOCTYPE, and a
  truncated file. The two icons were accepted as drawings and the other eight refused, each
  with the line saying which of those it did. The `data:` one is why that case is refused
  rather than allowed: the file's own base64 is a valid 16×16 png, and the drawing package
  silently draws nothing for it while a browser draws it — so allowing it would have been
  the one thing all this is meant to prevent, a tile that differs between the screens.

  Then both screens, side by side: the three placements rendered from an svg and looked at,
  and the same three served to a browser over the real endpoint as `image/svg+xml` and
  looked at there. Sharp on both. What has *not* been run is a font-dependent svg on a
  tablet that lacks the font — the case the caveat above is about.

  The label plumbing has a fallback in it for a reason, found by using the thing: reading
  only the automation name blanked the picker's **Cancel** on the tablet — the one tile on
  that card declared in XAML rather than built by `MakeTile`, so the one tile without a
  name to read. It drew "Cancel" on the laptop the whole time, which is exactly why it
  needed catching from the other screen. `LabelOf` now reads the name first and the text
  second, and was checked over six kinds of tile: XAML with and without a name, a built
  label, a built picture with no text at all, a two-line folder label, and a button with
  nothing on it.

  Copy-into-place came after the rest and was driven through the designer's own method on
  the built window, over eight picks: one from outside the config's folder into a
  `pictures\` folder that didn't exist yet, the same file again, a different picture
  sharing its name, two already under the folder, and three that can't be used at all — a
  text file, an svg carrying script, and a path to nothing. The folder ended with exactly
  two files and no duplicate bytes; the three unusable ones were written as the paths they
  are and copied nowhere. The failure branch was exercised by pointing the designer at a
  config whose folder is a file, so the copy can't be made: the absolute path is written
  and the line under the box turns red and says why.

## Resizing

Drag the grip in the bottom-right corner. The size is saved to
`%LOCALAPPDATA%\Deckhand\panel.json` and the panel comes back up at that size,
so it's a one-time adjustment — the `Width`/`Height` in `Panel/MainWindow.xaml` are only
the first-run fallback. Nothing about the size lives in `dashboard.json`: the grid is
proportional, so a section pinned to columns 1-2 and rows 1-3 gets 2/12 of the width
and 3/12 of the height whatever the panel's size, and ⛶ full-screen is the same
layout scaled to the monitor.

The resize is implemented by hand, with `ResizeMode="NoResize"` left in place, and
two Win32 rules dictate how:

- **Windows' own resize loop is off the table.** It starts from a non-client hit on
  a window whose entire design rests on never being activated, so the grip avoids
  that path altogether.
- **Mouse capture doesn't work either** — and this is the subtle one. Only the
  *foreground* window may capture the mouse, and this window can never be foreground.
  `CaptureMouse` therefore delivers events only while the pointer is over the panel,
  which drops the drag the instant it leaves — precisely the direction you drag to
  make the panel bigger. Growing it would have been impossible; only shrinking could
  ever have worked.

So the grip polls instead: on mouse-down it starts a 15ms `DispatcherTimer` that
reads `GetCursorPos` and sets `Width`/`Height`, ending when `GetAsyncKeyState` shows
the button released. Polling also covers the release itself, since a release outside
the panel produces no `MouseUp` to hook. Cursor positions are converted to DIPs, so
the drag stays 1:1 at any display scaling.

Consequences worth knowing:

- The grip is hidden while the panel fills a screen, since the monitor decides that
  size; ⛶ back to a panel restores the size you dragged.
- Position isn't remembered, only size — the panel still parks at the right edge of
  the work area on start. Dragging it by the title bar lasts for the session.
- A corrupt or nonsensical `panel.json` (zero or negative) is ignored rather than
  leaving an invisible window with no grip to grab.

## Unlocking, and FancyZones

Tap **🔒** in the top bar and the panel becomes an ordinary window: activatable,
resizable by its edges, present in Alt-Tab, and something FancyZones will snap.
Arrange it however you like — drag the edges, Shift-drag into a zone, Win+arrow — then
tap **🔓** to lock it back. The placement is remembered between runs.

Everything that changes the panel's geometry lives behind the lock. Locked, the top
bar is just 🔒 ↻ ✕ — the ⛶ full-screen button and the corner resize grip are hidden,
and the title bar can't be dragged, so the panel is genuinely anchored and a stray
tap or swipe can't shift it. Unlocking reveals ⛶ and the grip and makes the title bar
draggable.

While unlocked, the border turns blue, the title bar says so, and the tiles are dimmed
and inert. That last part matters: an unlocked panel can hold keyboard focus, so a
tapped snippet would type into the dashboard itself rather than the app you were
working in. Locking restores focus to whatever had it before.

One consequence: if you fill the screen with ⛶ and then lock, the ❐ button that
shrinks it back is hidden too — unlock again to get it. Two taps, not a dead end.

Snapping to the top of the screen maximizes the panel, which Windows does without
going through ⛶ at all. Two things are needed to make that behave:

- The window answers `WM_GETMINMAXINFO` to confine a maximized panel to the monitor's
  **work area**. Left alone, Windows maximizes a borderless window to the whole
  monitor plus its invisible resize border — 2574×1454 on a 2560×1440 screen — which
  covers the taskbar and pushes the panel's own edges off screen. This is the same
  problem `FillCurrentScreen` sidesteps by sizing by hand.
- `OnStateChanged` re-runs the chrome, so ⛶ flips to ❐ and the grip hides even though
  the app never asked to be maximized. Tapping ❐ then un-maximizes rather than
  layering our own fill on top of Windows'.

A maximized panel is not remembered across restarts — the saved placement is its
`RestoreBounds`, so it comes back as a panel. Use ⛶ or re-snap it.

This exists because FancyZones only snaps windows it considers *standard*, and at rest
this one is a `WS_EX_TOOLWINDOW` carrying `WS_EX_NOACTIVATE` — filtered out before the
zone overlay is even drawn. (Its `allowPopupWindowSnap` setting is a red herring here:
the window isn't `WS_POPUP`.) An earlier attempt dropped those styles only for the
duration of a Shift-drag; that was too brief a window for FancyZones in practice, so
the state is now explicit and lasts until you lock it.

Unlocking takes two steps, and the second is the one that's easy to miss.
`ResizeMode = CanResize` adds `WS_THICKFRAME`, but that alone changes nothing:
`AllowsTransparency` leaves the window with **no non-client area at all** — its client
rect covers the full window rect with zero inset — so the system finds no border to
hit-test and the edges stay dead. The window therefore answers `WM_NCHITTEST` itself
while unlocked, reporting the outer 10px as `HTLEFT`/`HTBOTTOMRIGHT`/etc., which is
what lets the system run its ordinary resize loop. Verified: dragging an edge while
locked does nothing, while unlocked it resizes from any edge or corner, and dragging
the top edge to the screen edge triggers Windows' own vertical-maximize — so external
window management genuinely applies.

That last point is the best evidence FancyZones will work too, since it snaps through
the same mechanism. **If it still won't**, the remaining cause is elevation: the
dashboard runs as administrator (see [Elevation](#elevation)), and Windows won't let a
normal-integrity process reposition a High-integrity window. Run PowerToys as
administrator too. Either way you can always move and resize by hand while unlocked,
which needs nothing from PowerToys.

## How the no-focus trick works

- `WS_EX_NOACTIVATE` extended window style + `ShowActivated="False"` — the
  window can never be activated.
- `WM_MOUSEACTIVATE` is answered with `MA_NOACTIVATE` — clicks and taps
  register on the buttons without activating the window.
- `WS_EX_TOOLWINDOW` keeps it out of the taskbar and Alt-Tab.
- Text is delivered with `SendInput`, which targets whichever window currently
  has keyboard focus.
- Profiles come from a `SetWinEventHook` on `EVENT_SYSTEM_FOREGROUND`. Since the
  dashboard is never activated, the foreground window is always the app that
  will receive the input — the panel and the target can't disagree.

## The example Work Items section, key by key

The dashboard knows nothing about any particular branch tool — no config file of
theirs parsed, no cache format assumed, no command built in. The Work Items section
in `dashboard.example.json` is one configuration of the general
[folder section](#configure) machinery, and it is worth reading as the worked
example of wiring that machinery to a tool the dashboard has never heard of. It
assumes a shell where `goto <folder>` opens one branch's working copy and a
handful of sibling functions open particular sub-projects inside it; substitute
whatever yours are called.

| Source key | What it's set to | Why |
| --- | --- | --- |
| `folders` | `C:/git` | the directory the working copies sit in, written out — the dashboard scans folders and nothing else, so there is no tool config for it to discover |
| `split` | `.*wi(\d+)` | work item branches are named `…wi<number>…`; greedy, so the *last* `wi<number>` wins (`r15.01_wi171083` is wi 171083), and the capture is `{id}` |
| `command` | `goto {name}` | the function takes the directory name, which is why it's `{name}` and not `{id}` — `goto wi171083` would not resolve when the folder is `r15.01_wi171083` |
| `commands` | `web`/`api`/`admin`/`docs`/`shared`/`be`/`e2e`/`src`, all `… {name}` | the `▾` list: eight sibling functions that each open one sub-project of the same branch, so they want the folder name for the same reason |
| `subtitles` | `%USERPROFILE%/.branch_cache/titles_*` | a glob of `key\|text` lines matched against `{id}`. Whatever fetches work item titles is probably already caching them in some such form — point at that cache as-is and the titles appear under the branch names for free |
| `exclude` | the four plain repos | they aren't branches, so the command and the eight sub-project functions would mean nothing in them |

Titles are therefore only as fresh as whatever wrote that cache: refresh it the way
that tool refreshes it, and the dashboard's ↻ picks the new lines up. A branch with
no cached title shows just its name — a stale title beats a blank button, and
keeping the cache current is the other tool's job, not the dashboard's. Ordering is
the dashboard's own tap record (see [folder sections](#configure)), so it starts
from folder change times and learns from use.

The four excluded repos ride along as the Repos section — plain snippets typing the
shell alias for each and pressing Enter, gated by the same profiles. They needed
nothing more general than what snippets already did.

Both sections are shell-only the ordinary way: they list the Git Bash profiles,
which is what shows them when a Git Bash window is focused (`mintty`, or
`WindowsTerminal` with `MINGW` in the title) and hides them otherwise — a plain
PowerShell window is deliberately not a match, these being bash functions. The
same profile list is re-checked at the moment a tile is tapped, so the tiles can't
type into a window the section wasn't written for even when focus changed between
the render and the tap.

Two mechanics that live in the dashboard rather than the config, because they're
about Windows rather than about any branch tool: the command is typed keystroke by
than pasted (mintty doesn't map Ctrl+V to paste — the clipboard route would put a
control character in the prompt), and a tile's `▾` list is built inline in the
dashboard's own window, not as a `ContextMenu` or `Popup`, which live in their own
HWND and would take focus — making the dashboard the foreground window and sending
the keystrokes to itself.

## Known limitations

- **Elevated apps:** if the focused app runs as administrator and this app
  doesn't, Windows (UIPI) silently drops the synthesized input. Run the
  dashboard elevated too if you need that.
- The `paste` method briefly replaces your clipboard (what was there — text,
  images, copied files — is put back, as far as the app that owned it allows).
- **Raising a window is a request, not a command.** Windows only lets the process
  holding the foreground, or the one that received the last input, change which
  window is in front. A finger on the panel counts as that input; a tap from the
  tablet is an HTTP request and counts as nothing. So a remote tap takes a longer
  route — sharing the foreground thread's input queue for the length of one call,
  and failing that, the call the task switcher itself makes. If a window ever
  refuses to come forward, its taskbar button flashes instead, and the log says
  the attempt failed rather than pretending it worked.

## License

MIT — see [LICENSE](https://github.com/xenoxsis/Deckhand/blob/main/LICENSE). Do what
you like with it; keep the copyright notice.

Two dependencies come with it: [QRCoder](https://github.com/codebude/QRCoder), which
encodes the QR in the status window, is MIT, and
[SharpVectors](https://github.com/ElinamLLC/SharpVectors), which draws an svg on a tile,
is BSD-3-Clause. If you redistribute a built copy rather than the source, put both their
license files in the archive alongside this one — BSD-3 asks for its notice and its
no-endorsement clause to travel with the binaries.
