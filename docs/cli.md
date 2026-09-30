# CLI

`kairec` is a small NativeAOT binary that talks to the daemon over a Unix socket. It starts in a
few milliseconds, which is what makes it usable as a hotkey target.

```
kairec mark [label...]            drop a marker on the current session
kairec save [--last 30s] [label]  write the last N seconds to a clip (30s, 2m, 90)
kairec start [profile]            start a session by hand (works even while paused)
kairec stop                       stop the current session
kairec status                     one line: state, profile, elapsed, window, markers, GSI age
kairec pause [session|2h|forever] auto recording off; stops the current session
kairec resume                     auto recording on; a running game is picked up at once
kairec ui [--browser|--app]       open the web UI in its own window, or bring it to the front if it is open
kairec top                        live view until Ctrl+C: CPU/RAM per process, storage, write rate
kairec log [-n 40] [kind]         recent events; kinds: session mark clip export purge config daemon error
kairec retention                  run the retention pass now and list what it removed
kairec reload                     re-read config.toml after a hand edit
kairec config                     print the config file path
```

Exit codes: 0 ok, 1 the daemon said no (message printed), 2 usage, 3 daemon not running.

## Hotkeys on KDE Plasma

*System Settings → Keyboard → Shortcuts → Add New → Command or Script*, then the full path,
because shortcuts run outside your shell profile:

```
/home/you/.local/bin/kairec mark
/home/you/.local/bin/kairec save
```

Assign keys with *Add custom shortcut*. A `kairec pause` binding makes a handy kill switch.

Other desktops: anything that can run a command on a global shortcut works the same way.

## Environment

`KAIREC_SOCKET` points the CLI (and a daemon) at a different control socket. Use it whenever you
run a second daemon for testing, or the two will fight over the socket file.
