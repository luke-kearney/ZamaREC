# Web UI

<http://127.0.0.1:3556>. Never opened automatically. Plain HTML and JavaScript served by the
daemon; the only library is hls.js for timeline playback.

## Header

- **On/off switch**: auto recording. Off opens a menu: for the rest of this game, for 1/2/8 hours,
  or until turned back on. A running session is stopped. Manual `kairec start` still works.
- **Status pill**: idle, recording (profile, mode, elapsed, window), or paused with the reason.
- **cpu / ram / tmpfs**: kairec's own usage, daemon plus recorder, from `/proc`. Click the CPU
  figure to switch between share of all cores and one core. **clear** drops RAM footage of finished
  sessions (asks for a second click).
- **Games**, **Settings**, then icon buttons for Stats, Log, the theme and GitHub, then **Mark**.
- **Theme button**: the page follows the OS or browser light/dark preference and switches with it.
  The button cycles auto, light, dark; a fixed choice is remembered by the browser. The video area
  and the fullscreen controls stay dark in both.

## Sessions and clips

Left column, filterable by profile. A card shows its game's banner behind it, dimmed; grey once
the footage is gone. A banner is, in order: an image uploaded for the profile in Settings, the
profile's `banner_url`, Steam's header art for its `steam_app_id` (own or the preset's), or the
first hit of a Steam store search for the profile name. The daemon fetches each once into
`<root>/banners`, so the page never contacts the internet. Badges: LIVE, RAM (segments in tmpfs), gone (RAM footage
dropped; markers and clips remain), starred, a clock with the time left on a mark clip that will
expire, and *new* for a session of a Steam game that
has no profile yet.

A clip is listed and sorted by when its footage was recorded. An export cut today from Monday's
session or clip is dated Monday, in its file name too, and sits next to what it was cut from.
Retention still counts an export's age from the day it was cut.

## Player and timeline

- Timeline sessions play through hls.js, including the live one, a few seconds behind. Buffer
  sessions show their clips as bands on the bar; clicking inside a band plays that clip at the right
  offset. Hatched areas have no footage.
- Transport bar: previous marker, back 10 s, play/pause, forward 10 s, next marker, clock, then the
  range tools (set in, set out, jump to in, play range, the current range, and the save icon that
  opens the export panel), mute, volume, fullscreen.
- The bar: ruler, markers colour-coded by type, playhead, window (purged part hatched). Click to
  seek. Ctrl+wheel zooms around the mouse, wheel pans; the zoom buttons and `+`/`-`/`0` zoom centred on the playhead. The view
  follows the playhead while playing.
- **In/out**: *set in* takes the playhead and sets out to in + the `len` box (30 s default);
  *set out* the other way round. Drag the handles or the whole range. *Play range* plays in to out
  and pauses at out; *jump to in* seeks to in.
- **Export panel** (save icon, or `E`): the in/out numbers, `len`, a clip label, and *Export range*,
  which writes a clip (stream copy, or frame-exact re-encode with the box ticked). Its lower row is
  **around markers**: the before/after boxes used by the scissors button on a marker, and *Export all* for every marker
  of one type. Escape or a click outside closes the panel. *Export range* closes it too; when the
  clip is written a toast says so and the in/out range and label are cleared, with the playhead left
  where it is. A failed export keeps the range and label for another try.
- A clip's title bar has a *Star* button: a starred clip never expires and is never removed by
  retention. Starring a marker (the star in its row) protects its whole session the same way.
- The marker pane collapses to a strip (chevron in its header). The sidebar has a pin, on by
  default, at the left of the Sessions/Clips switch; unpinned it collapses to a strip. Hovering that strip
  floats it over the page until the mouse leaves; clicking its strip pins it open.
- Markers pane: chips per type with counts, star toggle, click to seek. In a live buffer session a
  mark whose clip is not written yet reads "clip pending".

Keys: space, ←/→ 5 s, shift+←/→ 30 s, J/L 10 s, `,`/`.` one frame, `[`/`]` markers, I/O set
in/out, shift+I jump to in, P play range, E export panel / export, M mark, F fullscreen, +/−/0 zoom.

## Games

One card per game, with its banner.

- **Your games**: the profiles. A card shows mode, storage, window or buffer length, quality, the
  RAM or disk it needs, how it is detected (process match or Steam launch) and when it was last
  played. Clicking it opens the profile's editor beside the grid: every field with helper text, a
  process picker for the match field, the banner upload, and a footprint line showing what the
  profile needs against what is available. Overrides shown depend on the mode. *Add profile* offers
  the game presets (Counter-Strike 2, Minecraft, Black Ops III) or a blank one.
- **Detected, no profile yet**: Steam games that were launched without a profile. They are recorded
  to RAM with the `[auto_games]` defaults. *Keep as profile* creates a profile with those settings
  and opens it; *Never record* puts the game on the ignore list and stops a session recording it.
- **Never recorded**: the ignore list, with *allow* to take a game off it.

Profile edits are saved with **Save**, like Settings. Keep, Never record and allow apply at once.

## Settings

Left: capture (monitor, default preset, cursor, audio sources as chips, and a switch for separate
audio tracks with a row per track: name, sources, a list to add a device or a running application
from, remove), the defaults for
unconfirmed games (on/off, RAM budget, quality, ignored app ids), and the quality presets table
with GB/h. Right: defaults for timeline, buffer, clips, retention, feedback and ports.

Save applies immediately or when the current session ends; the notice says which. **Raw TOML**
edits the file directly and keeps comments. Settings and Games edit the same config: switching
between them keeps unsaved edits, and Save on either saves both.

## Stats

Lifetime footage to disk vs to RAM (buffer sessions estimated, marked ≈), clip bytes and counts,
sessions and marks, what the folders hold now, free space, and a per-session table.

## Log

Everything the daemon did: sessions, marks, clips, exports, purges, config applies, errors.
Filter chips, live follow. Also `kairec log`.
