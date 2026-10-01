#!/usr/bin/env bash
# Builds and installs zamarec for the current user:
#   daemon  -> ~/.local/share/zamarec/daemon/   (systemd user service "zamarec")
#   cli     -> ~/.local/bin/zamarec             (NativeAOT, bind this to a KDE shortcut)
set -euo pipefail
cd "$(dirname "$0")"

DAEMON_DIR="$HOME/.local/share/zamarec/daemon"
BIN_DIR="$HOME/.local/bin"
UNIT_DIR="$HOME/.config/systemd/user"

# One-time move of an install made under the project's old name, kairec. Has to run before anything is
# published, so the old data dir can be renamed as a whole.
migrate_from_kairec() {
  local old_cfg="$HOME/.config/kairec" new_cfg="$HOME/.config/zamarec"
  local old_data="$HOME/.local/share/kairec" new_data="$HOME/.local/share/zamarec"
  [ -d "$old_cfg" ] || [ -d "$old_data" ] || [ -e "$UNIT_DIR/kairec.service" ] || [ -e "$BIN_DIR/kairec" ] || return 0
  if [ -e "$new_cfg" ] || [ -e "$new_data" ]; then
    echo "note: found both a kairec and a zamarec install; leaving the kairec one alone" >&2
    return 0
  fi
  local lock pid
  lock=$(readlink "$old_data/ui-profile/SingletonLock" 2>/dev/null || true); pid=${lock##*-}
  if [ -n "$pid" ] && [ -d "/proc/$pid" ]; then
    echo "error: close the kairec window first, its browser profile is about to move" >&2
    exit 1
  fi
  echo ">> migrating the kairec install to zamarec"
  systemctl --user disable --now kairec kairec-tray >/dev/null 2>&1 || true
  rm -f "$UNIT_DIR/kairec.service" "$UNIT_DIR/kairec-tray.service" "$BIN_DIR/kairec"
  [ -d "$old_cfg" ] && mv "$old_cfg" "$new_cfg"
  [ -d "$old_data" ] && mv "$old_data" "$new_data"
  # Recordings move only from the default places, where the config still names them literally.
  local cfg="$new_cfg/config.toml"
  if [ -f "$cfg" ]; then
    if grep -qxF 'root = "~/Videos/kairec"' "$cfg" && [ -d "$HOME/Videos/kairec" ] && [ ! -e "$HOME/Videos/zamarec" ]; then
      mv "$HOME/Videos/kairec" "$HOME/Videos/zamarec"
      sed -i 's|^root = "~/Videos/kairec"$|root = "~/Videos/zamarec"|' "$cfg"
    fi
    if grep -qxF 'ram_dir = "/dev/shm/kairec"' "$cfg" && [ ! -e /dev/shm/zamarec ]; then
      [ -d /dev/shm/kairec ] && mv /dev/shm/kairec /dev/shm/zamarec
      sed -i 's|^ram_dir = "/dev/shm/kairec"$|ram_dir = "/dev/shm/zamarec"|' "$cfg"
      # Sessions record where their segments are with the full path.
      local root; root=$(sed -n 's|^root = "\(.*\)"$|\1|p' "$cfg" | head -1); root="${root/#\~/$HOME}"
      find "$root/sessions" -maxdepth 2 -name session.json -exec sed -i 's|"/dev/shm/kairec/|"/dev/shm/zamarec/|' {} + 2>/dev/null || true
    fi
  fi
  rm -f "$HOME/.local/share/icons/hicolor/scalable/apps/kairec.svg" "$HOME"/.local/share/icons/hicolor/*/apps/kairec.png
  # Shortcut entries keep their file names (KDE binds the key to the file) and get the new command.
  local d
  for d in "$HOME"/.local/share/applications/*.desktop; do
    grep -q "^Exec=\(.*/\)\?kairec " "$d" 2>/dev/null && sed -i 's|^Exec=\(.*/\)\?kairec |Exec=\1zamarec |' "$d"
  done
  return 0
}
migrate_from_kairec

echo ">> publishing daemon"
dotnet publish src/ZamaRec.Daemon -c Release -o "$DAEMON_DIR.new" --nologo -v q
rm -rf "$DAEMON_DIR" && mv "$DAEMON_DIR.new" "$DAEMON_DIR"

echo ">> publishing tray"
TRAY_DIR="$HOME/.local/share/zamarec/tray"
dotnet publish src/ZamaRec.Tray -c Release -o "$TRAY_DIR.new" --nologo -v q
rm -rf "$TRAY_DIR" && mv "$TRAY_DIR.new" "$TRAY_DIR"

echo ">> publishing cli (NativeAOT)"
dotnet publish src/ZamaRec.Cli -c Release -r linux-x64 -o "$BIN_DIR" --nologo -v q
rm -f "$BIN_DIR"/zamarec.dbg

echo ">> installing icons and desktop entry"
ICON_DIR="$HOME/.local/share/icons/hicolor"
mkdir -p "$ICON_DIR/scalable/apps" "$HOME/.local/share/applications"
cp packaging/brand/icons/zamarec-icon.svg "$ICON_DIR/scalable/apps/zamarec.svg"
for s in 16 24 32 48 64 128 256 512; do
  mkdir -p "$ICON_DIR/${s}x${s}/apps" && cp "packaging/brand/icons/png/zamarec-icon-$s.png" "$ICON_DIR/${s}x${s}/apps/zamarec.png"
done
# The menu runs entries without the shell's PATH: point it at the CLI by its full path.
# The entry is installed under the name of the UI window's app id (what Chrome calls an --app window of
# 127.0.0.1): KWin finds a window's icon by that file name, and shows a generic Wayland "W" without it.
APP_ID="chrome-127.0.0.1__-Default"
rm -f "$HOME/.local/share/applications/zamarec.desktop"
sed "s|^Exec=zamarec |Exec=$BIN_DIR/zamarec |" packaging/zamarec.desktop > "$HOME/.local/share/applications/$APP_ID.desktop"
command -v kbuildsycoca6 >/dev/null && kbuildsycoca6 >/dev/null 2>&1 || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$ICON_DIR" 2>/dev/null || true
command -v update-desktop-database >/dev/null && update-desktop-database -q "$HOME/.local/share/applications" 2>/dev/null || true

echo ">> installing systemd user unit"
mkdir -p "$UNIT_DIR"
cp packaging/zamarec.service "$UNIT_DIR/zamarec.service"
cp packaging/zamarec-tray.service "$UNIT_DIR/zamarec-tray.service"
systemctl --user daemon-reload
systemctl --user enable zamarec zamarec-tray >/dev/null

if systemctl --user is-active --quiet zamarec; then
  systemctl --user restart zamarec
  echo ">> restarted zamarec"
else
  systemctl --user start zamarec
  echo ">> started zamarec"
fi
if systemctl --user is-active --quiet zamarec-tray; then systemctl --user restart zamarec-tray; else systemctl --user start zamarec-tray; fi
echo ">> tray running (Plasma may hide new icons under the tray's expander until you promote it)"
echo
echo "config:  ~/.config/zamarec/config.toml  (created on first start)"
echo "logs:    journalctl --user -u zamarec -f"
echo "cli:     $BIN_DIR/zamarec status"
case ":$PATH:" in *":$BIN_DIR:"*) ;; *) echo "note: $BIN_DIR is not on your PATH; use the full path in the KDE shortcut" ;; esac
