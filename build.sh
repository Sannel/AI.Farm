#!/usr/bin/env bash
# Builds the Godot client (Linux release) and publishes it to ~/Downloads/AI Farm.
#
#   ./build.sh                       build and publish
#   OUT=/some/folder ./build.sh      publish somewhere else
#   GODOT=/path/to/godot ./build.sh  use a different Godot (.NET/mono) binary
#
# The export goes to a temporary folder first, so a failed build leaves the published copy as it
# was. Publishing replaces only the build's own files (AIFarm.x86_64, AIFarm.pck, the data_*
# folder) and rewrites the licenses folder from the license files in this repo; anything else
# in the output folder is left alone.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$ROOT/src/Client/ai-farm"
OUT="${OUT:-$HOME/Downloads/AI Farm}"
PRESET="Linux"
NAME="AIFarm"

GODOT="${GODOT:-$HOME/Apps/Godot/Godot_v4.7.2-stable_mono_linux.x86_64}"
if [[ ! -x "$GODOT" ]]; then
	GODOT="$(command -v godot-mono || command -v godot || true)"
fi
[[ -n "$GODOT" && -x "$GODOT" ]] || { echo "Can't find Godot (.NET). Set GODOT=/path/to/godot." >&2; exit 1; }

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

echo "==> Compiling the C# project"
dotnet build "$PROJECT" --nologo -v quiet

echo "==> Importing assets"
"$GODOT" --headless --path "$PROJECT" --import >/dev/null 2>&1 || true

echo "==> Exporting the '$PRESET' release"
"$GODOT" --headless --path "$PROJECT" --export-release "$PRESET" "$STAGE/$NAME.x86_64" 2>&1 \
	| grep -E "ERROR|error:|SCRIPT ERROR" || true
for expected in "$NAME.x86_64" "$NAME.pck"; do
	[[ -s "$STAGE/$expected" ]] || { echo "The export didn't produce $expected." >&2; exit 1; }
done
compgen -G "$STAGE/data_*" >/dev/null || { echo "The export didn't produce its data_ folder." >&2; exit 1; }

echo "==> Publishing to $OUT"
mkdir -p "$OUT"
rm -rf "$OUT/$NAME.x86_64" "$OUT/$NAME.pck" "$OUT"/data_*
cp -a "$STAGE/$NAME.x86_64" "$STAGE/$NAME.pck" "$OUT/"
cp -a "$STAGE"/data_* "$OUT/"

# Licenses of what's bundled, named after the thing they cover.
rm -rf "$OUT/licenses"
mkdir -p "$OUT/licenses"
while IFS='|' read -r source target; do
	if [[ -f "$ROOT/$source" ]]; then
		cp "$ROOT/$source" "$OUT/licenses/$target"
	else
		echo "    (no $source, so no '$target')" >&2
	fi
done <<'LICENSES'
LICENSE|AI Farm - Apache-2.0.txt
src/Client/ai-farm/assets/kaykit_forest/License.txt|KayKit Forest Nature Pack - License.txt
src/Client/ai-farm/assets/kaykit_farmers/License.txt|KayKit Mystery Monthly Series 6 - License.txt
src/Client/ai-farm/assets/kaykit_animations/License.txt|KayKit Character Animations - License.txt
src/Client/ai-farm/assets/FarmHouse/License.txt|KayKit Dungeon Pack - License.txt
src/Client/ai-farm/assets/fonts/OFL.txt|Lilita One - OFL.txt
LICENSES

echo "==> Done: $(du -sh "$OUT" | cut -f1) in $OUT"
ls -1 "$OUT"
