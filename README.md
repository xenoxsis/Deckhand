# Deckhand

An always-on-top, touch-friendly WPF panel that **never takes focus**. Tap a
tile to launch an app or insert a text snippet into whatever window is focused
(VSCode, a browser, anything).

**Setting one up?** Open [`docs/index.html`](docs/index.html) in a browser — a small
documentation site covering how to run and configure the panel, with a worked example
of every configuration key and five complete config files to copy. This README is the
design document behind it: why each decision went the way it did, and what was tested.

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

One project, four folders, and a root that holds only what has to be there:

| | |
|---|---|
| `Config/` | `dashboard.json` as objects, and the reading, merging and checking of it — plus the folder scan a `source` section is built from. Deliberately free of WPF and Win32, which is what lets the loader be compiled into a plain console project and tested without a screen. |
| `Panel/` | The panel window: placing sections on the grid, building tiles, what a tap does, and the chrome around the edge. |
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

The question is only asked when there's something to choose between: with
`remote.enabled: false` it goes straight to the panel, exactly as it did before there
was a remote side at all. Closing the question without answering quits, rather than
falling back to the panel — that would put a window on the screen you'd just decided
against, in the corner it was last left in.

`--local` and `--remote` answer it in advance, which is what a pinned shortcut wants:

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
whether that tablet is managing to keep its own screen on, and a log of everything that
passed between the two sides.

The QR follows **Show**: masked, it encodes only the address; with the token on screen it
folds the token into the URL fragment too, and scanning is the whole setup — the page
stores it and scrubs it from the address bar, and a fragment is never sent with any
request. Tying it to Show keeps Show the one switch that reveals the secret.

**Unpair**, next to ↻, lets go of the paired device so another one with the right token
can take the session. ↻ still unpairs too — restarting the server is what it does — but it
also re-reads the config, and swapping tablets shouldn't have to mean that. Its size and position are
remembered in `%LOCALAPPDATA%\Deckhand\remote-window.json`, separately from the
panel's own.

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
  dashboard**, **↻ Reload** and **Quit** — the same two buttons as the bottom of the
  window, so re-reading the config or stopping the server doesn't need the window back
  first.

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

The menu is a WPF `ContextMenu` templated dark in `App.xaml` (`TrayMenu`, `TrayMenuItem`,
`TrayMenuLine`) rather than a Win32 or WinForms one, so it matches the window it belongs
to; a stock `MenuItem` paints its highlight from a trigger inside its own template, which
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
window it's for.

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

**The icon** is drawn by [`icon.ps1`](icon.ps1) — a dark panel of four tiles with one of
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

MIT — see [LICENSE](LICENSE). Do what you like with it; keep the copyright notice.

The one dependency, [QRCoder](https://github.com/codebude/QRCoder), is MIT too. If
you redistribute a built copy rather than the source, put its license file in the
archive alongside this one.
