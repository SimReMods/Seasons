#!/usr/bin/env python3
import argparse, datetime, hashlib, json, os, sys, zipfile

PRODUCT = "TheSimsSeasons"
NATIVE_EXT = (".dll", ".dylib", ".so")
IGNORED = {".DS_Store", "Thumbs.db", "desktop.ini", ".gitkeep"}
EDITIONS = ("steam", "ea", "cc")

def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()

def collect(root):
    out = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames.sort()
        for name in sorted(filenames):
            if name in IGNORED:
                continue
            full = os.path.join(dirpath, name)
            out.append((os.path.relpath(full, root).replace(os.sep, "/"), full))
    return out

def is_readme(rel):
    name = rel.lower()
    return "/" not in name and name.startswith("readme") and name.endswith(".txt")

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--files", required=True)
    ap.add_argument("--version", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--docs")
    ap.add_argument("--runtime", help="publish dir; its native libraries go into runtime/")
    ap.add_argument("--commit", default=os.environ.get("GITHUB_SHA"))
    ap.add_argument("--extra-zip", action="append", default=[], metavar="ZIP@EDITION")
    a = ap.parse_args()

    entries = {}

    def add_entry(rel, variant, size, digest, source, origin):
        key = (rel.lower(), variant)
        if key in entries:
            prev = entries[key]
            if prev["sha256"] != digest:
                print(f"warning: {rel} ({variant}) differs between {prev['origin']} and {origin}; using {prev['origin']}")
            return
        clash = (rel.lower(), None) if variant else next((k for k in entries if k[0] == rel.lower() and k[1]), None)
        if clash in entries:
            sys.exit(f"{rel} is both a common file ({entries[clash]['origin'] if variant else origin}) and edition-specific")
        entries[key] = dict(path=rel, variant=variant, size=size, sha256=digest, source=source, origin=origin)

    for rel, full in collect(a.files):
        add_entry(rel, None, os.path.getsize(full), sha256(full), ("file", full), "install_files")
    if not entries:
        sys.exit("no files under " + a.files)

    docs = []
    for spec in a.extra_zip:
        path, _, edition = spec.partition("@")
        if edition not in EDITIONS:
            sys.exit(f"--extra-zip needs ZIP@EDITION with edition one of {EDITIONS}: {spec}")
        with zipfile.ZipFile(path) as z:
            for info in z.infolist():
                rel = info.filename.replace("\\", "/")
                if info.is_dir() or rel.rsplit("/", 1)[-1] in IGNORED:
                    continue
                data = z.read(info)
                if is_readme(rel):
                    docs.append((f"docs/{edition}/{rel}", data))
                    continue
                add_entry(rel, edition, len(data), hashlib.sha256(data).hexdigest(),
                          ("zip", path, info.filename), os.path.basename(path))

    for edition in EDITIONS:
        mods = sorted(e["path"] for e in entries.values() if e["variant"] == edition and e["path"].lower().startswith("mods/"))
        if a.extra_zip:
            print(f"{edition}: {', '.join(mods) or 'no mods'}")

    files = sorted(entries.values(), key=lambda e: (e["path"].lower(), e["variant"] or ""))

    def manifest_file(e):
        f = {"path": e["path"], "size": e["size"], "sha256": e["sha256"]}
        if e["variant"]:
            f["variant"] = e["variant"]
        return f

    manifest = {
        "product": PRODUCT,
        "version": a.version,
        "commit": a.commit,
        "builtAt": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
        "files": [manifest_file(e) for e in files],
    }

    stamp = (1980, 1, 1, 0, 0, 0)

    def add(z, arcname, path=None, data=None):
        info = zipfile.ZipInfo(arcname, stamp)
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = 0o644 << 16
        if data is None:
            with open(path, "rb") as f, z.open(info, "w", force_zip64=True) as dst:
                for chunk in iter(lambda: f.read(1 << 20), b""):
                    dst.write(chunk)
        else:
            z.writestr(info, data)

    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    natives = []
    with zipfile.ZipFile(a.out, "w", compresslevel=6, allowZip64=True) as z:
        add(z, "manifest.json", data=json.dumps(manifest, indent=2))
        zips = {}
        for e in files:
            arc = ("variants/" + e["variant"] + "/" if e["variant"] else "files/") + e["path"]
            src = e["source"]
            if src[0] == "file":
                add(z, arc, src[1])
            else:
                zf = zips.setdefault(src[1], zipfile.ZipFile(src[1]))
                add(z, arc, data=zf.read(src[2]))
        for zf in zips.values():
            zf.close()
        if a.docs and os.path.isdir(a.docs):
            for rel, full in collect(a.docs):
                add(z, "docs/" + rel, full)
        for arc, data in docs:
            add(z, arc, data=data)
        if a.runtime:
            for name in sorted(os.listdir(a.runtime)):
                if name.lower().endswith(NATIVE_EXT):
                    add(z, "runtime/" + name, os.path.join(a.runtime, name))
                    natives.append(name)

    common = [f for f in manifest["files"] if "variant" not in f]
    print(f"payload {a.out}: version {a.version}, {len(common)} common files "
          f"({sum(f['size'] for f in common) / 1e6:.1f} MB), {len(files) - len(common)} edition files, "
          f"{len(docs)} mod readmes, runtime: {', '.join(natives) or 'none'}")

if __name__ == "__main__":
    main()
