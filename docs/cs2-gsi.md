# CS2 events (Game State Integration)

Valve's Game State Integration is an official feature: a cfg file tells the game to POST its state
as JSON to a URL. It is one-way, initiated by the game, read-only, and involves no injection or
memory reading. Tournament HUDs and streaming overlays use it.

## Setup

Set `events = "cs2-gsi"` on the profile (the default `cs2` profile has it). On startup the daemon
writes `gamestate_integration_zamarec.cfg` into CS2's `game/csgo/cfg` folder, found through Steam's
library list or `gsi.cfg_dir`. CS2 reads it at launch, so restart the game once. The cfg carries a
random token stored in `~/.local/share/zamarec/gsi-token`; payloads without it are rejected.

`zamarec status` shows "gsi Ns ago on de_map" once payloads arrive.

## Events

Every payload is timestamped on arrival on the monotonic clock, so markers land where the event
happened, within the game's own throttle of about 100 ms.

| marker type | when | data |
|---|---|---|
| `kill` | your round kill count goes up | `count` (kills this round), `headshot`; label 2k/3k/4k/ace |
| `death` | your health hits 0 or your death count goes up | `round` |
| `round_win` / `round_loss` | the round ends, compared to your team | `round`, `win_team` |
| `mvp` | your MVP count goes up | `mvps` |
| `map_start` / `map_end` | map name appears/changes, game over, or back to menu | `map` |

The map name goes into the session folder name.

## The spectating rule

When you are dead or watching a teammate, CS2 reports the observed player in the player block.
The detector only counts player events when the player block's steamid equals the provider's
steamid, so nothing your teammate does registers. Round results still use your team, remembered
from your last own payload. This is covered by tests over a fixture match that includes a
spectating stretch.

## Clips from events

`clips.event_clips` lists which events also write a clip around themselves, same before/after
window as a manual mark. Default `["kill:2", "mvp"]`: a round with two or more kills, and an MVP.
Entries: `kill:N`, `mvp`, `death`, `round_win`, `round_loss`, `map_start`, `map_end`.

`clips.event_clip_modes` limits those clips to some game modes, as CS2 names them in `map.mode`:
`competitive`, `rush`, `casual`, `deathmatch`, `scrimcomp2v2` (wingman) and so on. Settings offers
"competitive, premier and rush only"; empty means every mode. Events in other modes still become
markers. The log's `map` line shows the mode CS2 reported, which is how to find a mode's name
(Premier's has not been seen in a payload yet, so `premier` is a guess next to `competitive`).

## Raw capture

With `gsi.capture_raw` on, every payload is appended to `gsi.jsonl` in the session folder with its
arrival time. Useful for building test fixtures and for checking field names against a real match.
