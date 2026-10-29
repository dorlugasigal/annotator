#!/usr/bin/env python3
"""Release versions, notes and the changelog.

Every release has a plain semantic version, X.Y.Z. `check`, `plan` and `changelog` only read; `prepare` writes the next
version and its notes entry for a release pull request. Nothing here signs or publishes.
"""
import argparse
import datetime
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROJECT = "apps/macos/project.yml"
PLAIN = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)")
# Releases up to 0.1.0-beta.17 had pre-release labels and hand-counted builds. Their notes, tags and published files
# keep them, so the tools still read them; nothing new may use one.
LEGACY = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-(alpha|beta|rc)\.([1-9][0-9]*)")
CATEGORIES = ("Added", "Changed", "Fixed", "Removed")
CHANGES = ("fix", "feature", "breaking", "docs")
# Optional per-entry notes for an edition other than the Mac app. The Mac app reads only the entry's own
# changes and knownIssues, so those always describe the Mac app; macOS is never a key here.
PLATFORMS = {"windows": "Windows"}
ENTRY_FIELDS = {"version", "build", "date", "changes", "knownIssues"}
# Releases before 0.1.0-beta.14 recorded the commit they were built from. A release now builds from the main-branch
# commit its Release run starts on, and the published release tag and manifest record that commit.
OPTIONAL_FIELDS = {"source", "platforms"}


class ReleaseError(ValueError):
    pass


def parse_version(value):
    """A release version's (major, minor, patch). Only a plain X.Y.Z is a release version."""
    match = PLAIN.fullmatch(value) if isinstance(value, str) else None
    if not match:
        raise ReleaseError(f"Release versions are plain X.Y.Z, like 0.2.0, not {value!r}.")
    return tuple(map(int, match.groups()))


def is_legacy(value):
    """Whether a version is one of the pre-release labels that releases up to 0.1.0-beta.17 used."""
    return isinstance(value, str) and LEGACY.fullmatch(value) is not None


def parse_any(value):
    """(major, minor, patch) of a release version or of a legacy pre-release label, for reading history."""
    match = LEGACY.fullmatch(value) if isinstance(value, str) else None
    return tuple(map(int, match.groups()[:3])) if match else parse_version(value)


def derived_build(version):
    """
    The build number of a version, where a platform needs a growing number (CFBundleVersion and Sparkle, the notes, the
    portable Windows feed). It is never counted: major * 10000 + minor * 100 + patch grows with every version while minor
    and patch stay at 99 or lower, and is above every legacy build (the last was 35).
    """
    major, minor, patch = parse_version(version)
    if minor > 99 or patch > 99:
        raise ReleaseError("Keep minor and patch at 99 or lower, so the derived build keeps growing.")
    return major * 10000 + minor * 100 + patch


def msix_version(version, build=None):
    """The Windows package (MSIX) version: (major + 1).minor.patch.0, so 0.2.0 is 1.2.0.0. A legacy label used its build."""
    major, minor, patch = parse_any(version)
    if is_legacy(version):
        if build is None or not re.fullmatch(r"[1-9][0-9]*", str(build)):
            raise ReleaseError(f"{version} needs its recorded build for its Windows package version.")
        patch = int(build)
    return f"{major + 1}.{minor}.{patch}.0"


def version_key(text):
    return tuple(int(part) for part in text.split("."))


def setting(text, name):
    matches = re.findall(rf"^    {name}: *(.*?) *$", text, re.MULTILINE)
    if len(matches) != 1:
        raise ReleaseError(f"Expected one base {name} setting in project.yml.")
    return matches[0].strip("\"'")


def configured_version(root=ROOT):
    """The shared version in apps/macos/project.yml and its build, which must be the derived one."""
    text = (root / PROJECT).read_text()
    if re.search(r"^ *ANNOTATOR_PRERELEASE:", text, re.MULTILINE):
        raise ReleaseError("Remove ANNOTATOR_PRERELEASE from project.yml: releases have no pre-release labels.")
    version = setting(text, "MARKETING_VERSION")
    build = derived_build(version)
    if setting(text, "CURRENT_PROJECT_VERSION") != str(build):
        raise ReleaseError(f"CURRENT_PROJECT_VERSION must be {build}, derived from {version}. Change the version with release.py prepare.")
    return version, build


def strings(value, name, allow_empty=False):
    if not isinstance(value, list) or (not allow_empty and not value):
        raise ReleaseError(f"{name} must be a list of nonempty strings.")
    if any(not isinstance(item, str) or not item.strip() or "\n" in item or "\r" in item for item in value):
        raise ReleaseError(f"{name} contains an empty, multiline or non-string item.")


def changes(value, allow_empty=False):
    if not isinstance(value, dict) or (not value and not allow_empty) or set(value) - set(CATEGORIES):
        raise ReleaseError("Changes must use Added, Changed, Fixed or Removed.")
    for name, items in value.items():
        strings(items, name)


def platforms(value):
    if not isinstance(value, dict) or not value or set(value) - set(PLATFORMS):
        raise ReleaseError(f"Platform notes must use {', '.join(PLATFORMS)}.")
    for name, notes in value.items():
        if not isinstance(notes, dict) or set(notes) != {"changes", "knownIssues"}:
            raise ReleaseError(f"{PLATFORMS[name]} notes need exactly changes and knownIssues.")
        changes(notes["changes"])
        strings(notes["knownIssues"], f"{PLATFORMS[name]} knownIssues", allow_empty=True)


def load_notes(root=ROOT):
    notes = json.loads((root / "releases/notes.json").read_text())
    if not isinstance(notes, dict) or set(notes) != {"schemaVersion", "unreleased", "releases"} or type(notes["schemaVersion"]) is not int or notes["schemaVersion"] != 1:
        raise ReleaseError("Unsupported release-notes schema.")
    changes(notes["unreleased"], allow_empty=True)
    if not isinstance(notes["releases"], list) or not notes["releases"]:
        raise ReleaseError("At least one release entry is required.")
    versions, builds = set(), set()
    newer = None
    legacy = False
    for entry in notes["releases"]:
        if not isinstance(entry, dict) or set(entry) - OPTIONAL_FIELDS != ENTRY_FIELDS:
            raise ReleaseError("Release entries have missing or unknown fields.")
        version, build = entry["version"], entry["build"]
        if type(build) is not int or build <= 0:
            raise ReleaseError("Release builds must be positive integers.")
        if is_legacy(version):
            legacy = True
        elif legacy:
            raise ReleaseError(f"{version} is listed below a legacy pre-release; newest releases come first.")
        elif build != derived_build(version):
            raise ReleaseError(f"{version} must record its derived build, {derived_build(version)}.")
        package = version_key(msix_version(version, build))
        if newer and (build >= newer[0] or package >= newer[1]):
            raise ReleaseError("Builds and Windows package versions must grow with every release, newest first.")
        if version in versions or build in builds:
            raise ReleaseError("A release version or build cannot be reused.")
        newer = (build, package)
        versions.add(version)
        builds.add(build)
        if not isinstance(entry["date"], str) or not re.fullmatch(r"\d{4}-\d{2}-\d{2}", entry["date"]):
            raise ReleaseError("Release date must be YYYY-MM-DD.")
        datetime.date.fromisoformat(entry["date"])
        if "source" in entry and not (isinstance(entry["source"], str) and re.fullmatch(r"[0-9a-f]{7,40}", entry["source"])):
            raise ReleaseError("A recorded source must be a commit hash.")
        changes(entry["changes"])
        strings(entry["knownIssues"], "knownIssues", allow_empty=True)
        if "platforms" in entry:
            platforms(entry["platforms"])
    version, build = configured_version(root)
    latest = notes["releases"][0]
    if (latest["version"], latest["build"]) != (version, build):
        raise ReleaseError("Newest release notes must match the configured app version.")
    return notes


def plan(version, change):
    """The next version for a kind of change: a numbering suggestion that changes nothing."""
    if change not in CHANGES:
        raise ReleaseError("Unknown change classification.")
    major, minor, patch = parse_version(version)
    if change == "docs":
        return dict(current=version, proposed=version, action="keep", approvalRequired=False,
                    reason="Documentation alone does not need a release.")
    if change == "fix":
        proposed = (major, minor, patch + 1)
    elif change == "feature" or major == 0:
        # Before 1.0.0 a breaking change moves the minor version, as semantic versioning allows.
        proposed = (major, minor + 1, 0)
    else:
        proposed = (major + 1, 0, 0)
    proposed = ".".join(map(str, proposed))
    return dict(current=version, proposed=proposed, build=derived_build(proposed), msix=msix_version(proposed),
                action="propose", approvalRequired=True, publicationAuthorized=False,
                reason="Numbering suggestion only; review the changes, test evidence and owner approval.")


def write_notes(root, notes):
    (root / "releases/notes.json").write_text(json.dumps(notes, indent=2, ensure_ascii=False) + "\n", newline="\n")


def prepare(version, root=ROOT, today=None):
    """
    Start a release pull request: write the version and its derived build to project.yml, move Unreleased into a dated
    entry that starts with the previous release's known limits, and regenerate CHANGELOG.md.
    """
    notes = load_notes(root)
    current, _ = configured_version(root)
    if parse_version(version) <= parse_version(current):
        raise ReleaseError(f"{version} must be newer than the current version, {current}.")
    if not notes["unreleased"]:
        raise ReleaseError("Unreleased has no changes. Add the release's changes to releases/notes.json first.")
    build = derived_build(version)
    previous = notes["releases"][0]
    if version_key(msix_version(version)) <= version_key(msix_version(previous["version"], previous["build"])):
        raise ReleaseError(f"{version} must have a higher Windows package version than {previous['version']}.")
    entry = {"version": version, "build": build, "date": (today or datetime.date.today()).isoformat(),
             "changes": notes["unreleased"], "knownIssues": list(previous["knownIssues"])}
    text = (root / PROJECT).read_text()
    for name, value in (("MARKETING_VERSION", version), ("CURRENT_PROJECT_VERSION", build)):
        text = re.sub(rf"^(    {name}: *).*$", rf"\g<1>{value}", text, count=1, flags=re.MULTILINE)
    (root / PROJECT).write_text(text, newline="\n")
    write_notes(root, {**notes, "unreleased": {}, "releases": [entry] + notes["releases"]})
    (root / "CHANGELOG.md").write_text(changelog(load_notes(root)), newline="\n")
    return entry


def render_changes(items, level="###"):
    lines = []
    for category in CATEGORIES:
        if category in items:
            lines += [f"{level} {category}", ""]
            lines += [f"- {item}" for item in items[category]]
            lines += [""]
    return lines


def render_notes(changes_, known_issues, level="###"):
    lines = render_changes(changes_, level)
    if known_issues:
        lines += [f"{level} Known limits", ""] + [f"- {item}" for item in known_issues] + [""]
    return lines


def changelog(notes, version=None):
    lines = [] if version else ["# Changelog", "", "Generated from `releases/notes.json`. Unreleased changes are not shipped.", "",
                              "## Unreleased", ""] + render_changes(notes["unreleased"])
    entries = notes["releases"]
    if version:
        entries = [entry for entry in entries if entry["version"] == version]
        if not entries:
            raise ReleaseError("No notes exist for that release.")
    for entry in entries:
        lines += [f"## {entry['version']} - {entry['date']}", ""]
        if "platforms" not in entry:
            lines += render_notes(entry["changes"], entry["knownIssues"])
            continue
        lines += ["### Mac", ""] + render_notes(entry["changes"], entry["knownIssues"], "####")
        for name, title in PLATFORMS.items():
            if name in entry["platforms"]:
                notes_ = entry["platforms"][name]
                lines += [f"### {title}", ""] + render_notes(notes_["changes"], notes_["knownIssues"], "####")
    return "\n".join(lines).rstrip() + "\n"


def verify_candidate(root=ROOT):
    """A release builds the checked-out commit as it is: the worktree must be clean."""
    result = subprocess.run(["git", "-C", str(root), "status", "--porcelain"], capture_output=True, text=True)
    if result.returncode:
        raise ReleaseError(f"Could not check the candidate: {result.stderr.strip()}")
    if result.stdout.strip():
        raise ReleaseError("Packaging requires a clean worktree. Commit the release candidate first.")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    check = sub.add_parser("check")
    check.add_argument("--candidate", action="store_true", help="Also require a clean worktree, as packaging does.")
    proposal = sub.add_parser("plan")
    proposal.add_argument("--change", choices=CHANGES, required=True)
    bump = sub.add_parser("prepare", help="Write the next version and its notes entry for a release pull request.")
    bump.add_argument("--version", required=True, help="The approved plain version, like 0.3.0.")
    render = sub.add_parser("changelog")
    render.add_argument("--version")
    render.add_argument("--write", action="store_true", help="Regenerate CHANGELOG.md; never edits notes or version settings.")
    args = parser.parse_args()
    try:
        if args.command == "prepare":
            entry = prepare(args.version)
            print(f"Prepared {entry['version']} (build {entry['build']}, Windows package {msix_version(entry['version'])}).")
            print("Review its known limits in releases/notes.json, then run make generate on a Mac for the Xcode project.")
            return 0
        notes = load_notes()
        if args.command == "check":
            if (ROOT / "CHANGELOG.md").read_text() != changelog(notes):
                raise ReleaseError("CHANGELOG.md is stale. Run changelog --write.")
            if args.candidate:
                verify_candidate()
            print("Release metadata, bundled notes and changelog agree.")
        elif args.command == "plan":
            print(json.dumps(plan(configured_version()[0], args.change), indent=2))
        elif args.write:
            if args.version:
                raise ReleaseError("--write regenerates the full changelog; omit --version.")
            (ROOT / "CHANGELOG.md").write_text(changelog(notes), newline="\n")
            print("Updated CHANGELOG.md.")
        else:
            print(changelog(notes, args.version), end="")
    except (ReleaseError, OSError, ValueError, TypeError) as error:
        print(f"Release workflow failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
