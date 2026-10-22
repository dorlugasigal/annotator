#!/usr/bin/env python3
"""Build the signed Annotator drag-to-Applications disk image."""
import argparse
import hashlib
import importlib.util
import json
import os
import plistlib
import re
import shutil
import subprocess
import sys
import tempfile
import time
from contextlib import contextmanager
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DMG_VENV = ROOT / "build/dmg-tools"
VOLUME_NAME = "Annotator"
# Icon centres in the 640x420 window; tools/release/dmg/background.html draws the arrow and text around them.
ICON_LOCATIONS = {"Annotator.app": (170, 176), "Applications": (470, 176)}
WINDOW_RECT = ((120, 120), (640, 420))

spec = importlib.util.spec_from_file_location("package_app", ROOT / "tools/release/package-app.py")
packager = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packager)


class DiskImageError(ValueError):
    pass


def command(*args, stderr=False):
    result = subprocess.run([str(a) for a in args], capture_output=True, text=True)
    if result.returncode:
        detail = (result.stderr or result.stdout).strip().splitlines()
        reason = f": {detail[-1]}" if detail else ""
        raise DiskImageError(f"{Path(str(args[0])).name} failed with exit code {result.returncode}{reason}")
    return result.stderr if stderr else result.stdout


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def add_dmgbuild_path():
    for site in DMG_VENV.glob("lib/python*/site-packages"):
        if (site / "dmgbuild").exists():
            sys.path.insert(0, str(site))
            return
    raise DiskImageError(
        "DMG tooling is missing. Run: python3 -m venv build/dmg-tools && "
        "build/dmg-tools/bin/pip install --require-hashes -r tools/release/dmg-requirements.txt"
    )


def background_tiff(directory):
    """Combine the committed 1x and 2x PNGs into one HiDPI TIFF, outside the source tree."""
    source = ROOT / "tools/release/dmg/background.png"
    hidpi = ROOT / "tools/release/dmg/background@2x.png"
    if not source.is_file() or not hidpi.is_file():
        raise DiskImageError("DMG background files are missing. Run: node tools/release/dmg/render_background.mjs")
    out = Path(directory) / "background.tiff"
    command("tiffutil", "-cathidpicheck", source, hidpi, "-out", out)
    return out


@contextmanager
def scratch_directory(parent):
    """A private temporary folder for dmgbuild and hdiutil; restores the process's temp settings after."""
    Path(parent).mkdir(parents=True, exist_ok=True)
    previous_tempdir, previous_env = tempfile.tempdir, os.environ.get("TMPDIR")
    with tempfile.TemporaryDirectory(prefix=".dmg-", dir=parent) as tmp:
        tempfile.tempdir = tmp
        os.environ["TMPDIR"] = tmp
        try:
            yield Path(tmp)
        finally:
            tempfile.tempdir = previous_tempdir
            if previous_env is None:
                os.environ.pop("TMPDIR", None)
            else:
                os.environ["TMPDIR"] = previous_env


def detach(mount):
    # Spotlight or Finder can hold a just-mounted image for a moment; retry before forcing.
    for attempt in range(3):
        try:
            command("hdiutil", "detach", mount)
            return
        except DiskImageError:
            time.sleep(1 + attempt)
    command("hdiutil", "detach", "-force", mount)


def inspect_release_app(app):
    return packager.inspect_app(Path(app), "release")


def default_name(app):
    metadata = inspect_release_app(app)
    return packager.archive_name("annotator", metadata["releaseVersion"], metadata["build"],
                                 packager.architectures(Path(app), metadata), "dmg")


def mounted_path_from_plist(output):
    value = plistlib.loads(output.encode())
    for entity in value.get("system-entities", []):
        mount = entity.get("mount-point")
        if mount:
            return Path(mount)
    raise DiskImageError("hdiutil did not report a mount point.")


def verify_contents(dmg, expected_sha=None):
    dmg = Path(dmg)
    if expected_sha and digest(dmg) != expected_sha:
        raise DiskImageError("Disk image checksum changed.")
    command("hdiutil", "verify", dmg)
    mounts = Path(tempfile.mkdtemp(prefix=".mounts-", dir=dmg.parent))
    mount = None
    try:
        mount = mounted_path_from_plist(command("hdiutil", "attach", "-plist", "-nobrowse", "-noautoopen", "-readonly", "-mountrandom", mounts, dmg))
        names = {p.name for p in mount.iterdir()}
        if "Annotator.app" not in names or "Applications" not in names:
            raise DiskImageError("Disk image contents are incomplete.")
        if not (mount / "Applications").is_symlink() or os.readlink(mount / "Applications") != "/Applications":
            raise DiskImageError("Applications link is missing or points to the wrong place.")
        command("codesign", "--verify", "--deep", "--strict", mount / "Annotator.app")
    finally:
        if mount:
            detach(mount)
        shutil.rmtree(mounts, ignore_errors=True)


def build(app, output_dir, identity=None, name=None, verify=True):
    app = Path(app).resolve()
    output_dir = Path(output_dir).resolve()
    if not app.is_dir() or app.name != "Annotator.app":
        raise DiskImageError("--app must point to Annotator.app.")
    inspect_release_app(app)
    output_dir.mkdir(parents=True, exist_ok=True)
    artifact = output_dir / (name or default_name(app))
    if artifact.exists() or artifact.is_symlink():
        raise DiskImageError("Disk image output already exists. Use a fresh name or directory.")
    add_dmgbuild_path()
    import dmgbuild  # type: ignore

    with scratch_directory(output_dir) as scratch:
        # Build and check in a scratch folder beside the output (same volume), then rename the finished
        # image into place, so an interrupted build never leaves a partial image a resumed release would find.
        staged = scratch / artifact.name
        _build_into(dmgbuild, app, staged, background_tiff(scratch), identity, verify)
        staged.replace(artifact)
    return {"archive": artifact.name, "sha256": digest(artifact), "size": artifact.stat().st_size,
            "volumeName": VOLUME_NAME, "iconLocations": ICON_LOCATIONS}


def _build_into(dmgbuild, app, artifact, background, identity, verify):
    icon = app / "Contents/Resources/AppIcon.icns"
    settings = {
        "format": "UDZO",
        "filesystem": "HFS+",
        "files": [(str(app), "Annotator.app")],
        "symlinks": {"Applications": "/Applications"},
        "icon_locations": ICON_LOCATIONS,
        "background": str(background),
        "window_rect": WINDOW_RECT,
        "show_toolbar": False,
        "show_sidebar": False,
        "show_status_bar": False,
        "show_tab_view": False,
        "show_pathbar": False,
        "icon_size": 128,
        "text_size": 13,
        "label_pos": "bottom",
        "arrange_by": None,
        "hide": [".background.tiff"],
    }
    if icon.is_file():
        settings["icon"] = str(icon)
    dmgbuild.build_dmg(str(artifact), VOLUME_NAME, settings=settings)
    if identity:
        args = ["codesign", "--force", "--sign", identity]
        if identity != "-":
            args.append("--timestamp")
        args.append(artifact)
        command(*args)
        command("codesign", "--verify", "--strict", artifact)
    if verify:
        verify_contents(artifact)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--identity", help="Developer ID identity for release, or '-' for local ad-hoc previews.")
    parser.add_argument("--name", help="Output DMG file name. Defaults to annotator-<version>-<build>-<arm64|universal>.dmg")
    args = parser.parse_args(argv)
    try:
        report = build(args.app, args.output, args.identity, args.name)
    except (DiskImageError, packager.PackagingError, OSError, ValueError, plistlib.InvalidFileException) as error:
        print(f"Disk image failed: {error}", file=sys.stderr)
        return 1
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
