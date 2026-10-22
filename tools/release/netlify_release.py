"""Checksum-bound local Netlify artifacts and the legacy Sparkle-feed bridge."""
import base64
import hashlib
import html
import json
from pathlib import Path
import re
import shutil
import zipfile
from urllib.parse import urlsplit
from urllib.error import HTTPError, URLError
from urllib.request import urlopen

import distribution as d
import local_release as local
import pages

FUNCTIONS = {"admin-login", "admin-logout", "admin-summary", "aggregate-daily", "collect-app", "collect-web"}
ROUTES = {
    "admin-login": "/api/admin/login", "admin-logout": "/api/admin/logout",
    "admin-summary": "/api/admin/summary", "collect-app": "/api/metrics/app",
    "collect-web": "/api/metrics/web",
}


def site_url(value):
    try:
        value = d.website_url(value)
    except d.packager.PackagingError as error:
        raise d.DistributionError(str(error)) from error
    parts = urlsplit(value)
    if parts.path not in ("", "/") or parts.hostname.endswith(".github.io"):
        raise d.DistributionError("The Netlify site must use an approved HTTPS root URL, not Pages.")
    return value.rstrip("/")


def site_id(value):
    if not re.fullmatch(r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", value or ""):
        raise d.DistributionError("Supply the existing Netlify site UUID; this command never creates a site.")
    return value.lower()


def files_in(directory):
    local.safe_path(directory)
    if not directory.is_dir():
        raise d.DistributionError("The prepared Netlify directory is missing.")
    result = {}
    for path in sorted(directory.rglob("*")):
        if path.is_symlink():
            raise d.DistributionError("Netlify artifacts cannot contain symlinks.")
        if path.is_file():
            local.ordinary(path)
            relative = path.relative_to(directory).as_posix()
            if not re.fullmatch(r"[A-Za-z0-9_./-]+", relative):
                raise d.DistributionError("Unsafe artifact path.")
            result[relative] = local.digest(path)
    return result


def verify_functions(folder):
    manifest = local.read_json(folder / "manifest.json")
    entries = manifest.get("functions")
    if not isinstance(entries, list) or {entry.get("name") for entry in entries} != FUNCTIONS or len(entries) != 6:
        raise d.DistributionError("The artifact needs all six telemetry functions.")
    for entry in entries:
        if (entry.get("buildData", {}).get("runtimeAPIVersion") != 2 or
                not entry.get("buildData", {}).get("bootstrapVersion") or
                entry.get("invocationMode") != "stream" or
                not re.fullmatch(r"nodejs\d+\.x", entry.get("runtimeVersion", ""))):
            raise d.DistributionError("A function is missing its v2 runtime metadata.")
        route = ROUTES.get(entry["name"])
        if route and not any(item.get("pattern") == route for item in entry.get("routes", [])):
            raise d.DistributionError("A function is missing its API route.")
        if entry["name"] not in ("admin-logout", "aggregate-daily") and not entry.get("trafficRules"):
            raise d.DistributionError("A function is missing its rate-limit metadata.")
        archive = folder / (entry["name"] + ".zip")
        local.ordinary(archive)
        if Path(entry.get("path", "")).resolve() != archive.resolve():
            raise d.DistributionError("A function archive points outside the verified artifact.")
        try:
            with zipfile.ZipFile(archive) as package:
                if "NETLIFY-LICENSE.txt" not in package.namelist():
                    raise d.DistributionError("A function archive is missing its retained runtime notice.")
        except zipfile.BadZipFile as error:
            raise d.DistributionError("A function archive is not a valid ZIP.") from error
    if next(entry for entry in entries if entry["name"] == "aggregate-daily").get("schedule") != "15 0 * * *":
        raise d.DistributionError("The daily aggregation schedule is missing.")
    return manifest


def include_notices(folder, notice):
    data = notice.read_bytes()
    for name in sorted(FUNCTIONS):
        with zipfile.ZipFile(folder / (name + ".zip"), "a") as package:
            if "NETLIFY-LICENSE.txt" in package.namelist():
                raise d.DistributionError("Use newly bundled functions when adding retained notices.")
            info = zipfile.ZipInfo("NETLIFY-LICENSE.txt", (1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            package.writestr(info, data)


def prepare(checkout, output, site, source):
    site = site_url(site)
    checkout = local.safe_path(checkout)
    output = local.safe_path(output)
    if output.exists():
        raise d.DistributionError("Use a fresh Netlify artifact directory.")
    if d.command("git", "-C", checkout, "rev-parse", "HEAD").strip() != source:
        raise d.DistributionError("Netlify packaging needs the exact candidate checkout.")
    changes = d.command("git", "-C", checkout, "status", "--porcelain", "--untracked-files=all").splitlines()
    generated = {"website/src/data/distribution.json",
                 "website/public/updates/preview.xml", "website/public/updates/stable.xml"}
    if any(line[3:] not in generated for line in changes):
        raise d.DistributionError("Commit source changes before preparing Netlify artifacts.")
    directory = checkout / "website/dist"
    inventory = pages.inventory(directory)
    for path in directory.rglob("*.html"):
        pages.LocalLinks("").feed(path.read_text())
    if not {"admin/index.html", "admin/admin.js", "admin/admin.css"}.issubset(inventory):
        raise d.DistributionError("The combined website is missing the owner dashboard.")
    output.mkdir(parents=True)
    shutil.copytree(directory, output / "site")
    functions = output / "functions"
    cli = checkout / "services/metrics/node_modules/.bin/netlify"
    d.command(cli, "functions:build", "--src", checkout / "services/metrics/netlify/functions",
              "--functions", functions, cwd=checkout)
    include_notices(functions, checkout / "services/metrics/NETLIFY-LICENSE.txt")
    verify_functions(functions)
    shutil.copy2(checkout / "netlify.toml", output / "netlify.toml")
    contents = files_in(output)
    local.write_once(output / "receipt.json", {
        "schemaVersion": 1, "source": source, "siteURL": site, "files": contents,
    })
    return output


def verify(folder, site, source):
    folder = local.safe_path(folder)
    receipt = local.read_json(folder / "receipt.json")
    if (receipt.get("schemaVersion"), receipt.get("source"), receipt.get("siteURL")) != (1, source, site_url(site)):
        raise d.DistributionError("Netlify receipt differs from the approved source or host.")
    contents = files_in(folder)
    contents.pop("receipt.json", None)
    if receipt.get("files") != contents:
        raise d.DistributionError("Netlify artifact checksums do not match.")
    verify_functions(folder / "functions")
    return pages.inventory(folder / "site")


def check_remote(cli, ident, site):
    ident, site = site_id(ident), site_url(site)
    data = json.loads(d.command(cli, "api", "getSite", "--data", json.dumps({"site_id": ident})))
    if data.get("id") != ident:
        raise d.DistributionError("Netlify returned a different site.")
    urls = {data.get("ssl_url"), data.get("url")}
    # A site keeps its netlify.app address after it gains a custom domain; both are the same site.
    for host in (data.get("custom_domain"), data.get("default_domain")):
        if host:
            urls.add("https://" + host)
    if site not in {value.rstrip("/") for value in urls if isinstance(value, str)}:
        raise d.DistributionError("The approved homepage does not match the existing Netlify site.")


def deploy(folder, site, source, ident, cli):
    verify(folder, site, source)
    if d.command("git", "-C", local.ROOT, "rev-parse", "HEAD").strip() != source or \
            d.command("git", "-C", local.ROOT, "status", "--porcelain").strip():
        raise d.DistributionError("Netlify deployment requires the exact clean candidate source.")
    check_remote(cli, ident, site)
    # Bundle verified source, not ZIPs: the CLI cannot recover metadata from ZIPs.
    data = json.loads(d.command(
        cli, "deploy", "--no-build", "--prod", "--json", "--skip-functions-cache",
        "--site", site_id(ident), "--dir", folder / "site",
        "--functions", local.ROOT / "services/metrics/netlify/functions", cwd=local.ROOT))
    if not isinstance(data.get("deploy_id"), str) or data.get("site_id") != site_id(ident):
        raise d.DistributionError("Netlify did not confirm the expected deployment. Inspect the saved artifact before retrying.")
    verify_service(cli, ident, data["deploy_id"], site)
    return data["deploy_id"]


def verify_service(cli, ident, deployment, site):
    data = json.loads(d.command(cli, "api", "getDeploy", "--data",
                               json.dumps({"deploy_id": deployment})))
    expected = [{"name": "aggregate-daily", "cron": "15 0 * * *"}]
    if data.get("site_id") != site_id(ident) or data.get("function_schedules") != expected:
        raise d.DistributionError("The deployed metrics schedule is missing or the site differs.")
    try:
        with urlopen(site_url(site) + "/api/admin/summary", timeout=20):
            raise d.DistributionError("The deployed admin API did not require sign-in.")
    except HTTPError as error:
        with error:
            if error.code != 401:
                raise d.DistributionError(f"The deployed admin API returned HTTP {error.code}, expected 401.") from error
            try:
                body = json.loads(error.read(8192))
            except (ValueError, UnicodeError) as invalid:
                raise d.DistributionError("The deployed admin API returned an invalid sign-in response.") from invalid
        if body != {"error": "Sign in to view metrics."}:
            raise d.DistributionError("The deployed admin API returned an unexpected sign-in response.")
    except (URLError, TimeoutError) as error:
        raise d.DistributionError("The deployed admin API could not be reached.") from error


def prepare_bridge(artifact, output, legacy, target, source):
    files = verify(artifact, target, source)
    legacy = d.website_url(legacy)
    pages.site_prefix(legacy)
    if output.exists():
        raise d.DistributionError("Use a fresh update-feed bridge directory.")
    directory = output / "site"
    (directory / "updates").mkdir(parents=True)
    for path in ("updates/preview.xml", "updates/stable.xml"):
        if path in files:
            shutil.copy2(artifact / "site" / path, directory / path)
    if not any((directory / "updates").iterdir()):
        raise d.DistributionError("A migration bridge must preserve at least one verified update feed.")
    for route in ("", "docs", "downloads", "ai", "privacy", "support"):
        destination = site_url(target) + "/" + (route + "/" if route else "")
        escaped = html.escape(destination, quote=True)
        path = directory / route / "index.html"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(
            '<!doctype html><html lang="en"><head><meta charset="utf-8">'
            f'<meta http-equiv="refresh" content="0;url={escaped}"><link rel="canonical" href="{escaped}">'
            '<title>Annotator has moved</title></head><body>'
            f'<p><a href="{escaped}">Open the Annotator website</a>.</p></body></html>')
    local.write_once(output / "receipt.json", {
        "schemaVersion": 1, "source": source, "siteURL": legacy, "files": pages.inventory(directory),
    })
    pages.verify(output, legacy, source)
    return output


# Pages that another public repository shows, published from a folder in this repository. Website links
# in text files are written with the legacy host and pointed at the approved website here. The source
# repository holds its own README and community files, so only the Homebrew tap uses this.
LEGACY_SITE = "https://dorlugasigal.github.io/annotator"
# The Homebrew tap shows only its README. Releases write its cask; this never does.
TAP_PAGES = {"root": "packaging/homebrew-tap", "files": ("README.md",), "reporting": False}


def repository_files(site, pages):
    root = local.ROOT / pages["root"]
    files = {}
    for name in pages["files"]:
        data = (root / name).read_bytes()
        if not name.endswith((".png", ".jpg")):
            data = data.decode().replace(LEGACY_SITE, site).encode()
        files[name] = data
    return files


def git_blob(data):
    return hashlib.sha1(b"blob %d\0" % len(data) + data).hexdigest()


def repository_changes(repo, site, pages):
    """Read-only: the files and settings that differ from the public repository."""
    repo = d.repository(repo)
    site = site_url(site)
    details = d.gh_json("api", f"repos/{repo}")
    if details.get("private") is not False:
        raise d.DistributionError("The product README destination must be public.")
    branch = details["default_branch"]
    head = d.gh_json("api", f"repos/{repo}/git/ref/heads/{branch}")["object"]["sha"]
    tree = d.gh_json("api", f"repos/{repo}/git/commits/{head}")["tree"]["sha"]
    listing = d.gh_json("api", f"repos/{repo}/git/trees/{tree}?recursive=1")
    if listing.get("truncated"):
        raise d.DistributionError("The public repository listing is incomplete; review it before publishing.")
    current = {row["path"]: row["sha"] for row in listing["tree"] if row["type"] == "blob"}
    files = {name: data for name, data in repository_files(site, pages).items() if current.get(name) != git_blob(data)}
    meta = json.loads((local.ROOT / pages["root"] / "repository.json").read_text())
    settings = {"description": meta["description"], "homepage": site + "/"}
    topics = sorted(meta["topics"])
    reporting = pages["reporting"] and \
        d.gh_json("api", f"repos/{repo}/private-vulnerability-reporting").get("enabled") is not True
    return dict(repo=repo, branch=branch, head=head, tree=tree, files=files,
                settings=settings if {key: details.get(key) for key in settings} != settings else None,
                topics=topics if sorted(details.get("topics") or []) != topics else None,
                reporting=reporting)


def publish_repository(repo, site, source, pages):
    """Publish the repository's pages as one commit and apply its settings."""
    if d.command("git", "-C", local.ROOT, "rev-parse", "HEAD").strip() != source or \
            d.command("git", "-C", local.ROOT, "status", "--porcelain").strip():
        raise d.DistributionError("README publication requires the exact clean candidate source.")
    plan = repository_changes(repo, site, pages)
    repo = plan["repo"]
    def post(path, body, method="POST"):
        return json.loads(d.command("gh", "api", "--method", method, f"repos/{repo}/{path}", "--input", "-",
                                    input=json.dumps(body)) or "{}")
    if plan["files"]:
        entries = [{"path": name, "mode": "100644", "type": "blob",
                    "sha": post("git/blobs", {"content": base64.b64encode(data).decode(), "encoding": "base64"})["sha"]}
                   for name, data in plan["files"].items()]
        tree = post("git/trees", {"base_tree": plan["tree"], "tree": entries})["sha"]
        commit = post("git/commits", {"message": f"Update the product pages for {source[:12]}",
                                      "tree": tree, "parents": [plan["head"]]})["sha"]
        # Fails instead of overwriting if the branch moved since it was read.
        post(f"git/refs/heads/{plan['branch']}", {"sha": commit, "force": False}, method="PATCH")
    if plan["settings"]:
        d.command("gh", "api", "--method", "PATCH", f"repos/{repo}", "--input", "-", input=json.dumps(plan["settings"]))
    if plan["topics"]:
        d.command("gh", "api", "--method", "PUT", f"repos/{repo}/topics", "--input", "-", input=json.dumps({"names": plan["topics"]}))
    if plan["reporting"]:
        d.command("gh", "api", "--method", "PUT", f"repos/{repo}/private-vulnerability-reporting")
    return sorted(plan["files"])