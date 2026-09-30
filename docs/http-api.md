# HTTP API

Base: `http://127.0.0.1:3556/api`. JSON in and out, camelCase. No authentication; it is bound to
localhost. Everything the web UI does goes through these.

## Daemon

| method | path | notes |
|---|---|---|
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
| POST | `/sessions/{id}/markers/{mid}/star` | toggle |
| POST | `/sessions/{id}/export` | `{ "start": s, "end": s, "label": "", "exact": false }` |
| POST | `/sessions/{id}/export-around` | `{ "markerId": "", "before": 10, "after": 5, "label": "", "exact": false }` |
| POST | `/sessions/{id}/export-batch` | `{ "type": "kill", "before": 10, "after": 5, "exact": false }` |

## Clips

| method | path | notes |
|---|---|---|
| GET | `/clips` | all clips with their sidecar data, newest footage first. `recordedAt` is when the footage was recorded, `createdAt` when the file was written; they differ for exports |
| GET | `/clips/{profile}/{file}` | the MP4, with range support |
| POST | `/clips/{profile}/{file}/star` | toggle; starring clears the expiry |
| DELETE | `/clips/{profile}/{file}` | |
| POST | `/clips/{profile}/{file}/export` | cut a clip out of a clip: `{ "start", "end", "label", "exact" }` |

## Game state

`POST /gsi` on `daemon.gsi_port` is where CS2 posts. It expects `auth.token` to match the
generated cfg and returns 401 otherwise.

## Control socket

The CLI's protocol is one JSON object per connection on `$XDG_RUNTIME_DIR/kairec.sock`:
`{ "cmd": "mark", "label": "..." }` → `{ "ok": true, "message": "...", "status": {...} }`.
Commands: `mark`, `save` (`seconds`), `start` (`profile`), `stop`, `status`, `pause` (`mode`,
`hours`), `resume`, `retention`, `reload`, `config`. Types are in `Kairec.Protocol`.
