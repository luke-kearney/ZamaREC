# Troubleshooting

**`kairec` says the daemon is not running.** `systemctl --user status kairec` and
`journalctl --user -u kairec -n 50`. If the service is active but the socket is missing, something
else deleted `$XDG_RUNTIME_DIR/kairec.sock` (a second daemon without `KAIREC_SOCKET` set will do
that); restart the service.

**A low tone every second while the game runs.** The watcher sees the game but the session cannot
start, and it beeps on each attempt (it now backs off for a minute and logs why). Common cause: a
RAM timeline whose window × bitrate does not fit in tmpfs. The Settings page shows the need against
what is available; shorten the window, lower the bitrate, or use disk. `kairec log error` shows the
refusal.

**Nothing records when the game starts.** `kairec status` should say "recording" within a second.
Check the profile's match regex against the real process name: `pgrep -x cs2` or the process
picker in Settings. Check the switch in the header is on (`kairec resume`).

**A game I never set up was recorded.** Steam games without a profile are recorded to RAM with the
`[auto_games]` defaults and show a *new* badge. On the Games page, *Keep as profile* or *Never
record*; to turn the whole thing off, untick it under Settings → Unconfirmed games.

**My profile's game started as a "new" game.** Its process took longer than 30 s to appear after
Steam launched it. The session switches to the profile as soon as the process shows up, and from
then on kairec remembers that Steam app belongs to the profile. Setting `steam_app_id` on the
profile avoids it from the start.

**No CS2 events.** CS2 reads the cfg only at launch: restart the game. Check the cfg exists in
`.../Counter-Strike Global Offensive/game/csgo/cfg/` and that `daemon.gsi_port` matches its `uri`.
`kairec status` shows "gsi Ns ago" once payloads arrive. If Steam is in a non-standard place, set
`gsi.cfg_dir`.

**Markers are off from the video.** They should be exact in timeline mode. If they are not, look
at `live.m3u8.ts` in the session folder; if it is missing, the recorder did not write the
first-frame stamp and markers fall back to monotonic times only. In buffer mode a few hundred
milliseconds of error is expected.

**"Save" comes out shorter than asked.** The recorder writes a segment only when it completes;
a save waits for that, up to `segment_seconds`. Shorter segments make saves faster.

**The video will not play in the browser.** Timeline sessions need MediaSource (any current
Firefox or Chromium). hevc and av1 presets do not play in Firefox on Linux; use h264 for anything
you want to scrub in the UI. If nothing plays at all, not even a clip MP4, the browser's media stack
is at fault, not the daemon: try another browser.

**Playback flashes at a clip boundary in buffer mode.** Buffer clips are separate files; the
player switches files when one runs out. Marks close together are merged into one clip to reduce
this; timeline mode has no boundaries at all.

**The tray icon is missing.** While kairec is idle the icon reports itself as passive, so with
Plasma's default "Shown when relevant" it sits under the tray's expander arrow and comes out
when a recording starts, auto recording is off, or the daemon is down. To keep it on the panel
all the time: right-click the tray → Configure System Tray → Entries → kairec → "Always shown".
`systemctl --user status kairec-tray` for the service itself.

**Settings changes do not take effect.** They wait for the current session to end; the page
says so. Port and bind changes need `systemctl --user restart kairec`.

**High CPU?** The recorder costs roughly 9% of one core at 60 fps, about 0.6% of a 16-thread CPU.
The daemon should sit near 1%. `kairec top` shows both. Nothing in kairec touches the GPU driver;
if frame times suffer, compare with the recorder paused and report what you see.
