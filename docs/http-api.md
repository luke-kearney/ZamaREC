# HTTP API

Base: `http://127.0.0.1:3556/api`. JSON in and out, camelCase. No authentication; it is bound to
localhost. Everything the web UI does goes through these.

## Daemon

| method | path | notes |
|---|---|---|
| POST | `/control` | the CLI's request (`{ "cmd": "mark", "label": "..." }`, `save`, `start`, `stop`, `pause`, `resume`, `status`, ...) and its reply, snake_case as on the control socket; for clients that cannot reach the socket (the [deck plugin](deck-plugin.md) on Windows) |
| GET | `/status` | state, profile, mode, elapsed, window, markers, GSI age and map, usage (cpu %, rss, tmpfs bytes, cores), pause, profiles |
| POST | `/mark` | `{ "label": "..." }` |
| POST | `/pause` | `{ "mode": "session" \| "until" \| "forever", "hours": 2 }`; stops the current session |
| POST | `/resume` | |
| POST | `/retention/run` | returns what was removed |
| POST | `/ram/clear` | drops RAM footage of finished sessions |
| GET | `/events?limit=200&kind=clip` | tail of the event log |
| GET | `/stats` | lifetime totals, folder sizes, per-session rows |
| GET | `/processes` | your running processes (for the settings picker) |

## Config

| method | path | notes |
|---|---|---|
| GET | `/config` | path, raw text, structured config, pending flag, monitors, audio devices, applications playing audio, codecs, free space |
| PUT | `/config` | `{ "text": "<toml>" }`; validated, applied now or when the session ends |
| PUT | `/config/structured` | the structured config object; rewritten as TOML without comments |

## Games

| method | path | notes |
|---|---|---|
| GET | `/games` | the `[auto_games]` defaults in effect, and every Steam game seen launched: app id, name, launches, `state` (`profile`, `unconfirmed`, `ignored`), its profile, running/recording |
| POST | `/games/{appId}/keep` | make the game a profile with the settings it was recorded with, or give the app id to a profile of the same name |
| POST | `/games/{appId}/ignore` | add it to `auto_games.ignore`; a session recording it is stopped |
| DELETE | `/games/{appId}/ignore` | take it off the list |

## Sessions

| method | path | notes |
|---|---|---|
| GET | `/sessions` | list with profile, mode, map, times, marker and clip counts, live/ram/gone flags, `steamAppId`, `unconfirmed`, `audioTracks` (track names, when more than the mix) |
| GET | `/sessions/{id}` | details: segments with availability, window start/end, markers, clip details |
| GET | `/sessions/{id}/playlist.m3u8` | HLS playlist of the segments on disk (live or VOD) |
| GET | `/sessions/{id}/seg/{file}` | a segment, with range support |
| GET | `/sessions/{id}/audio/{n}/playlist.m3u8` | the same playlist for audio track `n` alone (0 is the mix); its segments are audio-only, remuxed on request |
| GET | `/sessions/{id}/audio/{n}/seg/{file}` | track `n` of one segment as MPEG-TS |
| GET | `/sessions/{id}/thumb?t=12` | a 240 px wide JPEG of the keyframe at second `t` of the timeline; 404 where there is no footage |
| DELETE | `/sessions/{id}` | remove a finished session (409 while it records). Its clips stay; `?clips=true` removes them too, except starred ones |
| POST | `/sessions/{id}/markers/{mid}/star` | toggle |
| POST | `/sessions/{id}/export` | `{ "start": s, "end": s, "label": "", "exact": false }`. Every export call also takes `"mix": { "game": 0.5, "mic": 1.5 }`: remake the first track from the separate tracks at these levels (0 to 4; a track left out counts as 1). Ignored on a single-track recording. `"maxMb": 10` makes a re-encoded copy that fits under that size instead (source `share`, one audio track); the reply carries `bytes` |
| POST | `/sessions/{id}/export-around` | `{ "markerId": "", "before": 10, "after": 5, "label": "", "exact": false }` |
| POST | `/sessions/{id}/export-batch` | `{ "type": "kill", "before": 10, "after": 5, "exact": false }` |

## Clips

| method | path | notes |
|---|---|---|
| GET | `/clips` | all clips with their sidecar data, newest footage first. `recordedAt` is when the footage was recorded, `createdAt` when the file was written; they differ for exports |
| GET | `/clips/{profile}/{file}` | the MP4, with range support |
| GET | `/clips/{profile}/{file}/audio/{n}` | audio track `n` alone as an audio-only MP4, with range support; made on first request and kept a few hours under the RAM dir |
| GET | `/clips/{profile}/{file}/thumb?t=12` | the same for a clip |
| POST | `/clips/{profile}/{file}/star` | toggle, or set with `?on=true\|false`; starring clears the expiry |
| PATCH | `/clips/{profile}/{file}` | `{ "label": "..." }`: rename (the file keeps its name) |
| POST | `/clips/{profile}/{file}/reveal` | show the clip in the file manager |
| POST | `/clips/{profile}/{file}/share/{target}` | `{ "message": "..." }`: send it to a share target, `discord` (`share.discord_webhook`) or the name of a `[[share.targets]]` module; re-encoded first if over the target's size limit. Runs in the background and returns a job: `{ "id", "target", "stage", "progress", "note", "url", "error", "file", "bytes", "reencoded" }`, `stage` being `encoding`, `sending`, `done`, `failed` or `cancelled` |
| GET | `/share/jobs/{id}` | how that job is going (kept for 30 minutes after it ends) |
| DELETE | `/share/jobs/{id}` | cancel it: a running module is killed |
| DELETE | `/clips/{profile}/{file}` | |
| POST | `/clips/{profile}/{file}/export` | cut a clip out of a clip: `{ "start", "end", "label", "exact" }` |

## Game state

`POST /gsi` on `daemon.gsi_port` is where CS2 posts. It expects `auth.token` to match the
generated cfg and returns 401 otherwise.

## Control socket

The CLI's protocol is one JSON object per connection on `$XDG_RUNTIME_DIR/zamarec.sock`:
`{ "cmd": "mark", "label": "..." }` → `{ "ok": true, "message": "...", "status": {...} }`.
Commands: `mark`, `save` (`seconds`), `start` (`profile`), `stop`, `status`, `pause` (`mode`,
`hours`), `resume`, `retention`, `reload`, `config`. Types are in `ZamaRec.Protocol`.
