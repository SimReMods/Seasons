#!/usr/bin/env python3
import argparse, hashlib, os, shutil, stat, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import make_payload

STATE = "SeasonsSetup"

def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data if isinstance(data, bytes) else data.encode())

def snapshot(root, include_state=False):
    out = {}
    for dirpath, dirnames, filenames in os.walk(root):
        rel_dir = os.path.relpath(dirpath, root).replace(os.sep, "/")
        if not include_state and (rel_dir == STATE or rel_dir.startswith(STATE + "/")):
            continue
        if rel_dir != ".":
            out[rel_dir + "/"] = None
        for name in filenames:
            full = os.path.join(dirpath, name)
            with open(full, "rb") as f:
                out[os.path.relpath(full, root).replace(os.sep, "/")] = hashlib.sha256(f.read()).hexdigest()
    return out

def diff(a, b):
    lines = []
    for k in sorted(set(a) | set(b)):
        if k not in b:
            lines.append("  missing: " + k)
        elif k not in a:
            lines.append("  extra:   " + k)
        elif a[k] != b[k]:
            lines.append("  changed: " + k)
    return "\n".join(lines)

class Runner:
    def __init__(self, work):
        self.work = work
        self.failures = 0

    def build(self, exe, name, files, version, docs=None):
        pkg = os.path.join(self.work, name + ".zip")
        cmd = [sys.executable, os.path.join(HERE, "make_payload.py"), "--files", files, "--version", version, "--out", pkg]
        if docs:
            cmd += ["--docs", docs]
        subprocess.run(cmd, check=True)
        ext = ".exe" if exe.lower().endswith(".exe") else ""
        out = os.path.join(self.work, name + ext)
        subprocess.run([sys.executable, os.path.join(HERE, "append_payload.py"), exe, pkg, out], check=True)
        return out

    def cli(self, exe, *args, env=None, expect=0):
        e = dict(os.environ)
        e.pop("SEASONS_TEST_FAIL_AFTER", None)
        e.pop("SEASONS_TEST_CRASH_AFTER", None)
        e.update(env or {})
        p = subprocess.run([exe, "--cli", *args], capture_output=True, text=True, env=e, timeout=900)
        out = (p.stdout + p.stderr).strip()
        print(f"    $ {os.path.basename(exe)} --cli {' '.join(args[:1])} -> {p.returncode}: {out}")
        if p.returncode != expect:
            self.fail(f"exit code {p.returncode}, expected {expect}")
        return out

    def check(self, label, ok, detail=""):
        print(("  ok   " if ok else "  FAIL ") + label + ("" if ok else "\n" + detail))
        if not ok:
            self.failures += 1

    def expect_tree(self, label, game, expected):
        actual = snapshot(game)
        self.check(label, actual == expected, diff(expected, actual))

    def fail(self, msg):
        print("  FAIL " + msg)
        self.failures += 1

def synthetic(r, exe):
    print("== synthetic upgrade / rollback / recovery scenario")
    work = r.work
    v1 = os.path.join(work, "v1")
    v2 = os.path.join(work, "v2")
    write(f"{v1}/GameData/Objects/Trees.iff", "trees v1")
    write(f"{v1}/Downloads/Plants2/Plants2.iff", "plants2 v1")
    write(f"{v1}/ExpansionPack8/ExpansionPack8.far", "far " * 5000)
    write(f"{v1}/ExpansionPack8/sub/old.skn", "old skin")
    write(f"{v1}/GameData/Walls/w1.wll", "wall 1")
    write(f"{v2}/GameData/Objects/Trees.iff", "trees v2")
    write(f"{v2}/ExpansionPack8/ExpansionPack8.far", "far " * 5000)
    write(f"{v2}/ExpansionPack8/new.iff", "new object")
    write(f"{v2}/GameData/Walls/w1.wll", "wall 1")
    write(f"{v2}/GameData/Floors/Seasons/f1.flr", "floor")

    exe1 = r.build(exe, "setup_v1", v1, "1.0.0")
    exe2 = r.build(exe, "setup_v2", v2, "1.1.0")

    game = os.path.join(work, "game")
    write(f"{game}/Sims.exe", "MZ fake game")
    write(f"{game}/GameData/Objects/Trees.iff", "ORIGINAL trees")
    write(f"{game}/Downloads/Plants2/Plants2.iff", "ORIGINAL plants2")
    write(f"{game}/GameData/UserObjects/keep.iff", "user stuff")
    os.chmod(f"{game}/GameData/Objects/Trees.iff", stat.S_IREAD)
    pristine = snapshot(game)

    def expected(*layers):
        exp = dict(pristine)
        for layer in layers:
            exp.update(snapshot(layer))
        return exp

    print("-- install 1.0.0")
    r.check("status says Install", "action: Install" in r.cli(exe1, "status", game))
    r.cli(exe1, "install", game)
    after_v1 = expected(v1)
    r.expect_tree("game folder = originals + v1", game, after_v1)
    r.check("backup holds original Trees.iff", open(f"{game}/{STATE}/backup/GameData/Objects/Trees.iff").read() == "ORIGINAL trees")
    r.check("version.txt written", open(f"{game}/{STATE}/version.txt").read().strip() == "1.0.0")

    print("-- upgrade to 1.1.0")
    r.check("status says Upgrade", "action: Upgrade" in r.cli(exe2, "status", game))
    r.cli(exe2, "install", game)
    after_v2 = expected(v2)
    r.expect_tree("game folder = originals + v2 (removed files cleaned, originals restored)", game, after_v2)
    r.check("Plants2 original restored", open(f"{game}/Downloads/Plants2/Plants2.iff").read() == "ORIGINAL plants2")
    r.check("Trees original still backed up", os.path.exists(f"{game}/{STATE}/backup/GameData/Objects/Trees.iff"))
    r.check("no transaction left", not os.path.exists(f"{game}/{STATE}/txn"))

    print("-- downgrade to 1.0.0 fails half-way -> rollback")
    r.check("status says Downgrade", "action: Downgrade" in r.cli(exe1, "status", game))
    r.cli(exe1, "install", game, env={"SEASONS_TEST_FAIL_AFTER": "2"}, expect=1)
    r.expect_tree("game folder unchanged after failed downgrade", game, after_v2)
    r.check("still 1.1.0 installed", "installed: 1.1.0" in r.cli(exe2, "status", game))

    print("-- hard crash during downgrade -> journal recovery")
    r.cli(exe1, "install", game, env={"SEASONS_TEST_CRASH_AFTER": "2"}, expect=3)
    r.check("pending operation detected", "pending: True" in r.cli(exe2, "status", game))
    r.cli(exe2, "recover", game)
    r.expect_tree("game folder restored after crash recovery", game, after_v2)
    r.check("no pending operation", "pending: False" in r.cli(exe2, "status", game))

    print("-- crash during upgrade, then the next install recovers and completes")
    r.cli(exe1, "install", game, env={"SEASONS_TEST_CRASH_AFTER": "1"}, expect=3)
    r.cli(exe2, "install", game)
    r.expect_tree("install after crash completes cleanly", game, after_v2)

    print("-- repair: user deleted/changed mod files")
    os.remove(f"{game}/GameData/Walls/w1.wll")
    write(f"{game}/ExpansionPack8/new.iff", "corrupted")
    r.check("status says Reinstall", "action: Reinstall" in r.cli(exe2, "status", game))
    r.cli(exe2, "install", game)
    r.expect_tree("repair restored mod files", game, after_v2)

    print("-- uninstall")
    r.cli(exe2, "uninstall", game)
    r.expect_tree("game folder identical to before the first install", game, pristine)
    r.check("SeasonsSetup folder removed", not os.path.exists(f"{game}/{STATE}"))
    r.check("read-only original kept its content", open(f"{game}/GameData/Objects/Trees.iff").read() == "ORIGINAL trees")
    r.check("uninstall again is a no-op", "not installed" in r.cli(exe2, "uninstall", game))

def versions(r, exe):
    print("== version ordering (beta / dev / release)")
    pkg = os.path.join(r.work, "vfiles")
    write(f"{pkg}/ExpansionPack8/x.iff", "x")
    game = os.path.join(r.work, "game_versions")
    write(f"{game}/Sims.exe", "MZ")
    os.makedirs(f"{game}/GameData", exist_ok=True)
    exes = {v: r.build(exe, "setup_" + v.replace(".", "_"), pkg, v) for v in
            ["1.0-beta1", "1.0-beta2", "1.0-beta10", "1.0", "1.0.0", "1.0.1-dev.3", "1.0.1"]}
    r.cli(exes["1.0-beta2"], "install", game)
    for v, want in [("1.0-beta1", "Downgrade"), ("1.0-beta2", "Reinstall"), ("1.0-beta10", "Upgrade"),
                    ("1.0", "Upgrade"), ("1.0.1-dev.3", "Upgrade")]:
        r.check(f"1.0-beta2 installed, package {v} -> {want}", f"action: {want}" in r.cli(exes[v], "status", game))
    r.cli(exes["1.0"], "install", game)
    for v, want in [("1.0.0", "Reinstall"), ("1.0-beta10", "Downgrade"), ("1.0.1-dev.3", "Upgrade"), ("1.0.1", "Upgrade")]:
        r.check(f"1.0 installed, package {v} -> {want}", f"action: {want}" in r.cli(exes[v], "status", game))
    r.cli(exes["1.0"], "uninstall", game)

def zip_of(path, files):
    import zipfile
    with zipfile.ZipFile(path, "w") as z:
        for rel, data in files.items():
            z.writestr(rel, data)
    return path

def editions(r, exe):
    print("== game editions: one SimsModLoader zip per mod and edition")
    pkg = os.path.join(r.work, "efiles")
    write(f"{pkg}/ExpansionPack8/x.iff", "x")
    zips = []

    def modzip(name, edition, files):
        zips.append(zip_of(os.path.join(r.work, f"{name}_{edition}.zip"), files) + "@" + edition)

    for ed in ("steam", "ea", "cc"):
        modzip("seasons", ed, {"README-SEASONS.txt": f"seasons {ed}", "winmm.dll": f"LOADER {ed}",
                               "mods/seasons.dll": f"SEASONS {ed}"})
    for ed in ("steam", "ea", "cc"):
        modzip("eyedropper", ed, {"README-EYEDROPPER.txt": f"eye {ed}", "winmm.dll": f"LOADER {ed}",
                                  "mods/eyedropper.dll": f"EYE {ed}", "UIGraphics/Eyedropper/Eyedropper.cur": "cur"})
    for ed in ("steam", "ea"):
        modzip("multistory", ed, {"README-MULTISTORY.txt": f"4 stories {ed}", "winmm.dll": "OLDER LOADER",
                                  "mods/multistory.dll": f"MULTI {ed}"})
    zipped = os.path.join(r.work, "editions.zip")
    cmd = [sys.executable, os.path.join(HERE, "make_payload.py"), "--files", pkg, "--version", "2.0.0", "--out", zipped]
    for z in zips:
        cmd += ["--extra-zip", z]
    subprocess.run(cmd, check=True)
    ext = ".exe" if exe.lower().endswith(".exe") else ""
    setup = os.path.join(r.work, "setup_editions" + ext)
    subprocess.run([sys.executable, os.path.join(HERE, "append_payload.py"), exe, zipped, setup], check=True)

    game = os.path.join(r.work, "game_steam")
    write(f"{game}/Sims.exe", "MZ")
    write(f"{game}/steam_api.dll", "steam")
    write(f"{game}/winmm.dll", "ORIGINAL loader from another mod")
    os.makedirs(f"{game}/GameData", exist_ok=True)
    pristine = snapshot(game)
    read = lambda p: open(f"{game}/{p}").read() if os.path.exists(f"{game}/{p}") else None

    st = r.cli(setup, "status", game)
    r.check("Steam edition detected, its three mods listed",
            "edition: steam" in st and "mods: eyedropper.dll,multistory.dll,seasons.dll" in st)
    r.check("Steam readmes selected", "docs: README-EYEDROPPER.txt,README-MULTISTORY.txt,README-SEASONS.txt" in st)
    r.cli(setup, "install", game)
    r.check("steam: steam builds of all three mods + steam loader",
            read("mods/seasons.dll") == "SEASONS steam" and read("mods/eyedropper.dll") == "EYE steam"
            and read("mods/multistory.dll") == "MULTI steam" and read("winmm.dll") == "LOADER steam")
    r.check("readmes not copied to the game", read("README-SEASONS.txt") is None)

    r.cli(setup, "install", game, "cc")
    r.check("switch to cc: cc builds, no 4 Stories (no cc build)",
            read("mods/seasons.dll") == "SEASONS cc" and read("mods/eyedropper.dll") == "EYE cc"
            and read("mods/multistory.dll") is None and read("winmm.dll") == "LOADER cc")
    st = r.cli(setup, "status", game)
    r.check("edition remembered", "edition: cc" in st and "mods: eyedropper.dll,seasons.dll" in st)

    r.cli(setup, "uninstall", game)
    r.expect_tree("uninstall restores the other mod's winmm.dll and removes mods/", game, pristine)

def saves(r, exe):
    print("== saved-games backup (game folder, VirtualStore, Legacy 'Saved Games')")
    pkg = os.path.join(r.work, "bfiles")
    write(f"{pkg}/ExpansionPack8/x.iff", "x")
    setup = r.build(exe, "setup_backup", pkg, "1.0.0")
    profile = os.path.join(r.work, "profile")
    local = os.path.join(r.work, "localappdata")
    backups = os.path.join(r.work, "backups")
    game = os.path.join(r.work, "game_saves")
    write(f"{game}/Sims.exe", "MZ")
    write(f"{game}/UserData/Neighborhood.iff", "hood 1")
    write(f"{game}/UserData/Houses/House01.iff", "house 1")
    write(f"{game}/UserData3/Neighborhood.iff", "hood 3")
    os.makedirs(f"{game}/UserData5", exist_ok=True)
    root = os.path.splitdrive(os.path.abspath(game))[1].lstrip("\\/")
    write(os.path.join(local, "VirtualStore", root, "UserData", "Neighborhood.iff"), "virtualised hood")
    write(f"{profile}/Saved Games/Electronic Arts/The Sims 25/UserData2/Houses/House05.iff", "legacy house")
    before = snapshot(game)
    env = {"SEASONS_BACKUP_ROOT": backups, "USERPROFILE": profile, "LOCALAPPDATA": local}
    out = r.cli(setup, "backup", game, env=env)
    dirs = os.listdir(backups) if os.path.isdir(backups) else []
    r.check("one timestamped backup folder", len(dirs) == 1, str(dirs))
    if dirs:
        b = os.path.join(backups, dirs[0])
        got = snapshot(b)
        want = {
            "Game folder/UserData/Neighborhood.iff", "Game folder/UserData/Houses/House01.iff",
            "Game folder/UserData3/Neighborhood.iff", "VirtualStore/UserData/Neighborhood.iff",
            "Saved Games/The Sims 25/UserData2/Houses/House05.iff", "BACKUP-INFO.txt"}
        files = {k for k, v in got.items() if v is not None}
        r.check("all save folders copied (empty ones skipped)", files == want, "\n".join(sorted(files ^ want)))
        r.check("content identical", open(f"{b}/VirtualStore/UserData/Neighborhood.iff").read() == "virtualised hood"
                and open(f"{b}/Saved Games/The Sims 25/UserData2/Houses/House05.iff").read() == "legacy house")
    r.expect_tree("originals untouched", game, before)
    r.cli(setup, "backup", game, env=env)
    r.check("second backup gets its own folder", len(os.listdir(backups)) == 2)

def real(r, exe, files, docs):
    print("== real package install / uninstall")
    exe_real = r.build(exe, "setup_real", files, "9.9.9", docs)
    game = os.path.join(r.work, "game_real")
    write(f"{game}/Sims.exe", "MZ fake game")
    write(f"{game}/Downloads/Plants2/Plants2.iff", "ORIGINAL plants2")
    pristine = snapshot(game)
    r.cli(exe_real, "install", game)
    exp = dict(pristine)
    exp.update(snapshot(files))
    exp = {k: v for k, v in exp.items() if not k.split("/")[-1] in (".DS_Store",)}
    r.expect_tree("real package installed exactly", game, exp)
    r.cli(exe_real, "install", game)
    r.cli(exe_real, "uninstall", game)
    r.expect_tree("real package uninstalled exactly", game, pristine)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", required=True, help="published installer WITHOUT payload")
    ap.add_argument("--real", help="install_files folder for the real-package test")
    ap.add_argument("--docs")
    a = ap.parse_args()
    work = tempfile.mkdtemp(prefix="seasons-test-")
    r = Runner(work)
    try:
        synthetic(r, os.path.abspath(a.exe))
        versions(r, os.path.abspath(a.exe))
        editions(r, os.path.abspath(a.exe))
        saves(r, os.path.abspath(a.exe))
        if a.real:
            real(r, os.path.abspath(a.exe), a.real, a.docs)
    finally:
        def onerror(func, path, _):
            os.chmod(path, stat.S_IWRITE)
            func(path)
        shutil.rmtree(work, onerror=onerror)
    print(f"\n{'ALL TESTS PASSED' if r.failures == 0 else str(r.failures) + ' FAILURE(S)'}")
    sys.exit(1 if r.failures else 0)

if __name__ == "__main__":
    main()
