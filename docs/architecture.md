# Architecture

## Components

```
 game process ──▶ GameWatcherService ──▶ RecordingCoordinator ──▶ gpu-screen-recorder (child)
                     (/proc scan)              │  state machine        │ KMS capture, NVENC
                                               │                       ▼
 zamarec CLI / tray ──▶ ControlSocketServer ────┤              segments / RAM ring
   (Unix socket)                               │
 CS2 ──▶ POST /gsi ──▶ GsiService ─────────────┤
                                               ▼
 browser ──▶ Kestrel: static UI + /api ──▶ SessionCatalog, ClipExporter (ffmpeg), RetentionService
```

| project | role |
|---|---|
| `ZamaRec.Protocol` | request/response types for the control socket. No dependencies, AOT-safe |
| `ZamaRec.Core` | config (TOML), sessions and markers, segment index, recorder wrapper and its IPC, process watcher, clip exporter, GSI parser and detector, telemetry, sounds |
| `ZamaRec.Daemon` | the service: coordinator, watcher, socket, web API, GSI endpoint, retention, pause control, config hot reload, static web UI |
| `ZamaRec.Cli` | `zamarec`, NativeAOT so a hotkey costs milliseconds |
| `ZamaRec.Tray` | StatusNotifierItem and dbusmenu over D-Bus |
| `ZamaRec.Tests` | xunit: config, segment index, GSI detector over fixture payloads |

The daemon runs as the `zamarec` systemd user service, the tray as `zamarec-tray`.

## Capture

The recorder is spawned per session with flags built in `GsrArguments.cs`, verified against
gpu-screen-recorder 6.1's man page. It is driven over its `-ipc` JSON socket (`stop`,
`save-replay`, `start/stop-replay-recording`). `-write-first-frame-ts` makes it write a file with
the monotonic and wall-clock time of the first encoded frame; that is what makes marker times
exact.

- **Timeline mode** asks for HLS output: `-o live.m3u8` with `hls_time=<segment_seconds>`,
  `hls_list_size=<window/segment>` and `delete_segments`, so ffmpeg's muxer prunes the window.
  The daemon re-reads the playlist as it changes and keeps `segments.json`, an index of every
  segment ever seen with its start offset from the first frame. That index survives the window
  sliding and is what maps a marker time to a file and an offset.
- **Buffer mode** asks for a replay buffer (`-r <seconds> -replay-storage ram|disk`). `save-replay`
  dumps the last N seconds to an MP4.
- **RAM timelines** are ordinary timelines whose media directory is under tmpfs. Metadata stays in
  the session folder on disk. The coordinator refuses to start if window × bitrate exceeds the
  tmpfs free space, and drops the footage after `ram_keep_hours` or when the next session starts.

Nothing polls the GPU. The only periodic work while idle is a `/proc` directory scan once a second.

## Audio tracks and the mixer

With `capture.multitrack` the recorder gets one `-a` per track: the mix of `capture.audio` first,
then each configured track. Segments and clips carry all of them; every ordinary player, and the
viewer by default, plays the first.

A browser cannot select or mix the further tracks of a file, so the viewer's mixer fetches each
one separately. `StemService` makes them with ffmpeg stream copies, on request only: a timeline
segment's track is remuxed to audio-only MPEG-TS straight into the response (about 60 ms for a
5 s segment), a clip's track is copied once to an audio-only MP4 under `<ram_dir>/.stems` and
dropped after six hours unused. In the page (`mixer.js`) each track is an `<audio>` element fed
through a Web Audio gain. They follow the video: parked slightly ahead while it is paused or
seeking, then kept within a few milliseconds by nudging their playback rate. The video's own
sound is turned to zero once every track can play. None of this runs unless the mixer is on.

An export can carry those levels: ffmpeg scales each separate track, sums them (`amix` without
normalising, then a limiter) and encodes the result as the clip's first track in place of the
recorded mix. Video and the separate tracks are still copied, and the clip's sidecar records the
levels used (`mix_levels`).

## Share-size copies and Discord

A clip can be re-encoded to fit under a size (`ClipExporter.PlanShare` and `ShareAsync`): the byte
budget, less 6%, is spread over the length; audio takes 64 to 128 kbps and the rest is the video
bitrate; the picture steps down a ladder until the bitrate gives each pixel enough. The encode is
NVENC two-pass VBR with the frames decoded and scaled on the GPU (`-hwaccel cuda`, `scale_cuda`),
and the result is checked against the cap and redone at a lower bitrate if the encoder overshot.
`DiscordShare` uploads a clip to a webhook as one multipart request.

Sending is a background job (`ShareJobs`): fit the clip to the target's size limit, send it, write
the event log; the page asks how the job is going and can cancel it. A target is Discord or a share
module from `[[share.targets]]`: `ShareModule` (Core) runs the configured program with the clip and
the target's settings as JSON on stdin and reads progress and the result as JSON lines from stdout
(the contract is [share-modules.md](share-modules.md)). The daemon knows nothing about the services
behind the modules. All of it runs only on a click.

## Hover previews

`ThumbService` makes the timeline's hover thumbnails, one per request: ffmpeg seeks to the second,
decodes that single keyframe (`-skip_frame nokey -noaccurate_seek`) on one CPU thread at low
priority and writes a 240 px JPEG under `<ram_dir>/.thumbs`, kept six hours. About 0.1 s each, at
most two at a time, never on the GPU. The page only asks after the cursor has rested for 64 ms
and keeps what it has fetched, so moving across the bar costs nothing.

## What starts a session

The scan looks for two things. A process that a profile's `match` or `match_cmdline` fits starts
that profile. Otherwise Steam's launch wrapper (`reaper SteamLaunch AppId=N -- ...`, one per
running game, alive until the game's whole process tree is gone) is matched by app id: a profile
with that `steam_app_id` and no process match starts at once; an app no profile knows is an
unconfirmed game and, after a 30 s grace period, records with the `[auto_games]` defaults.
`SteamLaunchTracker` holds that decision. It leaves alone any launch that was running when a
session ended (a lingering launcher, a game stopped by hand) and any app a process-matched
profile has recorded before, which the daemon remembers in `games.json`.

While an unconfirmed session runs, the scan keeps looking for profile processes and hands the
session over when one appears. During a profiled session only that game's pid is checked.
Nothing here talks to the Steam client or needs its overlay; it is `/proc` and the library's
manifest files.

## Timing

Every marker records the monotonic clock at the moment it was requested, before any lock or I/O.
Its position on the timeline is `t_mono - first_frame_mono`. In buffer mode there is no first-frame
stamp (nothing is written until a save), so the session origin is the recorder spawn, and a clip's
markers are placed as `duration - (save_time - mark_time)`, accurate to a few hundred milliseconds.

The recorder writes a segment file only when it completes, so "save the last 30 s" waits for the
segment covering "now" to close, at most `segment_seconds`.

## Clips

Everything in `clips/<profile>/` is an MP4 with a `.json` sidecar (`ClipInfo`): source (`mark`,
`save`, `export`), the session and offset it came from, an expiry for mark clips, a starred flag,
and its own marker list. The web UI opens a clip like a session and can cut it again.

Mark clips: a mark schedules a job due `mark_after_seconds` later covering `[first mark - before,
last mark + after]`. Marks arriving while a job is pending join it. A mark whose before-window
reaches back into the last written clip extends that clip (both marks, one file, old file deleted)
as long as the buffer or window still holds the earlier footage.

Exports from a timeline concatenate segments with ffmpeg's concat demuxer. The demuxer cannot
input-seek, so `-ss` is an output option, snapped down to a keyframe so the copy starts cleanly; the
clip may begin up to one keyframe interval early.

Frame-exact exports re-encode only the head (`ClipExporter.SmartCutAsync`): the frames from the In
point up to the next keyframe, under a second at the default 1 s keyframes, joined to a stream copy of
the rest. That works because gpu-screen-recorder's H.264 has no B-frames and puts an IDR with its SPS
and PPS at every keyframe: the copy can start on one and end on any frame, and the head, encoded with
the same profile and `-bf 0`, gets the same SPS and PPS from NVENC (only the HRD rate differs). The
head is counted in frames rather than cut by time, because a segment's last frame can sit a fraction
of a millisecond past where the concat list places the next segment. The video goes through a second
concat list (head, then the source with `inpoint` on the keyframe, in that file's own timestamps);
the audio of the range is copied to a temporary file first, since the concat input cannot seek and an
output `-ss` would cut the video too. A source with B-frames or another codec, a range with no
keyframe inside, or a failed step falls back to re-encoding the whole range.

## Data on disk

```
~/Videos/zamarec/
  sessions/<date>_<time>_<profile>[_<map>]/
    session.json      profile, mode, timings, first-frame stamp, media_dir for RAM sessions
    markers.json      markers with t_video (seconds into the session)
    segments.json     the segment index (timeline mode)
    live.m3u8, live.m3u8.ts, seg_NNNNNN.ts   (timeline mode, disk storage)
    gsi.jsonl         raw CS2 payloads (if gsi.capture_raw)
  clips/<profile>/<date>_<time>[_label].mp4 (+ .json sidecar)
~/.local/share/zamarec/
  events.jsonl      the event log     stats.json   lifetime counters
  gsi-token         the GSI shared secret          pause.json   pause state
  sounds/           generated tones
~/.config/zamarec/config.toml
$XDG_RUNTIME_DIR/zamarec.sock, zamarec-gsr.sock
```

## Control paths

The CLI and tray talk newline-delimited JSON over `$XDG_RUNTIME_DIR/zamarec.sock`
(`ZAMAREC_SOCKET` overrides it). The web UI and any script use the HTTP API on `daemon.ui_port`.
CS2 posts to `/gsi` on `daemon.gsi_port` with a token from the generated cfg.

## Constraints the design keeps

- No periodic GPU, NVML or driver queries anywhere. The header's CPU/RAM figures and `zamarec top`
  read `/proc` only.
- Nothing opens a window or takes focus by itself. The UI window opens, or comes to the front, only
  when you ask: the menu entry, a tray click, `zamarec ui`.
- The GPU encoder is the only encoder; CPU fallback is disabled.
- Hotkeys are the desktop's own global shortcuts running the CLI, not key grabbing.
