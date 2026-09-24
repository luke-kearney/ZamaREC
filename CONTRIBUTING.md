# Contributing

Thanks for looking. A few things that keep this project what it is:

- **No GPU polling.** Nothing may query the GPU, NVML or the driver periodically. The recorder is
  the only thing that touches the GPU. This is not negotiable; it is why the project exists.
- **Nothing takes focus.** No windows, no notifications that can pop over a game. Sound is the only
  feedback during play. A browser opens only when the user clicks.
- **Hot path stays cold.** While a game runs, the daemon does a `/proc` scan and a playlist mtime
  check per second. Anything heavier belongs after the session or on an explicit request.
- **Verify recorder flags** against `gpu-screen-recorder --help` and its man page; do not assume.

Before a PR: `dotnet build` and `dotnet test` pass, UI changes were looked at in a browser, and
anything that touches the recorder was tried against a real session (`docs/development.md` shows
how to run a scratch daemon). Keep commits focused and say what you verified.

Bug reports: include `kairec log -n 50`, `kairec status`, the profile from your config, and the
gpu-screen-recorder and ffmpeg versions.
