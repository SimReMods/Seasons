# The Sims: Seasons installer - development notes

```
install_files/     the mod, laid out exactly like the game folder (ExpansionPack8/, GameData/, ...)
installer_src/     Avalonia 12 / .NET 10 Native AOT installer (autorun-style menu)
  Assets/          background, buttons, Maxis tiles, sounds, icon, about picture
  docs/INSTALL.txt readme shown by "More Information" (shipped inside the package)
  tools/           make_payload.py, append_payload.py, test_installer.py, extract_buttons.py
VERSION            base version for development builds
```

`*.far` is stored with **Git LFS** (ExpansionPack8.far is over GitHub's 100 MB file limit).
Run `git lfs install` once on a new machine before cloning.

## Releasing

1. Update `install_files/` (and `installer_src/docs/INSTALL.txt`), commit.
2. `git tag v1.2.0 && git push origin main v1.2.0`

The workflow (`.github/workflows/build.yml`, Windows runner) then:

1. publishes `SeasonsSetup.exe` (Native AOT, win-x64) with version `1.2.0`;
2. runs `tools/test_installer.py` against it: install, upgrade, failed downgrade with rollback, hard crash with
   journal recovery, repair, uninstall on a fake game folder, plus a real-package install/uninstall;
3. builds the payload (mod files + `manifest.json` with every file's size and SHA-256 + readme + Avalonia's
   native DLLs) and appends it to the exe, so **one exe** is all that ships;
4. zips it as `TheSimsSeasons-1.2.0.zip` (`SeasonsSetup.exe` + `version.txt`) and attaches it to a GitHub Release.

The workflow also downloads the latest [SimsModLoader](https://github.com/SimReMods/SimsModLoader/releases) assets,
one zip per mod and game edition, and merges them into the package as edition-specific files:

| mod | zips |
|---|---|
| Seasons | `seasons_steam-DO-NOT-DOWNLOAD.zip`, `seasons_ea-DO-NOT-DOWNLOAD.zip`, `seasons_cc-DO-NOT-DOWNLOAD.zip` |
| Visit Lots | `visit_lots_steam.zip`, `visit_lots_ea.zip`, `visit_lots_cc.zip` |
| 4 Stories | `multistory_steam.zip`, `multistory_ea.zip` (no Complete Collection build) |
| Eyedropper | `eyedropper_steam.zip`, `eyedropper_ea.zip`, `eyedropper_cc.zip` |

Setup detects the game edition (Steam Legacy Collection / EA app Legacy / Complete Collection, overridable with
"Game version..." in the install dialog) and installs only that edition's files; the install dialog lists the mods.
Each zip's `README*.txt` is shown in "More Information" for the user's edition instead of being copied to the game.
`winmm.dll` (the loader) comes from the Seasons zip of that edition. If a game already has a `winmm.dll`, it is
backed up and restored on uninstall. Missing zips fail a tag build and only warn on other builds. `version.txt` in
the release zip lists the exact release URLs that were bundled.

Pushes without a tag build `VERSION-dev.<run number>` (e.g. `1.0.0-dev.57`) and upload the zip as a workflow
artifact only. A release (`1.0.0`) counts as newer than its dev builds.

## What the installer does in the game folder

Everything it knows lives in `<game>/SeasonsSetup/`:

| file | purpose |
|---|---|
| `state.json` | installed version + commit, every file we own (path, size, SHA-256), backed-up originals, folders we created |
| `version.txt` | installed version, for humans |
| `backup/<path>` | original game files we replaced (restored on uninstall or when a new version stops shipping that file) |
| `txn/` | journal of an operation in progress (only exists while installing / after a crash) |

- **Install**: copies the files; any existing game file is moved to `backup/` first.
- **Upgrade / Reinstall / Downgrade** (button label compares the package version with `state.json`):
  files the old version had but the new one doesn't are removed (or their original restored), unchanged files
  are skipped (hash-checked, so a reinstall also repairs deleted or modified files), changed files are replaced.
- **Uninstall**: removes every file listed in `state.json`, restores all backups, removes the folders it created,
  deletes `SeasonsSetup/`.
- **Crash safety**: every step is written to `txn/journal.log` before it happens; the previous files are kept in
  `txn/old/` until the new `state.json` is atomically published. A failure rolls back immediately; after a crash
  or power loss the next run (any button, or `--cli recover`) undoes the half-done operation.
- **Saved games backup**: before every install / upgrade / uninstall, all `UserData*` folders are copied to
  `Documents\The Sims Seasons Backups\<date time> before <action>\` (with a `BACKUP-INFO.txt` listing where each came
  from). Scanned: `<game>\UserData*` (Complete Collection / CD), `%LOCALAPPDATA%\VirtualStore\<game path>\UserData*`
  (UAC redirection under Program Files) and `Saved Games\Electronic Arts\*\UserData*`, `Documents\Electronic Arts\*`,
  `Documents\EA Games\*` (Legacy Collection). Never deletes; if the copy fails the user can cancel.
- Refuses to run while `Sims.exe` is running; checks free disk space for the bytes that actually change.

Support / scripting: `SeasonsSetup.exe --cli status|install|uninstall|recover "<game folder>"` and `--cli version`.

## Local development (macOS)

```
cd installer_src
dotnet run                                   # UI without a payload (Install shows "installer damaged")
SDKROOT=$(xcrun --sdk macosx --show-sdk-path) dotnet publish -c Release -r osx-arm64 -o /tmp/pub
python3 tools/test_installer.py --exe /tmp/pub/SeasonsSetup --real ../install_files --docs docs
```

A `payload.zip` (from `make_payload.py`) next to the executable is also picked up, for UI testing.

