#!/usr/bin/env bash
# Generated patterns only. The wire decoder receives the same RDM2 representation as a Host stream.
set -euo pipefail
cd "$(dirname "$0")/.."
raw=$(mktemp)
trap 'rm -f "$raw"' EXIT
ffmpeg -hide_banner -loglevel error -f lavfi -i 'testsrc2=size=640x360:rate=24' \
    -frames:v 48 -c:v libx264 -threads 1 -profile:v main -pix_fmt yuv420p -preset ultrafast -tune zerolatency \
    -x264-params 'keyint=48:min-keyint=48:scenecut=0:cabac=1:aud=1:repeat-headers=1' -f h264 -y "$raw"
mkdir -p src/RelaxDesk.Android/Assets
python3 - "$raw" src/RelaxDesk.Android/Assets/probe.rdm2 <<'PY'
import pathlib, re, struct, sys
raw = pathlib.Path(sys.argv[1]).read_bytes()
offsets = [m.start() for m in re.finditer(rb"\x00\x00(?:\x00)?\x01\x09", raw)]
if len(offsets) != 48 or offsets[0] != 0:
    raise SystemExit("Unexpected AVC access unit boundaries")
offsets.append(len(raw))
with open(sys.argv[2], "wb") as output:
    for n, (start, end) in enumerate(zip(offsets, offsets[1:])):
        sample = raw[start:end]
        output.write(struct.pack("<IHHIiiiiiqqqqq", 0x324D4452, 2, 2, int(n == 0),
                                 640, 360, 0, len(sample), 1, n + 1, n * 10_000_000 // 24, 0, 0, 1))
        output.write(sample)
PY
