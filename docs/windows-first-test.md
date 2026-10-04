# Windows: first run on real hardware (2026-10-01/02)

The first time the Windows build ran outside Wine. This is what happened, what the tester's
`daemon.log` shows, what was changed in response, and what is still to do.

**Machine:** Windows, AMD Radeon RX 9070 XT (no NVIDIA card), Steam on `G:`. Game: Rocket League
(Steam app 252950, no profile, so recorded as an unconfirmed game: timeline, 12 min window, "high"
preset, 60 fps, 20000 kbps). GPU load in game was reported as about 30%.

**State of the tree:** the two fixes below are on `port/windows`, uncommitted. The bundle the tester
has is the third one (built 00:06 on 2026-10-02).

## Timeline

| Time | Bundle | What happened |
|---|---|---|
| 23:56 | 1 | Recorder used `h264_nvenc` (the default `capture.gpu = "nvidia"`). ffmpeg: `Cannot load nvcuda.dll`. No recording. |
| 00:05 | 2 | GPU detection picked AMD. `ddagrab -> h264_amf` failed at the first frame: `SubmitInput() failed with error 18`. No recording. |
| 00:10 | 3 | AMD through `hwdownload,format=bgra,format=nv12`. Recording started, a save wrote a 27.7 s clip. Looked like about 5 fps. Recorder died after 84 s. |
| 00:14 | 3 | Tester switched the codec to AV1. Recording started, the save failed, recorder died after 43 s. |

## Findings

### 1. The default encoder was NVIDIA on every machine — fixed, confirmed

`capture.gpu` defaulted to `nvidia`, and the first-run config writes that value into the file.

**Fix (in the tree):** `capture.gpu` defaults to `auto`. `GpuVendor` reads the display adapters' PCI
vendor ids once at first use (registry display-adapter class on Windows, `/sys/class/drm` on Linux;
no driver or GPU call, never repeated). A configured vendor the machine does not have is replaced by
a detected one, so a config that already says `nvidia` is corrected without the user editing it.

**Confirmed by the log:** `capture.gpu is nvidia but this machine has amd: using the amd encoder`,
then `gpu encoder: amd (capture.gpu = amd; found: amd)`.

### 2. AMF refuses ddagrab's frames — worked around, workaround is suspect

`ddagrab` hands over D3D11 BGRA textures. NVENC takes them; `h264_amf` fails with
`SubmitInput() failed with error 18` (AMF_DIRECTX_FAILED) on bundled ffmpeg 9.0.2.

**Workaround (in the tree):** AMD records like Intel, downloading each frame to system memory and
converting it to NV12 on the CPU. It starts, but it puts a per-frame read-back and CPU conversion on
the hot path while a game runs, which the project's own constraints argue against. It was chosen
without first checking what the bundled ffmpeg offers.

**Better route, not yet tried:** the bundled ffmpeg has `scale_d3d11` (D3D11 scaling and format
conversion), `vpp_amf` (AMF's own converter) and `vsrc_amf` (AMF screen capture), plus an `amf`
hardware device type. `ddagrab -> scale_d3d11 (to NV12) -> h264_amf` should keep the frame on the
GPU end to end. Exact option names must be read from `ffmpeg -h filter=scale_d3d11` before use.

### 3. The recording looked like about 5 fps — cause not established

The log does not record ffmpeg's frame rate, so it cannot settle this. Two candidates:

- **Capture starved (more likely).** Finding 4 suggests the game runs in exclusive fullscreen. In
  that mode Desktop Duplication can receive only occasional updates, and `ddagrab` repeats the last
  frame to keep 60 fps. The file is 60 fps but the picture moves at a few frames a second.
- **The memory route is slow (less likely).** The read-back and conversion from finding 2 cannot
  keep up, so few frames are produced. GPU starvation is ruled out: load was about 30%.

**How to tell them apart:** the saved clip's frame rate (Explorer: Properties → Details → Frame
rate, or `ffprobe`). Around 60 means repeated frames, so the capture is starved. Around 5 means the
pipeline is the bottleneck.

Note that the NVIDIA path on Windows is equally untested on real hardware; this may not be an AMD
problem at all.

### 4. The recorder dies when capture access is lost — open, affects every Windows user

Both sessions ended with:

```
[Parsed_ddagrab_0] AcquireNextFrame failed: 887a0026
recorder exited unexpectedly; closing session
```

`887a0026` is `DXGI_ERROR_ACCESS_LOST`: Desktop Duplication loses the output on a display-mode
switch (a game entering or leaving exclusive fullscreen, alt-tab out of it, a resolution change, a
UAC prompt). `ddagrab` does not recover, ffmpeg exits, and the coordinator closes the session.

**To do:** on this error, restart ffmpeg and continue the same session (new segments appended, same
time base) instead of ending it. Retry for a few seconds, since the output is briefly unavailable
during the switch.

### 5. AV1 cannot be recorded into the timeline — open

With the codec set to AV1 the recorder started (`av1_amf`) but ffmpeg warned
`codec av1 is muxed as a private data stream`: MPEG-TS segments do not carry AV1 properly. The save
then failed with `Stream map '' matches no streams`.

**To do:** either reject AV1 for timeline recording at config validation with a clear message, or
write fragmented-MP4 HLS segments when the codec is AV1. Check whether the Linux recorder has the
same problem before deciding.

### 6. The default mark hotkey was taken — open, minor

Every start logged `hotkey ctrl+alt+m could not be registered (in use by another program?)`. Save
and UI hotkeys registered. The tester had no working mark key and no hint in the UI.

**To do:** show unregistered hotkeys in Settings → Hotkeys (and in the tray tooltip or menu), not
only in the log. Consider a less commonly taken default.

### 7. Things that worked

Steam launch detection (`RunningAppID`), the unconfirmed-game flow, WASAPI audio through named pipes
(two sources, 96 kHz and 48 kHz, mixed), the control socket (it bound on real Windows), banner
fetch, config edits from the UI, a save from the timeline (27.7 s clip written), the tray icon.

Still unverified: A/V sync over a long session, the app window and its raise, Explorer "reveal",
`zamarec.exe` against the socket, NVENC and Quick Sync on Windows.

## Bundle 4 (2026-10-04): no CPU step for AMD, and a log that says more

Not yet run by the tester. Built for findings 2 and 3; finding 4 (the recorder dying on
`ACCESS_LOST`) is **not** fixed in it, so the game should still be set to Borderless.

- **AMD on the GPU (finding 2).** The recorder now tries, in order: `ddagrab -> scale_d3d11=format=nv12
  -> h264_amf` (Direct3D 11's video processor converts the frame on the GPU), then
  `ddagrab -> hwmap=derive_device=amf -> vpp_amf=format=nv12 -> h264_amf` (AMF's own converter), and
  only if both fail to start the old system-memory route, with a warning. The log names the route:
  `video route: d3d11 nv12 (the frame stays on the GPU)`, or
  `video route d3d11 nv12 did not start (...); trying amf nv12`. Both chains are accepted by the
  bundled ffmpeg's parser; neither has run on an AMD card.
- **Frame rate in the log (finding 3).** `recorder: 59.9 fps over the last 30 s (... frames in all,
  N duplicated and N dropped by ffmpeg, speed 1x, ...)` after 10 s and then every 30 s, and
  `recorder ended (exit code N): ... fps on average` at the end. ddagrab repeats frames to hold 60, so
  a steady 60 here with a picture that still stutters means the capture is starved (exclusive
  fullscreen); a low number means the pipeline cannot keep up.
- **What ffmpeg sees.** ffmpeg's own description of the capture and the encoder (frame size, pixel
  format, rate) is now in the log, and the daemon logs its version, the Windows version and the
  ffmpeg build at start.

From the tester we need `daemon.log` after one session of a few minutes, and whether the clip looks
smooth.

## What to do next, in order

1. **Survive `ACCESS_LOST`** (finding 4): restart the recorder inside the session.
2. **Log the recorder's real frame rate** every so often from ffmpeg's `-progress` output, plus the
   count of duplicated frames if ffmpeg reports it. This makes finding 3 answerable from a log.
3. **AMD on the GPU end to end** (finding 2): try `scale_d3d11` to NV12 first, fall back to the
   memory route if ffmpeg exits at start, and log which one ran.
4. **AV1 and the timeline** (finding 5): reject or switch segment format.
5. **Hotkey feedback** (finding 6).
6. Commit the two fixes already in the tree, then merge `port/windows` into `master` once the
   Windows recorder holds a session.

## For the tester, with the bundle they already have

- Set Rocket League to **Borderless** instead of Fullscreen. This should stop the dropouts
  (finding 4) and, if the capture is being starved, the low frame rate too.
- Set the codec back to **H.264** (or HEVC).
- Pick another **mark hotkey** under Settings → Hotkeys.
- Report the saved clip's frame rate (finding 3), the monitor's resolution and refresh rate, and
  whether HDR is on.
- If anything fails, send `%LOCALAPPDATA%\zamarec\daemon.log` again.
