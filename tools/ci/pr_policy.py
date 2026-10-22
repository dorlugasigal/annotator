"""Pull request policy: a linked issue, and a release note for app changes.

The Pull request checks workflow runs this on every pull request:

    python3 tools/ci/pr_policy.py issue --event "$GITHUB_EVENT_PATH"
    python3 tools/ci/pr_policy.py notes --event "$GITHUB_EVENT_PATH" --files changed.txt

Maintainers can waive a check with the `no-issue` or `no-release-note` label.
"""

import argparse
import json
import re
import sys
from pathlib import Path

BOTS = {"dependabot[bot]", "github-actions[bot]"}
NOTES = "releases/notes.json"
# Paths that change what the apps do. Tests, tools and docs don't need a note.
APP_PATHS = ("apps/macos/Annotator/", "apps/macos/Sources/", "apps/macos/Packages/", "apps/windows/src/")
REFERENCE = re.compile(
    r"\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?|part of|refs?|related to)\b:?\s+"
    r"(?:[\w.-]+/[\w.-]+)?#\d+"
    r"|\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?|part of|refs?|related to)\b:?\s+"
    r"https://github\.com/[\w.-]+/[\w.-]+/issues/\d+",
    re.I)


def strip_comments(body):
    """The template's HTML comments contain an example reference; never count it."""
    return re.sub(r"<!--.*?-->", "", body or "", flags=re.S)


def linked_issue(body, labels, author):
    if author in BOTS or "no-issue" in labels:
        return None
    if REFERENCE.search(strip_comments(body)):
        return None
    return ("Link the issue this pull request works on, for example \"Fixes #123\" or \"Part of #123\". "
            "If there is no issue, open one first, or ask a maintainer to add the no-issue label.")


def release_note(files, labels, author):
    if author in BOTS or "no-release-note" in labels:
        return None
    app = sorted(path for path in files if path.startswith(APP_PATHS))
    if not app or NOTES in files:
        return None
    return (f"This pull request changes the apps ({app[0]}{' and more' if len(app) > 1 else ''}) "
            f"but not {NOTES}. Add the user-visible change to \"unreleased\" and run "
            "python3 tools/release/release.py changelog --write. If nothing changes for people "
            "using Annotator, ask a maintainer to add the no-release-note label.")


def pull_request(event_path):
    event = json.loads(Path(event_path).read_text())
    pr = event["pull_request"]
    return pr.get("body") or "", {label["name"] for label in pr.get("labels", [])}, pr["user"]["login"]


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("check", choices=["issue", "notes"])
    parser.add_argument("--event", required=True, type=Path)
    parser.add_argument("--files", type=Path, help="Changed paths, one per line (notes only).")
    args = parser.parse_args(argv)
    body, labels, author = pull_request(args.event)
    if args.check == "issue":
        problem = linked_issue(body, labels, author)
    else:
        if not args.files:
            parser.error("notes needs --files")
        files = {line.strip() for line in args.files.read_text().splitlines() if line.strip()}
        problem = release_note(files, labels, author)
    if problem:
        print(f"::error::{problem}")
        return 1
    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
