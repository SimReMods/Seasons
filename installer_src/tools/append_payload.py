#!/usr/bin/env python3
import os, shutil, struct, sys

MAGIC = b"SEASONS-PAYLOAD1"
assert len(MAGIC) == 16

def main():
    if len(sys.argv) != 4:
        sys.exit("usage: append_payload.py <SeasonsSetup.exe> <payload.zip> <out.exe>")
    exe, payload, out = sys.argv[1:]
    with open(exe, "rb") as f:
        f.seek(-24, os.SEEK_END) if os.path.getsize(exe) >= 24 else None
        if f.read(24)[8:] == MAGIC:
            sys.exit(f"{exe} already has a payload; use the plain published exe")
    size = os.path.getsize(payload)
    tmp = out + ".tmp"
    with open(tmp, "wb") as dst:
        with open(exe, "rb") as src:
            shutil.copyfileobj(src, dst, 1 << 20)
        with open(payload, "rb") as src:
            shutil.copyfileobj(src, dst, 1 << 20)
        dst.write(struct.pack("<q", size) + MAGIC)
    os.replace(tmp, out)
    os.chmod(out, 0o755)
    print(f"{out}: {os.path.getsize(out) / 1e6:.1f} MB (payload {size / 1e6:.1f} MB)")

if __name__ == "__main__":
    main()
