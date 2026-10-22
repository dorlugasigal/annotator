"""Conservative changed-path selection for the validation jobs."""
import json
import os
from pathlib import Path
import re
import subprocess


def select(paths):
    shared = any(p.startswith((".github/", "tools/")) or "/" not in p for p in paths)
    return {
        "website": shared or any(p.startswith(("website/", "integrations/", "releases/")) for p in paths),
        "metrics": shared or any(p.startswith(("services/metrics/", "website/")) for p in paths),
        "macos": any(p.startswith(("apps/macos/", "reference/", "tools/macos/", "integrations/", "releases/",
                                   ".github/workflows/validate.yml")) or p in ("Makefile", "Brewfile") for p in paths),
        "windows": any(p.startswith(("apps/windows/", "reference/", "tools/windows/", "integrations/", "releases/",
                                     ".github/workflows/validate.yml")) for p in paths),
    }


def everything():
    return {suite: True for suite in select([])}


def scope(event_name, event, release_validation):
    if release_validation or event_name not in ("pull_request", "push"):
        print("Checking all source suites for release/manual validation.")
        return everything()
    # PR checkout is GitHub's merge commit: compare its tree with the actual
    # first parent, not an API file list that can be truncated.
    base = "HEAD^1" if event_name == "pull_request" else event.get("before", "")
    if event_name == "push" and not re.fullmatch(r"[0-9a-f]{40}", base):
        raise ValueError("Push event is missing a valid before SHA.")
    available = subprocess.run(["git", "cat-file", "-e", f"{base}^{{commit}}"],
                               capture_output=True)
    if available.returncode:
        print("::notice::Comparison commit is unavailable; running all cheap source suites.")
        return everything()
    paths = subprocess.check_output(
        ["git", "diff", "--name-only", "--no-renames", "-z", base, "HEAD"]
    ).decode("utf-8", errors="surrogateescape").split("\0")
    return select([p for p in paths if p])


def main():
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text())
    selected = scope(os.environ["GITHUB_EVENT_NAME"], event,
                     os.environ.get("RELEASE_VALIDATION") == "true")
    with Path(os.environ["GITHUB_OUTPUT"]).open("a") as output:
        for suite, enabled in selected.items():
            print(f"{suite}={str(enabled).lower()}", file=output)
            print(f"{suite}: {'run' if enabled else 'unchanged; skip cheap source suite'}")


if __name__ == "__main__":
    main()
