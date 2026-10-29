#!/usr/bin/env bash
# Builds the Windows bundle from Linux (or from Windows in Git Bash): dist/windows/zamarec-windows-x64.zip
#
#   zamarec-daemon.exe   the daemon, tray icon and hotkeys; one self-contained exe, no .NET install needed
#   zamarec.exe          the command line (optional; the hotkeys and tray cover everyday use)
#   ffmpeg.exe          the recorder and exporter (gyan.dev's "essentials" build: NVENC, AMF, QSV, ddagrab)
#   ffprobe.exe
#   README.txt          how to set it up
#   com.zamarec.deck.streamDeckPlugin   the Stream Deck plugin (packaging/opendeck); opening it installs it
#
# The same source builds the Linux install (install.sh); nothing here changes it.
set -euo pipefail
cd "$(dirname "$0")/../.."

OUT=${1:-dist/windows}
FFMPEG_URL=${ZAMAREC_FFMPEG_URL:-https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip}
CACHE=${ZAMAREC_FFMPEG_ZIP:-$HOME/.cache/zamarec/ffmpeg-release-essentials.zip}
# CI sets the release version; a local build keeps Directory.Build.props' MAJOR.MINOR.0-dev.
VERSION_ARG=${ZAMAREC_VERSION:+-p:Version=$ZAMAREC_VERSION}

rm -rf "$OUT"
mkdir -p "$OUT/stage/zamarec"

echo ">> publishing daemon (win-x64, single file)"
dotnet publish src/ZamaRec.Daemon -c Release -r win-x64 -o "$OUT/daemon" --nologo -v q $VERSION_ARG
echo ">> publishing cli (win-x64, single file)"
dotnet publish src/ZamaRec.Cli -c Release -r win-x64 -o "$OUT/cli" --nologo -v q $VERSION_ARG
cp "$OUT/daemon/zamarec-daemon.exe" "$OUT/cli/zamarec.exe" "$OUT/stage/zamarec/"

echo ">> ffmpeg"
if [ ! -s "$CACHE" ] || ! unzip -tq "$CACHE" >/dev/null 2>&1; then
  mkdir -p "$(dirname "$CACHE")"
  curl -L --retry 3 --fail -o "$CACHE" "$FFMPEG_URL"
fi
rm -rf "$OUT/ffmpeg" && mkdir -p "$OUT/ffmpeg"
unzip -q "$CACHE" -d "$OUT/ffmpeg"
FF=$(ls -d "$OUT"/ffmpeg/ffmpeg-*/ | head -1)
cp "$FF/bin/ffmpeg.exe" "$FF/bin/ffprobe.exe" "$OUT/stage/zamarec/"
cp "$FF/LICENSE" "$OUT/stage/zamarec/ffmpeg-LICENSE.txt"
cp packaging/windows/README.txt "$OUT/stage/zamarec/README.txt"
# The Stream Deck plugin: a .streamDeckPlugin is the plugin folder zipped; opening it installs it.
STAGE=$(cd "$OUT/stage/zamarec" && pwd)
(cd packaging/opendeck && python3 -m zipfile -c "$STAGE/com.zamarec.deck.streamDeckPlugin" com.zamarec.deck.sdPlugin)

echo ">> zipping"
rm -f "$OUT/zamarec-windows-x64.zip"
if command -v zip >/dev/null; then (cd "$OUT/stage" && zip -q -r ../zamarec-windows-x64.zip zamarec)
else (cd "$OUT/stage" && python3 -m zipfile -c ../zamarec-windows-x64.zip zamarec); fi
ls -la "$OUT/zamarec-windows-x64.zip"
echo "done: $OUT/zamarec-windows-x64.zip"
