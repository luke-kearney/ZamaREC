# Development

```sh
dotnet build                    # everything
dotnet test tests/Kairec.Tests  # config, segment index, GSI detector
./install.sh                    # publish + install services for this user
```

Run a second daemon against a scratch config without disturbing the installed one:

```sh
export KAIREC_SOCKET=/tmp/kairec-test.sock XDG_DATA_HOME=/tmp/kairec-xdg
dotnet run --project src/Kairec.Daemon -- /path/to/test-config.toml   # set daemon.ui_port too
KAIREC_SOCKET=/tmp/kairec-test.sock kairec status
```

Without `KAIREC_SOCKET` the test daemon deletes the live daemon's socket on exit and the hotkeys
stop working. `XDG_DATA_HOME` keeps its log, stats and GSI token separate.

## Layout

See [architecture](architecture.md). The interesting files:

- `Kairec.Core/Recording/GsrArguments.cs`: every recorder flag, one place.
- `Kairec.Core/Sessions/SegmentIndex.cs`: playlist parsing and the segment index.
- `Kairec.Core/Export/ClipExporter.cs`: ffmpeg concat and the keyframe snapping.
- `Kairec.Core/Gsi/GsiEventDetector.cs`: pure function from snapshots to events.
- `Kairec.Daemon/RecordingCoordinator.cs`: the state machine; everything that touches the recorder.
- `Kairec.Daemon/wwwroot/`: the UI, no build step; `app.js` player and timeline (and `icon()`, the inline SVG icons), `mixer.js` (levels of separate audio tracks), `settings.js` (the Settings and Games views),
  `pages.js` stats and log.
- `Kairec.Tray/`: StatusNotifierItem and dbusmenu by hand over Tmds.DBus.Protocol.

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

`tests/Kairec.Tests/Fixtures/gsi_match.json` is a hand-written match: kills, a headshot, a death, a
spectating stretch where a teammate gets kills and dies, round results, MVP, game over. Real
captures from `gsi.jsonl` make better fixtures; keep the spectating case.
