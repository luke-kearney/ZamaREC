# Configuration reference

The file is `~/.config/zamarec/config.toml`. It is created with defaults on first start. Edit it in
the web UI's **Settings** page or by hand; after a hand edit run `zamarec reload`. Changes apply
immediately, or when the current session ends if one is running. Only `daemon.*` ports and bind
address need a daemon restart.

Every key below is optional. Profiles override the global sections under the same names
(`timeline.*`, `buffer.*`, `clips.*`, `retention.*`); a blank override means "use the global value".

## `[storage]`

| key | default | meaning |
|---|---|---|
| `root` | `"~/Videos/zamarec"` | holds `sessions/` and `clips/` |
| `footage_dir` | not set | where a timeline's segments go, one folder per session; not set = inside the session's folder under `root`. A plain folder on any drive: a RAM disk on Windows (which has no tmpfs, and where buffer mode and `storage = "ram"` do not exist), or a second disk. Clips, markers and session details stay under `root`. A session whose footage folder has gone keeps its markers and clips and shows "footage gone". If the folder cannot be created when a session starts, that session's footage goes under `root` and the log says so |

## `[capture]`

| key | default | meaning |
|---|---|---|
| `monitor` | `"DP-1"` | name from `gpu-screen-recorder --list-monitors`, or `"screen"` for the first. Windows: the display number, `"0"` for the first |
| `gpu` | `"auto"` | `auto`, `nvidia`, `amd` or `intel`: whose encoder and decoder ffmpeg uses for exports, and on Windows for recording too. `auto` takes the maker of the machine's own graphics card (a discrete one before an integrated one), and a maker the machine does not have is replaced the same way. Only NVIDIA has been tried |
| `quality` | `"high"` | the `[[quality]]` preset used by profiles that do not pick their own |
| `audio` | `["default_output"]` | recorder audio sources merged into the recording's audio: `default_output`, `default_input`, `device:NAME`, `app:NAME`, `app-inverse:NAME` |
| `multitrack` | `false` | also record the `[[capture.tracks]]` as separate audio tracks |
| `cursor` | `false` | draw the pointer into the recording |
| `grabber` | `"gfxcapture"` | Windows only: how the screen is captured. `gfxcapture` (Windows.Graphics.Capture) records each frame at the time it was displayed, at most `fps` of them a second, which stays smooth when the game does not run at a multiple of `fps`; nothing is recorded while the screen is completely still. `ddagrab` (Desktop Duplication) records an even `fps`, repeating a frame where none arrived, and is what the recorder falls back to if `gfxcapture` does not start |
| `grab_fps` | `0` | Windows only: how often the screen is looked at. `0` leaves it to the grabber (four times `fps` for `gfxcapture`, `fps` for `ddagrab`) |
| `fps`, `codec`, `bitrate_kbps`, `keyframe_seconds`, `scale` | 60, h264, 20000, 1.0, native | raw encoder values; presets fall back to these for fields they leave blank |

### `[[capture.tracks]]`: separate audio tracks

Off by default. With `multitrack = true` a recording's first audio track is still the mix of
`capture.audio`, which is the track every player plays, and each entry here follows it as a track
of its own: for setting levels afterwards, or for an editor. Sessions and clips remember the track
names, and clips and exports keep every track, named.

| key | meaning |
|---|---|
| `name` | shown in the mixer and written into clips as the track name. `mix` is taken by the first track |
| `sources` | sources merged into this track, the same forms as `capture.audio` |

```toml
[capture]
audio = ["default_output", "default_input"]
multitrack = true
[[capture.tracks]]
name = "game"
sources = ["app-inverse:WEBRTC VoiceEngine"]   # everything except Discord's voice chat
[[capture.tracks]]
name = "mic"
sources = ["default_input"]
[[capture.tracks]]
name = "discord"
sources = ["app:WEBRTC VoiceEngine"]
```

`app:NAME` is one application and `app-inverse:NAME` everything except it, matched without case
against the names `gpu-screen-recorder --list-application-audio` prints. One track takes either
kind, not both: the recorder will not start with a mix of them, so the settings page refuses to
save one. To leave several applications out, give each its own `app-inverse:`. Discord's voice chat
appears there as `WEBRTC VoiceEngine` once Discord is open; its other sounds, like those of any
Electron app, come under `Chromium` and stay in the game track. An application that is
not running when the session starts is picked up when it appears. The mix holds what plays on the
default output; an application sent to another device is in its own track but not in the mix.
Each extra track costs a little CPU while recording: four tracks measured 8 to 11% of one core
against 5% for one. At most 8 tracks.

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
| `ram_dir` | `"/dev/shm/zamarec"` | where RAM segments go |
| `ram_keep_hours` | `6` | RAM footage of a finished session is dropped after this, or once the next session has recorded for 10 s |

## `[buffer]` (defaults; profiles override under `buffer.*`)

| key | default | meaning |
|---|---|---|
| `minutes` | `30` | ring buffer length; the most a mark or save can reach back |
| `storage` | `"ram"` | `ram` or `disk`. RAM costs about 150 MB per minute at 20000 kbps |

## `[clips]` (defaults; profiles override under `clips.*`)

| key | default | meaning |
|---|---|---|
| `save_seconds` | `30` | what `zamarec save` writes when `--last` is not given |
| `mark_before_seconds` | `1500` | a mark clip starts this long before the mark |
| `mark_after_seconds` | `300` | and ends this long after it. Marks inside that time join the same clip |
| `mark_clip_expire_hours` | `30` | mark clips are deleted after this unless starred. `0` = never |
| `mark_saves` | unset | timeline mode: also write a clip on every mark. Unset = on for RAM timelines, off for disk. Buffer mode always does |
| `event_clips` | `["kill:2", "mvp"]` | CS2 events that also write a clip: `kill:N` (N or more in the round), `mvp`, `death`, `round_win`, `round_loss`, `map_start`, `map_end` |
| `event_clip_modes` | `[]` | CS2 modes (`map.mode`) in which `event_clips` write clips, e.g. `["competitive", "premier", "rush"]`. Empty = every mode. Markers are set in every mode |

## `[retention]` (defaults; profiles override under `retention.*`)

| key | default | meaning |
|---|---|---|
| `max_gb` | `100` | per profile, sessions plus clips. Over this, the oldest unprotected item goes first |
| `max_days` | `30` | older sessions and clips are deleted. `0` = no limit |

Always deleted, regardless of the caps: a finished session with no footage left, no markers and
no clips. Never deleted: starred clips, the live session, sessions with starred markers, sessions whose
clips are still on disk. A pass runs a minute after start and every ten minutes; `zamarec retention`
runs one now.

## `[hotkeys]` (Windows only)

| key | default | meaning |
|---|---|---|
| `mark` | `"ctrl+alt+m"` | drops a marker. `ctrl`, `alt`, `shift`, `win` plus a letter, digit, F-key or key name (`space`, `home`, `pause`, `numpad0`); `""` turns it off |
| `save` | `"ctrl+alt+s"` | saves the last `save_seconds` |
| `ui` | `"ctrl+alt+k"` | opens or raises the ZamaREC window |

On Linux the daemon registers nothing; bind the desktop's shortcuts to the `zamarec` CLI instead (see [CLI](cli.md)).
On Windows a buffer profile records a timeline of `buffer.minutes` and `storage = "ram"` means disk; see [Windows](windows.md).

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
| `ui_window` | `"app"` | how the menu entry, the tray and `zamarec ui` open the UI: `app` for a window of its own, `browser` for a tab in the default browser |
| `process_scan_seconds` | `1.0` | how often `/proc` is scanned for game processes while idle |

## `[gsi]`

| key | default | meaning |
|---|---|---|
| `cfg_dir` | `""` | CS2's `csgo/cfg` folder. Empty = found through Steam's library list |
| `capture_raw` | `true` | keep every payload in `gsi.jsonl` inside the session folder |

## `[share]`

| key | default | meaning |
|---|---|---|
| `discord_webhook` | unset | a Discord webhook address (Server Settings → Integrations → Webhooks). With it set, a clip's Share menu can post the clip to that channel. It is a secret: anyone who has it can post there. Only `discord.com` addresses are accepted |
| `discord_max_mb` | `10` | the largest file the channel takes: 10 without Nitro or boosts. A clip over it is re-encoded to fit before it is sent |

### `[[share.targets]]`: share modules

A share module is a program of your own that uploads a clip to some other service; each entry adds a
*Send to …* line to the Share menu. [share-modules.md](share-modules.md) has what a module is given
and what it answers.

| key | default | meaning |
|---|---|---|
| `name` | required | what the Share menu calls it; unique, and not `discord` |
| `command` | required | the module: an executable, run directly without a shell. `~/` is expanded |
| `args` | `[]` | arguments given to it |
| `max_mb` | `0` | the largest file the service takes; a clip over it is re-encoded to fit first. `0` sends the clip as it is |
| `settings` | empty | a table of strings handed to the module untouched: its address, a token. Tokens are secrets |

Nothing is ever sent on its own; a clip goes out only when you click a *Send to* entry.

A share-size copy is always a re-encode on the GPU (NVENC, decoded and scaled on the GPU too): the
bitrate is what fits the size over the clip's length, and the picture steps down from the source
through 1080p60, 720p60, 720p30, 540p, 480p to 360p until that bitrate is enough for it. A clip too
long for the size even at 360p is refused. It carries one audio track: the mix, or the mixer's
levels when exporting with them.

## `[auto_games]`

A Steam game with no profile (an "unconfirmed" game) is still recorded: a RAM timeline with the
defaults below, so nothing reaches the disk unless you mark or save. The Games page turns one into
a real profile or puts it on the ignore list.

| key | default | meaning |
|---|---|---|
| `enabled` | `true` | record unconfirmed games. `false` only lists them on the Games page |
| `ram_gb` | `2` | RAM the rolling window may use; its length follows from the preset's bitrate (24 min at 10000 kbps) |
| `quality` | unset | a `[[quality]]` name; unset uses `capture.quality` |
| `ignore` | `[]` | Steam app ids never recorded this way. Runtimes, Proton, SteamVR and Wallpaper Engine are always skipped |

Detection reads Steam's launch wrapper from `/proc` (`reaper SteamLaunch AppId=N`) in the same scan
that looks for profile processes, and the name from the app's manifest in the Steam library.
It waits 30 seconds before recording an unconfirmed game, so a profile's process has time to
appear; if one appears later still, the session switches to that profile. Non-Steam shortcuts are
not recorded this way.

## `[[profiles]]`

| key | default | meaning |
|---|---|---|
| `name` | | used for the session folder and the clips subfolder |
| `game` | | id of a built-in game preset (`cs2`, `minecraft`, `black-ops-3`): supplies the banner when nothing else does. A profile whose name equals a preset id or name resolves without it |
| `steam_app_id` | | the Steam app this profile is for. With no `match` or `match_cmdline`, recording starts when Steam launches the app and stops when its whole process tree is gone. It also supplies the banner (Steam's header art); blank, the daemon uses the app id it saw the game launched with, or searches the Steam store for the profile name once |
| `banner_url` | | any image URL as the banner, for games not on Steam. An image uploaded on the settings page beats every other source |
| `match` | | regex against the process name (`/proc/PID/comm`, 15 chars max). Recording starts when it appears and stops when it exits. A profile needs `match`, `match_cmdline` or `steam_app_id` |
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
