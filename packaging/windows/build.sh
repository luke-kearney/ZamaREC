#!/usr/bin/env bash
# Builds the Windows bundle from Linux (or from Windows in Git Bash): dist/windows/kairec-windows-x64.zip
#
#   kairec-daemon.exe   the daemon, tray icon and hotkeys; one self-contained exe, no .NET install needed
#   kairec.exe          the command line (optional; the hotkeys and tray cover everyday use)
#   ffmpeg.exe          the recorder and exporter (gyan.dev's "essentials" build: NVENC, AMF, QSV, ddagrab)
#   ffprobe.exe
#   README.txt          how to set it up
#
# The same source builds the Linux install (install.sh); nothing here changes it.
set -euo pipefail
cd "$(dirname "$0")/../.."

OUT=${1:-dist/windows}
FFMPEG_URL=${KAIREC_FFMPEG_URL:-https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip}
CACHE=${KAIREC_FFMPEG_ZIP:-$HOME/.cache/kairec/ffmpeg-release-essentials.zip}

rm -rf "$OUT"
mkdir -p "$OUT/stage/kairec"

echo ">> publishing daemon (win-x64, single file)"
dotnet publish src/Kairec.Daemon -c Release -r win-x64 -o "$OUT/daemon" --nologo -v q
echo ">> publishing cli (win-x64, single file)"
dotnet publish src/Kairec.Cli -c Release -r win-x64 -o "$OUT/cli" --nologo -v q
cp "$OUT/daemon/kairec-daemon.exe" "$OUT/cli/kairec.exe" "$OUT/stage/kairec/"

echo ">> ffmpeg"
if [ ! -s "$CACHE" ] || ! unzip -tq "$CACHE" >/dev/null 2>&1; then
  mkdir -p "$(dirname "$CACHE")"
  curl -L --retry 3 --fail -o "$CACHE" "$FFMPEG_URL"
fi
rm -rf "$OUT/ffmpeg" && mkdir -p "$OUT/ffmpeg"
unzip -q "$CACHE" -d "$OUT/ffmpeg"
FF=$(ls -d "$OUT"/ffmpeg/ffmpeg-*/ | head -1)
cp "$FF/bin/ffmpeg.exe" "$FF/bin/ffprobe.exe" "$OUT/stage/kairec/"
cp "$FF/LICENSE" "$OUT/stage/kairec/ffmpeg-LICENSE.txt"
cp packaging/windows/README.txt "$OUT/stage/kairec/README.txt"

echo ">> zipping"
rm -f "$OUT/kairec-windows-x64.zip"
if command -v zip >/dev/null; then (cd "$OUT/stage" && zip -q -r ../kairec-windows-x64.zip kairec)
else (cd "$OUT/stage" && python3 -m zipfile -c ../kairec-windows-x64.zip kairec); fi
ls -la "$OUT/kairec-windows-x64.zip"
echo "done: $OUT/kairec-windows-x64.zip"
