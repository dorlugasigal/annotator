#!/usr/bin/env python3
"""Owner-run Windows release handoff (WIN-DST-05). Plan by default; --execute runs the local stages.

Freezes the source, runs local validation, builds the MSIX packages and bundle, certifies the bundle with the Windows App
Certification Kit (WACK), then stops with the manual Microsoft Store submission steps. It never signs, uploads or submits:
the Store signs what it publishes, and the owner submits by hand. Run the same command again to resume; finished stages
are kept in the release folder. --check-store only checks the Partner Center values that the checkout keeps
(docs/maintainers/windows-store.md). --metrics-url gives the build the usage-count service's origin, as the Mac
release passes metrics_url; the app still sends nothing while UsageMetrics.ServiceAcceptsWindows is off.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
from urllib.parse import urlsplit
import xml.etree.ElementTree as ElementTree

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "release"))
import release  # noqa: E402

STAGES = ("Freeze source", "Validate locally", "Build packages", "Certify with WACK", "Hand off to the Store")
MANIFEST = Path("apps/windows/src/Annotator.App/Package.appxmanifest")
METRICS = Path("apps/windows/src/Annotator.Workspace/Metrics/UsageMetrics.cs")
APPCERT = (Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"))
           / "Windows Kits" / "10" / "App Certification Kit" / "appcert.exe")
FOUNDATION = "{http://schemas.microsoft.com/appx/manifest/foundation/windows10}"
UAP = "{http://schemas.microsoft.com/appx/manifest/uap/windows10}"
HOURS = 3600
# The website's Windows listing (WIN-DST-02, WIN-DST-08): storeId stays null until the Store listing is public.
WEBSITE_LISTING = Path("website/src/data/windows-listing.json")
# The same rule as the website's parseWindowsListing: a 12-character Store ID, such as 9NBLGGH4R315.
STORE_ID = re.compile(r"[0-9A-Z]{12}")
# The manifest schema's package name: 3 to 50 letters, digits, periods or hyphens.
PACKAGE_NAME = re.compile(r"[-.A-Za-z0-9]{3,50}")
MISSING = object()


class Stop(Exception):
    """The run cannot go on: fix the cause, then run the same command again (exit 1)."""


class Pending(Exception):
    """The owner has a step to do first, then runs the same command again (exit 3)."""


def msix_version(version, build):
    """WIN-DST-04: (Major + 1).Minor.Build.0 of the shared version, as SharedVersion.props computes it."""
    (major, minor, _), _, _ = release.parse_version(version)
    if not isinstance(build, int) or build < 1:
        raise Stop("The shared build number must be a positive integer.")
    return f"{major + 1}.{minor}.{build}.0"


def metrics_url(value):
    """The usage-count service's origin for the build, with the Mac release's rule for metrics_url (an HTTPS root URL, not
    GitHub Pages, which has no service) and the app's (UsageMetrics.ValidatedEndpoint: no port, user, path, query or
    fragment). Empty means the build has none."""
    if not value:
        return ""
    try:
        parts = urlsplit(value.strip())
        port = parts.port
    except ValueError as error:
        raise Stop(f"--metrics-url is not a URL: {error}") from error
    host = parts.hostname or ""
    if (parts.scheme != "https" or not re.fullmatch(r"[a-z0-9.-]+", host) or port is not None or parts.username or parts.password
            or parts.path not in ("", "/") or parts.query or parts.fragment or host.endswith(".github.io")):
        raise Stop("--metrics-url must be the website's HTTPS root, such as https://annotator.example, with no port, path, "
                   "query or fragment, and not GitHub Pages.")
    return f"https://{host}"


def service_accepts_windows(root):
    """UsageMetrics.ServiceAcceptsWindows in the source: whether the app sends usage counts at all (WIN-PRV-01)."""
    try:
        text = (Path(root) / METRICS).read_text(encoding="utf-8")
    except OSError as error:
        raise Stop(f"{METRICS} could not be read: {error}") from error
    found = re.findall(r"public const bool ServiceAcceptsWindows = (true|false);", text)
    if len(found) != 1:
        raise Stop(f"{METRICS} has no single ServiceAcceptsWindows switch.")
    return found[0] == "true"


def package_identity(manifest):
    """The Store identity in Package.appxmanifest and whether it still has the owner's PLACEHOLDER values."""
    try:
        root = ElementTree.parse(manifest).getroot()
    except (OSError, ElementTree.ParseError) as error:
        raise Stop(f"Package.appxmanifest could not be read: {error}") from error
    identity = root.find(f"{FOUNDATION}Identity")
    display = root.find(f"{FOUNDATION}Properties/{FOUNDATION}PublisherDisplayName")
    if identity is None or display is None:
        raise Stop("Package.appxmanifest has no Identity or PublisherDisplayName.")
    values = {"name": identity.get("Name", ""), "publisher": identity.get("Publisher", ""),
              "publisherDisplayName": (display.text or "").strip()}
    values["placeholder"] = any("PLACEHOLDER" in value for value in values.values()) or not all(values.values())
    # Partner Center compares the values exactly, so spaces around a pasted value make an upload fail.
    values["padded"] = [label for label, text in (("Package/Identity/Name", values["name"]),
                                                  ("Package/Identity/Publisher", values["publisher"]),
                                                  ("Package/Properties/PublisherDisplayName", display.text or ""))
                        if text != text.strip()]
    name = root.find(f"{FOUNDATION}Properties/{FOUNDATION}DisplayName")
    start = root.find(f"{FOUNDATION}Applications/{FOUNDATION}Application/{UAP}VisualElements")
    values["displayName"] = (name.text or "").strip() if name is not None else ""
    values["startDisplayName"] = start.get("DisplayName", "") if start is not None else None
    return values


def identity_problems(identity):
    """Copy mistakes in the Partner Center values of Package.appxmanifest, in the owner's words; empty when none."""
    if identity["placeholder"]:
        return ["Package.appxmanifest still has a PLACEHOLDER or empty Store value. Copy Package/Identity/Name, "
                "Package/Identity/Publisher and Package/Properties/PublisherDisplayName from Partner Center > "
                "Product management > Product identity."]
    problems = [f"{label} has spaces or line breaks around it. Remove them." for label in identity["padded"]]
    if not PACKAGE_NAME.fullmatch(identity["name"].strip()):
        problems.append(f"Package/Identity/Name {identity['name']!r} is not a package name: use 3 to 50 letters, digits, "
                        "periods or hyphens. Copy it again from Product identity.")
    if not identity["publisher"].strip().startswith("CN="):
        problems.append(f"Package/Identity/Publisher {identity['publisher']!r} must start with CN=. Copy the whole value "
                        "again from Product identity.")
    if not identity["displayName"]:
        problems.append("Package.appxmanifest has no DisplayName. Use the name you reserved in Partner Center.")
    elif identity["startDisplayName"] is not None and identity["startDisplayName"] != identity["displayName"]:
        problems.append(f"The Start menu name {identity['startDisplayName']!r} (uap:VisualElements DisplayName) is not "
                        f"the package DisplayName {identity['displayName']!r}. Use the name you reserved for both.")
    return problems


def website_store_id(root):
    """The website's Windows listing storeId: a string, None while it is null, or MISSING when the checkout has no file."""
    path = root / WEBSITE_LISTING
    if not path.is_file():
        return MISSING
    try:
        listing = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        raise Stop(f"{WEBSITE_LISTING.as_posix()} could not be read: {error}") from error
    if not isinstance(listing, dict) or list(listing) != ["storeId"]:
        raise Stop(f"{WEBSITE_LISTING.as_posix()} must contain only storeId: null, or the Store ID of a public listing.")
    return listing["storeId"]


def check_store(root, store_id=None, out=print):
    """--check-store: shows each Partner Center value where the checkout keeps it and whether it looks right. Changes
    nothing; True when nothing needs fixing."""
    identity = package_identity(root / MANIFEST)
    problems = identity_problems(identity)
    out(f"{MANIFEST.as_posix()} (from Partner Center > Product management > Product identity):")
    out(f"  Package/Identity/Name                    {identity['name']}")
    out(f"  Package/Identity/Publisher               {identity['publisher']}")
    out(f"  Package/Properties/PublisherDisplayName  {identity['publisherDisplayName']}")
    out(f"  DisplayName (must be a reserved name)    {identity['displayName']}")
    out(f"Store ID (--store-id): {store_id or 'not given; add --store-id <Store ID> to check it'}")
    try:
        listed = website_store_id(root)
    except Stop as error:
        problems.append(str(error))
    else:
        if listed is MISSING:
            out(f"{WEBSITE_LISTING.as_posix()}: not in this checkout.")
        elif listed is None:
            out(f"{WEBSITE_LISTING.as_posix()}: storeId is null, so the website names no Windows download. "
                "Set it only after the Store listing is public.")
        elif not isinstance(listed, str) or not STORE_ID.fullmatch(listed):
            problems.append(f"The website's storeId {listed!r} is not a 12-character Store ID, such as 9NBLGGH4R315.")
        else:
            out(f"{WEBSITE_LISTING.as_posix()}: storeId {listed}. Keep it only while the Store listing is public.")
            if store_id and listed != store_id:
                problems.append(f"The website's storeId {listed} is not the Store ID {store_id}. Use the one from "
                                "Product identity in both places.")
    for problem in problems:
        out(f"PROBLEM: {problem}")
    out("NOT READY: fix each problem, then run --check-store again." if problems else
        "READY: the Store values look right. Partner Center still compares them exactly when you upload.")
    return not problems


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def wack_result(report, version):
    """OVERALL_RESULT of a WACK report for this package version: PASS or WARNING; anything else stops the run."""
    try:
        text = Path(report).read_text(encoding="utf-8-sig", errors="replace")
        result = ElementTree.fromstring(text).get("OVERALL_RESULT", "")
    except (OSError, ElementTree.ParseError) as error:
        raise Stop(f"The WACK report could not be read: {error}") from error
    if version not in text:
        raise Stop(f"The WACK report in {report} is for another package. Delete it and run WACK again on version {version}.")
    if result not in ("PASS", "WARNING"):
        raise Stop(f"WACK says {result or 'nothing'} for {version}. Open the report next to {report}, fix the failures, "
                   "prepare a new candidate and run again.")
    return result


def running_as_admin():
    try:
        import ctypes
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except (AttributeError, OSError):
        return False


class Publisher:
    """The five stages over one release folder. The command runner, git and the system probes can be replaced in tests."""

    def __init__(self, folder, *, rehearsal=False, store_id=None, flight_id=None, metrics="", root=ROOT, run=None, git=None,
                 admin=running_as_admin, appcert=APPCERT, platform=os.name, environ=None, out=print):
        self.folder = Path(folder)
        self.rehearsal = rehearsal
        self.store_id = store_id
        self.flight_id = flight_id
        self.metrics = metrics_url(metrics)
        self.root = Path(root)
        self.run = run or self._run
        self.git = git or self._git
        self.admin = admin
        self.appcert = Path(appcert)
        self.platform = platform
        self.environ = os.environ if environ is None else environ
        self.out = out
        self.progress_path = self.folder / "progress.json"
        self.log_path = self.folder / "pipeline.log"
        self.state = {"schema": 1, "stages": {}}

    # Saved progress -----------------------------------------------------------------------------------------------

    def load(self):
        if not self.progress_path.exists():
            return
        try:
            state = json.loads(self.progress_path.read_text(encoding="utf-8"))
        except ValueError as error:
            raise Stop(f"{self.progress_path} could not be read: {error}") from error
        if state.get("schema") != 1 or not isinstance(state.get("stages"), dict) or set(state["stages"]) - set(STAGES):
            raise Stop(f"{self.progress_path} is not a Windows publisher progress file.")
        self.state = state

    def save(self):
        temporary = self.progress_path.with_name(".progress.json.tmp")
        temporary.write_text(json.dumps(self.state, indent=2) + "\n", encoding="utf-8")
        os.replace(temporary, self.progress_path)

    def saved(self, name):
        return self.state["stages"].get(name, {})

    def log(self, message):
        line = f"{datetime.now().astimezone():%H:%M:%S} {message}"
        self.out(line)
        with open(self.log_path, "a", encoding="utf-8") as stream:
            stream.write(line + "\n")

    def stage(self, name, action):
        index = STAGES.index(name) + 1
        entry = self.saved(name)
        if entry.get("status") == "done":
            self.log(f"[{index}/{len(STAGES)}] DONE {name} (saved)")
            return entry["detail"]
        self.state["stages"][name] = {"status": "running", "started": datetime.now(timezone.utc).isoformat()}
        self.save()
        self.log(f"[{index}/{len(STAGES)}] RUN  {name}")
        started = time.monotonic()
        try:
            detail = action()
        except BaseException as error:
            status = "paused" if isinstance(error, (Pending, KeyboardInterrupt)) else "failed"
            self.state["stages"][name].update(status=status, seconds=round(time.monotonic() - started, 1))
            self.save()
            self.log(f"[{index}/{len(STAGES)}] {status.upper()} {name}")
            raise
        self.state["stages"][name] = {"status": "done", "seconds": round(time.monotonic() - started, 1), "detail": detail}
        self.save()
        self.log(f"[{index}/{len(STAGES)}] DONE {name} ({time.monotonic() - started:.1f} s)")
        return detail

    # Commands -----------------------------------------------------------------------------------------------------

    def _run(self, argv, timeout, label):
        """Runs a command from the source root with its output in pipeline.log; every wait is bounded."""
        self.log(f"  > {label}")
        with open(self.log_path, "a", encoding="utf-8") as stream:
            try:
                code = subprocess.run([str(arg) for arg in argv], cwd=self.root, stdout=stream, stderr=subprocess.STDOUT,
                                      stdin=subprocess.DEVNULL, timeout=timeout, check=False).returncode
            except subprocess.TimeoutExpired as error:
                raise Stop(f"{label} did not finish within {timeout // 60} minutes; see {self.log_path}.") from error
            except OSError as error:
                raise Stop(f"{label} could not start: {error}") from error
        if code:
            raise Stop(f"{label} failed with exit code {code}; see {self.log_path}.")

    def _git(self, *args):
        result = subprocess.run(["git", "-C", str(self.root), *args], capture_output=True, text=True, timeout=120, check=False)
        if result.returncode:
            raise Stop(f"git {' '.join(args)} failed: {result.stderr.strip()}")
        return result.stdout.strip()

    # Stages -------------------------------------------------------------------------------------------------------

    def freeze(self):
        if self.environ.get("GITHUB_ACTIONS") == "true":
            raise Stop("Run the Windows publisher on the owner's computer, not in GitHub Actions.")
        if self.platform != "nt":
            raise Stop("The Windows publisher runs on Windows.")
        if self.git("status", "--porcelain"):
            raise Stop("The checkout has changes. Commit the approved candidate first, then run again.")
        source = self.git("rev-parse", "HEAD")
        version, build = release.configured_version(self.root)
        identity = package_identity(self.root / MANIFEST)
        if identity["placeholder"] and not self.rehearsal:
            raise Stop("Package.appxmanifest still has the PLACEHOLDER Store identity. Copy Package/Identity/Name, "
                       "Package/Identity/Publisher and Package/Properties/PublisherDisplayName from Partner Center > "
                       "Product identity, commit them and run again. Use --rehearsal for a local run without them.")
        problems = [] if self.rehearsal else identity_problems(identity)
        if problems:
            raise Stop("The Store identity in Package.appxmanifest needs fixing: " + " ".join(problems)
                       + " Check it with --check-store, commit the fix and run again.")
        if service_accepts_windows(self.root) and not self.metrics and not self.rehearsal:
            raise Stop("UsageMetrics.ServiceAcceptsWindows is on, so the Store build needs the service: run again with "
                       "--metrics-url <the website's HTTPS root>.")
        self.run([sys.executable, "tools/release/release.py", "check"], 600, "release notes and changelog check")
        return {"source": source, "version": version, "build": build, "msix": msix_version(version, build),
                "identity": identity, "rehearsal": self.rehearsal, "metricsUrl": self.metrics}

    def check_frozen(self, frozen):
        """A resumed run must use the same checkout, unchanged, and the same mode and usage-count service."""
        if frozen.get("rehearsal") != self.rehearsal:
            raise Stop("This release folder was started " + ("as a rehearsal." if frozen.get("rehearsal") else "for the Store.")
                       + " Run it the same way, or use a new folder.")
        if frozen.get("metricsUrl", "") != self.metrics:
            raise Stop("This release folder was started " + (f"with --metrics-url {frozen['metricsUrl']}." if frozen.get("metricsUrl")
                       else "without --metrics-url.") + " Run it the same way, or use a new folder.")
        if self.git("rev-parse", "HEAD") != frozen["source"] or self.git("status", "--porcelain"):
            raise Stop(f"The checkout changed since {frozen['source'][:12]} was frozen. Go back to that commit with no "
                       "changes, or prepare a new candidate in a new folder.")

    def validate(self):
        self.run(["pwsh", "-NoProfile", "-File", "tools/windows/validate.ps1", "all"], 3 * HOURS, "validate.ps1 all")
        return {"suite": "all"}

    def build(self, frozen):
        output = self.folder / "packages"
        argv = ["pwsh", "-NoProfile", "-File", "tools/windows/package.ps1", "-Output", output,
                "-BuildStamp", f"Release {frozen['source'][:12]}"]
        if frozen.get("metricsUrl"):
            argv += ["-MetricsUrl", frozen["metricsUrl"]]
        if not self.rehearsal:
            argv.append("-RequireArm64")
        self.run(argv, 3 * HOURS, "package.ps1")
        folder = output / frozen["msix"]
        try:
            report = json.loads((folder / "package-report.json").read_text(encoding="utf-8-sig"))
        except (OSError, ValueError) as error:
            raise Stop(f"package.ps1 left no readable package-report.json in {folder}: {error}") from error
        if report.get("version") != frozen["msix"] or report.get("problems"):
            raise Stop(f"The package report does not match {frozen['msix']} or lists problems; see {folder}.")
        if (report.get("metricsUrl") or "") != frozen.get("metricsUrl", ""):
            raise Stop(f"The packages were built with another usage-count service than {frozen.get('metricsUrl') or 'none'}; "
                       f"see {folder}.")
        architectures = report.get("bundle", {}).get("architectures", [])
        if not self.rehearsal and sorted(architectures) != ["arm64", "x64"]:
            raise Stop("The Store bundle needs x64 and ARM64 packages.")
        bundle = folder / report["bundle"]["file"]
        if not bundle.is_file() or sha256(bundle) != report["bundle"]["sha256"]:
            raise Stop(f"{bundle} is missing or differs from its package report.")
        return {"bundle": str(bundle), "bundleSha256": report["bundle"]["sha256"], "architectures": architectures}

    def certify(self, frozen, built):
        bundle = Path(built["bundle"])
        report = self.folder / "wack" / "report.xml"
        if not (report.is_file() and report.stat().st_mtime >= bundle.stat().st_mtime):
            if not self.appcert.is_file():
                raise Stop(f"The Windows App Certification Kit is missing ({self.appcert}). Install it with the Windows SDK, "
                           "then run again.")
            if not self.admin():
                raise Pending("WACK needs an elevated terminal. Open Terminal as administrator and run:\n"
                              f"  & '{self.appcert}' reset\n"
                              f"  & '{self.appcert}' test -appxpackagepath '{bundle}' -reportoutputpath '{report}'\n"
                              "Then run this publisher again; it reads the report. Or run the publisher itself as administrator.")
            report.parent.mkdir(parents=True, exist_ok=True)
            self.run([self.appcert, "reset"], 600, "appcert reset")
            self.run([self.appcert, "test", "-appxpackagepath", bundle, "-reportoutputpath", report], 2 * HOURS, "appcert test")
        result = wack_result(report, frozen["msix"])
        if result == "WARNING":
            self.log(f"  WACK passed with warnings; review them in {report.parent} before submitting.")
        return {"report": str(report), "result": result}

    def hand_off(self, frozen, built):
        bundle = Path(built["bundle"])
        if not bundle.is_file() or sha256(bundle) != built["bundleSha256"]:
            raise Stop(f"{bundle} changed after it was built and certified. Prepare a new candidate in a new folder.")
        if self.rehearsal:
            self.log("  REHEARSAL: the identity is not the Store's or ARM64 was skipped, so there is nothing to submit.")
            return {"submitted": False, "rehearsal": True}
        command = f'msstore publish "{bundle}" -id {self.store_id or "<Store product ID>"} --noCommit'
        if self.flight_id:
            command += f" -f {self.flight_id}"
        self.log("  Nothing was submitted. When you approve this candidate, submit it yourself:")
        self.log(f"    Partner Center: open Annotator's submission, then Packages, and upload {bundle}")
        self.log("    or, once Annotator has a published submission, the Microsoft Store Developer CLI (after msstore "
                 f"reconfigure): {command}")
        self.log("  Both leave a draft. Check the listing, the package flight and the notes in Partner Center, then submit.")
        return {"submitted": False, "command": command}

    # Run ----------------------------------------------------------------------------------------------------------

    def execute(self):
        self.folder.mkdir(parents=True, exist_ok=True)
        lock = self.folder / ".publisher.lock"
        try:
            descriptor = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        except FileExistsError as error:
            raise Stop(f"Another publisher run uses {self.folder}. If none runs, delete {lock} and run again.") from error
        os.close(descriptor)
        try:
            self.load()
            frozen = self.stage("Freeze source", self.freeze)
            self.check_frozen(frozen)
            self.stage("Validate locally", self.validate)
            built = self.stage("Build packages", lambda: self.build(frozen))
            self.stage("Certify with WACK", lambda: self.certify(frozen, built))
            self.stage("Hand off to the Store", lambda: self.hand_off(frozen, built))
            self.log(f"COMPLETE: {frozen['version']} ({frozen['build']}), package {frozen['msix']}, is ready for the owner.")
        finally:
            lock.unlink(missing_ok=True)

    def plan(self):
        version, build = release.configured_version(self.root)
        identity = package_identity(self.root / MANIFEST)
        self.out(f"Candidate: {version} ({build}), MSIX package {msix_version(version, build)}")
        self.out(f"Release folder: {self.folder}")
        for index, name in enumerate(STAGES, 1):
            self.out(f"[{index}/{len(STAGES)}] {name}")
        self.out("Store identity: " + ("PLACEHOLDER (only --rehearsal can run)" if identity["placeholder"] else identity["name"]))
        sending = service_accepts_windows(self.root)
        self.out("Usage counts: " + (self.metrics or "no service in the build")
                 + ("; the app sends them once a person turns them on." if sending and self.metrics
                    else "; nothing is sent while UsageMetrics.ServiceAcceptsWindows is off." if not sending
                    else "; a Store run stops until --metrics-url is given."))
        self.out(f"WACK: {'found' if self.appcert.is_file() else 'missing'}; elevated terminal: {'yes' if self.admin() else 'no'}")
        self.out("PLAN ONLY. --execute builds and checks locally; nothing is signed, uploaded or submitted.")

    def status(self):
        self.load()
        for index, name in enumerate(STAGES, 1):
            self.out(f"[{index}/{len(STAGES)}] {self.saved(name).get('status', 'not started').upper():11} {name}")
        self.out("Saved progress only. A running label does not prove the publisher still runs.")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--folder", type=Path,
                        help="Release folder outside the source checkout; it keeps progress, the log and the packages. "
                             "Every mode except --check-store needs it.")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--execute", action="store_true", help="Run the local stages: validate, build, certify, hand off.")
    mode.add_argument("--status", action="store_true", help="Read saved progress only.")
    mode.add_argument("--check-store", action="store_true",
                      help="Check the Partner Center values in Package.appxmanifest and the website's Windows listing. "
                           "Changes nothing.")
    parser.add_argument("--rehearsal", action="store_true",
                        help="Allow the PLACEHOLDER identity and an x64-only bundle; the last stage then submits nothing.")
    parser.add_argument("--store-id", help="The 12-character Store ID from Partner Center > Product identity.")
    parser.add_argument("--flight-id", help="The package flight for testers (WIN-DST-03) in the printed submission command.")
    parser.add_argument("--metrics-url", default="",
                        help="The usage-count service's origin (the website's HTTPS root), built in as AnnotatorMetricsUrl.")
    args = parser.parse_args(argv)
    if args.folder is None and not args.check_store:
        parser.error("--folder is required, except with --check-store.")
    try:
        if args.store_id is not None and not STORE_ID.fullmatch(args.store_id):
            raise Stop("--store-id must be the 12-character Store ID from Partner Center > Product management > "
                       "Product identity, such as 9NBLGGH4R315.")
        if args.check_store:
            return 0 if check_store(ROOT, args.store_id) else 1
        folder = args.folder.resolve()
        publisher = Publisher(folder, rehearsal=args.rehearsal, store_id=args.store_id, flight_id=args.flight_id,
                              metrics=args.metrics_url)
        if folder == ROOT or ROOT in folder.parents:
            raise Stop("Keep the release folder outside the source checkout.")
        if args.status:
            publisher.status()
        elif args.execute:
            publisher.execute()
        else:
            publisher.plan()
        return 0
    except Pending as error:
        print(f"PAUSED: {error}", file=sys.stderr)
        return 3
    except KeyboardInterrupt:
        print("PAUSED. Saved stages were kept; run the same command to resume.", file=sys.stderr)
        return 130
    except (Stop, release.ReleaseError) as error:
        print(f"STOPPED: {error}", file=sys.stderr)
        return 1
    except OSError as error:
        print(f"STOPPED: a file or folder could not be used: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
