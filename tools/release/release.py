#!/usr/bin/env python3
"""Read-only release planning and deterministic local notes; never bumps or publishes."""
import argparse
import datetime
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
VERSION = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(alpha|beta|rc)\.([1-9][0-9]*))?")
CATEGORIES = ("Added", "Changed", "Fixed", "Removed")
STAGES = ("alpha", "beta", "rc", "stable")
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
    match = VERSION.fullmatch(value) if isinstance(value, str) else None
    if not match:
        raise ReleaseError(f"Invalid release version: {value!r}")
    major, minor, patch, stage, number = match.groups()
    return tuple(map(int, (major, minor, patch))), stage or "stable", int(number or 0)


def configured_version(root=ROOT):
    text = (root / "apps/macos/project.yml").read_text()
    def setting(name):
        matches = re.findall(rf"^    {name}: *(.*?) *$", text, re.MULTILINE)
        if len(matches) != 1:
            raise ReleaseError(f"Expected one base {name} setting in project.yml.")
        return matches[0].strip("\"'")
    base = setting("MARKETING_VERSION")
    suffix = setting("ANNOTATOR_PRERELEASE")
    build = setting("CURRENT_PROJECT_VERSION")
    parse_version(base)
    if "-" in base or (suffix and not re.fullmatch(r"(alpha|beta|rc)\.[1-9][0-9]*", suffix)):
        raise ReleaseError("Keep the numeric bundle version and prerelease setting separate.")
    if not re.fullmatch(r"[1-9][0-9]*", build):
        raise ReleaseError("Build must be a positive integer.")
    return base + (f"-{suffix}" if suffix else ""), int(build)


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
    previous_build = float("inf")
    for entry in notes["releases"]:
        if not isinstance(entry, dict) or set(entry) - OPTIONAL_FIELDS != ENTRY_FIELDS:
            raise ReleaseError("Release entries have missing or unknown fields.")
        parse_version(entry["version"])
        build = entry["build"]
        if type(build) is not int or build <= 0 or build >= previous_build:
            raise ReleaseError("Release builds must be positive, unique and newest first.")
        if entry["version"] in versions or build in builds:
            raise ReleaseError("A release label/build cannot be reused.")
        previous_build = build
        versions.add(entry["version"])
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
        raise ReleaseError("Newest release notes must match the configured app version/build.")
    return notes


def plan(version, build, change, target=None):
    base, stage, number = parse_version(version)
    if change not in ("fix", "feature", "breaking", "docs"):
        raise ReleaseError("Unknown change classification.")
    if target and target not in STAGES:
        raise ReleaseError("Unknown target stage.")
    if change == "docs" and (target is None or target == stage):
        return dict(current=version, proposed=version, build=build, action="keep",
                    reason="Documentation alone does not require a binary candidate.", approvalRequired=False)
    next_stage = target or stage
    if stage == "stable":
        if target not in (None, "alpha"):
            raise ReleaseError("Start a new release line at alpha; do not skip testing stages.")
        major, minor, patch = base
        if change == "breaking":
            base = (0, minor + 1, 0) if major == 0 else (major + 1, 0, 0)
        elif change == "feature":
            base = (major, minor + 1, 0)
        else:
            base = (major, minor, patch + 1)
        next_stage, number = "alpha", 1
    elif next_stage != stage:
        if STAGES.index(next_stage) != STAGES.index(stage) + 1:
            raise ReleaseError("Promote only one stage at a time after reviewing its evidence gates.")
        if change == "breaking":
            raise ReleaseError("Breaking changes need a new alpha line, not stage promotion.")
        number = 1
    elif change == "breaking":
        major, minor, _ = base
        base = (0, minor + 1, 0) if major == 0 else (major + 1, 0, 0)
        next_stage, number = "alpha", 1
    else:
        number += 1
    proposed = ".".join(map(str, base))
    if next_stage != "stable":
        proposed += f"-{next_stage}.{number}"
    return dict(current=version, proposed=proposed, build=build + 1, action="propose",
                approvalRequired=True, publicationAuthorized=False,
                reason="Numbering suggestion only; review changes, test evidence and owner approval.")


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
        lines += [f"## {entry['version']} (build {entry['build']}) - {entry['date']}", ""]
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
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    check = sub.add_parser("check")
    check.add_argument("--candidate", action="store_true", help="Also require a clean worktree, as packaging does.")
    proposal = sub.add_parser("plan")
    proposal.add_argument("--change", choices=["fix", "feature", "breaking", "docs"], required=True)
    proposal.add_argument("--stage", choices=STAGES)
    render = sub.add_parser("changelog")
    render.add_argument("--version")
    render.add_argument("--write", action="store_true", help="Regenerate CHANGELOG.md; never edits notes or version settings.")
    args = parser.parse_args()
    try:
        notes = load_notes()
        if args.command == "check":
            if (ROOT / "CHANGELOG.md").read_text() != changelog(notes):
                raise ReleaseError("CHANGELOG.md is stale. Run changelog --write.")
            if args.candidate:
                verify_candidate()
            print("Release metadata, bundled notes and changelog agree.")
        elif args.command == "plan":
            print(json.dumps(plan(*configured_version(), args.change, args.stage), indent=2))
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
