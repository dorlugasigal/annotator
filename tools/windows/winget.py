#!/usr/bin/env python3
"""Windows portable release files and their winget manifests (specification 013, WIN-DST-09).

The portable release is a ZIP per architecture with Annotator.exe at its root (tools/windows/package.ps1 -Portable).
It goes on the public GitHub release next to the Mac files, described by annotator-windows.json. winget installs it as a
portable app from our own package, so people without the Microsoft Store run:

    winget install DorLugasiGal.Annotator

Commands:
  describe  Writes annotator-windows.json for the ZIPs in a folder (after package.ps1 -Portable).
  verify    Checks a folder's annotator-windows.json against the ZIPs and the shared version.
  manifests Writes the three winget manifests (version, installer, defaultLocale) for a release tag.

Nothing here uploads, submits or signs anything. Submitting the manifests to microsoft/winget-pkgs is an owner step
(docs/maintainers/winget.md).
"""
import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "release"))
import release  # noqa: E402

PACKAGE_IDENTIFIER = "DorLugasiGal.Annotator"
# 1.9.0 or later: ArchiveBinariesDependOnPath is new in 1.9.0.
MANIFEST_VERSION = "1.10.0"
ARCHITECTURES = ("x64", "arm64")
DESCRIPTION_FILE = "annotator-windows.json"
MINIMUM_OS = "10.0.22621.0"


class WingetError(Exception):
    pass


def shared_version():
    return release.configured_version(ROOT)


def zip_name(label, architecture):
    return f"Annotator-{label}-windows-{architecture}.zip"


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def describe(folder, source=""):
    label, build = shared_version()
    files = []
    for architecture in ARCHITECTURES:
        path = folder / zip_name(label, architecture)
        if path.is_file():
            files.append({"architecture": architecture, "file": path.name, "size": path.stat().st_size, "sha256": sha256(path)})
    if not any(row["architecture"] == "x64" for row in files):
        raise WingetError(f"{zip_name(label, 'x64')} is missing in {folder}.")
    data = {"schemaVersion": 1, "releaseVersion": label, "build": build, "source": source, "files": files}
    (folder / DESCRIPTION_FILE).write_text(json.dumps(data, indent=2) + "\n")
    return data


def verify(folder, source=None, require_arm64=True):
    path = folder / DESCRIPTION_FILE
    if not path.is_file() or path.is_symlink():
        raise WingetError(f"{DESCRIPTION_FILE} is missing in {folder}.")
    data = json.loads(path.read_text())
    label, build = shared_version()
    if data.get("schemaVersion") != 1 or data.get("releaseVersion") != label or data.get("build") != build:
        raise WingetError(f"{DESCRIPTION_FILE} describes {data.get('releaseVersion')} ({data.get('build')}), not {label} ({build}).")
    if source is not None and data.get("source") != source:
        raise WingetError("The Windows files were built from a different commit than the release candidate.")
    architectures = {row["architecture"] for row in data["files"]}
    wanted = set(ARCHITECTURES) if require_arm64 else {"x64"}
    if not wanted <= architectures or architectures - set(ARCHITECTURES):
        raise WingetError(f"The Windows files cover {sorted(architectures)}; expected {sorted(wanted)}.")
    for row in data["files"]:
        zip_path = folder / row["file"]
        if row["file"] != zip_name(label, row["architecture"]) or not zip_path.is_file() or zip_path.is_symlink():
            raise WingetError(f"{row['file']} is missing or misnamed.")
        if zip_path.stat().st_size != row["size"] or sha256(zip_path) != row["sha256"]:
            raise WingetError(f"{row['file']} differs from {DESCRIPTION_FILE}.")
    return data


def _yaml_text(value):
    return json.dumps(value, ensure_ascii=False)


def manifests(data, repository, tag, output, notes_url=None):
    if not re.fullmatch(r"[A-Za-z0-9-]+/[A-Za-z0-9._-]+", repository):
        raise WingetError("The repository must be owner/name.")
    if tag != f"v{data['releaseVersion']}":
        raise WingetError(f"The tag must be v{data['releaseVersion']}.")
    version = data["releaseVersion"]
    folder = output / PACKAGE_IDENTIFIER.split(".")[0][0].lower() / PACKAGE_IDENTIFIER.replace(".", "/") / version
    folder.mkdir(parents=True, exist_ok=True)
    header = f"# Created by tools/windows/winget.py\n# yaml-language-server: $schema=https://aka.ms/winget-manifest.{{}}.{MANIFEST_VERSION}.schema.json\n\n"
    base = f"PackageIdentifier: {PACKAGE_IDENTIFIER}\nPackageVersion: {version}\n"
    (folder / f"{PACKAGE_IDENTIFIER}.yaml").write_text(
        header.format("version") + base + f"DefaultLocale: en-US\nManifestType: version\nManifestVersion: {MANIFEST_VERSION}\n")
    installers = "".join(
        f"- Architecture: {row['architecture']}\n"
        f"  InstallerUrl: https://github.com/{repository}/releases/download/{tag}/{row['file']}\n"
        f"  InstallerSha256: {row['sha256'].upper()}\n"
        for row in sorted(data["files"], key=lambda item: item["architecture"]))
    # Annotator.exe loads the Windows App SDK files next to it. Started through winget's usual link in its Links
    # folder, it looks for them there and stops with DllNotFoundException, so winget adds the app's folder to PATH.
    (folder / f"{PACKAGE_IDENTIFIER}.installer.yaml").write_text(
        header.format("installer") + base +
        f"MinimumOSVersion: {MINIMUM_OS}\nInstallerType: zip\nNestedInstallerType: portable\n"
        "NestedInstallerFiles:\n- RelativeFilePath: Annotator.exe\n  PortableCommandAlias: annotator\n"
        "ArchiveBinariesDependOnPath: true\n"
        "UpgradeBehavior: uninstallPrevious\n"
        f"Installers:\n{installers}ManifestType: installer\nManifestVersion: {MANIFEST_VERSION}\n")
    notes = f"ReleaseNotesUrl: {notes_url or f'https://github.com/{repository}/releases/tag/{tag}'}\n"
    (folder / f"{PACKAGE_IDENTIFIER}.locale.en-US.yaml").write_text(
        header.format("defaultLocale") + base +
        "PackageLocale: en-US\nPublisher: Dor Lugasi-Gal\nPublisherUrl: https://getannotator.app\n"
        "PackageName: Annotator\nPackageUrl: https://getannotator.app\nLicense: Apache-2.0\n"
        "LicenseUrl: https://github.com/dorlugasigal/annotator/blob/main/LICENSE\n"
        f"ShortDescription: {_yaml_text('Draw on your screen while you present, then hide or clear it with one shortcut.')}\n"
        "Moniker: annotator\nTags:\n- annotation\n- presentation\n- screen\n- drawing\n- teaching\n"
        + notes + f"ManifestType: defaultLocale\nManifestVersion: {MANIFEST_VERSION}\n")
    return folder


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("describe")
    p.add_argument("--folder", type=Path, required=True)
    p.add_argument("--source", default="", help="The commit the ZIPs were built from.")
    p = sub.add_parser("verify")
    p.add_argument("--folder", type=Path, required=True)
    p.add_argument("--source")
    p.add_argument("--x64-only", action="store_true")
    p = sub.add_parser("manifests")
    p.add_argument("--folder", type=Path, required=True, help="Folder with annotator-windows.json and the ZIPs.")
    p.add_argument("--repository", default="dorlugasigal/annotator")
    p.add_argument("--tag")
    p.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command == "describe":
            print(json.dumps(describe(args.folder, args.source), indent=2))
        elif args.command == "verify":
            verify(args.folder, args.source, require_arm64=not args.x64_only)
            print("Windows files match annotator-windows.json and the shared version.")
        else:
            data = verify(args.folder, require_arm64=False)
            print(manifests(data, args.repository, args.tag or f"v{data['releaseVersion']}", args.output))
    except (WingetError, OSError, ValueError, KeyError, release.ReleaseError) as error:
        print(f"winget: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
