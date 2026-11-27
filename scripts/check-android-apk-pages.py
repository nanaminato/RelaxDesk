import hashlib
from pathlib import Path
import shutil
import subprocess
import sys
from tempfile import TemporaryDirectory
import zipfile

if len(sys.argv) != 3:
    raise SystemExit("Usage: python3 scripts/check-android-apk-pages.py APK ZIPALIGN")
apk, zipalign = sys.argv[1:]
page = 16 * 1024
aligned = subprocess.run([zipalign, "-c", "-P", "16", "-v", "4", apk], check=False)
good = aligned.returncode == 0
with open(apk, "rb") as source:
    print("SHA256:", hashlib.file_digest(source, "sha256").hexdigest())
with TemporaryDirectory() as directory, zipfile.ZipFile(apk) as package:
    entries = [entry for entry in package.infolist() if entry.filename.startswith("lib/arm64-v8a/") and entry.filename.endswith(".so")]
    if not entries or len({entry.filename for entry in entries}) != len(entries):
        raise SystemExit("Missing/duplicate ARM64 shared libraries")
    print(f"ARM64 libraries: {len(entries)} ({sum(e.compress_type == zipfile.ZIP_STORED for e in entries)} ZIP-stored)")
    for entry in entries:
        path = Path(directory) / "library.so"
        with package.open(entry) as source, open(path, "wb") as output:
            shutil.copyfileobj(source, output)
        result = subprocess.run(["readelf", "-Wl", path], text=True, capture_output=True, check=True)
        loads, relros = [], []
        for line in result.stdout.splitlines():
            fields = line.split()
            if not fields or fields[0] not in ("LOAD", "GNU_RELRO"):
                continue
            _, offset, virt, _, _, size, *permissions, alignment = fields
            values = (int(offset, 16), int(virt, 16), int(size, 16), int(alignment, 16), "W" in "".join(permissions))
            (loads if fields[0] == "LOAD" else relros).append(values)
        load_ok = bool(loads) and all(alignment >= page and (virt - offset) % page == 0 for offset, virt, _, alignment, _ in loads)
        relro_ok = all(
            (virt + size) % page == 0 or
            any(start <= virt and start + load_size == virt + size for _, start, load_size, _, writable in loads if writable)
            for _, virt, size, _, _ in relros
        )
        good &= load_ok and relro_ok
        print(entry.filename, f"LOAD {len(loads)}: {'pass' if load_ok else 'FAIL'};",
              "align", ",".join(hex(s[3]) for s in loads),
              f"RELRO {len(relros)}: {'pass' if relro_ok else 'FAIL'}")
raise SystemExit(0 if good else 1)
