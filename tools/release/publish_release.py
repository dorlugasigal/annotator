#!/usr/bin/env python3
"""One owner-run local release pipeline. Plan by default; --execute publishes."""
import argparse
from contextlib import contextmanager, redirect_stdout
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time
from types import SimpleNamespace
from urllib.error import HTTPError, URLError
from urllib.request import urlopen

import distribution as d
import local_release as local
import pages
import release
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "windows"))
import winget  # noqa: E402
import netlify_release as netlify
from release_progress import TerminalProgress

STAGES = ("Preflight", "Build and sign", "Submit to Apple", "Wait for Apple",
          "Staple and package", "Notarize disk image", "Get Windows files", "Publish release", "Update Homebrew",
          "Build website locally", "Sync website", "Verify live website")
REQUIRED = {"identity", "team", "public_key", "notary_key", "key_id", "issuer",
            "sparkle_key", "repository", "homepage", "github_user"}
OPTIONAL = {"support_url", "tap_repository", "website_repository", "netlify_site_id",
            "legacy_site_url", "metrics_url"}


class Pending(d.DistributionError):
    pass


def config(path):
    value = local.read_json(local.safe_path(path))
    if set(value) - REQUIRED - OPTIONAL or REQUIRED - set(value):
        raise d.DistributionError("Config fields differ from the documented example.")
    if any(not isinstance(v, str) or not v.strip() for v in value.values()):
        raise d.DistributionError("Config values must be nonempty strings.")
    value = dict(value)
    for key in ("notary_key", "sparkle_key"):
        value[key] = str(local.safe_path(Path(value[key]).expanduser()))
    for key in ("repository", "tap_repository", "website_repository"):
        value[key] = d.repository(value.get(key, value["repository"]))
        if value[key] == "dorlugasigal/annotator-code":
            raise d.DistributionError("Public artifacts must not target the archived private dorlugasigal/annotator-code.")
    value["homepage"] = d.website_url(value["homepage"])
    if value.get("netlify_site_id"):
        value["netlify_site_id"] = netlify.site_id(value["netlify_site_id"])
        value["homepage"] = netlify.site_url(value["homepage"])
        value["legacy_site_url"] = d.website_url(value.get("legacy_site_url", "https://dorlugasigal.github.io/annotator"))
        pages.site_prefix(value["legacy_site_url"])
        if value.get("metrics_url") and netlify.site_url(value["metrics_url"]) != value["homepage"]:
            raise d.DistributionError("App metrics must use the combined website's origin.")
        if value.get("metrics_url"):
            value["metrics_url"] = netlify.site_url(value["metrics_url"])
    elif value.get("metrics_url") or value.get("legacy_site_url"):
        raise d.DistributionError("Metrics and feed migration require the configured Netlify site.")
    value.setdefault("support_url", "")
    if not re.fullmatch(r"[A-Za-z0-9-]+", value["github_user"]):
        raise d.DistributionError("Supply the GitHub account that owns publication access.")
    return value


class Progress:
    def __init__(self, directory, hidden=(), display=None, stages=STAGES):
        self.directory = directory
        self.hidden = [v for v in hidden if v]
        self.state = {"schema": 1, "stages": {}}
        self.output = None
        self.private_block = False
        self.display = display
        self.stages = stages

    def clean(self, text):
        if re.search(r"-----BEGIN (?:[A-Z0-9]+ )?PRIVATE KEY-----", text):
            self.private_block = True
        if self.private_block:
            if re.search(r"-----END (?:[A-Z0-9]+ )?PRIVATE KEY-----", text):
                self.private_block = False
            return "[private-key output omitted]"
        for value in self.hidden:
            text = text.replace(value, "[redacted]")
        text = re.sub(r"\b(?:github_pat_|gh[pousr]_)[A-Za-z0-9_]+", "[redacted]", text)
        text = re.sub(r"(?i)(authorization\s*[:=]\s*)(?:bearer|basic)\s+\S+", r"\1[redacted]", text)
        return re.sub(r"(https?://)[^/\s:@]+:[^/\s@]+@", r"\1[redacted]@", text)

    def log(self, message, *, detail=False):
        clean = self.clean(str(message))
        line = f"{datetime.now().astimezone().strftime('%H:%M:%S')} {clean}"
        if self.display:
            self.display.message(clean, detail=detail)
        elif not detail:
            print(line, flush=True)
        if self.output:
            self.output.write(line + "\n")
            self.output.flush()

    def save(self):
        target = self.directory / "progress.json"
        if target.exists():
            local.ordinary(target)
        with tempfile.NamedTemporaryFile(mode="w", dir=self.directory, prefix=".progress-", delete=False) as stream:
            json.dump(self.state, stream, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
            name = stream.name
        os.replace(name, target)

    @contextmanager
    def stage(self, name):
        index = self.stages.index(name) + 1
        started = time.monotonic()
        self.state["stages"][name] = {"status": "running", "started": datetime.now(timezone.utc).isoformat()}
        self.save()
        self.log(f"[{index:02}/{len(self.stages)}] RUN  {name}", detail=True)
        if self.display:
            self.display.stage(name, "running")
        try:
            yield
        except BaseException as error:
            state = "paused" if isinstance(error, (Pending, KeyboardInterrupt)) else "failed"
            seconds = time.monotonic() - started
            self.state["stages"][name].update(status=state, seconds=round(seconds, 1))
            self.save()
            self.log(f"[{index:02}/{len(self.stages)}] {state.upper()} {name}; saved artifacts were kept.", detail=True)
            if self.display:
                self.display.stage(name, state, seconds)
            if isinstance(error, d.DistributionError):
                self.log(f"  {error}")
            raise
        self.state["stages"][name].update(status="done", seconds=round(time.monotonic() - started, 1))
        self.save()
        seconds = time.monotonic() - started
        self.log(f"[{index:02}/{len(self.stages)}] DONE {name} ({seconds:.1f}s)", detail=True)
        if self.display:
            self.display.stage(name, "done", seconds)


class HelperOutput:
    """Route helper messages through the same redaction/logging as stage output."""
    def __init__(self, progress):
        self.progress = progress
        self.pending = ""

    def write(self, value):
        self.pending += value
        while "\n" in self.pending:
            line, self.pending = self.pending.split("\n", 1)
            if line:
                self.progress.log(line)
        return len(value)

    def flush(self):
        if self.pending:
            self.progress.log(self.pending)
            self.pending = ""


@contextmanager
def command_logs(progress):
    original, page_command = d.command, pages.command

    def run(*args, input=None, cwd=None, stderr=False):
        name = Path(str(args[0])).name
        operation = " ".join(Path(str(arg)).name for arg in args[1:3]) if name in ("gh", "xcrun") else ""
        progress.log(f"  > {name} {operation}".rstrip(), detail=True)
        bundle_command = name == "netlify" and len(args) > 1 and args[1] == "functions:build"
        if name not in ("xcodebuild", "npm") and not bundle_command:
            return original(*args, input=input, cwd=cwd, stderr=stderr)
        if input is not None or stderr:
            raise d.DistributionError("Streaming commands must not receive private input.")
        argv = [str(arg) for arg in args if str(arg) != "-quiet"]
        child_env = {key: value for key, value in os.environ.items()
                     if not key.endswith(("_TOKEN", "_PASSWORD", "_SECRET", "_PRIVATE_KEY", "_P8_BASE64", "_P12_BASE64"))}
        process = subprocess.Popen(argv, cwd=cwd, env=child_env,
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        captured = []
        try:
            for line in process.stdout:
                captured.append(line)
                progress.log("  " + line.rstrip(), detail=True)
            code = process.wait()
        except BaseException:
            process.terminate()
            process.wait()
            raise
        finally:
            process.stdout.close()
        if code:
            raise d.DistributionError(f"{name} failed with exit code {code}; inspect pipeline.log.")
        return "".join(captured)

    d.command = run
    pages.command = run
    try:
        yield
    finally:
        d.command, pages.command = original, page_command


@contextmanager
def environment(values):
    before = {key: os.environ.get(key) for key in values}
    os.environ.update(values)
    try:
        yield
    finally:
        for key, value in before.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value


def wait_for_apple(folder, receipt, auth, progress, interval, timeout):
    deadline = time.monotonic() + timeout
    ident = local.attached(folder, receipt)
    while True:
        state = local.info(ident, folder, receipt, auth)
        progress.log(f"Apple: {state}; submission {ident}")
        if state == "Accepted":
            return
        if state != "In Progress":
            raise d.DistributionError(f"Apple returned {state}. Inspect submission {ident}; do not resubmit.")
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise Pending("Apple is still processing. Run this same command later to resume, not resubmit.")
        time.sleep(min(interval, remaining))


def wait_for_disk_image(folder, receipt, auth, progress, interval, timeout):
    deadline = time.monotonic() + timeout
    ident = local.attached_disk_image(folder, receipt)
    while True:
        state = local.info_disk_image(ident, folder, receipt, auth)
        progress.log(f"Apple disk image: {state}; submission {ident}")
        if state == "Accepted":
            return
        if state != "In Progress":
            raise d.DistributionError(f"Apple returned {state} for the disk image. Inspect submission {ident}; do not resubmit.")
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise Pending("Apple is still processing the disk image. Run this same command later to resume, not resubmit.")
        time.sleep(min(interval, remaining))


def verify_final(folder, receipt):
    final = folder / "final"
    local.safe_path(final)
    data = d.validate_manifest(local.read_json(final / "annotator-manifest.json"), receipt["repository"])
    if (data["source"], data["releaseVersion"], int(data["build"]), data["homepage"]) != (
            receipt["source"], receipt["version"], receipt["build"], receipt["homepage"]):
        raise d.DistributionError("Final manifest differs from the saved candidate.")
    if data.get("notarizationID") != local.attached(folder, receipt):
        raise d.DistributionError("Final output belongs to a different notarization submission.")
    archive = final / data["archive"]
    local.ordinary(archive)
    if local.digest(archive) != data["sha256"] or archive.stat().st_size != data["size"]:
        raise d.DistributionError("Final archive checksum or length changed.")
    expected = {
        "release-notes.md": (folder / "release-notes.md").read_text(),
        "SHA256SUMS": f"{data['sha256']}  {data['archive']}\n",
        "appcast.xml": d.appcast(data),
    }
    for name, content in expected.items():
        local.ordinary(final / name)
        if (final / name).read_text() != content:
            raise d.DistributionError(f"Final {name} differs from the verified release.")
    d.command("xcrun", "swift", local.ROOT / "tools/release/verify-update.swift",
              receipt["publicKey"], data["edSignature"], archive)
    return data


def verify_disk_image_final(folder, zip_manifest):
    final = folder / "disk-image" / "final"
    local.safe_path(final)
    data = d.validate_disk_image_manifest(local.read_json(final / "annotator-disk-image.json"), zip_manifest)
    archive = final / data["archive"]
    local.ordinary(archive)
    if local.digest(archive) != data["sha256"] or archive.stat().st_size != data["size"]:
        raise d.DistributionError("Final disk image checksum or length changed.")
    return data


SOURCE_REPOSITORY = "dorlugasigal/annotator-code"


def windows_part_of_release():
    return json.loads((local.ROOT / "releases/windows.json").read_text()).get("partOfReleases") is True


def get_windows_files(folder, source, progress):
    """The portable Windows ZIPs of this candidate (WIN-DST-09), or None when Windows is not part of releases.

    They come from <folder>/windows when the owner copied them there (package.ps1 -Portable, then winget.py describe),
    or from the windows-portable artifact of a successful Release workflow run of the same commit. Nothing is built here.
    """
    if not windows_part_of_release():
        progress.log("releases/windows.json leaves Windows out; no Windows files.")
        return None
    target = folder / "windows"
    if not (target / winget.DESCRIPTION_FILE).is_file():
        # gh's --commit filter misses manually started runs, so match the run's commit here.
        runs = json.loads(d.command("gh", "run", "list", "--repo", SOURCE_REPOSITORY, "--workflow", "release.yml",
                                    "--json", "databaseId,conclusion,status,headSha", "--limit", "50"))
        for run in runs:
            if run.get("headSha") != source or run.get("status") != "completed":
                continue
            try:
                with tempfile.TemporaryDirectory(prefix="windows-", dir=folder) as tmp:
                    d.command("gh", "run", "download", str(run["databaseId"]), "--repo", SOURCE_REPOSITORY,
                              "--name", "windows-portable", "--dir", tmp)
                    winget.verify(Path(tmp), source)
                    Path(tmp).rename(target)
                progress.log(f"Windows files from Release run {run['databaseId']}.")
                break
            except (d.DistributionError, winget.WingetError, OSError):
                continue
    if not (target / winget.DESCRIPTION_FILE).is_file():
        raise Pending("No Windows files for this candidate yet. Run the Release workflow on it with windows_packages, "
                      f"or put the ZIPs and annotator-windows.json in {target}, then run this again.")
    winget.verify(target, source)
    return target


def verify_casks(settings, expected):
    """The tap must hold the expected cask. A copy in the release repository must match it too."""
    for repo in dict.fromkeys((settings["tap_repository"], settings["repository"])):
        remote = d.published_cask(repo)
        # The release repository keeps a copy only for Macs that tapped it by URL.
        if remote is None and repo != settings["tap_repository"]:
            continue
        if remote != expected:
            raise d.DistributionError(f"The Homebrew cask in {repo} differs from the expected cask.")


def check_published(settings, source, version, build):
    for repo in {settings["repository"], settings["tap_repository"], settings["website_repository"]}:
        details = d.gh_json("api", f"repos/{repo}")
        if details.get("private") is not False or details.get("archived"):
            raise d.DistributionError("All publication destinations must be public and not archived.")
    entries = d.published_manifests(settings["repository"])
    same = [entry for entry in entries if entry["releaseVersion"] == version]
    if same:
        if any(entry["source"] != source or int(entry["build"]) != build for entry in same):
            raise d.DistributionError("This version was already published from different source. Prepare a new version before building.")
    elif any(int(entry["build"]) >= build for entry in entries):
        raise d.DistributionError("The candidate version must be newer than every published release before preparing an app.")


def build_website(directory, source, site, settings=None):
    artifact = directory / "package"
    checkout = directory / "source"
    d.command("git", "-C", local.ROOT, "worktree", "add", "--detach", checkout, source)
    try:
        d.command(sys.executable, checkout / "tools/release/distribution.py", "website", cwd=checkout)
        d.command("npm", "--prefix", checkout / "website", "ci", "--no-audit", "--no-fund")
        if settings and settings.get("netlify_site_id"):
            d.command("npm", "--prefix", checkout / "services/metrics", "ci", "--no-audit", "--no-fund")
            for script in ("typecheck", "test"):
                d.command("npm", "--prefix", checkout / "services/metrics", "run", script)
            with environment({"ANNOTATOR_WEB_METRICS_ENABLED": str(bool(settings.get("metrics_url"))).lower()}):
                d.command("npm", "--prefix", checkout / "website", "run", "build:netlify")
            d.command("npm", "--prefix", checkout / "website", "run", "test")
            netlify.prepare(checkout, artifact, site, source)
        else:
            for script in ("typecheck", "build", "test"):
                d.command("npm", "--prefix", checkout / "website", "run", script)
            d.command(sys.executable, checkout / "tools/release/pages.py", "prepare",
                      "--folder", artifact, "--site", site, cwd=checkout)
            pages.verify(artifact, site, source)
    finally:
        # This detached worktree was created by this invocation, never a user's checkout.
        d.command("git", "-C", local.ROOT, "worktree", "remove", "--force", checkout)
    return artifact


def verify_site(artifact, site, source, progress, interval, timeout, netlify_host=False):
    files = netlify.verify(artifact, site, source) if netlify_host else pages.verify(artifact, site, source)
    paths = [name for name in ("index.html", "downloads/index.html", "admin/index.html", "updates/preview.xml", "updates/stable.xml",
                               "updates/windows-preview.json", "updates/windows-stable.json") if name in files]
    deadline = time.monotonic() + timeout
    while True:
        missing = []
        for name in paths:
            try:
                with urlopen(site.rstrip("/") + "/" + name, timeout=20) as response:
                    data = response.read(2 * 1024 * 1024 + 1)
                if hashlib.sha256(data).hexdigest() != files[name]:
                    missing.append(name)
            except (HTTPError, URLError, TimeoutError):
                missing.append(name)
        if not missing:
            progress.log("Live homepage, downloads and available update feeds match the generated files.")
            return
        progress.log("Waiting for public site delivery: " + ", ".join(missing))
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise Pending("Site files were synced, but live delivery is not confirmed. Inspect the hosting deployment; rerun to resume.")
        time.sleep(min(interval, remaining))


def execute(args, settings, progress):
    folder = local.safe_path(args.folder)
    source = d.command("git", "-C", local.ROOT, "rev-parse", "HEAD").strip()
    version, build = release.configured_version(local.ROOT)
    public = {key: settings[key] for key in ("repository", "homepage", "tap_repository", "website_repository")}
    public.update({key: settings[key] for key in ("netlify_site_id", "legacy_site_url", "metrics_url") if key in settings})
    binding = {"source": source, "version": version, **public}
    binding_path = progress.directory / "candidate.json"
    if binding_path.exists():
        if local.read_json(binding_path) != binding:
            raise d.DistributionError("Pipeline source or destinations changed. Return to the original checkout and config.")
    prep = SimpleNamespace(folder=folder, version=version, candidate=source, **settings)
    with progress.stage("Preflight"):
        if progress.display:
            progress.display.subtitle = version
        progress.log(f"Candidate: {version}, source {source}")
        if os.environ.get("GITHUB_ACTIONS") == "true":
            raise d.DistributionError("This publishing command must run locally, not in Actions.")
        local.candidate(version, source)
        local.credentials(prep, folder)
        if settings.get("netlify_site_id"):
            netlify.check_remote(local.ROOT / "services/metrics/node_modules/.bin/netlify",
                                 settings["netlify_site_id"], settings["homepage"])
        key = local.safe_path(settings["sparkle_key"])
        local.ordinary(key)
        if key.is_relative_to(local.ROOT) or key.is_relative_to(folder):
            raise d.DistributionError("Keep the Sparkle key outside source and release directories.")
        token = d.command("gh", "auth", "token", "--user", settings["github_user"]).strip()
        if not token:
            raise d.DistributionError("GitHub CLI returned no credential for the selected account.")
        progress.hidden.append(token)
        with environment({"GH_TOKEN": token}):
            check_published(settings, source, version, build)
        if not binding_path.exists():
            local.write_once(binding_path, binding)
    values = {"GH_TOKEN": token, "PUBLICATION_ENABLED": "true", "CANDIDATE_SHA": source,
              "DISTRIBUTION_REPOSITORY": settings["repository"],
              "HOMEBREW_TAP_REPOSITORY": settings["tap_repository"],
              "WEBSITE_REPOSITORY": settings["website_repository"],
              "WEBSITE_URL": settings["homepage"], "SITE_URL": settings["homepage"]}
    with environment(values):
        with progress.stage("Build and sign"):
            if not folder.exists():
                local.prepare(prep)
            with local.locked(folder):
                receipt = local.load(folder)
                if any(receipt[key] != value for key, value in (
                        ("source", source), ("version", version), ("repository", settings["repository"]),
                        ("homepage", settings["homepage"]), ("publicKey", settings["public_key"]),
                        ("identity", settings["identity"]), ("team", settings["team"]))):
                    raise d.DistributionError("Saved release differs from the requested source or settings.")
        with local.locked(folder):
            auth = local.credentials(prep, folder)
            with progress.stage("Submit to Apple"):
                if (folder / "submission.json").exists():
                    local.attached(folder, receipt)
                    progress.log("Reusing the saved submission; no upload.")
                else:
                    local.submit(folder, receipt, auth)
            with progress.stage("Wait for Apple"):
                wait_for_apple(folder, receipt, auth, progress, args.interval, args.apple_timeout)
            with progress.stage("Staple and package"):
                if not (folder / "final").exists():
                    local.finalize(folder, receipt, auth, Path(settings["sparkle_key"]))
                data = verify_final(folder, receipt)
            with progress.stage("Notarize disk image"):
                if not (folder / "disk-image" / "disk-image-receipt.json").exists():
                    local.build_disk_image(folder, receipt)
                if (folder / "disk-image" / "final").exists():
                    disk_data = verify_disk_image_final(folder, data)
                    progress.log(f"Reusing the saved disk image {disk_data['archive']}.")
                else:
                    if (folder / "disk-image" / "submission.json").exists():
                        local.attached_disk_image(folder, receipt)
                        progress.log("Reusing the saved disk image submission; no upload.")
                    else:
                        local.submit_disk_image(folder, receipt, auth)
                    wait_for_disk_image(folder, receipt, auth, progress, args.interval, args.apple_timeout)
                    local.finalize_disk_image(folder, receipt, auth)
                    disk_data = verify_disk_image_final(folder, data)
                progress.log(f"Disk image ready: {disk_data['archive']}")
            with progress.stage("Get Windows files"):
                windows = get_windows_files(folder, source, progress)
            with progress.stage("Publish release"):
                d.publish(folder / "final", folder / "disk-image" / "final", windows)
                progress.log(f"Published and verified https://github.com/{settings['repository']}/releases/tag/{data['tag']}")
            with progress.stage("Update Homebrew"):
                    verify_casks(settings, d.update_tap())
        with progress.stage("Build website locally"):
            site_dir = Path(tempfile.mkdtemp(prefix="site-", dir=progress.directory))
            artifact = build_website(site_dir, source, settings["homepage"], settings)
        with progress.stage("Sync website"):
            if settings.get("netlify_site_id"):
                deployment = netlify.deploy(artifact, settings["homepage"], source, settings["netlify_site_id"],
                                            local.ROOT / "services/metrics/node_modules/.bin/netlify")
                progress.log(f"Netlify deployment: {deployment}. Website, dashboard and functions share one site.")
            else:
                commit = pages.sync(artifact, settings["homepage"], source, local=True)
                progress.log(f"Public site commit: {commit}. GitHub Pages hosts/deploys the generated files.")
        with progress.stage("Verify live website"):
            use_netlify = bool(settings.get("netlify_site_id"))
            verify_site(artifact, settings["homepage"], source, progress, args.interval, args.site_timeout, use_netlify)
            if use_netlify:
                # The Website workflow deploys the old GitHub Pages feed bridge; nothing is committed. Its feeds
                # come from the same published releases, so the live bridge must match this locally built one.
                bridge = netlify.prepare_bridge(artifact, site_dir / "bridge", settings["legacy_site_url"],
                                                settings["homepage"], source)
                d.command("gh", "workflow", "run", "website.yml", "--ref", "main", "--repo", settings["repository"])
                progress.log("Requested the Website workflow for the old Pages feed bridge.")
                verify_site(bridge, settings["legacy_site_url"], source, progress, args.interval, args.site_timeout)
                if settings["tap_repository"] != settings["repository"]:
                    netlify.publish_repository(settings["tap_repository"], settings["homepage"], source, netlify.TAP_PAGES)
                progress.log("Legacy Pages redirect and both available Sparkle feeds verified. Do not delete the bridge.")
        progress.log("COMPLETE: app release, canonical Homebrew cask and live website verified.")
        progress.log(f"Website: {settings['homepage']}")
        progress.log("Install: brew install --cask " + d.tap_name(settings["tap_repository"]) + "/annotator")


def saved_progress(directory):
    saved = local.read_json(directory / "progress.json")
    if type(saved.get("schema")) is not int or saved["schema"] != 1 or not isinstance(saved.get("stages"), dict):
        raise d.DistributionError("Unsupported progress file.")
    if set(saved["stages"]) - set(STAGES):
        raise d.DistributionError("Saved progress contains unknown stages.")
    rows = {}
    for name in STAGES:
        entry = saved["stages"].get(name, {})
        if not isinstance(entry, dict):
            raise d.DistributionError("Invalid saved stage entry.")
        state = entry.get("status", "not started")
        if state not in ("not started", "running", "paused", "failed", "done"):
            raise d.DistributionError("Invalid saved stage status.")
        seconds = entry.get("seconds")
        if seconds is not None and (type(seconds) not in (int, float) or not 0 <= seconds < float("inf")):
            raise d.DistributionError("Invalid saved elapsed time.")
        if state == "running" and entry.get("started"):
            started = datetime.fromisoformat(entry["started"])
            if started.tzinfo is None:
                raise d.DistributionError("Saved start time needs a timezone.")
            seconds = max(0, (datetime.now(timezone.utc) - started).total_seconds())
        rows[name] = (state, seconds)
    return rows


def watch_progress(args, directory):
    display = TerminalProgress(STAGES, plain=args.plain, verbose=args.verbose)
    display.subtitle = "Read-only saved progress"
    display.log_path = str(directory / "pipeline.log")
    display.note = "Reading saved status; this view does not control the publisher."
    with display:
        warning = "Saved status only; a running label does not prove the publisher is still alive."
        display.message(warning)
        while True:
            rows = saved_progress(directory)
            for name, (state, seconds) in rows.items():
                if state != "not started":
                    display.stage(name, state, seconds)
            if display.live:
                display.message(warning)
            states = [row[0] for row in rows.values()]
            if "failed" in states:
                return 1
            if "paused" in states:
                return 3
            if all(state == "done" for state in states):
                return 0
            time.sleep(1)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, help="Required to plan or execute; not needed for saved status.")
    parser.add_argument("--folder", type=Path, required=True)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--execute", action="store_true", help="Authorize real signing, submission and public app/site updates.")
    mode.add_argument("--status", action="store_true", help="Read saved progress only; no network, build or publication.")
    mode.add_argument("--watch", action="store_true", help="Live view of saved progress only; does not control the publisher.")
    parser.add_argument("--interval", type=int, default=60, help="Local polling seconds; minimum 10.")
    parser.add_argument("--apple-timeout", type=int, default=7200, help="Seconds before pausing pending notarization.")
    parser.add_argument("--site-timeout", type=int, default=900)
    parser.add_argument("--plain", action="store_true", help="Disable animation and color.")
    parser.add_argument("--verbose", action="store_true", help="Stream detailed logs instead of the live display.")
    args = parser.parse_args(argv)
    progress = None
    try:
        if args.interval < 10 or args.apple_timeout < 1 or args.site_timeout < 1:
            raise d.DistributionError("Use interval >= 10 and positive timeout values.")
        folder = local.safe_path(args.folder)
        directory = local.safe_path(folder.parent / (folder.name + ".pipeline"))
        if args.watch:
            return watch_progress(args, directory)
        if args.status:
            for index, (name, (state, _)) in enumerate(saved_progress(directory).items(), 1):
                print(f"[{index:02}/{len(STAGES)}] {state.upper():11} {name}")
            print("Saved progress only. A running label does not prove the process is still alive.")
            return 0
        if args.config is None:
            raise d.DistributionError("Supply --config to plan or execute publication.")
        settings = config(args.config)
        print(f"Website: {settings['homepage']}")
        if not args.execute:
            for index, stage in enumerate(STAGES, 1):
                print(f"[{index:02}/{len(STAGES)}] {stage}")
            print("Release assets: ZIP for Sparkle/Homebrew, plus a signed and notarized drag-to-Applications DMG.")
            if settings.get("netlify_site_id"):
                print(f"Combined Netlify site: {settings['homepage']}. Legacy feeds: {settings['legacy_site_url']}.")
                print("Client counts configured." if settings.get("metrics_url") else "Client counts remain disabled.")
            else:
                print("LEGACY PAGES MODE: this config does not deploy the metrics service. See release-handoff.md before migrating.")
            print("PLAN ONLY. --execute signs, submits and publishes. Hosting and any legacy-feed bridge run remotely.")
            return 0
        directory.mkdir(mode=0o700, parents=True, exist_ok=True)
        hidden = [settings["notary_key"], settings["sparkle_key"]]
        hidden += [value for name, value in os.environ.items()
                   if name.endswith(("_TOKEN", "_PASSWORD", "_SECRET", "_PRIVATE_KEY"))]
        display = TerminalProgress(STAGES, plain=args.plain, verbose=args.verbose)
        progress = Progress(directory, hidden, display)
        with local.locked(directory):
            log = directory / "pipeline.log"
            if log.exists():
                local.ordinary(log)
            fd = os.open(log, os.O_CREAT | os.O_APPEND | os.O_WRONLY | os.O_NOFOLLOW, 0o600)
            display.log_path = str(log)
            with os.fdopen(fd, "a") as stream, command_logs(progress), display:
                progress.output = stream
                progress.log(f"Log: {log}")
                helper_output = HelperOutput(progress)
                with redirect_stdout(helper_output):
                    try:
                        execute(args, settings, progress)
                    finally:
                        helper_output.flush()
        return 0
    except KeyboardInterrupt:
        print("Status view closed; the publisher was not interrupted." if args.watch else
              "PAUSED. Saved archives were kept. Run the same command to resume.", file=sys.stderr)
        return 130
    except Pending as error:
        print(f"PAUSED: {error}", file=sys.stderr)
        return 3
    except (d.DistributionError, release.ReleaseError) as error:
        text = progress.clean(str(error)) if progress else str(error)
        print(f"STOPPED: {text}", file=sys.stderr)
        return 1
    except (OSError, ValueError, KeyError, TypeError):
        print("STOPPED: invalid local inputs, files or tool response. Saved archives were kept; inspect pipeline.log.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
