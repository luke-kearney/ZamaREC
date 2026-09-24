# Configuration reference

The file is `~/.config/kairec/config.toml`. It is created with defaults on first start. Edit it in
the web UI's **Settings** page or by hand; after a hand edit run `kairec reload`. Changes apply
immediately, or when the current session ends if one is running. Only `daemon.*` ports and bind
address need a daemon restart.

Every key below is optional. Profiles override the global sections under the same names
(`timeline.*`, `buffer.*`, `clips.*`, `retention.*`); a blank override means "use the global value".

## `[storage]`

| key | default | meaning |
|---|---|---|
| `root` | `"~/Videos/kairec"` | holds `sessions/` and `clips/` |

## `[capture]`

| key | default | meaning |
|---|---|---|
| `monitor` | `"DP-1"` | name from `gpu-screen-recorder --list-monitors`, or `"screen"` for the first |
| `quality` | `"high"` | the `[[quality]]` preset used by profiles that do not pick their own |
| `audio` | `["default_output"]` | recorder audio sources: `default_output`, `default_input`, `device:NAME`, `app:NAME` |
| `cursor` | `false` | draw the pointer into the recording |
| `fps`, `codec`, `bitrate_kbps`, `keyframe_seconds`, `scale` | 60, h264, 20000, 1.0, native | raw encoder values; presets fall back to these for fields they leave blank |

## `[[quality]]`

Named encoder presets. Four ship by default: `low` (720p30, 6000 kbps), `medium` (60 fps,
12000), `high` (60 fps, 20000), `ultra` (60 fps, 35000).

| key | meaning |
|---|---|
| `name` | what profiles and `capture.quality` refer to |
| `fps` | capture rate |
| `codec` | `h264`, `hevc` or `av1`. Only h264 plays in every browser; Firefox on Linux will not play hevc |
| `bitrate_kbps` | constant bitrate. 20000 is about 9 GB per hour |
| `keyframe_seconds` | keyframe interval; stream-copy cuts land on keyframes, so keep it at 1 |
| `scale` | output size limit like `"1280x720"`; empty is native |

## `[timeline]` (defaults; profiles override under `timeline.*`)

| key | default | meaning |
|---|---|---|
| `window_minutes` | `120` | how far back you can scrub; older segments are deleted as you play. `0` keeps everything |
| `segment_seconds` | `5` | segment length. A save waits for the current segment to close, so keep it short |
| `storage` | `"disk"` | `disk` or `ram`. RAM puts the segments on tmpfs; needs window × bitrate of memory |
| `ram_dir` | `"/dev/shm/kairec"` | where RAM segments go |
| `ram_keep_hours` | `6` | RAM footage of a finished session is dropped after this, or when the next session starts |

## `[buffer]` (defaults; profiles override under `buffer.*`)

| key | default | meaning |
|---|---|---|
| `minutes` | `30` | ring buffer length; the most a mark or save can reach back |
| `storage` | `"ram"` | `ram` or `disk`. RAM costs about 150 MB per minute at 20000 kbps |

## `[clips]` (defaults; profiles override under `clips.*`)

| key | default | meaning |
|---|---|---|
| `save_seconds` | `30` | what `kairec save` writes when `--last` is not given |
| `mark_before_seconds` | `1500` | a mark clip starts this long before the mark |
| `mark_after_seconds` | `300` | and ends this long after it. Marks inside that time join the same clip |
| `mark_clip_expire_hours` | `30` | mark clips are deleted after this unless starred. `0` = never |
| `mark_saves` | unset | timeline mode: also write a clip on every mark. Unset = on for RAM timelines, off for disk. Buffer mode always does |
| `event_clips` | `["kill:2", "mvp"]` | CS2 events that also write a clip: `kill:N` (N or more in the round), `mvp`, `death`, `round_win`, `round_loss`, `map_start`, `map_end` |

## `[retention]` (defaults; profiles override under `retention.*`)

| key | default | meaning |
|---|---|---|
| `max_gb` | `100` | per profile, sessions plus clips. Over this, the oldest unprotected item goes first |
| `max_days` | `30` | older sessions and clips are deleted. `0` = no limit |

Always deleted, regardless of the caps: a finished session with no footage left, no markers and
no clips. Never deleted: starred clips, the live session, sessions with starred markers, sessions whose
clips are still on disk. A pass runs a minute after start and every ten minutes; `kairec retention`
runs one now.

## `[feedback]`

| key | default | meaning |
|---|---|---|
| `sound` | `true` | a short tone on start, stop, a manual mark or save, and on an error. Automated marks and clips are silent |
| `volume` | `0.5` | 0 to 1 |

## `[daemon]`

| key | default | meaning |
|---|---|---|
| `ui_port` | `3556` | web UI and API |
| `gsi_port` | `3555` | where CS2 posts game state |
| `bind` | `"127.0.0.1"` | keep it local |
| `process_scan_seconds` | `1.0` | how often `/proc` is scanned for game processes while idle |

## `[gsi]`

| key | default | meaning |
|---|---|---|
| `cfg_dir` | `""` | CS2's `csgo/cfg` folder. Empty = found through Steam's library list |
| `capture_raw` | `true` | keep every payload in `gsi.jsonl` inside the session folder |

## `[[profiles]]`

| key | default | meaning |
|---|---|---|
| `name` | | used for the session folder and the clips subfolder |
| `game` | | id of a built-in game preset (`cs2`, `minecraft`, `black-ops-3`): display name and sidebar banner. A profile whose name equals a preset id or name resolves without it |
| `match` | | regex against the process name (`/proc/PID/comm`, 15 chars max). Recording starts when it appears and stops when it exits |
| `match_cmdline` | | regex against the full command line, for games whose process is `java` or similar |
| `mode` | `"timeline"` | `timeline` or `buffer` |
| `events` | `"none"` | `none` or `cs2-gsi` |
| `quality` | unset | a `[[quality]]` name; unset uses `capture.quality` |
| `fps` | unset | overrides the preset's fps |
| `timeline.*`, `buffer.*`, `clips.*`, `retention.*` | | overrides of the sections above |

A profile's RAM or disk need is shown in Settings next to what is available; a RAM timeline that
would not fit is refused at session start with a message in the log.

## Example

```toml
[capture]
monitor = "DP-1"
quality = "high"
audio = ["default_output"]

[[profiles]]
name = "cs2"
match = "^cs2$"
mode = "timeline"
events = "cs2-gsi"
timeline.window_minutes = 45
timeline.storage = "ram"
clips.event_clips = ["kill:3", "mvp"]

[[profiles]]
name = "minecraft"
match_cmdline = "net\\.minecraft|prismlauncher"
mode = "buffer"
quality = "medium"
buffer.minutes = 20
clips.mark_before_seconds = 600
clips.mark_after_seconds = 60
```
