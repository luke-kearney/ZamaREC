# kairec

*kairec*, from *kairos* (καιρός), the opportune moment. A Linux session recorder for games, built around
[gpu-screen-recorder](https://git.dec05eba.com/gpu-screen-recorder/): it records your play
sessions on the GPU encoder, drops markers on game events and hotkeys, and lets you cut clips
around them without re-encoding.

Designed for a KDE Plasma Wayland desktop with an Nvidia card, and for people who do not want
a recorder that polls the GPU, pops notifications or steals focus.

## Modes (per game profile)

| mode       | what happens                                                                                     |
|------------|--------------------------------------------------------------------------------------------------|
| `timeline` | a rolling window of the last N minutes on disk (default 2 h). Scrub and export from anywhere in the window, even mid-session. Older footage is deleted as you play, so disk use is bounded by the window, not by how long you play. |
| `buffer`   | a ring buffer in RAM (or on disk). Nothing is written until you act. A mark dumps `mark_before_seconds` + `mark_after_seconds` around it into a clip; marks close together share one clip, and a mark whose before-window reaches back into the previous clip extends that clip instead of writing a second one. Mark clips expire after `mark_clip_expire_hours` unless starred. |

A timeline can live in RAM instead: `timeline.storage = "ram"` puts the segments on tmpfs, so you
get scrubbing with no disk writes. It needs window × bitrate of memory (60 min at 20 Mbps is 9 GB),
the daemon refuses to start a session that would not fit, and the footage is dropped after
`ram_keep_hours` or when the next session starts. Marks and clips are on disk as usual.

In timeline mode a mark can also write a clip around itself (`clips.mark_saves`), the same
before/after window buffer mode uses. It is on by default for RAM timelines, so a marked moment is
safe on disk even after the RAM footage is dropped, and off by default for disk timelines.

`kairec save` works in both: it writes the last N seconds to a clip. Every clip carries its own
markers so it can be opened in the timeline UI and cut down further.

Timeline sessions are stored as 5-second MPEG-TS segments plus a playlist, so a crash loses at
most a few seconds, and a "save" cuts across segments with a stream copy (no re-encode).

## Install

```
./install.sh
```

Publishes the daemon to `~/.local/share/kairec/daemon`, the CLI to `~/.local/bin/kairec`,
and enables the `kairec` systemd user service. The config is created at
`~/.config/kairec/config.toml` on first start; edit it and `systemctl --user restart kairec`.

Bind the hotkeys in *System Settings → Shortcuts → Custom Shortcuts → Command*:

- `~/.local/bin/kairec mark` – drop a marker
- `~/.local/bin/kairec save` – save the last `save_seconds` from the buffer

## CLI

```
kairec mark [label...]            drop a marker on the current session
kairec save [--last 30s] [label]  save the last N seconds from the replay buffer
kairec start [profile]            start a session manually
kairec stop                       stop the current session
kairec status                     show what the daemon is doing
kairec log [-n 40] [kind]         recent events from the daemon's log
kairec pause [session|2h|forever] turn auto recording off (stops the current session)
kairec resume                     turn it back on; a running game is picked up at once
kairec top                        live view until Ctrl+C: state, CPU/RAM of the daemon and
                                  recorder, system load, storage and write rate (all from /proc)
```

## Layout on disk

```
~/Videos/kairec/
  sessions/2026-09-24_20-13-58_cs2_de_mirage/
    live.m3u8                          playlist of the segments still in the window
    live.m3u8.ts                       first-frame timestamp from the recorder
    seg_000123.ts ...                  5 s segments
    segments.json                      every segment ever seen, with its start offset
    session.json                       profile, map, timings
    markers.json                       markers with seconds-into-session
  clips/cs2/
    2026-09-24_20-20-08_clutch.mp4     from `kairec save`, a mark (buffer mode) or an export
    2026-09-24_20-20-08_clutch.mp4.json  source, expiry, and the clip's own markers
```

## Quality presets

`[[quality]]` entries bundle fps, codec, bitrate, keyframe interval and an optional output size
limit. A profile picks one with `quality = "high"`; blank means the `[capture]` values. Four ship by
default: low (720p30, 6 Mbps), medium (1080p60, 12 Mbps), high (20 Mbps), ultra (35 Mbps). Edit or
add your own in Settings, which also shows each preset's GB per hour.

## CS2 events

With `events = "cs2-gsi"` on a profile the daemon writes `gamestate_integration_kairec.cfg` into
CS2's cfg folder (found through Steam's library list, or `gsi.cfg_dir`) and listens on
`daemon.gsi_port`. CS2 reads the cfg at launch, so restart the game once after installing. Every
kill (with the round count and headshot flag), death, round win or loss, MVP, and map start or end
becomes a typed marker at the moment it arrived. Only payloads about *your* steamid count, so
spectating never produces events. The map name is added to the session folder. `clips.event_clips`
lists which events also write a clip, default a 2-kill round or better and an MVP.

## Web UI

Open <http://127.0.0.1:3556> in a browser (never opened automatically; nothing in kairec
takes focus from a game). Sessions and clips on the left, player and timeline in the middle,
markers on the right.

- Timeline sessions play as HLS via hls.js, including a session that is still recording (a few
  seconds behind). The greyed part of the bar is footage already purged from the window.
- Click a marker or the bar to jump. `I`/`O` set the in/out points from the playhead,
  `E` exports the range, `M` drops a marker, space pauses, arrows seek 5 s (shift: 30 s),
  `,`/`.` step a frame.
- "Clip around this marker" (✂) uses the before/after seconds; "Export all" does every marker
  of one type. Exports are stream copies unless *frame-exact* is ticked, which re-encodes on nvenc.
- A clip opens the same way and can be cut down again.
- **Settings** (top right) edits the whole config: capture, defaults and profiles, with the
  monitor and audio devices picked from what the recorder reports. Saves apply immediately, or
  when the current session ends if one is running. A raw TOML editor sits behind the form.
  `kairec reload` does the same for hand edits. Port changes still need a restart.

## Retention

A pass runs a minute after start and every ten minutes (`kairec retention` runs it now and lists
what went). Per profile: mark clips past `mark_clip_expire_hours` go first, then anything older than
`retention.max_days`, then, while sessions plus clips exceed `retention.max_gb`, the oldest
unprotected item. Never deleted: starred clips, the live session, sessions with starred markers,
and sessions whose clips are still on disk.

## Tray icon

`kairec-tray` is a StatusNotifierItem for Plasma (and anything else that speaks that protocol),
installed as the `kairec-tray` user service. The dot is grey when idle, red while recording, amber
when auto recording is off. Left click opens the web UI, middle click drops a marker, and the menu
has the on/off durations, Mark, Save, Stop, Open web UI, Open clips folder. It talks to the daemon
over the same socket as the CLI and never opens a window of its own. Plasma hides new tray icons
until you promote them: right-click the tray → Configure System Tray → Entries.

## Stats and log

The UI has a **Stats** page (lifetime footage written to disk vs to RAM, clip bytes, counts, what the
folders hold right now, and a per-session table) and a **Log** page (every session, mark, clip,
export, purge and config change, filterable, live-tailing). The log is `~/.local/share/kairec/events.jsonl`,
one JSON object per line; `kairec log [-n 40] [kind]` prints the tail from a terminal. Lifetime
counters live in `stats.json` next to it, so they survive sessions being deleted.

## Status

- [x] phase a: daemon, profiles, timeline/buffer modes, auto start/stop, save, CLI, systemd
- [x] phase b: CS2 game state integration markers (kills with count/HS, death, round result, MVP, map)
- [~] phase c: export exists in the API/UI; CLI `export` command still to do
- [x] phase d: web timeline (sessions, clips, live playback, markers, range/marker/batch export)
- [x] phase e: retention (age, size cap, mark-clip expiry, starred/protected)
