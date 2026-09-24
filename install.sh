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
