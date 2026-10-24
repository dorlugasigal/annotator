#!/usr/bin/env python3
"""Guarded distribution helpers. External actions require explicit workflow gates."""
import argparse
import base64
import hashlib
import html
import importlib.util
import json
import os
import plistlib
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path
from urllib.parse import urlsplit

import release
import disk_image

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "windows"))
import winget  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("package_app", ROOT / "tools/release/package-app.py")
packager = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packager)
SPARKLE = "http://www.andymatuschak.org/xml-namespaces/sparkle"
ET.register_namespace("sparkle", SPARKLE)


class DistributionError(ValueError):
    pass


def command(*args, input=None, cwd=None, stderr=False):
    result = subprocess.run(list(map(str, args)), input=input, cwd=cwd, capture_output=True, text=True)
    if result.returncode:
        # Command arguments and tool output may include credential material.
        raise DistributionError(f"{Path(str(args[0])).name} failed with exit code {result.returncode}. No later distribution step was run.")
    return result.stderr if stderr else result.stdout


def repository(value):
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", value or "") or any(p in (".", "..") for p in (value or "").split("/")):
        raise DistributionError("Set an explicit owner/repository destination.")
    return value


def website_url(value):
    raw = urlsplit(value)
    if raw.port or not re.fullmatch(r"/(?:[A-Za-z0-9_-]+/)*[A-Za-z0-9_-]*", raw.path or "/"):
        raise DistributionError("Use an HTTPS website URL with a simple optional site path.")
    value = packager.https_url(value)
    return value.rstrip("/")


def configuration():
    # The open-source repository is both the source and the release repository; publishing checks that it is public.
    repo = repository(os.environ.get("DISTRIBUTION_REPOSITORY"))
    return repo, website_url(os.environ.get("WEBSITE_URL", ""))


def metadata():
    version, build = release.configured_version()
    notes = release.load_notes(require_frozen=True)
    return version, build, notes


def preflight(expected, candidate):
    if not re.fullmatch(r"[0-9a-f]{40}", candidate or ""):
        raise DistributionError("Supply the full candidate commit SHA.")
    if command("git", "rev-parse", "HEAD").strip() != candidate:
        raise DistributionError("Checked-out source does not match the requested candidate.")
    if os.environ.get("GITHUB_REF") != "refs/heads/main" or os.environ.get("GITHUB_EVENT_NAME") not in ("push", "workflow_dispatch"):
        raise DistributionError("Signing requires a main-branch push or explicit main-branch dispatch.")
    if os.environ.get("GITHUB_SHA") != candidate:
        raise DistributionError("Candidate must match the exact commit that triggered this workflow.")
    version, build, notes = metadata()
    if expected != version:
        raise DistributionError("Approved version does not match project.yml.")
    release.verify_frozen_source(notes)
    repo, site = configuration()
    return dict(version=version, build=build, source=candidate, repository=repo,
                homepage=site, tag=f"v{version}", track="preview" if "-" in version else "stable")


def private_file(path, data):
    # codesign, notarytool and sign_update read keys from files. Create each one owner-only before
    # writing, never reuse or follow an existing path, and keep it in the removed signing folder.
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(path, flags, 0o600)
    with os.fdopen(descriptor, "wb") as handle:
        os.fchmod(handle.fileno(), 0o600)
        handle.write(data)


def secret(name):
    value = os.environ.pop(name, "")
    if not value:
        raise DistributionError(f"Missing required environment secret: {name}")
    return value


def decode_secret(value):
    try:
        return base64.b64decode(value, validate=True)
    except ValueError as error:
        raise DistributionError("A base64 signing secret is invalid.") from error


def distribution_targets(app):
    framework = app / "Contents/Frameworks/Sparkle.framework"
    version = framework / "Versions/B"
    targets = [
        version / "XPCServices/Installer.xpc",
        version / "XPCServices/Downloader.xpc",
        version / "Autoupdate",
        version / "Updater.app",
        framework,
        app,
    ]
    for target in targets:
        if not target.exists():
            raise DistributionError(f"Required distribution signing target is missing: {target.name}")
    return targets


def sign_distribution_bundle(app, identity, team):
    targets = distribution_targets(app)
    # Sparkle's SPM helpers are ad-hoc signed; sign inside out, never with --deep.
    for target in targets:
        preserve = ["--preserve-metadata=entitlements"] if target == app or target.name == "Downloader.xpc" else []
        command("codesign", "--force", "--sign", identity, "--options", "runtime",
                "--timestamp", *preserve, target)
    verify_distribution_bundle(app, identity, team)


def verify_distribution_bundle(app, identity, team):
    for target in distribution_targets(app):
        command("codesign", "--verify", "--strict", "--all-architectures", target)
        details = command("codesign", "--display", "--verbose=4", target, stderr=True)
        timestamp = re.search(r"^Timestamp=(.+)$", details, re.MULTILINE)
        if (f"Authority={identity}" not in details.splitlines()
                or f"TeamIdentifier={team}" not in details.splitlines()
                or timestamp is None or timestamp.group(1).strip().lower() in ("none", "not set")
                or not re.search(r"^CodeDirectory .*flags=.*\([^)]*\bruntime\b", details, re.MULTILINE)):
            raise DistributionError(f"{target.name} needs Developer ID signing, a secure timestamp and Hardened Runtime.")
        raw = command("codesign", "--display", "--entitlements", "-", "--xml", target)
        entitlements = plistlib.loads(raw.encode()) if raw.strip() else {}
        if not isinstance(entitlements, dict) or entitlements.get("com.apple.security.get-task-allow"):
            raise DistributionError(f"{target.name} contains invalid or debug-only distribution entitlements.")
    command("codesign", "--verify", "--deep", "--strict", "--all-architectures", app)


def keep_submitted_archive(recovery, archive, expected, build, candidate):
    """Saves the exact archive sent to Apple and its record outside the temporary signing folder, so a timeout or a
    lost response leaves the bytes, hash and submission ID for recovery instead of a second submission."""
    recovery.mkdir(parents=True)
    kept = recovery / archive.name
    kept.write_bytes(archive.read_bytes())
    record = {"schema": 1, "releaseVersion": expected, "build": build, "source": candidate, "archive": kept.name,
              "archiveSha256": hashlib.sha256(kept.read_bytes()).hexdigest(), "size": kept.stat().st_size,
              "submissionId": None}
    (recovery / "recovery.json").write_text(json.dumps(record, indent=2) + "\n")
    return record


def sign_and_package(expected, candidate, output, recovery=None):
    if os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted" or os.environ.get("RELEASE_AUTOMATION_ENABLED") != "true":
        raise DistributionError("Signing requires an enabled protected workflow on an ephemeral GitHub-hosted runner.")
    info = preflight(expected, candidate)
    if output.exists() or (recovery is not None and recovery.exists()):
        raise DistributionError("Use a fresh output directory; immutable release artifacts cannot be replaced.")
    team = os.environ.get("APPLE_TEAM_ID", "")
    identity = os.environ.get("DEVELOPER_ID_IDENTITY", "")
    key_id, issuer = os.environ.get("NOTARY_KEY_ID", ""), os.environ.get("NOTARY_ISSUER_ID", "")
    public_key = os.environ.get("SPARKLE_PUBLIC_KEY", "")
    support_url = os.environ.get("SUPPORT_URL", "")
    if support_url:
        support_url = packager.https_url(support_url)
    metrics_url = os.environ.get("METRICS_URL", "")
    if metrics_url and (website_url(metrics_url) != info["homepage"] or urlsplit(metrics_url).path not in ("", "/")
                        or urlsplit(metrics_url).hostname.endswith(".github.io")):
        raise DistributionError("Metrics must use the approved combined website origin.")
    if not re.fullmatch(r"[A-Z0-9]{10}", team) or not identity.startswith("Developer ID Application:"):
        raise DistributionError("A Developer ID Application identity and ten-character team ID are required.")
    if not re.fullmatch(r"[A-Za-z0-9]{10,}", key_id) or not re.fullmatch(r"[0-9a-fA-F-]{36}", issuer):
        raise DistributionError("Set the team App Store Connect API key ID and issuer UUID.")
    if len(decode_secret(public_key)) != 32:
        raise DistributionError("Sparkle public key must decode to 32 bytes.")
    p12 = decode_secret(secret("DEVELOPER_ID_P12_BASE64"))
    password = secret("DEVELOPER_ID_P12_PASSWORD")
    notary_key = decode_secret(secret("NOTARY_KEY_P8_BASE64"))
    sparkle_key = secret("SPARKLE_PRIVATE_KEY")
    scratch_root = Path(os.environ["RUNNER_TEMP"])
    previous = command("security", "list-keychains", "-d", "user")
    keychains = re.findall(r'"([^"]+)"', previous)
    with tempfile.TemporaryDirectory(prefix="annotator-signing-", dir=scratch_root) as tmp:
        temp = Path(tmp)
        keychain = temp / "signing.keychain-db"
        p12_path, p8_path = temp / "identity.p12", temp / "AuthKey.p8"
        private_file(p12_path, p12)
        private_file(p8_path, notary_key)
        keychain_password = os.urandom(32).hex()
        created = False
        try:
            command("security", "create-keychain", "-p", keychain_password, keychain)
            created = True
            command("security", "set-keychain-settings", "-lut", "7200", keychain)
            command("security", "unlock-keychain", "-p", keychain_password, keychain)
            command("security", "import", p12_path, "-k", keychain, "-P", password,
                    "-T", "/usr/bin/codesign", "-T", "/usr/bin/security")
            command("security", "set-key-partition-list", "-S", "apple-tool:,apple:,codesign:",
                    "-s", "-k", keychain_password, keychain)
            command("security", "list-keychains", "-d", "user", "-s", keychain, *keychains)
            if identity not in command("security", "find-identity", "-v", "-p", "codesigning", keychain):
                raise DistributionError("The imported identity does not match DEVELOPER_ID_IDENTITY.")
            derived = temp / "DerivedData"
            print("Building Developer ID-signed Release app.", flush=True)
            command("xcodebuild", "-quiet", "-project", ROOT / "apps/macos/Annotator.xcodeproj",
                    "-scheme", "Annotator", "-configuration", "Release", "-derivedDataPath", derived,
                    "CODE_SIGN_STYLE=Manual", f"CODE_SIGN_IDENTITY={identity}", f"DEVELOPMENT_TEAM={team}",
                    "CODE_SIGNING_REQUIRED=YES", "OTHER_CODE_SIGN_FLAGS=--timestamp",
                    f"ANNOTATOR_UPDATE_FEED_URL={info['homepage']}/updates/{info['track']}.xml",
                    f"ANNOTATOR_UPDATE_PUBLIC_KEY={public_key}", f"ANNOTATOR_BUILD_STAMP=Release {candidate}",
                    f"ANNOTATOR_ISSUES_URL=https://github.com/{info['repository']}/issues/new",
                    f"ANNOTATOR_WEBSITE_URL={info['homepage']}", f"ANNOTATOR_METRICS_URL={metrics_url}",
                    f"ANNOTATOR_SUPPORT_URL={support_url}", "COMPILER_INDEX_STORE_ENABLE=NO",
                    "build")
            app = derived / "Build/Products/Release/Annotator.app"
            actual = packager.inspect_app(app, "release")
            if (actual["releaseVersion"], int(actual["build"])) != (expected, info["build"]):
                raise DistributionError("The built version does not match the approved candidate.")
            sign_distribution_bundle(app, identity, team)
            upload = temp / "notarization.zip"
            command("ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", app, upload)
            record = keep_submitted_archive(recovery, upload, expected, info["build"], candidate) if recovery else None
            auth = ("--key", p8_path, "--key-id", key_id, "--issuer", issuer)
            print("Submitting the compiled app for notarization.", flush=True)
            submitted = json.loads(command("xcrun", "notarytool", "submit", upload, *auth, "--output-format", "json"))
            submission = submitted.get("id", "") if isinstance(submitted, dict) else ""
            if not re.fullmatch(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", submission):
                raise DistributionError("Apple returned no submission ID. Find the upload in the notarization history before submitting again.")
            if record:
                (recovery / "recovery.json").write_text(json.dumps({**record, "submissionId": submission}, indent=2) + "\n")
            print(f"Submission ID: {submission}. Waiting up to 30 minutes for Apple.", flush=True)
            command("xcrun", "notarytool", "wait", submission, *auth, "--timeout", "30m", "--output-format", "json")
            response = json.loads(command("xcrun", "notarytool", "info", submission, *auth, "--output-format", "json"))
            if response.get("id") != submission or response.get("status") != "Accepted":
                raise DistributionError(f"Notarization was not accepted ({response.get('status', 'unknown')}). Submission ID: {submission}")
            command("xcrun", "stapler", "staple", app)
            command("xcrun", "stapler", "validate", app)
            command("spctl", "--assess", "--type", "execute", app)
            name = packager.archive_name("annotator", expected, str(info["build"]), packager.architectures(app, actual), "zip")
            url = f"https://github.com/{info['repository']}/releases/download/{info['tag']}/{name}"
            report = packager.package(app, output, "release", url, info["homepage"])
            signer = derived / "SourcePackages/artifacts/sparkledistribution/Sparkle/bin/sign_update"
            key_file = temp / "sparkle-private"
            private_file(key_file, sparkle_key.encode())
            signature = command(signer, "--ed-key-file", key_file, "-p", output / name).strip()
            if len(decode_secret(signature)) != 64:
                raise DistributionError("Sparkle did not produce a valid archive signature.")
            command(signer, "--verify", "--ed-key-file", key_file, output / name, signature)
            command("xcrun", "swift", ROOT / "tools/release/verify-update.swift", public_key, signature, output / name)
            report.update(source=candidate, track=info["track"], tag=info["tag"],
                          repository=info["repository"], notarizationID=response["id"],
                          edSignature=signature, size=(output / name).stat().st_size)
            validate_manifest(report)
            (output / "annotator-manifest.json").write_text(json.dumps(report, indent=2) + "\n")
            (output / "release-notes.md").write_text(release.changelog(release.load_notes(), expected))
            (output / "SHA256SUMS").write_text(f"{report['sha256']}  {name}\n")
            (output / "appcast.xml").write_text(appcast(report))
            disk = notarized_disk_image(output, report, identity, auth, temp)
            print(f"Verified {name} and {disk['archive']}. Nothing has been published.", flush=True)
        finally:
            if created:
                try:
                    command("security", "list-keychains", "-d", "user", "-s", *keychains)
                finally:
                    command("security", "delete-keychain", keychain)


def notarized_disk_image(output, manifest, identity, auth, temp):
    """Builds the drag-to-Applications disk image from the final ZIP's stapled app, as the local publisher does,
    then notarizes and staples it. Writes output/disk-image/ for the publish job's --disk-image-folder."""
    name = packager.archive_name("annotator", manifest["releaseVersion"], str(manifest["build"]),
                                 manifest["architecture"], "dmg")
    extracted = temp / "disk-image-source"
    command("ditto", "-x", "-k", output / manifest["archive"], extracted)
    built = disk_image.build(extracted / "Annotator.app", temp / "disk-image", identity, name)
    dmg = temp / "disk-image" / name
    print("Submitting the disk image for notarization.", flush=True)
    submitted = json.loads(command("xcrun", "notarytool", "submit", dmg, *auth, "--output-format", "json"))
    submission = submitted.get("id", "") if isinstance(submitted, dict) else ""
    if not re.fullmatch(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", submission):
        raise DistributionError("Apple returned no submission ID for the disk image. Find the upload in the notarization history before submitting again.")
    print(f"Disk image submission ID: {submission}. Waiting up to 30 minutes for Apple.", flush=True)
    command("xcrun", "notarytool", "wait", submission, *auth, "--timeout", "30m", "--output-format", "json")
    response = json.loads(command("xcrun", "notarytool", "info", submission, *auth, "--output-format", "json"))
    if response.get("id") != submission or response.get("status") != "Accepted":
        raise DistributionError(f"Disk image notarization was not accepted ({response.get('status', 'unknown')}). Submission ID: {submission}")
    command("xcrun", "stapler", "staple", dmg)
    command("xcrun", "stapler", "validate", dmg)
    command("spctl", "--assess", "--type", "open", "--context", "context:primary-signature", "-v", dmg)
    data = dict(schema=1, releaseVersion=manifest["releaseVersion"], build=str(manifest["build"]),
                source=manifest["source"], repository=manifest["repository"], tag=manifest["tag"], archive=name,
                downloadURL=f"https://github.com/{manifest['repository']}/releases/download/{manifest['tag']}/{name}",
                sha256=hashlib.sha256(dmg.read_bytes()).hexdigest(), size=dmg.stat().st_size,
                volumeName=built["volumeName"], notarizationID=submission, stapled=True,
                appArchive=manifest["archive"], appSHA256=manifest["sha256"])
    validate_disk_image_manifest(data, manifest)
    target = output / "disk-image"
    target.mkdir()
    (target / name).write_bytes(dmg.read_bytes())
    if hashlib.sha256((target / name).read_bytes()).hexdigest() != data["sha256"]:
        raise DistributionError("The disk image changed while it was copied.")
    (target / "annotator-disk-image.json").write_text(json.dumps(data, indent=2) + "\n")
    return data


def validate_manifest(data, repo=None):
    version = data.get("releaseVersion", "")
    _, stage, _ = release.parse_version(version)
    build = str(data.get("build", ""))
    if not re.fullmatch(r"[1-9][0-9]*", build):
        raise DistributionError("Invalid manifest build.")
    expected_track = "stable" if stage == "stable" else "preview"
    destination = repository(data.get("repository", ""))
    # Releases up to alpha.6 are Apple Silicon only; later ones may be universal (Apple Silicon and Intel).
    architecture = data.get("architecture")
    if architecture not in packager.ARCHIVE_SUFFIXES:
        raise DistributionError("Only verified distribution artifacts are accepted.")
    name = packager.archive_name("annotator", version, build, architecture, "zip")
    url = f"https://github.com/{destination}/releases/download/v{version}/{name}"
    if repo and destination != repo:
        raise DistributionError("Manifest points to a different distribution repository.")
    if (data.get("channel"), data.get("identifier"), data.get("track")) != (
            "release", "com.dorlugasigal.annotator", expected_track):
        raise DistributionError("Only verified distribution artifacts are accepted.")
    if data.get("notarizationVerified") is not True or data.get("archive") != name or data.get("downloadURL") != url:
        raise DistributionError("Manifest archive URL or notarization status is invalid.")
    if not re.fullmatch(r"[0-9a-f]{64}", data.get("sha256", "")) or not re.fullmatch(r"[0-9a-f]{40}", data.get("source", "")):
        raise DistributionError("Manifest checksum/source is invalid.")
    if type(data.get("size")) is not int or data["size"] <= 0 or len(decode_secret(data.get("edSignature", ""))) != 64:
        raise DistributionError("Archive size or Sparkle signature is invalid.")
    website_url(data.get("homepage", ""))
    if data.get("tag") != f"v{version}":
        raise DistributionError("Release tag does not match the artifact version.")
    return data


def validate_disk_image_manifest(data, manifest):
    manifest = validate_manifest(manifest)
    version = data.get("releaseVersion", "")
    build = str(data.get("build", ""))
    name = packager.archive_name("annotator", version, build, manifest["architecture"], "dmg")
    url = f"https://github.com/{manifest['repository']}/releases/download/v{version}/{name}"
    for key in ("releaseVersion", "source", "repository", "tag"):
        if data.get(key) != manifest.get(key):
            raise DistributionError("Disk image manifest does not match the app manifest.")
    if build != str(manifest["build"]) or data.get("appArchive") != manifest["archive"] or data.get("appSHA256") != manifest["sha256"]:
        raise DistributionError("Disk image manifest does not match the ZIP archive.")
    if data.get("schema") != 1 or data.get("archive") != name or data.get("downloadURL") != url:
        raise DistributionError("Disk image archive URL is invalid.")
    if data.get("volumeName") != "Annotator" or data.get("stapled") is not True:
        raise DistributionError("Disk image volume or stapling state is invalid.")
    if not re.fullmatch(r"[0-9a-f]{64}", data.get("sha256", "")) or type(data.get("size")) is not int or data["size"] <= 0:
        raise DistributionError("Disk image checksum or size is invalid.")
    submission = data.get("notarizationID", "")
    if not re.fullmatch(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", submission):
        raise DistributionError("Disk image notarization ID is invalid.")
    return data


def update_notes(data, notes=None):
    """The Mac notes of the manifest's release as HTML for Sparkle's update window; fails closed without them."""
    if notes is None:
        notes = json.loads((ROOT / "releases/notes.json").read_text())
    entry = next((row for row in notes.get("releases", []) if isinstance(row, dict)
                  and row.get("version") == data["releaseVersion"] and str(row.get("build")) == str(data["build"])), None)
    if entry is None:
        raise DistributionError("releases/notes.json has no notes for this release. Add them before building the update feed.")
    try:
        release.changes(entry["changes"])
        release.strings(entry["knownIssues"], "knownIssues", allow_empty=True)
    except (KeyError, release.ReleaseError) as error:
        raise DistributionError("The release notes for this release are not valid.") from error

    def section(title, items):
        return f"<h3>{html.escape(title)}</h3><ul>" + "".join(f"<li>{html.escape(item)}</li>" for item in items) + "</ul>"
    parts = [section(category, entry["changes"][category]) for category in release.CATEGORIES if category in entry["changes"]]
    if entry["knownIssues"]:
        parts.append(section("Known limits", entry["knownIssues"]))
    return "".join(parts)


def appcast(data, notes=None):
    validate_manifest(data)
    rss = ET.Element("rss", version="2.0")
    channel = ET.SubElement(rss, "channel")
    ET.SubElement(channel, "title").text = f"Annotator {data['track']} updates"
    item = ET.SubElement(channel, "item")
    ET.SubElement(item, "title").text = data["releaseVersion"]
    # Sparkle shows the item's description as the release notes in its update window.
    ET.SubElement(item, "description").text = update_notes(data, notes)
    ET.SubElement(item, f"{{{SPARKLE}}}version").text = str(data["build"])
    ET.SubElement(item, f"{{{SPARKLE}}}shortVersionString").text = data["releaseVersion"]
    ET.SubElement(item, f"{{{SPARKLE}}}minimumSystemVersion").text = "14.0.0"
    ET.SubElement(item, "link").text = f"https://github.com/{data['repository']}/releases/tag/{data['tag']}"
    ET.SubElement(item, "enclosure", url=data["downloadURL"], length=str(data["size"]),
                  type="application/octet-stream", attrib={f"{{{SPARKLE}}}edSignature": data["edSignature"]})
    return ET.tostring(rss, encoding="unicode", xml_declaration=True) + "\n"


def gh_json(*args):
    return json.loads(command("gh", *args))


def published_manifests(repo):
    releases = gh_json("api", "--paginate", "--slurp", f"repos/{repo}/releases?per_page=100")
    rows = [row for page in releases for row in page]
    result = []
    for row in rows:
        if row.get("draft"):
            continue
        asset = next((a for a in row.get("assets", []) if a["name"] == "annotator-manifest.json"), None)
        if asset is None:
            continue
        data = gh_json("api", "-H", "Accept: application/octet-stream", f"repos/{repo}/releases/assets/{asset['id']}")
        validate_manifest(data, repo)
        if row["tag_name"] != data["tag"] or row["prerelease"] != (data["track"] == "preview"):
            raise DistributionError("Published release metadata disagrees with its manifest.")
        archive = next((a for a in row["assets"] if a["name"] == data["archive"]), None)
        if not archive or archive["size"] != data["size"] or archive["browser_download_url"] != data["downloadURL"]:
            raise DistributionError("Published archive is missing or mismatched.")
        disk_asset = next((a for a in row.get("assets", []) if a["name"] == "annotator-disk-image.json"), None)
        if disk_asset is not None:
            disk = gh_json("api", "-H", "Accept: application/octet-stream", f"repos/{repo}/releases/assets/{disk_asset['id']}")
            validate_disk_image_manifest(disk, data)
            dmg = next((a for a in row["assets"] if a["name"] == disk["archive"]), None)
            if not dmg or dmg["size"] != disk["size"] or dmg["browser_download_url"] != disk["downloadURL"]:
                raise DistributionError("Published disk image is missing or mismatched.")
            data = {**data, "diskImage": disk}
        windows_asset = next((a for a in row.get("assets", []) if a["name"] == winget.DESCRIPTION_FILE), None)
        if windows_asset is not None:
            windows = gh_json("api", "-H", "Accept: application/octet-stream", f"repos/{repo}/releases/assets/{windows_asset['id']}")
            data = {**data, "windows": validate_windows_description(windows, data, row["assets"])}
        result.append(data)
    return result


def validate_windows_description(windows, data, assets):
    """The portable Windows files of a published release (WIN-DST-09), checked against its Mac manifest and assets."""
    if not isinstance(windows, dict) or windows.get("schemaVersion") != 1 or windows.get("releaseVersion") != data["releaseVersion"] \
            or str(windows.get("build")) != str(data["build"]) or not isinstance(windows.get("files"), list):
        raise DistributionError("Published Windows files disagree with the release manifest.")
    published = {asset["name"]: asset for asset in assets}
    architectures = set()
    for row in windows["files"]:
        if not isinstance(row, dict) or row.get("architecture") not in winget.ARCHITECTURES \
                or row.get("file") != winget.zip_name(data["releaseVersion"], row["architecture"]):
            raise DistributionError("Published Windows files are misnamed.")
        asset = published.get(row["file"])
        if asset is None or asset["size"] != row.get("size"):
            raise DistributionError("A published Windows ZIP is missing or mismatched.")
        architectures.add(row["architecture"])
    if "x64" not in architectures:
        raise DistributionError("Published Windows files have no x64 ZIP.")
    return windows


def windows_feed(data):
    """What a portable Windows copy reads to learn about a newer release; it downloads nothing from it."""
    return {"schemaVersion": 1, "track": data["track"], "releaseVersion": data["releaseVersion"], "build": int(data["build"]),
            "downloadPage": f"{data['homepage']}/downloads/",
            "releaseURL": f"https://github.com/{data['repository']}/releases/tag/{data['tag']}"}


def publish(folder, disk_image_folder=None, windows_folder=None):
    if os.environ.get("PUBLICATION_ENABLED") != "true":
        raise DistributionError("Publication is disabled.")
    repo, site = configuration()
    data = validate_manifest(json.loads((folder / "annotator-manifest.json").read_text()), repo)
    if data["homepage"] != site:
        raise DistributionError("Artifact and website destinations differ.")
    if data["source"] != os.environ.get("CANDIDATE_SHA"):
        raise DistributionError("Artifact source differs from the workflow's validated candidate.")
    archive = folder / data["archive"]
    if hashlib.sha256(archive.read_bytes()).hexdigest() != data["sha256"] or archive.stat().st_size != data["size"]:
        raise DistributionError("Archive checksum or length mismatch; refusing upload.")
    disk_data = None
    disk_files = []
    if disk_image_folder is not None:
        disk_image_folder = Path(disk_image_folder)
        disk_data = validate_disk_image_manifest(json.loads((disk_image_folder / "annotator-disk-image.json").read_text()), data)
        disk_archive = disk_image_folder / disk_data["archive"]
        if hashlib.sha256(disk_archive.read_bytes()).hexdigest() != disk_data["sha256"] or disk_archive.stat().st_size != disk_data["size"]:
            raise DistributionError("Disk image checksum or length mismatch; refusing upload.")
        disk_files = [disk_archive, disk_image_folder / "annotator-disk-image.json"]
    windows_files = []
    windows_data = None
    if windows_folder is not None:
        # The portable Windows release (WIN-DST-09): its ZIPs, built from the same candidate, go on the same release.
        try:
            windows_data = winget.verify(Path(windows_folder), data["source"])
        except winget.WingetError as error:
            raise DistributionError(f"Windows files: {error}") from error
        windows_files = [Path(windows_folder) / row["file"] for row in windows_data["files"]] + [Path(windows_folder) / winget.DESCRIPTION_FILE]
    details = gh_json("api", f"repos/{repo}")
    if details.get("private") is not False:
        raise DistributionError("Public distribution requires a public release repository.")
    existing = gh_json("api", "--paginate", "--slurp", f"repos/{repo}/releases?per_page=100")
    found = next((row for page in existing for row in page if row["tag_name"] == data["tag"]), None)
    files = [archive, folder / "annotator-manifest.json", folder / "release-notes.md",
             folder / "SHA256SUMS", folder / "appcast.xml"] + disk_files + windows_files
    if any(not path.is_file() or path.is_symlink() for path in files):
        raise DistributionError("Release assets must be ordinary files in the verified artifact.")
    marker = f"\n\nCandidate: `{data['source']}`\nArchive SHA-256: `{data['sha256']}`\n"
    if disk_data:
        marker += f"Disk image SHA-256: `{disk_data['sha256']}`\n"
    for row in (windows_data or {}).get("files", []):
        marker += f"Windows {row['architecture']} ZIP SHA-256: `{row['sha256']}`\n"
    body = (folder / "release-notes.md").read_text().rstrip() + marker
    if found:
        if found["body"] != body or found["prerelease"] != (data["track"] == "preview"):
            raise DistributionError("Existing release belongs to a different immutable candidate.")
        present = {asset["name"] for asset in found["assets"]}
        if present - {path.name for path in files}:
            raise DistributionError("Existing release has unexpected assets; manual review is required.")
        with tempfile.TemporaryDirectory(prefix="annotator-existing-release-") as tmp:
            for path in files:
                if path.name not in present:
                    continue
                command("gh", "release", "download", data["tag"], "--repo", repo, "--pattern", path.name, "--dir", tmp)
                if hashlib.sha256((Path(tmp) / path.name).read_bytes()).digest() != hashlib.sha256(path.read_bytes()).digest():
                    raise DistributionError("Existing release asset differs; refusing to replace it.")
        if not found["draft"]:
            # The release repository is also the source repository: its tag must name the built commit.
            tagged = gh_json("api", f"repos/{repo}/commits/{data['tag']}").get("sha")
            if tagged != data["source"]:
                raise DistributionError("The published release tag doesn't point at the built candidate.")
            if len(present) != len(files):
                raise DistributionError("Published release is incomplete; do not silently rewrite it.")
            return
    else:
        present = set()
    previous = published_manifests(repo)
    if any(int(row["build"]) >= int(data["build"]) for row in previous):
        raise DistributionError("Release build must exceed every published build.")
    if not found:
        command("gh", "release", "create", data["tag"], "--repo", repo, "--draft", "--target", data["source"],
                "--title", f"Annotator {data['releaseVersion']}", "--notes-file", "-",
                "--latest=false", *(["--prerelease"] if data["track"] == "preview" else []), input=body)
    # Only explicit deliverables can leave the private repository. Never upload logs or source.
    missing = [path for path in files if path.name not in present]
    if missing:
        command("gh", "release", "upload", data["tag"], "--repo", repo, *missing)
    with tempfile.TemporaryDirectory(prefix="annotator-release-verify-") as tmp:
        command("gh", "release", "download", data["tag"], "--repo", repo, "--pattern", data["archive"], "--dir", tmp)
        if hashlib.sha256((Path(tmp) / data["archive"]).read_bytes()).hexdigest() != data["sha256"]:
            raise DistributionError("Uploaded archive verification failed; release remains a draft.")
        if disk_data:
            command("gh", "release", "download", data["tag"], "--repo", repo, "--pattern", disk_data["archive"], "--dir", tmp)
            if hashlib.sha256((Path(tmp) / disk_data["archive"]).read_bytes()).hexdigest() != disk_data["sha256"]:
                raise DistributionError("Uploaded disk image verification failed; release remains a draft.")
    command("gh", "release", "edit", data["tag"], "--repo", repo, "--draft=false",
            "--latest=false" if data["track"] == "preview" else "--latest")


def update_tap():
    if os.environ.get("PUBLICATION_ENABLED") != "true":
        raise DistributionError("Publication is disabled.")
    repo, _ = configuration()
    tap = repository(os.environ.get("HOMEBREW_TAP_REPOSITORY", ""))
    if tap == os.environ.get("GITHUB_REPOSITORY"):
        raise DistributionError("Write casks to the dedicated tap, never to the repository that runs this workflow.")
    if gh_json("api", f"repos/{tap}").get("private") is not False:
        raise DistributionError("The configured tap must be public.")
    published = published_manifests(repo)
    stable = [row for row in published if row["track"] == "stable"]
    # Use the alpha under the canonical name until a stable release exists.
    entries = stable or [row for row in published if row["track"] == "preview"]
    if not entries:
        raise DistributionError("No verified published release is available for the Annotator cask.")
    data = max(entries, key=lambda row: int(row["build"]))
    token = "annotator"
    content = packager.cask({**data, "token": token, "name": "Annotator.app"},
                           data["downloadURL"], data["sha256"], data["homepage"], "release")
    path = f"Casks/{token}.rb"
    message = f"Update {token} to {data['releaseVersion']} ({data['build']})"
    write_cask(tap, path, content, message)
    # Macs that tapped the release repository by URL, before the dedicated tap existed, still read
    # its copy. Keep that copy current, but never start one there. A workflow running in the release
    # repository leaves it alone, so it never commits to its own main branch; a maintainer's local run
    # updates it.
    if tap != repo and repo != os.environ.get("GITHUB_REPOSITORY"):
        write_cask(repo, path, content, message, create=False)
    return content


def tap_name(repo):
    """Homebrew's name for a tap repository: owner/homebrew-name is tapped as owner/name."""
    owner, name = repository(repo).split("/")
    return f"{owner}/{name.removeprefix('homebrew-')}"


def cask_file(repo, path="Casks/annotator.rb"):
    """The repository's contents entry for the cask, or None when it has no such file."""
    listing = gh_json("api", f"repos/{repo}/git/trees/HEAD?recursive=1")
    if listing.get("truncated"):
        raise DistributionError("Tap listing is incomplete; refusing to guess existing cask state.")
    if not any(row["path"] == path for row in listing["tree"]):
        return None
    return gh_json("api", f"repos/{repo}/contents/{path}")


def published_cask(repo, path="Casks/annotator.rb"):
    current = cask_file(repo, path)
    return None if current is None else base64.b64decode(current["content"]).decode()


def write_cask(repo, path, content, message, create=True):
    current = cask_file(repo, path)
    if current is None and not create:
        return False
    request = {"message": message, "content": base64.b64encode(content.encode()).decode()}
    if current is not None:
        if base64.b64decode(current["content"]).decode() == content:
            return False
        request["sha"] = current["sha"]
    command("gh", "api", "--method", "PUT", f"repos/{repo}/contents/{path}", "--input", "-", input=json.dumps(request))
    return True


def website_metadata():
    repo, _ = configuration()
    entries = published_manifests(repo)
    output = {"stable": None, "preview": None}
    directory = ROOT / "website/public/updates"
    directory.mkdir(parents=True, exist_ok=True)
    for track in output:
        candidates = [row for row in entries if row["track"] == track]
        if not candidates:
            stale = directory / f"{track}.xml"
            if stale.exists():
                stale.unlink()
            continue
        data = max(candidates, key=lambda row: int(row["build"]))
        output[track] = {key: data[key] for key in ("releaseVersion", "build", "downloadURL", "sha256", "track", "architecture")}
        if data.get("diskImage"):
            output[track].update(diskImageURL=data["diskImage"]["downloadURL"],
                                 diskImageSHA256=data["diskImage"]["sha256"],
                                 diskImageSize=data["diskImage"]["size"])
        (directory / f"{track}.xml").write_text(appcast(data))
    # Portable Windows copies check this feed (WIN-SYS-03); it names the newest release with Windows files.
    for track in output:
        feed = directory / f"windows-{track}.json"
        candidates = [row for row in entries if row["track"] == track and row.get("windows")]
        if not candidates:
            if feed.exists():
                feed.unlink()
            continue
        feed.write_text(json.dumps(windows_feed(max(candidates, key=lambda row: int(row["build"]))), indent=2) + "\n")
    (ROOT / "website/src/data/distribution.json").write_text(json.dumps(output, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    for action in ("preflight", "sign"):
        p = sub.add_parser(action)
        p.add_argument("--version", required=True)
        p.add_argument("--candidate", required=True)
        if action == "sign":
            p.add_argument("--output", type=Path, required=True)
            p.add_argument("--recovery", type=Path, help="Fresh folder that keeps the archive sent to Apple and its submission ID.")
    p = sub.add_parser("publish")
    p.add_argument("--folder", type=Path, required=True)
    p.add_argument("--disk-image-folder", type=Path)
    p.add_argument("--windows-folder", type=Path)
    sub.add_parser("tap")
    sub.add_parser("website")
    args = parser.parse_args()
    try:
        if args.command == "preflight": print(json.dumps(preflight(args.version, args.candidate), indent=2))
        elif args.command == "sign": sign_and_package(args.version, args.candidate, args.output.resolve(),
                                                      args.recovery.resolve() if args.recovery else None)
        elif args.command == "publish": publish(args.folder.resolve(), args.disk_image_folder.resolve() if args.disk_image_folder else None,
                                                args.windows_folder.resolve() if args.windows_folder else None)
        elif args.command == "tap": update_tap()
        elif args.command == "website": website_metadata()
    except (DistributionError, packager.PackagingError, release.ReleaseError, OSError, ValueError, KeyError) as error:
        print(f"Distribution stopped: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
