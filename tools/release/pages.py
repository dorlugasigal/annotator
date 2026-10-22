#!/usr/bin/env python3
"""Sync only verified generated files into a separate, approved user Pages repo."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys
from html.parser import HTMLParser
from urllib.parse import urlsplit

from distribution import DistributionError, command, gh_json, repository, website_url

ROOT = Path(__file__).resolve().parents[2]
ALLOWED = {".html", ".css", ".js", ".svg", ".jpg", ".jpeg", ".png", ".webp", ".ico",
           ".woff", ".woff2", ".mp4", ".vtt", ".glb", ".xml"}
SPECIAL = {"fonts/OFL-bricolage.txt", "downloads/annotator-skill.zip"}


class LocalLinks(HTMLParser):
    def __init__(self, prefix):
        super().__init__()
        self.prefix = "/" + prefix.strip("/") + "/" if prefix else "/"

    def handle_starttag(self, tag, attrs):
        for name, value in attrs:
            if name in ("href", "src", "poster") and value and value.startswith("/"):
                if value.startswith("//") or not value.startswith(self.prefix):
                    raise DistributionError("Generated HTML contains a local URL outside the configured site path.")


def verify_paths(directory, site):
    for path in directory.rglob("*.html"):
        LocalLinks(site_prefix(site)).feed(path.read_text())


def site_prefix(url):
    prefix = urlsplit(website_url(url)).path.strip("/")
    if not prefix:
        raise DistributionError("Site sync requires a nonempty subfolder, such as /annotator/.")
    return prefix


def inventory(directory):
    if directory.is_symlink() or not directory.is_dir():
        raise DistributionError("The generated site must be an ordinary directory.")
    files = {}
    for path in sorted(directory.rglob("*")):
        if path.is_symlink():
            raise DistributionError("Static artifacts cannot contain symlinks.")
        if path.is_dir():
            continue
        relative = path.relative_to(directory).as_posix()
        if not re.fullmatch(r"[A-Za-z0-9_./-]+", relative) or any(part.startswith(".") for part in Path(relative).parts):
            raise DistributionError("Unexpected hidden or unsafe static path.")
        if path.suffix.lower() not in ALLOWED and relative not in SPECIAL:
            raise DistributionError(f"Unexpected generated file type: {relative}")
        if path.stat().st_size >= 90 * 1024 * 1024:
            raise DistributionError(f"Static asset exceeds the repository size budget: {relative}")
        data = path.read_bytes()
        if b"-----BEGIN PRIVATE KEY-----" in data or b"-----BEGIN RSA PRIVATE KEY-----" in data:
            raise DistributionError("Private keys cannot be published.")
        files[relative] = hashlib.sha256(data).hexdigest()
    if "index.html" not in files or len(files) > 1000:
        raise DistributionError("Site needs index.html and at most 1000 files.")
    return files


def prepare(output, site):
    site_prefix(site)
    if output.exists():
        raise DistributionError("Use a fresh site artifact directory.")
    directory = ROOT / "website/dist"
    files = inventory(directory)
    verify_paths(directory, site)
    source = command("git", "rev-parse", "HEAD").strip()
    if not re.fullmatch(r"[a-f0-9]{40}", source):
        raise DistributionError("Could not identify site source.")
    changed = command("git", "diff", "--name-only", "HEAD").splitlines()
    if set(changed) - {"website/src/data/distribution.json"}:
        raise DistributionError("Commit source changes before preparing a publishable site artifact.")
    output.mkdir(parents=True)
    shutil.copytree(directory, output / "site")
    (output / "receipt.json").write_text(json.dumps({
        "schemaVersion": 1, "source": source, "siteURL": website_url(site), "files": files
    }, indent=2) + "\n")


def verify(folder, site, source):
    site_prefix(site)
    if not re.fullmatch(r"[0-9a-f]{40}", source or "") or folder.is_symlink() or (folder / "receipt.json").is_symlink():
        raise DistributionError("Site verification needs a full source SHA and ordinary artifact paths.")
    receipt = json.loads((folder / "receipt.json").read_text())
    if receipt.get("schemaVersion") != 1 or receipt.get("siteURL") != website_url(site) or receipt.get("source") != source:
        raise DistributionError("Site receipt differs from the approved source or URL.")
    files = inventory(folder / "site")
    verify_paths(folder / "site", site)
    if receipt.get("files") != files:
        raise DistributionError("Site checksums do not match.")
    return files


def tree_updates(existing, files, prefix):
    if any(row["path"] == prefix and row.get("type") != "tree" for row in existing):
        raise DistributionError("The destination is not a directory.")
    if any(row["path"].startswith(prefix + "/") and row.get("type") not in ("tree", "blob") for row in existing):
        raise DistributionError("Submodules or unsupported entries inside the managed website need manual review.")
    old = {row["path"]: row for row in existing if row.get("type") == "blob" and row["path"].startswith(prefix + "/")}
    desired = {f"{prefix}/{name}": sha for name, sha in files.items()}
    deletes = [{"path": path, "mode": "100644", "type": "blob", "sha": None} for path in sorted(old.keys() - desired.keys())]
    return old, desired, deletes


def post(repo, endpoint, payload):
    return json.loads(command("gh", "api", "--method", "POST", f"repos/{repo}/{endpoint}",
                              "--input", "-", input=json.dumps(payload)))


def sync(folder, site, expected_source, *, local=False):
    if local:
        if os.environ.get("GITHUB_ACTIONS") == "true":
            raise DistributionError("Local website publication cannot run in Actions.")
        if command("git", "-C", ROOT, "rev-parse", "HEAD").strip() != expected_source:
            raise DistributionError("Local site publication requires the exact source checkout.")
        if command("git", "-C", ROOT, "status", "--porcelain").strip():
            raise DistributionError("Commit local source changes before publishing the generated site.")
    elif os.environ.get("WEBSITE_DEPLOY_ENABLED") != "true" or os.environ.get("GITHUB_REF") != "refs/heads/main":
        raise DistributionError("Site sync requires an enabled protected main-branch workflow.")
    destination = repository(os.environ.get("WEBSITE_REPOSITORY", ""))
    owner, name = destination.split("/")
    if destination == os.environ.get("GITHUB_REPOSITORY"):
        raise DistributionError("Never commit generated site files into the repository that runs this workflow.")
    if not re.fullmatch(r"[a-f0-9]{40}", expected_source or ""):
        raise DistributionError("Expected source must be a full commit SHA.")
    details = gh_json("api", f"repos/{destination}")
    if details.get("private") is not False:
        raise DistributionError("The destination must be explicitly approved and public.")
    prefix = site_prefix(site)
    parsed = urlsplit(website_url(site))
    if parsed.hostname.endswith(".github.io") and parsed.hostname.lower() != f"{owner.lower()}.github.io":
        raise DistributionError("Pages host does not match the destination owner.")
    if parsed.hostname.endswith(".github.io") and name.lower() != f"{owner.lower()}.github.io" and prefix != name:
        raise DistributionError("A project Pages URL must use the destination repository name as its base path.")
    files = verify(folder, site, expected_source)
    branch = details["default_branch"]
    if not re.fullmatch(r"[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*", branch):
        raise DistributionError("Unsafe branch name.")
    ref = gh_json("api", f"repos/{destination}/git/ref/heads/{branch}")
    parent = ref["object"]["sha"]
    commit = gh_json("api", f"repos/{destination}/git/commits/{parent}")
    tree = gh_json("api", f"repos/{destination}/git/trees/{commit['tree']['sha']}?recursive=1")
    if tree.get("truncated"):
        raise DistributionError("Cannot safely sync an incomplete tree.")
    old, desired, updates = tree_updates(tree["tree"], files, prefix)
    for path in desired:
        data = (folder / "site" / path.removeprefix(prefix + "/")).read_bytes()
        git_sha = hashlib.sha1(f"blob {len(data)}\0".encode() + data).hexdigest()
        if old.get(path, {}).get("sha") == git_sha and old[path].get("mode") == "100644":
            continue
        blob = post(destination, "git/blobs", {"content": base64.b64encode(data).decode(), "encoding": "base64"})
        if blob["sha"] != git_sha:
            raise DistributionError("Uploaded blob checksum differs.")
        updates.append({"path": path, "mode": "100644", "type": "blob", "sha": blob["sha"]})
    if not updates:
        print("Generated site is already up to date.")
        return parent
    new_tree = post(destination, "git/trees", {"base_tree": commit["tree"]["sha"], "tree": updates})
    new_commit = post(destination, "git/commits", {
        "message": f"Publish generated {prefix} site from {expected_source}",
        "tree": new_tree["sha"], "parents": [parent]
    })
    command("gh", "api", "--method", "PATCH", f"repos/{destination}/git/refs/heads/{branch}",
            "--input", "-", input=json.dumps({"sha": new_commit["sha"], "force": False}))
    print(f"Synced generated {prefix}/ files; the public repository handles Pages deployment.")
    return new_commit["sha"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["prepare", "verify", "sync", "sync-local"])
    parser.add_argument("--folder", type=Path, required=True)
    parser.add_argument("--site", required=True)
    parser.add_argument("--source")
    args = parser.parse_args()
    try:
        if args.action == "prepare": prepare(args.folder, args.site)
        elif args.action == "verify":
            verify(args.folder, args.site, args.source)
            print("Verified generated-site inventory.")
        else: sync(args.folder, args.site, args.source, local=args.action == "sync-local")
    except (DistributionError, OSError, ValueError, KeyError) as error:
        print(f"Website sync stopped: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
