#!/usr/bin/env bash
# Regenerates all raster icons from assets/dbclient-icon.svg
# (sizes <= 32px use the flat assets/dbclient-icon-small.svg variant).
#
# Outputs:
#   assets/dbclient-icon-256.png, assets/dbclient-icon-512.png
#   assets/png/dbclient-icon-{16,32,48,64,128,256}.png
#   src/dbclient/Assets/app-icon.ico   (multi-size: 16,32,48,64,128,256)
#   src/dbclient/Assets/app-icon-small.png (32px flat variant for the title bar)
#
# Requires ONE of: rsvg-convert, inkscape, ImageMagick (magick/convert),
# or python3 with cairosvg. ICO assembly uses python3 + Pillow if present,
# otherwise ImageMagick.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
svg="$here/dbclient-icon.svg"
smallsvg="$here/dbclient-icon-small.svg"   # flat variant used for 16/32px
pngdir="$here/png"
ico="$root/src/dbclient/Assets/app-icon.ico"
sizes=(16 32 48 64 128 256)

mkdir -p "$pngdir"

render() { # render <size> <out.png>
  local s="$1" out="$2" svg="$svg"
  if [ "$s" -le 32 ] && [ -f "$smallsvg" ]; then svg="$smallsvg"; fi
  if command -v rsvg-convert >/dev/null; then
    rsvg-convert -w "$s" -h "$s" "$svg" -o "$out"
  elif command -v inkscape >/dev/null; then
    inkscape "$svg" -w "$s" -h "$s" -o "$out" >/dev/null 2>&1
  elif command -v magick >/dev/null; then
    magick -background none -density 384 "$svg" -resize "${s}x${s}" "$out"
  elif command -v convert >/dev/null; then
    convert -background none -density 384 "$svg" -resize "${s}x${s}" "$out"
  elif python3 -c 'import cairosvg' 2>/dev/null; then
    python3 -c "import cairosvg,sys; cairosvg.svg2png(url=sys.argv[1], write_to=sys.argv[2], output_width=int(sys.argv[3]), output_height=int(sys.argv[3]))" "$svg" "$out" "$s"
  else
    echo "No SVG rasterizer found (rsvg-convert, inkscape, magick, convert, or python3+cairosvg)." >&2
    exit 1
  fi
}

for s in "${sizes[@]}"; do
  render "$s" "$pngdir/dbclient-icon-$s.png"
  echo "wrote $pngdir/dbclient-icon-$s.png"
done
render 256 "$here/dbclient-icon-256.png"
# Flat small variant used by the in-app title bar (18px logical; 32px covers 2x DPI).
render 32 "$root/src/dbclient/Assets/app-icon-small.png"
echo "wrote $root/src/dbclient/Assets/app-icon-small.png"
render 512 "$here/dbclient-icon-512.png"

if python3 -c 'import PIL' 2>/dev/null; then
  python3 - "$pngdir" "$ico" "${sizes[@]}" <<'PY'
import sys
from PIL import Image
pngdir, ico, *sizes = sys.argv[1:]
sizes = [int(s) for s in sizes]
# Pillow drops any requested size larger than the base image, so start from the largest.
sizes = sorted(sizes, reverse=True)
imgs = [Image.open(f"{pngdir}/dbclient-icon-{s}.png").convert("RGBA") for s in sizes]
imgs[0].save(ico, format="ICO", sizes=[(s, s) for s in sizes], append_images=imgs[1:])
print(f"wrote {ico}")
PY
elif command -v magick >/dev/null; then
  magick "${sizes[@]/#/$pngdir/dbclient-icon-}" "$ico" 2>/dev/null || \
  magick $(for s in "${sizes[@]}"; do echo "$pngdir/dbclient-icon-$s.png"; done) "$ico"
  echo "wrote $ico"
else
  echo "Cannot assemble ICO: need python3+Pillow or ImageMagick." >&2
  exit 1
fi
