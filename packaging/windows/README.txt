kairec for Windows
==================

kairec records your games automatically, lets you drop markers with a hotkey,
and cuts clips from the recording afterwards in a browser-based timeline.

Setting it up
-------------
1. Unzip this folder anywhere, for example C:\Program Files\kairec or your
   Desktop. Keep the four exe files together: kairec-daemon.exe needs
   ffmpeg.exe and ffprobe.exe next to it.
2. Double-click kairec-daemon.exe. It appears as an icon in the system tray
   (bottom right, maybe behind the ^ arrow). Nothing else opens.
3. Right-click the tray icon -> Open kairec. That is the timeline, the clips,
   and Settings. Tick "Start with Windows" in the same menu if you want it
   to always be there.
4. In Settings, check the monitor to record and pick your GPU (nvidia is the
   default; amd and intel are wired up but untested). Add your games under
   Games: a game is matched by its process name (cs2, valheim, ...).

Using it
--------
- Steam games start recording by themselves. Games without a profile are
  recorded too and listed under Games so you can keep or ignore them.
- Ctrl+Alt+M drops a marker. Ctrl+Alt+S saves the last 30 seconds as a clip.
  Ctrl+Alt+K opens the window. Change these under Settings -> Hotkeys.
- Feedback is a short sound; kairec never pops anything up over a game.
- Recordings live in Videos\kairec. Old ones are deleted by the retention
  settings (100 GB / 30 days by default).

Where things are
----------------
config:   %APPDATA%\kairec\config.toml
log:      %LOCALAPPDATA%\kairec\daemon.log
videos:   %USERPROFILE%\Videos\kairec

Differences from the Linux build
--------------------------------
- Recording uses ffmpeg with Desktop Duplication straight into the GPU
  encoder. Audio comes from WASAPI: "what you hear" and the microphone, or
  a named device. Per-application audio is not available on Windows.
- "Buffer" profiles record a rolling timeline of the same length instead of
  a RAM ring buffer; marks and saves work the same. RAM storage is disk
  storage here.
- Windows 10 version 1803 or newer is needed (for the control socket the
  kairec.exe command line uses).

kairec.exe (optional command line, from a terminal in this folder):
  kairec status | mark [label] | save --last 60s | ui | pause | resume | log

ffmpeg is a separate program by its own authors (see ffmpeg-LICENSE.txt),
built by gyan.dev.
