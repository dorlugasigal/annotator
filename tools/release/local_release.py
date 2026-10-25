#!/usr/bin/env python3
"""Owner-run local releases: prepare, submit, status, recover, finalize. Never publishes."""
import argparse
from contextlib import contextmanager
import fcntl
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import stat
import sys
import tempfile
from urllib.parse import urlsplit

import distribution as d
import disk_image
import release

ROOT = d.ROOT
Error = d.DistributionError


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def safe_path(path):
    path = Path(os.path.abspath(path))
    if any(p.is_symlink() for p in (path, *path.parents)):
        raise Error("Symlink paths are not allowed. Choose an ordinary local directory.")
    return path


def ordinary(path):
    safe_path(path)
    if not path.is_file() or path.stat().st_nlink != 1:
        raise Error("A required release file is missing or unsafe.")


def tree_hash(app):
    safe_path(app)
    if not app.is_dir():
        raise Error("The saved signed app is missing.")
    rows = []
    for path in sorted(app.rglob("*")):
        mode = path.lstat().st_mode
        if stat.S_ISLNK(mode):
            target = os.readlink(path)
            if Path(target).is_absolute() or not path.resolve().is_relative_to(app) or not path.exists():
                raise Error("The saved app contains an unsafe symlink.")
            value = target
        elif stat.S_ISREG(mode):
            if path.stat().st_nlink != 1:
                raise Error("The saved app contains a hard-linked file.")
            value = digest(path)
        elif stat.S_ISDIR(mode):
            value = ""
        else:
            raise Error("The saved app contains a special file.")
        rows.append((str(path.relative_to(app)), mode, value))
    return hashlib.sha256(json.dumps(rows).encode()).hexdigest()


def write_once(path, value):
    data = (json.dumps(value, sort_keys=True, indent=2) + "\n").encode()
    with tempfile.NamedTemporaryFile(dir=path.parent, prefix=".state-") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())
        os.link(stream.name, path)
    fd = os.open(path.parent, os.O_RDONLY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def sync_files(folder):
    for path in folder.rglob("*"):
        if path.is_file() and not path.is_symlink():
            with path.open("rb") as stream:
                os.fsync(stream.fileno())


def bundle_settings(app, receipt):
    with (app / "Contents/Info.plist").open("rb") as stream:
        settings = plistlib.load(stream)
    if (settings.get("SUPublicEDKey") != receipt["publicKey"]
            or settings.get("SUFeedURL") != f"{receipt['homepage']}/updates/{receipt['track']}.xml"
            or settings.get("AnnotatorBuildStamp") != f"Release {receipt['source']}"):
        raise Error("The saved app's update settings or source stamp differ from the receipt.")
    if "websiteURL" in receipt and (
            settings.get("AnnotatorWebsiteURL") != receipt["websiteURL"] or
            settings.get("AnnotatorMetricsURL") != receipt["metricsURL"]):
        raise Error("The saved app's website or metrics destination differs from the receipt.")


def read_json(path):
    ordinary(path)
    value = json.loads(path.read_text())
    if not isinstance(value, dict):
        raise Error("Release state must be a JSON object.")
    return value


@contextmanager
def locked(folder):
    folder = safe_path(folder)
    if not folder.is_dir():
        raise Error("The release directory is missing.")
    fd = os.open(folder / ".lock", os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    try:
        if os.fstat(fd).st_nlink != 1 or not stat.S_ISREG(os.fstat(fd).st_mode):
            raise Error("The release lock is unsafe.")
        try:
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise Error("Another local release command is using this directory.") from error
        yield folder
    finally:
        os.close(fd)


def candidate(version, source):
    if not re.fullmatch(r"[0-9a-f]{40}", source):
        raise Error("Supply the full candidate commit SHA.")
    if d.command("git", "-C", ROOT, "rev-parse", "HEAD").strip() != source:
        raise Error("Check out the exact original candidate before preparing or finalizing.")
    actual_version, build = release.configured_version(ROOT)
    notes = release.load_notes(ROOT)
    release.verify_candidate(ROOT)
    if actual_version != version:
        raise Error("The requested version differs from the candidate's version.")
    return build, notes


def prepare(args):
    output = safe_path(args.folder)
    if output.exists():
        raise Error("Use a fresh release directory. Existing work will not be overwritten.")
    build, notes = candidate(args.version, args.candidate)
    repo, site = d.repository(args.repository), d.website_url(args.homepage)
    if repo == "dorlugasigal/annotator-code":
        raise Error("Use the public repository, not the archived private dorlugasigal/annotator-code.")
    if not re.fullmatch(r"[A-Z0-9]{10}", args.team) or not args.identity.startswith("Developer ID Application:"):
        raise Error("Supply a Developer ID Application identity and ten-character team ID.")
    if len(d.decode_secret(args.public_key)) != 32:
        raise Error("The Sparkle public key must decode to 32 bytes.")
    support = d.packager.https_url(args.support_url) if args.support_url else ""
    metrics = getattr(args, "metrics_url", "")
    if metrics:
        metrics = d.website_url(metrics)
        if metrics != site or urlsplit(metrics).path not in ("", "/") or urlsplit(metrics).hostname.endswith(".github.io"):
            raise Error("Metrics must use the approved combined HTTPS website origin.")
    track = "preview" if "-" in args.version else "stable"
    output.mkdir(mode=0o700, parents=True)
    with locked(output), tempfile.TemporaryDirectory(prefix="annotator-local-build-") as tmp:
        derived = Path(tmp).resolve() / "DerivedData"
        d.command("xcodebuild", "-quiet", "-project", ROOT / "apps/macos/Annotator.xcodeproj",
                  "-scheme", "Annotator", "-configuration", "Release", "-derivedDataPath", derived,
                  "CODE_SIGN_STYLE=Manual", f"CODE_SIGN_IDENTITY={args.identity}",
                  f"DEVELOPMENT_TEAM={args.team}", "CODE_SIGNING_REQUIRED=YES",
                  "OTHER_CODE_SIGN_FLAGS=--timestamp",
                  f"ANNOTATOR_UPDATE_FEED_URL={site}/updates/{track}.xml",
                  f"ANNOTATOR_UPDATE_PUBLIC_KEY={args.public_key}",
                  f"ANNOTATOR_BUILD_STAMP=Release {args.candidate}",
                  f"ANNOTATOR_ISSUES_URL=https://github.com/{repo}/issues/new",
                  f"ANNOTATOR_WEBSITE_URL={site}", f"ANNOTATOR_METRICS_URL={metrics}",
                  f"ANNOTATOR_SUPPORT_URL={support}", "build")
        built = derived / "Build/Products/Release/Annotator.app"
        metadata = d.packager.inspect_app(built, "release")
        if (metadata["releaseVersion"], int(metadata["build"])) != (args.version, build):
            raise Error("The built app does not match the candidate's version/build.")
        d.sign_distribution_bundle(built, args.identity, args.team)
        app = output / "Annotator.app"
        d.command("ditto", built, app)
        archive = output / "notarization.zip"
        d.command("ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", app, archive)
        restored = Path(tmp).resolve() / "archive-check"
        d.command("ditto", "-x", "-k", archive, restored)
        if tree_hash(restored / "Annotator.app") != tree_hash(app):
            raise Error("The submission archive does not preserve the signed app.")
        d.verify_distribution_bundle(restored / "Annotator.app", args.identity, args.team)
        signer = output / "sign_update"
        shutil.copy2(derived / "SourcePackages/artifacts/sparkledistribution/Sparkle/bin/sign_update", signer)
        # Recheck source after the build; never freeze a concurrently edited tree.
        candidate(args.version, args.candidate)
        (output / "release-notes.md").write_text(release.changelog(notes, args.version))
        receipt = dict(schema=1, version=args.version, build=build, source=args.candidate,
                       repository=repo, homepage=site, track=track, publicKey=args.public_key,
                       websiteURL=site, metricsURL=metrics,
                       identity=args.identity, team=args.team,
                       appHash=tree_hash(app), archiveHash=digest(archive),
                       signerHash=digest(signer), notesHash=digest(output / "release-notes.md"),
                       metadata=metadata)
        bundle_settings(app, receipt)
        sync_files(output)
        write_once(output / "receipt.json", receipt)
        write_once(output / "receipt.sha256.json", {"sha256": digest(output / "receipt.json")})
    print("Prepared and saved the signed app and submission archive. Nothing was submitted.")


def load(folder):
    receipt = read_json(folder / "receipt.json")
    if read_json(folder / "receipt.sha256.json") != {"sha256": digest(folder / "receipt.json")}:
        raise Error("The release receipt changed. Restore the original saved receipt.")
    fields = {"schema", "version", "build", "source", "repository", "homepage", "track",
              "publicKey", "identity", "team", "appHash", "archiveHash", "signerHash", "notesHash", "metadata"}
    extra = set(receipt) - fields
    if fields - set(receipt) or extra not in (set(), {"websiteURL", "metricsURL"}) or receipt["schema"] != 1:
        raise Error("Unsupported or incomplete release receipt.")
    release.parse_version(receipt["version"])
    if type(receipt["build"]) is not int or receipt["build"] <= 0 or not re.fullmatch(r"[0-9a-f]{40}", receipt["source"]):
        raise Error("Invalid receipt source or build.")
    d.repository(receipt["repository"])
    d.website_url(receipt["homepage"])
    if "websiteURL" in receipt and (receipt["websiteURL"] != receipt["homepage"] or
                                    receipt["metricsURL"] not in ("", receipt["homepage"])):
        raise Error("Invalid receipt website or metrics destination.")
    if receipt["track"] != ("preview" if "-" in receipt["version"] else "stable") or len(d.decode_secret(receipt["publicKey"])) != 32:
        raise Error("Invalid receipt update settings.")
    if not receipt["identity"].startswith("Developer ID Application:") or not re.fullmatch(r"[A-Z0-9]{10}", receipt["team"]):
        raise Error("Invalid receipt signing identity or team.")
    for name, field in (("notarization.zip", "archiveHash"), ("sign_update", "signerHash"),
                        ("release-notes.md", "notesHash")):
        ordinary(folder / name)
        if digest(folder / name) != receipt[field]:
            raise Error("A saved release artifact changed. Restore the original bytes.")
    if tree_hash(folder / "Annotator.app") != receipt["appHash"]:
        raise Error("The saved signed app changed. Restore the original bytes.")
    metadata = d.packager.inspect_app(folder / "Annotator.app", "release")
    if metadata != receipt["metadata"] or (metadata["releaseVersion"], int(metadata["build"])) != (receipt["version"], receipt["build"]):
        raise Error("The saved app metadata differs from the receipt.")
    bundle_settings(folder / "Annotator.app", receipt)
    d.verify_distribution_bundle(folder / "Annotator.app", receipt["identity"], receipt["team"])
    return receipt


def credentials(args, folder):
    key = safe_path(args.notary_key)
    ordinary(key)
    if key.is_relative_to(folder) or key.is_relative_to(ROOT):
        raise Error("Keep the existing notary private key outside the repository and release directory.")
    if not re.fullmatch(r"[A-Za-z0-9]{10,}", args.key_id):
        raise Error("Supply the App Store Connect key ID.")
    submission_id(args.issuer)
    return ("--key", key, "--key-id", args.key_id, "--issuer", args.issuer)


def submission_id(value):
    if not isinstance(value, str) or not re.fullmatch(
            r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", value):
        raise Error("Supply a valid submission UUID.")
    return value


def notary(action, target, auth):
    raw = d.command("xcrun", "notarytool", action, target, *auth, "--output-format", "json")
    try:
        value = json.loads(raw)
    except ValueError as error:
        raise Error("Apple returned an unreadable response. No response text was logged.") from error
    if not isinstance(value, dict):
        raise Error("Apple returned an unexpected response.")
    return value


def attempt(folder, receipt):
    value = read_json(folder / "submit-attempt.json")
    if value != {"receiptHash": digest(folder / "receipt.json"), "archiveHash": receipt["archiveHash"]}:
        raise Error("The submission attempt does not match the saved release.")


def submit(folder, receipt, auth):
    marker = folder / "submit-attempt.json"
    if marker.exists() or marker.is_symlink():
        raise Error("Submission was already attempted. Use status or recover; do not submit again.")
    write_once(marker, {"receiptHash": digest(folder / "receipt.json"), "archiveHash": receipt["archiveHash"]})
    response = notary("submit", folder / "notarization.zip", auth)
    ident = submission_id(response.get("id"))
    write_once(folder / "submission.json", {"id": ident, "archiveHash": receipt["archiveHash"]})
    print(f"Submission ID: {ident}. Use status later; the archive is saved.")


def info(ident, folder, receipt, auth, require_hash=False):
    return submission_info(ident, auth, "notarization.zip", receipt["archiveHash"], require_hash=require_hash)


def submission_info(ident, auth, archive_name, archive_hash, require_hash=False):
    result = notary("info", ident, auth)
    state = result.get("status")
    if result.get("id") != ident or result.get("name") != archive_name or state not in ("Accepted", "In Progress", "Invalid", "Rejected"):
        raise Error("Apple's submission details do not match or have an unknown status.")
    if state != "In Progress":
        log = notary("log", ident, auth)
        if (log.get("jobId") != ident or log.get("sha256") != archive_hash
                or log.get("archiveFilename") != archive_name or log.get("status") != state):
            raise Error("Apple's diagnostic record does not match the original archive.")
    elif require_hash:
        raise Error("Recovery needs Apple's archive checksum. Wait for a terminal status, then recover.")
    print(f"Submission {ident}: {state}. Diagnostic ID: {ident}")
    return state


def attached(folder, receipt):
    attempt(folder, receipt)
    record = read_json(folder / "submission.json")
    if set(record) != {"id", "archiveHash"} or record["archiveHash"] != receipt["archiveHash"]:
        raise Error("The attached submission does not match the archive.")
    return submission_id(record["id"])


def recover(folder, receipt, auth, ident):
    attempt(folder, receipt)
    if (folder / "submission.json").exists():
        raise Error("A submission is already attached. Use status.")
    ident = submission_id(ident)
    info(ident, folder, receipt, auth, require_hash=True)
    write_once(folder / "submission.json", {"id": ident, "archiveHash": receipt["archiveHash"]})


def finalize(folder, receipt, auth, key):
    final = folder / "final"
    if final.exists() or final.is_symlink():
        raise Error("Final output already exists. Inspect it; it will not be overwritten.")
    build, notes = candidate(receipt["version"], receipt["source"])
    if build != receipt["build"] or release.changelog(notes, receipt["version"]) != (folder / "release-notes.md").read_text():
        raise Error("Original source metadata or release notes differ from the receipt.")
    key = safe_path(key)
    ordinary(key)
    if key.is_relative_to(folder) or key.is_relative_to(ROOT):
        raise Error("Keep the Sparkle private key outside the repository and release directory.")
    ident = attached(folder, receipt)
    if info(ident, folder, receipt, auth) != "Accepted":
        raise Error("Finalization requires Accepted. The saved app and archive were kept.")
    with tempfile.TemporaryDirectory(prefix=".finalize-", dir=folder) as tmp:
        work = Path(tmp)
        app = work / "Annotator.app"
        d.command("ditto", folder / "Annotator.app", app)
        if tree_hash(app) != receipt["appHash"]:
            raise Error("The finalization copy differs from the original signed app.")
        d.verify_distribution_bundle(app, receipt["identity"], receipt["team"])
        d.command("xcrun", "stapler", "staple", app)
        d.command("xcrun", "stapler", "validate", app)
        d.command("spctl", "--assess", "--type", "execute", app)
        name = d.packager.archive_name("annotator", receipt["version"], str(receipt["build"]),
                                       d.packager.architectures(app, receipt["metadata"]), "zip")
        tag = f"v{receipt['version']}"
        url = f"https://github.com/{receipt['repository']}/releases/download/{tag}/{name}"
        output = work / "final"
        report = d.packager.package(app, output, "release", url, receipt["homepage"])
        signer = folder / "sign_update"
        signature = d.command(signer, "--ed-key-file", key, "-p", output / name).strip()
        if len(d.decode_secret(signature)) != 64:
            raise Error("Sparkle did not return a valid archive signature.")
        d.command(signer, "--verify", "--ed-key-file", key, output / name, signature)
        d.command("xcrun", "swift", ROOT / "tools/release/verify-update.swift",
                  receipt["publicKey"], signature, output / name)
        report.update(source=receipt["source"], track=receipt["track"], tag=tag,
                      repository=receipt["repository"], notarizationID=ident,
                      edSignature=signature, size=(output / name).stat().st_size)
        d.validate_manifest(report)
        (output / "annotator-manifest.json").write_text(json.dumps(report, indent=2) + "\n")
        shutil.copyfile(folder / "release-notes.md", output / "release-notes.md")
        (output / "SHA256SUMS").write_text(f"{report['sha256']}  {name}\n")
        (output / "appcast.xml").write_text(d.appcast(report))
        load(folder)
        candidate(receipt["version"], receipt["source"])
        sync_files(output)
        output.rename(final)
        fd = os.open(folder, os.O_RDONLY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)
    print("Final artifacts are saved in final/. Nothing has been published.")


def disk_image_directory(folder):
    path = folder / "disk-image"
    path.mkdir(mode=0o700, exist_ok=True)
    return path


def disk_image_receipt_path(folder):
    return disk_image_directory(folder) / "disk-image-receipt.json"


def load_disk_image_receipt(folder, receipt):
    path = disk_image_receipt_path(folder)
    value = read_json(path)
    fields = {"schema", "releaseVersion", "build", "source", "archive", "sha256", "size",
              "volumeName", "sourceAppHash", "appArchive", "appSHA256", "repository", "tag"}
    if set(value) != fields or value["schema"] != 1:
        raise Error("Unsupported or incomplete disk-image receipt.")
    if (value["releaseVersion"], int(value["build"]), value["source"], value["repository"]) != (
            receipt["version"], receipt["build"], receipt["source"], receipt["repository"]):
        raise Error("Disk image receipt differs from the app release.")
    if value["volumeName"] != disk_image.VOLUME_NAME:
        raise Error("Disk image volume name changed.")
    final_manifest = d.validate_manifest(read_json(folder / "final" / "annotator-manifest.json"), receipt["repository"])
    if value["appArchive"] != final_manifest["archive"] or value["appSHA256"] != final_manifest["sha256"]:
        raise Error("Disk image receipt does not match the ZIP manifest.")
    dmg = disk_image_directory(folder) / value["archive"]
    ordinary(dmg)
    if digest(dmg) != value["sha256"] or dmg.stat().st_size != value["size"]:
        raise Error("Disk image bytes changed. Restore the original image.")
    return value


def build_disk_image(folder, receipt):
    target = disk_image_directory(folder)
    existing = target / "disk-image-receipt.json"
    if existing.exists():
        return load_disk_image_receipt(folder, receipt)
    final = folder / "final"
    manifest = d.validate_manifest(read_json(final / "annotator-manifest.json"), receipt["repository"])
    archive = final / manifest["archive"]
    ordinary(archive)
    if digest(archive) != manifest["sha256"] or archive.stat().st_size != manifest["size"]:
        raise Error("Final ZIP checksum or length changed.")
    name = d.packager.archive_name("annotator", receipt["version"], str(receipt["build"]), manifest["architecture"], "dmg")
    with tempfile.TemporaryDirectory(prefix=".dmg-build-", dir=target) as tmp:
        work = Path(tmp)
        extracted = work / "zip"
        d.command("ditto", "-x", "-k", archive, extracted)
        app = extracted / "Annotator.app"
        source_hash = tree_hash(app)
        report = disk_image.build(app, target, receipt["identity"], name)
    result = dict(schema=1, releaseVersion=receipt["version"], build=str(receipt["build"]),
                  source=receipt["source"], repository=receipt["repository"], tag=f"v{receipt['version']}",
                  archive=name, sha256=report["sha256"], size=report["size"],
                  volumeName=report["volumeName"], sourceAppHash=source_hash,
                  appArchive=manifest["archive"], appSHA256=manifest["sha256"])
    sync_files(target)
    write_once(existing, result)
    return load_disk_image_receipt(folder, receipt)


def disk_image_attempt(folder, receipt, dmg_receipt):
    marker = disk_image_directory(folder) / "submit-attempt.json"
    write_once(marker, {"receiptHash": digest(disk_image_receipt_path(folder)), "archiveHash": dmg_receipt["sha256"]})


def verify_disk_image_attempt(folder, dmg_receipt):
    value = read_json(disk_image_directory(folder) / "submit-attempt.json")
    expected = {"receiptHash": digest(disk_image_receipt_path(folder)), "archiveHash": dmg_receipt["sha256"]}
    if value != expected:
        raise Error("The disk image submission attempt does not match the saved image.")


def submit_disk_image(folder, receipt, auth):
    dmg_receipt = load_disk_image_receipt(folder, receipt)
    marker = disk_image_directory(folder) / "submit-attempt.json"
    if marker.exists() or marker.is_symlink():
        raise Error("Disk image submission was already attempted. Use status or recover; do not submit again.")
    disk_image_attempt(folder, receipt, dmg_receipt)
    response = notary("submit", disk_image_directory(folder) / dmg_receipt["archive"], auth)
    ident = submission_id(response.get("id"))
    write_once(disk_image_directory(folder) / "submission.json", {"id": ident, "archiveHash": dmg_receipt["sha256"]})
    print(f"Disk image submission ID: {ident}. Use status later; the image is saved.")


def attached_disk_image(folder, receipt):
    dmg_receipt = load_disk_image_receipt(folder, receipt)
    verify_disk_image_attempt(folder, dmg_receipt)
    record = read_json(disk_image_directory(folder) / "submission.json")
    if set(record) != {"id", "archiveHash"} or record["archiveHash"] != dmg_receipt["sha256"]:
        raise Error("The attached disk image submission does not match the image.")
    return submission_id(record["id"])


def info_disk_image(ident, folder, receipt, auth, require_hash=False):
    dmg_receipt = load_disk_image_receipt(folder, receipt)
    return submission_info(ident, auth, dmg_receipt["archive"], dmg_receipt["sha256"], require_hash=require_hash)


def finalize_disk_image(folder, receipt, auth):
    target = disk_image_directory(folder)
    final = target / "final"
    if final.exists() or final.is_symlink():
        raise Error("Final disk image output already exists. Inspect it; it will not be overwritten.")
    dmg_receipt = load_disk_image_receipt(folder, receipt)
    ident = attached_disk_image(folder, receipt)
    if info_disk_image(ident, folder, receipt, auth) != "Accepted":
        raise Error("Disk image finalization requires Accepted. The saved image was kept.")
    zip_manifest = d.validate_manifest(read_json(folder / "final" / "annotator-manifest.json"), receipt["repository"])
    manifest = dict(schema=1, releaseVersion=receipt["version"], build=str(receipt["build"]),
                    source=receipt["source"], repository=receipt["repository"], tag=f"v{receipt['version']}",
                    archive=dmg_receipt["archive"],
                    downloadURL=f"https://github.com/{receipt['repository']}/releases/download/v{receipt['version']}/{dmg_receipt['archive']}",
                    sha256=dmg_receipt["sha256"], size=dmg_receipt["size"],
                    volumeName=dmg_receipt["volumeName"], notarizationID=ident, stapled=True,
                    appArchive=zip_manifest["archive"], appSHA256=zip_manifest["sha256"])
    d.validate_disk_image_manifest(manifest, zip_manifest)
    with tempfile.TemporaryDirectory(prefix=".dmg-final-", dir=target) as tmp:
        work = Path(tmp)
        output = work / "final"
        output.mkdir()
        dmg = output / dmg_receipt["archive"]
        shutil.copy2(target / dmg_receipt["archive"], dmg)
        if digest(dmg) != dmg_receipt["sha256"]:
            raise Error("Disk image copy changed before stapling.")
        d.command("xcrun", "stapler", "staple", dmg)
        d.command("xcrun", "stapler", "validate", dmg)
        d.command("spctl", "--assess", "--type", "open", "--context", "context:primary-signature", "-v", dmg)
        manifest["sha256"] = digest(dmg)
        manifest["size"] = dmg.stat().st_size
        d.validate_disk_image_manifest(manifest, zip_manifest)
        (output / "annotator-disk-image.json").write_text(json.dumps(manifest, indent=2) + "\n")
        sync_files(output)
        output.rename(final)
        fd = os.open(target, os.O_RDONLY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)
    print("Final disk image is saved in disk-image/final/. Nothing has been published.")


def recover_disk_image(folder, receipt, auth, ident):
    dmg_receipt = load_disk_image_receipt(folder, receipt)
    verify_disk_image_attempt(folder, dmg_receipt)
    if (disk_image_directory(folder) / "submission.json").exists():
        raise Error("A disk image submission is already attached. Use disk-image-status.")
    ident = submission_id(ident)
    info_disk_image(ident, folder, receipt, auth, require_hash=True)
    write_once(disk_image_directory(folder) / "submission.json", {"id": ident, "archiveHash": dmg_receipt["sha256"]})


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    for action in ("prepare", "submit", "status", "recover", "finalize", "disk-image-status", "recover-disk-image"):
        p = sub.add_parser(action)
        p.add_argument("--folder", type=Path, required=True)
        if action == "prepare":
            for name in ("version", "candidate", "identity", "team", "public-key", "repository", "homepage"):
                p.add_argument(f"--{name}", required=True)
            p.add_argument("--support-url", default="")
            p.add_argument("--metrics-url", default="")
        else:
            p.add_argument("--notary-key", type=Path, required=True)
            p.add_argument("--key-id", required=True)
            p.add_argument("--issuer", required=True)
        if action in ("recover", "recover-disk-image"):
            p.add_argument("--submission-id", required=True)
        if action == "finalize":
            p.add_argument("--sparkle-key", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        if args.command == "prepare":
            prepare(args)
        else:
            with locked(args.folder) as folder:
                receipt = load(folder)
                auth = credentials(args, folder)
                if args.command == "submit":
                    submit(folder, receipt, auth)
                elif args.command == "status":
                    state = info(attached(folder, receipt), folder, receipt, auth)
                    return 1 if state in ("Invalid", "Rejected") else 0
                elif args.command == "recover":
                    recover(folder, receipt, auth, args.submission_id)
                elif args.command == "disk-image-status":
                    state = info_disk_image(attached_disk_image(folder, receipt), folder, receipt, auth)
                    return 1 if state in ("Invalid", "Rejected") else 0
                elif args.command == "recover-disk-image":
                    recover_disk_image(folder, receipt, auth, args.submission_id)
                else:
                    finalize(folder, receipt, auth, args.sparkle_key)
    except (Error, release.ReleaseError) as error:
        print(f"Local release stopped: {error} Saved submission files were kept.", file=sys.stderr)
        return 1
    except (d.packager.PackagingError, OSError, ValueError, KeyError, TypeError):
        # Do not print raw exception text: private paths/tool responses may contain secrets.
        print("Local release stopped. Check the saved files, candidate and command inputs. "
              "After a submit error, use recover with Apple's submission ID; never delete the attempt marker. "
              "No saved submission archive was removed.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
