#!/usr/bin/env bash
# Builds and installs kairos for the current user:
#   daemon  -> ~/.local/share/kairos/daemon/   (systemd user service "kairos")
#   cli     -> ~/.local/bin/kairos             (NativeAOT, bind this to a KDE shortcut)
set -euo pipefail
cd "$(dirname "$0")"

DAEMON_DIR="$HOME/.local/share/kairos/daemon"
BIN_DIR="$HOME/.local/bin"
UNIT_DIR="$HOME/.config/systemd/user"

echo ">> publishing daemon"
dotnet publish src/Kairos.Daemon -c Release -o "$DAEMON_DIR.new" --nologo -v q
rm -rf "$DAEMON_DIR" && mv "$DAEMON_DIR.new" "$DAEMON_DIR"

echo ">> publishing cli (NativeAOT)"
dotnet publish src/Kairos.Cli -c Release -r linux-x64 -o "$BIN_DIR" --nologo -v q
rm -f "$BIN_DIR"/kairos.dbg

echo ">> installing systemd user unit"
mkdir -p "$UNIT_DIR"
cp packaging/kairos.service "$UNIT_DIR/kairos.service"
systemctl --user daemon-reload
systemctl --user enable kairos >/dev/null

if systemctl --user is-active --quiet kairos; then
  systemctl --user restart kairos
  echo ">> restarted kairos"
else
  systemctl --user start kairos
  echo ">> started kairos"
fi
echo
echo "config:  ~/.config/kairos/config.toml  (created on first start)"
echo "logs:    journalctl --user -u kairos -f"
echo "cli:     $BIN_DIR/kairos status"
case ":$PATH:" in *":$BIN_DIR:"*) ;; *) echo "note: $BIN_DIR is not on your PATH; use the full path in the KDE shortcut" ;; esac
