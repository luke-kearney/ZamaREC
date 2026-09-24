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
- **Stats**, **Log**, **Settings**, **Mark**.

## Sessions and clips

Left column, filterable by profile. Badges: LIVE, RAM (segments in tmpfs), gone (RAM footage
dropped; markers and clips remain), starred, expiring.

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
  **around markers**: the before/after boxes used by ✂ on a marker, and *Export all* for every marker
  of one type. Escape or a click outside closes the panel.
- A clip's title bar has a *Star* button: a starred clip never expires and is never removed by
  retention. Starring a marker (★ in its row) protects its whole session the same way.
- Both side panes collapse to a strip (chevron in their header). Hovering the collapsed sidebar
  floats it over the page until the mouse leaves; clicking its strip pins it open.
- Markers pane: chips per type with counts, star toggle, click to seek. In a live buffer session a
  mark whose clip is not written yet reads "clip pending".

Keys: space, ←/→ 5 s, shift+←/→ 30 s, J/L 10 s, `,`/`.` one frame, `[`/`]` markers, I/O set
in/out, shift+I jump to in, P play range, E export panel / export, M mark, F fullscreen, +/−/0 zoom.

## Settings

Left: capture (monitor, default preset, cursor, audio sources as chips), defaults for timeline,
buffer, clips, retention, feedback and ports, and the quality presets table with GB/h. Right:
profiles as collapsible cards with a summary line, a process picker for the match field, helper
text under every field, and a footprint line showing what the profile needs in RAM or disk against
what is available. Overrides shown depend on the mode.

Save applies immediately or when the current session ends; the notice says which. **Raw TOML**
edits the file directly and keeps comments.

## Stats

Lifetime footage to disk vs to RAM (buffer sessions estimated, marked ≈), clip bytes and counts,
sessions and marks, what the folders hold now, free space, and a per-session table.

## Log

Everything the daemon did: sessions, marks, clips, exports, purges, config applies, errors.
Filter chips, live follow. Also `kairec log`.
