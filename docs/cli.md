# CLI

`zamarec` is a small NativeAOT binary that talks to the daemon over a Unix socket. It starts in a
few milliseconds, which is what makes it usable as a hotkey target.

```
zamarec mark [label...]            drop a marker on the current session
zamarec save [--last 30s] [label]  write the last N seconds to a clip (30s, 2m, 90)
zamarec start [profile|desktop]    start by hand (works even while paused): the running game, else the desktop
zamarec stop                       stop the current session; a game stopped this way stays unrecorded until it exits or you start again
zamarec status                     one line: state, profile, elapsed, window, markers, GSI age
zamarec pause [session|2h|forever] auto recording off; stops the current session
zamarec resume                     auto recording on; a running game is picked up at once
zamarec ui [--browser|--app]       open the web UI in its own window, or bring it to the front if it is open
zamarec top                        live view until Ctrl+C: CPU/RAM per process, storage, write rate
zamarec log [-n 40] [kind]         recent events; kinds: session mark clip export purge config daemon error
zamarec retention                  run the retention pass now and list what it removed
zamarec reload                     re-read config.toml after a hand edit
zamarec config                     print the config file path
```

`zamarec start` with no profile looks at what runs right then: a profile's game process, else a
game Steam launched (its profile, or the `[auto_games]` defaults), else it records the desktop
(`[desktop]` in the config). A game it finds is tied to its process like an automatic start: game
events work and the session ends when the game exits. `zamarec start desktop` records the desktop
even with a game running; `zamarec start cs2` names the profile (tied to its game when that runs).
Bind `zamarec start` and `zamarec stop` to keys to have a start/stop pair that always works.

Exit codes: 0 ok, 1 the daemon said no (message printed), 2 usage, 3 daemon not running.

## Hotkeys on KDE Plasma

*System Settings → Keyboard → Shortcuts → Add New → Command or Script*, then the full path,
because shortcuts run outside your shell profile:

```
/home/you/.local/bin/zamarec mark
/home/you/.local/bin/zamarec save
```

Assign keys with *Add custom shortcut*. A `zamarec pause` binding makes a handy kill switch.

Other desktops: anything that can run a command on a global shortcut works the same way.

## Environment

`ZAMAREC_SOCKET` points the CLI (and a daemon) at a different control socket. Use it whenever you
run a second daemon for testing, or the two will fight over the socket file.
