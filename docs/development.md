# Development

```sh
dotnet build                    # everything
dotnet test tests/ZamaRec.Tests  # config, segment index, GSI detector
./install.sh                    # publish + install services for this user
./packaging/windows/build.sh    # the Windows bundle: dist/windows/zamarec-windows-x64.zip (cross-published from Linux)
```

Both builds come from the same source; the Windows one swaps the recorder and a few system calls at run time
(`Platform.IsWindows`). See [windows.md](windows.md) for the mapping and what has and has not been verified there.

## Branches and releases

- Work on a feature branch and open a pull request into `master`. Every pull request is built and
  tested, and its run carries the Windows bundle as an artifact (kept 3 days), for trying the
  change on Windows before it is merged.
- A push to `master` publishes a release, `vMAJOR.MINOR.PATCH`, which becomes the latest release.
  The patch goes up by itself with every merge. For a new minor or major, raise `ZamaRecVersion`
  in `Directory.Build.props` in the pull request; the patch then starts again at 0.
- Builds carry their version: a release says e.g. `0.1.3`, a pull request build `0.1.3-pr.12`, a
  local build `0.1.0-dev`. The daemon logs it when it starts.
- Merge with a merge commit, not a squash or rebase.

The workflow is `.github/workflows/ci.yml`. Releases are only published once the tests and the
Windows bundle both passed.

Run a second daemon against a scratch config without disturbing the installed one:

```sh
export ZAMAREC_SOCKET=/tmp/zamarec-test.sock XDG_DATA_HOME=/tmp/zamarec-xdg
dotnet run --project src/ZamaRec.Daemon -- /path/to/test-config.toml   # set daemon.ui_port too
ZAMAREC_SOCKET=/tmp/zamarec-test.sock zamarec status
```

Without `ZAMAREC_SOCKET` the test daemon deletes the live daemon's socket on exit and the hotkeys
stop working. `XDG_DATA_HOME` keeps its log, stats and GSI token separate.

## Layout

See [architecture](architecture.md). The interesting files:

- `ZamaRec.Core/Recording/GsrArguments.cs`: every recorder flag, one place.
- `ZamaRec.Core/Sessions/SegmentIndex.cs`: playlist parsing and the segment index.
- `ZamaRec.Core/Export/ClipExporter.cs`: ffmpeg concat and the keyframe snapping.
- `ZamaRec.Core/Gsi/GsiEventDetector.cs`: pure function from snapshots to events.
- `ZamaRec.Daemon/RecordingCoordinator.cs`: the state machine; everything that touches the recorder, through `IRecorder`.
- `ZamaRec.Core/Recording/FfmpegRecorder.cs` + `AudioPipe.cs`: the Windows recorder (ffmpeg ddagrab, WASAPI over named pipes) and its time base.
- `ZamaRec.Daemon/WindowsShell.cs`: the Windows tray icon and hotkeys, plain Win32.
- `ZamaRec.Core/Processes/WindowsProcesses.cs`: process table, command lines from the PEB, Steam's `RunningAppID`.
- `tools/screenshots/`: the README screenshots. `demo-data.py <dir>` builds a demo library of made-up
  games with generated footage and a config for a daemon that never records; `shoot.mjs` drives a
  headless Chrome over that daemon's UI and saves the shots. See "Screenshots" below.
- `ZamaRec.Daemon/wwwroot/`: the UI, no build step; `app.js` player and timeline (and `icon()`, the inline SVG icons), `mixer.js` (levels of separate audio tracks), `settings.js` (the Settings and Games views),
  `pages.js` stats and log.
- `ZamaRec.Tray/`: StatusNotifierItem and dbusmenu by hand over Tmds.DBus.Protocol.

## Gotchas worth knowing

- gpu-screen-recorder writes a segment file only when it completes; anything that wants "the last
  N seconds" must wait for the covering segment.
- Replay saves have no first-frame stamp; buffer-mode clip markers are computed from the save time.
- ffmpeg's concat demuxer cannot input-seek; seek on the output side, snapped to a keyframe.
- Tmds.DBus.Protocol's `MessageWriter` is a struct. Pass it by `ref`; a copy in a lambda writes
  nothing and dbus-broker disconnects you for an "invalid body".
- Kestrel is told not to cache the UI files, so a republish shows on reload.

## Adding a game event source

Implement a detector like `GsiEventDetector` that turns whatever the game offers (an HTTP hook, a
log file) into `GsiEvent`s, feed them to `RecordingCoordinator.EventMarkAsync`, and add the
source's name to the profile `events` values. Marker types are free-form; add a colour in
`app.js` `COLORS` for new ones.

## Tests

`tests/ZamaRec.Tests/Fixtures/gsi_match.json` is a hand-written match: kills, a headshot, a death, a
spectating stretch where a teammate gets kills and dies, round results, MVP, game over. Real
captures from `gsi.jsonl` make better fixtures; keep the spectating case.

## Screenshots

The images in `docs/screenshots/` come from a throwaway daemon, never from real recordings:

```
D=$(mktemp -d)
python3 tools/screenshots/demo-data.py $D                       # about a minute; needs ffmpeg with NVENC
ZAMAREC_SOCKET=$D/zamarec.sock XDG_DATA_HOME=$D/data \
  src/ZamaRec.Daemon/bin/Debug/net10.0/ZamaRec.Daemon $D/config.toml &     # ports 4555/4556
google-chrome-stable --headless=new --mute-audio --autoplay-policy=no-user-gesture-required \
  --user-data-dir=$D/chrome --remote-debugging-port=9335 --window-size=1680,910 about:blank &
node tools/screenshots/shoot.mjs 9335 http://127.0.0.1:4556/ $D/shots
for f in $D/shots/*.png; do magick $f -quality 92 -define webp:method=6 docs/screenshots/$(basename $f .png).webp; done
```

Run `dotnet build src/ZamaRec.Daemon` first so the daemon serves the current UI, and stop both
background processes afterwards. The demo daemon uses its own socket, data dir and ports, so it
can run beside the real one.
