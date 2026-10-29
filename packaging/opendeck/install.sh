#!/usr/bin/env bash
# Copies the ZamaREC deck plugin into OpenDeck's plugin folder (native or Flatpak install).
# OpenDeck loads plugins at start: restart it afterwards. The plugin runs on the system's node (22+ preferred).
set -euo pipefail
cd "$(dirname "$0")"
PLUGIN=com.zamarec.deck.sdPlugin
installed=0
for dir in "${XDG_CONFIG_HOME:-$HOME/.config}/opendeck/plugins" "$HOME/.var/app/me.amankhanna.opendeck/config/opendeck/plugins"; do
  [ -d "$dir" ] || continue
  rm -rf "${dir:?}/$PLUGIN.new" && cp -r "$PLUGIN" "$dir/$PLUGIN.new"
  rm -rf "${dir:?}/$PLUGIN" && mv "$dir/$PLUGIN.new" "$dir/$PLUGIN"
  echo ">> deck plugin installed in $dir (restart OpenDeck to load it)"
  installed=1
done
[ "$installed" = 1 ] || echo "no OpenDeck plugin folder found; start OpenDeck once, then run this again" >&2
command -v node >/dev/null || echo "note: node is not on PATH; OpenDeck needs it to run the plugin" >&2
