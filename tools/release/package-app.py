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
CHANNELS = {
    "preview": ("Annotator Dev.app", "com.dorlugasigal.annotator.dev", "annotator-dev"),
    "release": ("Annotator.app", "com.dorlugasigal.annotator", "annotator"),
}


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


def inspect_app(app, channel):
    name, identifier, token = CHANNELS[channel]
    if app.name != name or not app.is_dir():
        raise PackagingError(f"{channel} packaging requires {name}.")
    with (app / "Contents/Info.plist").open("rb") as file:
        info = plistlib.load(file)
    if info.get("CFBundleIdentifier") != identifier:
        raise PackagingError("The app bundle ID does not match this distribution channel.")
    if info.get("LSMinimumSystemVersion") not in ("14", "14.0"):
        raise PackagingError("This cask targets macOS 14. Update packaging if the app's minimum OS changes.")
    version = info.get("CFBundleShortVersionString", "")
    build = info.get("CFBundleVersion", "")
    if not isinstance(version, str) or not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", version):
        raise PackagingError("The macOS marketing version must be three numeric components without a prerelease suffix.")
    prerelease = info.get("AnnotatorPrerelease", "")
    if not isinstance(prerelease, str) or (prerelease and not re.fullmatch(r"(alpha|beta|rc)\.[1-9][0-9]*", prerelease)):
        raise PackagingError("AnnotatorPrerelease must be empty for stable, or alpha.N, beta.N or rc.N with a positive integer.")
    if not isinstance(build, str) or not re.fullmatch(r"[1-9][0-9]*", build):
        raise PackagingError("The app must have a positive numeric build number.")
    if not (app / "Contents/MacOS" / name.removesuffix(".app")).is_file():
        raise PackagingError("The app executable is missing.")
    release_version = version + (f"-{prerelease}" if prerelease else "")
    return dict(name=name, identifier=identifier, token=token, version=version, build=build,
                prerelease=prerelease, releaseVersion=release_version)


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
    return f"{token}-{release_version}-{build}-{ARCHIVE_SUFFIXES[architecture]}.{extension}"


def cask(metadata, url, checksum, homepage, channel):
    # JSON escaping also protects the Ruby string literals used here.
    def literal(text):
        return json.dumps(text, ensure_ascii=False).replace("#{", "\\#{")
    caveat = (
        "Development preview: not notarized for public distribution. "
        "Save drawings and quit other copies before installing."
        if channel == "preview" else
        "Save drawings and quit Annotator before upgrading. User boards and preferences are not removed on uninstall."
    )
    if metadata.get("prerelease"):
        caveat = "Testing prerelease. Read the release notes and known limits. " + caveat
    # A universal app installs on both kinds of Mac; only an Apple Silicon-only app limits the cask.
    arch = "  depends_on arch: :arm64\n" if metadata.get("architecture", "arm64") == "arm64" else ""
    conflict = ""
    # A release app updates itself with Sparkle (automatic checks are on by default), so brew upgrade leaves it alone
    # unless the person asks with --greedy. A development preview has no update feed.
    updates = "\n  auto_updates true" if channel == "release" else ""
    if channel == "release":
        other = "annotator" if metadata["token"] == "annotator-preview" else "annotator-preview"
        conflict = f"  conflicts_with cask: {literal(other)}\n"
    return f"""cask {literal(metadata['token'])} do
  version {literal(metadata.get('releaseVersion', metadata['version']) + ',' + metadata['build'])}
  sha256 {literal(checksum)}

  url {literal(url)}
  name {literal(metadata['name'].removesuffix('.app'))}
  desc "Native live desktop annotation"
  homepage {literal(homepage)}{updates}

{arch}  depends_on macos: :sonoma

{conflict}
  app {literal(metadata['name'])}

  uninstall quit: {literal(metadata['identifier'])}

  caveats {literal(caveat)}
end
"""


def package(app, output, channel, download_url=None, homepage=None):
    app, output = app.resolve(), output.resolve()
    metadata = inspect_app(app, channel)
    architecture = architectures(app, metadata)
    artifact_name = archive_name(metadata["token"], metadata["releaseVersion"], metadata["build"], architecture, "zip")
    if output == app or app in output.parents:
        raise PackagingError("The output directory cannot be inside the app bundle.")
    if channel == "release" and (not download_url or not homepage):
        raise PackagingError("Release packaging requires approved download and homepage HTTPS URLs.")
    remote = https_url(download_url, archive=True) if download_url else None
    if remote and unquote(urlsplit(remote).path.rsplit("/", 1)[-1]) != artifact_name:
        raise PackagingError(f"The download URL must end in the immutable archive name {artifact_name}.")
    if channel == "preview" and remote:
        raise PackagingError("Development previews use a local archive URL; do not publish development-signed builds.")
    homepage = https_url(homepage or "https://github.com/dorlugasigal/annotator")
    run("codesign", "--verify", "--deep", "--strict", str(app))
    if channel == "release":
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
        if inspect_app(restored, channel) != metadata:
            raise PackagingError("Archive metadata does not match the source app.")
        run("codesign", "--verify", "--deep", "--strict", str(restored))
        with archive.open("rb") as file:
            checksum = hashlib.file_digest(file, "sha256").hexdigest()
        url = remote or artifact.as_uri()
        generated = cask({**metadata, "architecture": architecture}, url, checksum, homepage, channel)
        report = {**metadata, "channel": channel, "archive": artifact.name,
                  "sha256": checksum, "downloadURL": url, "architecture": architecture,
                  "homepage": homepage, "notarizationVerified": channel == "release", "published": False}
        (staging / definition.name).write_text(generated)
        (staging / manifest.name).write_text(json.dumps(report, indent=2) + "\n")
        archive.replace(artifact)
        (staging / definition.name).replace(definition)
        (staging / manifest.name).replace(manifest)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--channel", choices=CHANNELS, default="preview")
    parser.add_argument("--app", type=Path, default=ROOT / "build/DerivedData/Build/Products/Debug/Annotator Dev.app")
    parser.add_argument("--output", type=Path, default=ROOT / "build/distribution/preview")
    parser.add_argument("--download-url")
    parser.add_argument("--homepage")
    args = parser.parse_args()
    try:
        report = package(args.app, args.output, args.channel, args.download_url, args.homepage)
    except (PackagingError, OSError, ValueError, plistlib.InvalidFileException) as error:
        print(f"Packaging failed: {error}", file=sys.stderr)
        return 1
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
