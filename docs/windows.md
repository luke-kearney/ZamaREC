# Windows

One code base, two builds. The Linux build wraps gpu-screen-recorder; the Windows build drives ffmpeg. Everything
above the recorder (sessions, markers, clips, the web UI, exports, Discord, retention, Steam detection) is the same
code, with runtime branches where the system differs (`Platform.IsWindows`). Neither build is a fork of the other.

## The bundle

`packaging/windows/build.sh` publishes from Linux (or Git Bash) and produces `dist/windows/zamarec-windows-x64.zip`:

| file | what |
|---|---|
| `zamarec-daemon.exe` | daemon, tray icon, hotkeys; self-contained single file (no .NET install), no console, logs to a file |
| `zamarec.exe` | the command line; optional |
| `ffmpeg.exe`, `ffprobe.exe` | gyan.dev's "essentials" build (NVENC, AMF, QSV, ddagrab), downloaded once into `~/.cache/zamarec` |
| `README.txt` | the friend-facing instructions |

The daemon looks for ffmpeg next to itself (also `ffmpeg\bin`), then `ZAMAREC_FFMPEG_DIR`, then PATH.
Double-clicking the exe while a daemon is running opens the UI instead of starting a second one.

## How the pieces map

| Linux | Windows |
|---|---|
| gpu-screen-recorder, `-ipc` socket | ffmpeg: `ddagrab` (Desktop Duplication, D3D11 textures) straight into `h264_nvenc`; for `_amf` and `_qsv` the frame is downloaded and converted to NV12 on the CPU first, since neither takes the textures as they are; stop = `q` on stdin |
| `-write-first-frame-ts` | see "Time base" below |
| PipeWire sources (`default_output`, `app:NAME`, …) | WASAPI through NAudio: `default_output` (loopback), `default_input`, `device:NAME`; no per-application audio |
| replay buffer (buffer mode) | none: a buffer profile records a timeline as long as the buffer would be; marks and saves cut clips from it |
| tmpfs (`storage = "ram"`) | none: RAM storage is disk storage |
| `/proc` scan, `reaper SteamLaunch AppId=N` | process table (`Process.GetProcesses`), command lines read from the PEB when a profile needs them; Steam's running game from `HKCU\Software\Valve\Steam\RunningAppID`, reported as a launch with pid `-appId` |
| `SteamAppId` in the game's environment | `RunningAppID` again |
| Steam libraries under `~/.local/share/Steam` | `SteamPath` from the registry plus `libraryfolders.vdf` |
| `pw-play` | `winmm PlaySound` (no volume control, so the files are written at the configured volume) |
| KDE shortcuts → `zamarec` CLI | `[hotkeys]` in the config, registered by the daemon with `RegisterHotKey` |
| StatusNotifierItem tray (`ZamaRec.Tray`) | `Shell_NotifyIcon` tray inside the daemon (`WindowsShell.cs`): open, mark, save, recording on/off, folder, start with Windows, quit |
| journal | `%LOCALAPPDATA%\zamarec\daemon.log`, rolled at 5 MB |
| `~/.config/zamarec`, `~/.local/share/zamarec` | `%APPDATA%\zamarec`, `%LOCALAPPDATA%\zamarec` |
| Chrome `--app` window raised through a KWin script | Chrome, Edge or Brave `--app` window; raised by title with `SetForegroundWindow` |
| Unix socket in `$XDG_RUNTIME_DIR` | Unix socket in `%LOCALAPPDATA%\zamarec` (Windows 10 1803+); if it cannot be made the daemon keeps running without the CLI |
| `-hwaccel cuda`, `scale_cuda` for exports | the same for NVIDIA; `-hwaccel d3d11va` and a CPU `scale` for AMD and Intel (`capture.gpu`) |

## Time base

gpu-screen-recorder writes the first frame's monotonic time to `live.m3u8.ts`, and every marker is `t_mono - first_frame_mono`.
ffmpeg has no such file, so the Windows recorder fixes the time base itself:

- Before spawning ffmpeg it takes one instant **B** (monotonic and realtime). Every captured frame's pts is set from
  the wall clock relative to B: `setpts=(RTCTIME-B)/(1000000*TB)`. ddagrab's own pts start at its first frame,
  which is when the recorder (not the session) began.
- Each audio pipe starts with as much silence as passed between B and ffmpeg opening the pipe, and is thereafter
  padded or trimmed whenever it drifts more than 0.2 s from the wall clock (WASAPI loopback stops delivering when
  nothing plays; a silent stream on the same device keeps it going).
- So media time `t` is simply `B + t`, and the recorder writes `live.m3u8.ts` with B in the same format the Linux
  recorder uses. The session code does not know which recorder it has.

The first HLS segment therefore has video starting a few hundred ms after 0 (the time ffmpeg took to start).
Players and exports cope; the only visible effect is that a clip cut from the first seconds may start with a
frame of black.

## What was verified where

- On Linux under Wine 11 (no Desktop Duplication, no Unix sockets): the single-file exe unpacks and runs; the file
  log; the tray window and hotkey registration; the embedded UI; monitors from `EnumDisplayDevices`; audio devices
  from WASAPI; the process list with command lines from the PEB; profile detection (a `notepad` profile) through
  to spawning ffmpeg with the audio pipe delivering samples; clean failure and retry when ffmpeg exits.
- Not verified, needs a real Windows machine: Desktop Duplication capture itself, the encoders, A/V sync over a
  long session, `RunningAppID`, the Unix socket, Chrome/Edge app windows, Explorer's "reveal", the message-box on
  a missing ffmpeg. AMD and Intel paths are wired per ffmpeg's docs and untested.

## Testing on Windows

Unzip the bundle, run `zamarec-daemon.exe`, and check `%LOCALAPPDATA%\zamarec\daemon.log`. To try a recording without
a game: add a profile matching `notepad` under Games, open Notepad, wait for the start tone, press Ctrl+Alt+M a
few times, close Notepad. The session shows on the timeline; check that markers land where the clock says.
`zamarec.exe status` in a terminal in the bundle folder shows the daemon's state.

A scratch daemon (separate config, ports and data) works like on Linux: `zamarec-daemon.exe C:\path\config.toml`
with `ZAMAREC_SOCKET` set to another path.
