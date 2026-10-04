# Share modules

A share module is a program of your own that uploads a clip somewhere: your group's clip site, a
file host, anything with an API. ZamaREC knows nothing about the service. It runs the module when
you pick it in a clip's **Share** menu, hands it the clip and your settings, and shows what the
module reports. A module lives outside this repository and can be written in any language.

Nothing is ever sent on its own: a module runs only on a click in the Share menu.

## Setting one up

Under Settings → Sharing → *share modules*, or in `config.toml`:

```toml
[[share.targets]]
name = "Our clips"                  # the entry in the Share menu: "Send to Our clips"
command = "~/bin/clips-upload"      # the module: an executable file, run directly (no shell)
args = []                           # optional arguments, given before anything else
max_mb = 0                          # the largest file the service takes; 0 = send the clip as it is

[share.targets.settings]            # handed to the module untouched; the names are the module's own
url = "https://clips.example"
token = "..."
```

- A clip over `max_mb` is first re-encoded on the GPU to fit, like a Discord share, and that copy is
  kept as a clip of its own. With `max_mb = 0` the file is sent as recorded.
- `settings` values are strings; quote numbers. A token there is a secret: it is stored in
  `config.toml` in plain text and shown on the Settings page, like the Discord webhook.
- `name` must be unique and cannot be `discord`.
- A module whose file is missing stays in the menu, greyed out, with the reason.

## The contract

### Start

The module is started as `command args...` with its own folder as the working directory. No
arguments are added: everything arrives on **stdin** as one JSON object, after which stdin is
closed. Settings travel there, and never in the arguments or the environment, because those can be
read by other processes.

```json
{
  "version": 1,
  "file": "/home/you/Videos/zamarec/clips/cs2/2026-10-04_21-14-03_ace.mp4",
  "file_name": "2026-10-04_21-14-03_ace.mp4",
  "bytes": 48211934,
  "content_type": "video/mp4",
  "title": "ace on mirage",
  "profile": "cs2",
  "duration_seconds": 40.0,
  "recorded_at": "2026-10-04T21:14:03+02:00",
  "reencoded": false,
  "markers": [ { "t": 31.2, "type": "kill", "label": "AK-47 headshot", "data": { } } ],
  "settings": { "url": "https://clips.example", "token": "..." }
}
```

| field | meaning |
|---|---|
| `version` | the contract's version, `1`. New fields may appear without it changing; ignore what you do not know |
| `file` | the file to upload, absolute. Read it; do not change, move or delete it |
| `title` | the clip's name in the library; absent when it has none |
| `profile` | the profile it was recorded under, which is the game |
| `duration_seconds`, `recorded_at` | the clip's length and when the footage was recorded; either may be absent |
| `reencoded` | true when `file` is a copy made to fit `max_mb` |
| `markers` | the clip's markers, `t` in seconds from its start; `label` and `data` may be absent |
| `settings` | the target's `settings` table as written in the config |

### Reporting back

The module answers on **stdout**, one JSON object per line. Flush after each line.

```json
{"progress": 0.42, "note": "chunk 5 of 12"}
{"ok": true, "url": "https://clips.example/c/abc123"}
```

- `{"progress": 0..1, "note": "..."}`, as often as you like, either field alone is fine. The UI
  shows the percentage and the note while the upload runs.
- One final line: `{"ok": true, "url": "..."}` (`url` optional: the link to what was posted, which
  the UI copies to the clipboard) or `{"ok": false, "error": "why, in words for the user"}`.
- Lines that are not a JSON object are ignored. **stderr** goes to the daemon's log
  (`journalctl --user -u zamarec`), line by line: use it for diagnostics, never for the token.

Without a final line the share counts as failed: the UI then shows the exit code and the last line
of stderr.

### Cancel

The upload can be cancelled from the UI. The module and every process it started are killed at
once, with no warning, so a half-finished upload must be something the service can discard or
resume by itself.

### Limits

There is no time limit; a module that hangs is ended by cancelling. Modules run with your user's
rights and are not sandboxed: only install one you trust.

## A minimal module

```python
#!/usr/bin/env python3
import json, sys, urllib.request

req = json.load(sys.stdin)
s = req["settings"]

def say(**line):
    print(json.dumps(line), flush=True)

try:
    with open(req["file"], "rb") as f:
        r = urllib.request.Request(s["url"] + "/upload?name=" + req["file_name"], data=f.read(),
                                   headers={"authorization": "Bearer " + s["token"], "content-type": req["content_type"]})
        say(progress=0.0, note="uploading")
        answer = json.load(urllib.request.urlopen(r))
    say(ok=True, url=answer["url"])
except Exception as e:
    say(ok=False, error=str(e))
```

Try a module without ZamaREC by piping a request into it:

```sh
echo '{"version":1,"file":"/path/clip.mp4","file_name":"clip.mp4","bytes":123,"content_type":"video/mp4","profile":"test","markers":[],"settings":{"url":"...","token":"..."}}' | ./clips-upload
```
