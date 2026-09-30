#!/usr/bin/env bash
# Builds and installs kairec for the current user:
#   daemon  -> ~/.local/share/kairec/daemon/   (systemd user service "kairec")
#   cli     -> ~/.local/bin/kairec             (NativeAOT, bind this to a KDE shortcut)
set -euo pipefail
cd "$(dirname "$0")"

DAEMON_DIR="$HOME/.local/share/kairec/daemon"
BIN_DIR="$HOME/.local/bin"
UNIT_DIR="$HOME/.config/systemd/user"

echo ">> publishing daemon"
dotnet publish src/Kairec.Daemon -c Release -o "$DAEMON_DIR.new" --nologo -v q
rm -rf "$DAEMON_DIR" && mv "$DAEMON_DIR.new" "$DAEMON_DIR"

echo ">> publishing tray"
TRAY_DIR="$HOME/.local/share/kairec/tray"
dotnet publish src/Kairec.Tray -c Release -o "$TRAY_DIR.new" --nologo -v q
rm -rf "$TRAY_DIR" && mv "$TRAY_DIR.new" "$TRAY_DIR"

echo ">> publishing cli (NativeAOT)"
dotnet publish src/Kairec.Cli -c Release -r linux-x64 -o "$BIN_DIR" --nologo -v q
rm -f "$BIN_DIR"/kairec.dbg

echo ">> installing icons and desktop entry"
ICON_DIR="$HOME/.local/share/icons/hicolor"
mkdir -p "$ICON_DIR/scalable/apps" "$HOME/.local/share/applications"
cp packaging/brand/kairec-icon.svg "$ICON_DIR/scalable/apps/kairec.svg"
for s in 16 24 32 48 64 128 256 512; do
  mkdir -p "$ICON_DIR/${s}x${s}/apps" && cp "packaging/brand/png/kairec-icon-$s.png" "$ICON_DIR/${s}x${s}/apps/kairec.png"
done
# The menu runs entries without the shell's PATH: point it at the CLI by its full path.
# The entry is installed under the name of the UI window's app id (what Chrome calls an --app window of
# 127.0.0.1): KWin finds a window's icon by that file name, and shows a generic Wayland "W" without it.
APP_ID="chrome-127.0.0.1__-Default"
rm -f "$HOME/.local/share/applications/kairec.desktop"
sed "s|^Exec=kairec |Exec=$BIN_DIR/kairec |" packaging/kairec.desktop > "$HOME/.local/share/applications/$APP_ID.desktop"
command -v kbuildsycoca6 >/dev/null && kbuildsycoca6 >/dev/null 2>&1 || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$ICON_DIR" 2>/dev/null || true
command -v update-desktop-database >/dev/null && update-desktop-database -q "$HOME/.local/share/applications" 2>/dev/null || true

echo ">> installing systemd user unit"
mkdir -p "$UNIT_DIR"
cp packaging/kairec.service "$UNIT_DIR/kairec.service"
cp packaging/kairec-tray.service "$UNIT_DIR/kairec-tray.service"
systemctl --user daemon-reload
systemctl --user enable kairec kairec-tray >/dev/null

if systemctl --user is-active --quiet kairec; then
  systemctl --user restart kairec
  echo ">> restarted kairec"
else
  systemctl --user start kairec
  echo ">> started kairec"
fi
if systemctl --user is-active --quiet kairec-tray; then systemctl --user restart kairec-tray; else systemctl --user start kairec-tray; fi
echo ">> tray running (Plasma may hide new icons under the tray's expander until you promote it)"
echo
echo "config:  ~/.config/kairec/config.toml  (created on first start)"
echo "logs:    journalctl --user -u kairec -f"
echo "cli:     $BIN_DIR/kairec status"
case ":$PATH:" in *":$BIN_DIR:"*) ;; *) echo "note: $BIN_DIR is not on your PATH; use the full path in the KDE shortcut" ;; esac
