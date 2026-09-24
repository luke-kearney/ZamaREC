# Installing

kairec has been used on one machine: CachyOS (Arch), KDE Plasma 6 on Wayland, an NVIDIA GPU. Everything
below the Arch section is a best-effort guide from package listings, not from testing. If you get it
running elsewhere, a note in an issue with what you did would be welcome.

## What it needs

`install.sh` builds from source and installs under `~/.local`, so the build tools are needed on the
machine that runs it. At run time the daemon finds its helpers on `PATH` by name: `gpu-screen-recorder`,
`ffmpeg`, `pw-play`.

| what | why | version |
|---|---|---|
| systemd user sessions | the `kairec` and `kairec-tray` user services | any recent |
| PipeWire | audio capture (`default_output`, `default_input`, per-app), and `pw-play` for the feedback tones | any recent |
| gpu-screen-recorder | capture and GPU encoding. kairec uses its `-ipc` socket and `-write-first-frame-ts`, which need 6.x | 6.1 or newer |
| ffmpeg | clip export and probing; nvenc, vaapi or similar for frame-exact exports | 6 or newer |
| .NET 10 SDK | building the daemon, tray and CLI | 10.0 |
| clang and zlib headers | the CLI is NativeAOT, which links with clang | any recent |
| a StatusNotifier tray | the tray icon; KDE, GNOME with an extension, most others. Optional | |

The GPU side is whatever gpu-screen-recorder supports on your machine: NVIDIA with the proprietary
driver, AMD and Intel through VAAPI. Only NVIDIA has been tried with kairec.

## Arch, CachyOS, EndeavourOS, Manjaro

```sh
sudo pacman -S gpu-screen-recorder ffmpeg pipewire dotnet-sdk clang
git clone <this repo> && cd kairec
./install.sh
```

`dotnet-sdk` in the Arch repos tracks the current release; check `dotnet --version` reports 10.x. If
it does not, the `dotnet-sdk-10.0` package or Microsoft's install script (below) does.

## Fedora

gpu-screen-recorder is not in the Fedora repos. The maintained routes are the Flatpak from Flathub
and building from source; see the Flatpak note below, as the Flatpak needs a wrapper.

```sh
# RPM Fusion for a full ffmpeg (the Fedora one lacks the hardware encoders)
sudo dnf install https://mirrors.rpmfusion.org/free/fedora/rpmfusion-free-release-$(rpm -E %fedora).noarch.rpm
sudo dnf swap ffmpeg-free ffmpeg --allowerasing
sudo dnf install pipewire-utils dotnet-sdk-10.0 clang zlib-devel
```

`pipewire-utils` is the package with `pw-play`. `dotnet-sdk-10.0` is in Fedora's own repos on
current releases; if your release does not have it, use Microsoft's install script.

## Ubuntu and Debian

Same story for gpu-screen-recorder: Flatpak or source.

```sh
sudo apt install ffmpeg pipewire-bin clang zlib1g-dev
```

`pipewire-bin` provides `pw-play`. For .NET 10 on Ubuntu, the `dotnet-sdk-10.0` package comes from
the Ubuntu .NET backports PPA (`ppa:dotnet/backports`) on releases that do not ship it, or from
Microsoft's package feed; on Debian use Microsoft's feed or the install script:

```sh
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
export PATH="$HOME/.dotnet:$PATH"   # put this in your shell profile too
```

## NixOS

`gpu-screen-recorder`, `ffmpeg-full`, `pipewire`, `dotnet-sdk_10` and `clang` are all in nixpkgs. A
shell with those on `PATH` is enough for `install.sh`; wiring the user services into a NixOS module
has not been done.

## gpu-screen-recorder from Flatpak or source

**Flatpak.** The app id is `com.dec05eba.gpu_screen_recorder`. kairec runs the plain
`gpu-screen-recorder` command, so give it one: a wrapper on `PATH` that hands everything to the
Flatpak.

```sh
flatpak install flathub com.dec05eba.gpu_screen_recorder
mkdir -p ~/.local/bin
cat > ~/.local/bin/gpu-screen-recorder <<'EOF'
#!/bin/sh
exec flatpak run --command=gpu-screen-recorder com.dec05eba.gpu_screen_recorder "$@"
EOF
chmod +x ~/.local/bin/gpu-screen-recorder
```

Untested. The recorder has to reach kairec's IPC socket under `$XDG_RUNTIME_DIR`, the session folders
under the storage root, and `/dev/shm` for RAM timelines, all from inside the sandbox. If recording
starts and stops at once, the log will say which of those it could not open, and
`flatpak override --user --filesystem=...` for that path is the fix.

**Source.** The author's instructions are at <https://git.dec05eba.com/gpu-screen-recorder/about/>;
the build script installs to `/usr/local`. This avoids the sandbox questions entirely and is the
route to prefer on a machine you control.

## After installing

`install.sh` publishes the daemon and tray to `~/.local/share/kairec`, the CLI to `~/.local/bin/kairec`,
installs and starts the two user services, and puts the app icon and a desktop entry in place. Then
open <http://127.0.0.1:3556>, check the monitor in Settings, and bind `kairec mark` and `kairec save`
to keys. On KDE that is *System Settings → Keyboard → Shortcuts → Add New → Command*; on other
desktops any global-shortcut mechanism that can run a command works, since the CLI only talks to the
daemon over a Unix socket.

Nothing in kairec depends on KDE. The tray uses the StatusNotifier protocol over D-Bus, which GNOME
supports with the AppIndicator extension and most other desktops support natively.
