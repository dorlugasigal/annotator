#!/usr/bin/env python3
"""Package a signed app and generate a checksum-pinned Homebrew cask. Never publishes."""
import argparse
import hashlib
import json
import plistlib
import re
import subprocess
import sys
import tempfile
from pathlib import Path
from urllib.parse import urlsplit, urlunsplit, quote, unquote

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import release  # noqa: E402

# The released app: its bundle name, bundle ID and Homebrew cask token.
APP = ("Annotator.app", "com.dorlugasigal.annotator", "annotator")


class PackagingError(Exception):
    pass


def run(*args):
    result = subprocess.run(args, text=True, capture_output=True)
    if result.returncode:
        raise PackagingError(f"{args[0]} failed: {(result.stderr or result.stdout).strip()}")
    return result.stdout + result.stderr


def https_url(value, archive=False):
    url = urlsplit(value)
    if (url.scheme != "https" or not url.hostname or url.username or url.password
            or url.query or url.fragment or any(ord(c) < 32 for c in value)):
        raise PackagingError("Use a permanent HTTPS URL without credentials, query strings or fragments.")
    if archive and not url.path.endswith(".zip"):
        raise PackagingError("The download URL must identify the versioned .zip archive.")
    return urlunsplit((url.scheme, url.netloc, quote(url.path, safe="/%:@-._~"), "", ""))


def inspect_app(app):
    name, identifier, token = APP
    if app.name != name or not app.is_dir():
        raise PackagingError(f"Release packaging requires {name}.")
    with (app / "Contents/Info.plist").open("rb") as file:
        info = plistlib.load(file)
    if info.get("CFBundleIdentifier") != identifier:
        raise PackagingError("The app bundle ID is not the released app's.")
    if info.get("LSMinimumSystemVersion") not in ("14", "14.0"):
        raise PackagingError("This cask targets macOS 14. Update packaging if the app's minimum OS changes.")
    version = info.get("CFBundleShortVersionString", "")
    build = info.get("CFBundleVersion", "")
    try:
        expected = release.derived_build(version)
    except release.ReleaseError as error:
        raise PackagingError(f"The app's version must be a plain X.Y.Z: {error}") from error
    if "AnnotatorPrerelease" in info:
        raise PackagingError("The app still has an AnnotatorPrerelease setting; releases have no pre-release labels.")
    if build != str(expected):
        raise PackagingError(f"The app's build must be {expected}, derived from {version}.")
    if not (app / "Contents/MacOS" / name.removesuffix(".app")).is_file():
        raise PackagingError("The app executable is missing.")
    return dict(name=name, identifier=identifier, token=token, version=version, build=build, releaseVersion=version)


# Sorted `lipo -archs` output for each supported build, and the suffix its archive names use.
ARCHIVE_SUFFIXES = {"arm64": "arm64", "arm64 x86_64": "universal"}


def architectures(app, metadata):
    """The app executable's architectures: "arm64", or "arm64 x86_64" for a universal app."""
    output = run("lipo", "-archs", str(app / "Contents/MacOS" / metadata["name"].removesuffix(".app")))
    value = " ".join(sorted(output.split()))
    if value not in ARCHIVE_SUFFIXES:
        raise PackagingError("Build the Apple Silicon or the universal (Apple Silicon and Intel) app.")
    return value


def archive_name(token, release_version, build, architecture, extension):
    """annotator-0.2.0-universal.zip. Releases with a legacy pre-release label also named their build."""
    middle = f"{release_version}-{build}" if release.is_legacy(release_version) else release_version
    return f"{token}-{middle}-{ARCHIVE_SUFFIXES[architecture]}.{extension}"


def cask(metadata, url, checksum, homepage):
    # JSON escaping also protects the Ruby string literals used here.
    def literal(text):
        return json.dumps(text, ensure_ascii=False).replace("#{", "\\#{")
    caveat = "Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
    # A universal app installs on both kinds of Mac; only an Apple Silicon-only app limits the cask.
    arch = "  depends_on arch: :arm64\n" if metadata.get("architecture", "arm64") == "arm64" else ""
    version = metadata.get("releaseVersion", metadata["version"])
    if release.is_legacy(version):
        version += "," + metadata["build"]
    # The app updates itself with Sparkle (automatic checks are on by default), so brew upgrade leaves it alone unless
    # the person asks with --greedy.
    return f"""cask {literal(metadata['token'])} do
  version {literal(version)}
  sha256 {literal(checksum)}

  url {literal(url)}
  name {literal(metadata['name'].removesuffix('.app'))}
  desc "Native live desktop annotation"
  homepage {literal(homepage)}
  auto_updates true

{arch}  depends_on macos: :sonoma

  app {literal(metadata['name'])}

  uninstall quit: {literal(metadata['identifier'])}

  caveats {literal(caveat)}
end
"""


def package(app, output, download_url, homepage):
    app, output = app.resolve(), output.resolve()
    metadata = inspect_app(app)
    architecture = architectures(app, metadata)
    artifact_name = archive_name(metadata["token"], metadata["releaseVersion"], metadata["build"], architecture, "zip")
    if output == app or app in output.parents:
        raise PackagingError("The output directory cannot be inside the app bundle.")
    if not download_url or not homepage:
        raise PackagingError("Release packaging requires approved download and homepage HTTPS URLs.")
    remote = https_url(download_url, archive=True)
    if unquote(urlsplit(remote).path.rsplit("/", 1)[-1]) != artifact_name:
        raise PackagingError(f"The download URL must end in the immutable archive name {artifact_name}.")
    homepage = https_url(homepage)
    run("codesign", "--verify", "--deep", "--strict", str(app))
    signature = run("codesign", "-d", "--verbose=4", str(app))
    if "Authority=Developer ID Application:" not in signature:
        raise PackagingError("Public release requires a Developer ID Application signature, not development/ad-hoc signing.")
    run("xcrun", "stapler", "validate", str(app))
    run("spctl", "--assess", "--type", "execute", "--verbose=2", str(app))
    output.mkdir(parents=True, exist_ok=True)
    artifact = output / artifact_name
    definition = output / f"{metadata['token']}.rb"
    manifest = output / f"{metadata['token']}-manifest.json"
    if any(p.exists() for p in (artifact, definition, manifest)):
        raise PackagingError("Package output already exists. Use a fresh output directory; do not overwrite a released artifact.")
    with tempfile.TemporaryDirectory(prefix="annotator-package-", dir=output) as temporary:
        staging = Path(temporary)
        archive = staging / artifact_name
        run("ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", str(app), str(archive))
        extracted = staging / "verify"
        run("ditto", "-x", "-k", str(archive), str(extracted))
        restored = extracted / metadata["name"]
        if inspect_app(restored) != metadata:
            raise PackagingError("Archive metadata does not match the source app.")
        run("codesign", "--verify", "--deep", "--strict", str(restored))
        with archive.open("rb") as file:
            checksum = hashlib.file_digest(file, "sha256").hexdigest()
        generated = cask({**metadata, "architecture": architecture}, remote, checksum, homepage)
        report = {**metadata, "channel": "release", "archive": artifact.name,
                  "sha256": checksum, "downloadURL": remote, "architecture": architecture,
                  "homepage": homepage, "notarizationVerified": True, "published": False}
        (staging / definition.name).write_text(generated)
        (staging / manifest.name).write_text(json.dumps(report, indent=2) + "\n")
        archive.replace(artifact)
        (staging / definition.name).replace(definition)
        (staging / manifest.name).replace(manifest)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path, required=True, help="The Developer ID-signed, stapled Release Annotator.app.")
    parser.add_argument("--output", type=Path, required=True, help="A fresh output folder.")
    parser.add_argument("--download-url", required=True)
    parser.add_argument("--homepage", required=True)
    args = parser.parse_args()
    try:
        report = package(args.app, args.output, args.download_url, args.homepage)
    except (PackagingError, OSError, ValueError, plistlib.InvalidFileException) as error:
        print(f"Packaging failed: {error}", file=sys.stderr)
        return 1
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
