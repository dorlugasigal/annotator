#!/usr/bin/env python3
"""Steps of the Windows part of .github/workflows/release.yml (WIN-DST-01, WIN-DST-03, WIN-DST-04).

plan       On the Linux preflight: does this run build the Windows bundle, and may it go to the Microsoft Store?
pins       The pinned .NET SDK (tools/windows/toolchain.json) for actions/setup-dotnet.
toolchain  Checks the runner against tools/windows/toolchain.json and reports each difference.
guards     The Store identity check of publish_windows.py --check-store and the usage-count service rule.
receipt    Checks package.ps1's report and bundle, then writes the artifact folder with SHA256SUMS and windows-release.json.
verify     Checks the downloaded artifact in the Store job against the build job's SHA-256.
cli        Downloads the pinned Microsoft Store Developer CLI and checks its SHA-256.
submit     Sends the bundle with the CLI: to the package flight, or to the private audience, and to the public listing only
           with an explicit "public" input.

Nothing here signs a package or runs a test suite: the Store signs what it publishes, and local validation, WACK and the
first Store submission stay with the owner (tools/windows/publish_windows.py, docs/maintainers/windows-store.md).
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "release"))
sys.path.insert(0, str(ROOT / "tools" / "windows"))
import release  # noqa: E402
import publish_windows  # noqa: E402

SWITCH = Path("releases/windows.json")
TOOLCHAIN = Path("tools/windows/toolchain.json")
TARGETS = ("none", "testers", "public")
ARCHITECTURES = ["arm64", "x64"]
RECEIPT = "windows-release.json"
SOURCE = re.compile(r"[0-9a-f]{40}")
DIGEST = re.compile(r"[0-9a-f]{64}")
FLIGHT = re.compile(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
CREDENTIALS = ("PARTNER_CENTER_TENANT_ID", "PARTNER_CENTER_SELLER_ID", "PARTNER_CENTER_CLIENT_ID",
               "PARTNER_CENTER_CLIENT_SECRET")
MINUTES = 60


class Stop(Exception):
    """The step cannot go on; the message says what to change (exit 1)."""


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def write_text(path, text):
    Path(path).write_text(text, encoding="utf-8", newline="\n")


def outputs(values, environ):
    """Appends key=value lines to $GITHUB_OUTPUT and prints them for the log."""
    lines = [f"{key}={str(value).lower() if isinstance(value, bool) else value}" for key, value in values.items()]
    for line in lines:
        print(line)
    if environ.get("GITHUB_OUTPUT"):
        with open(environ["GITHUB_OUTPUT"], "a", encoding="utf-8", newline="\n") as stream:
            stream.write("".join(line + "\n" for line in lines))


def summary(lines, environ):
    if environ.get("GITHUB_STEP_SUMMARY"):
        with open(environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8", newline="\n") as stream:
            stream.write("\n".join(lines) + "\n\n")


def read_json(path, what):
    try:
        return json.loads(Path(path).read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as error:
        raise Stop(f"{what} could not be read: {error}") from error


# Plan (Linux preflight) ------------------------------------------------------------------------------------------------

def part_of_releases(root=ROOT):
    """releases/windows.json: whether Windows is part of every release that this workflow signs (D-8)."""
    data = read_json(root / SWITCH, SWITCH.as_posix())
    if not isinstance(data, dict) or set(data) - {"$comment"} != {"partOfReleases"} or type(data["partOfReleases"]) is not bool:
        raise Stop(f"{SWITCH.as_posix()} must hold partOfReleases: true or false, and nothing else but a $comment.")
    return data["partOfReleases"]


def plan(environ, root=ROOT):
    event = environ.get("GITHUB_EVENT_NAME", "")
    proceeds = environ.get("RELEASE_PROCEEDS") == "true"
    requested = environ.get("BUILD_WINDOWS") == "true"
    target = environ.get("STORE_TARGET") or "none"
    if target not in TARGETS:
        raise Stop(f"windows_store must be one of {', '.join(TARGETS)}, not {target!r}.")
    if event != "workflow_dispatch" and (requested or target != "none"):
        raise Stop("Only a manual run of Release can ask for Windows test packages or a Store submission.")
    switch = part_of_releases(root)
    version, build = release.configured_version(root)
    msix = publish_windows.msix_version(version, build)
    if target != "none":
        owner = environ.get("REPOSITORY_OWNER", "")
        if not owner or environ.get("TRIGGERING_ACTOR") != owner:
            raise Stop(f"Only the repository owner ({owner or 'unknown'}) can send Annotator to the Microsoft Store.")
        if environ.get("STORE_SUBMISSION_ENABLED") != "true":
            raise Stop("Store submissions from GitHub are off. Make the first submission by hand in Partner Center. When it "
                       "is published, set the repository variable STORE_SUBMISSION_ENABLED to true.")
        if not publish_windows.STORE_ID.fullmatch(environ.get("WINDOWS_STORE_ID", "")):
            raise Stop("Set the repository variable WINDOWS_STORE_ID to the 12-character Store ID from Partner Center > "
                       "Product identity, such as 9NBLGGH4R315.")
    release_build = proceeds and switch
    values = {"windows": release_build or requested or target != "none", "store-ready": release_build or target != "none",
              "store": target, "msix": msix}
    if values["windows"]:
        reason = ("part of this release (releases/windows.json)" if release_build else
                  "for the Store submission that this run asks for" if target != "none" else "as a test artifact")
        line = f"Windows: this run builds {msix} for {version} ({build}) {reason}."
        if target != "none":
            line += f" After that, the windows-store job sends it to the Store ({target})."
    elif switch:
        line = "Windows: not built, because this run signs no release."
    else:
        line = "Windows: not built. releases/windows.json leaves Windows out of releases, and no Windows input was chosen."
    print(line)
    summary([line], environ)
    return values


# Toolchain (Windows runner) --------------------------------------------------------------------------------------------

def pins(root=ROOT):
    data = read_json(root / TOOLCHAIN, TOOLCHAIN.as_posix())
    sdk = data.get("dotnetSdk", "")
    if not re.fullmatch(r"\d+\.\d+\.\d+", sdk):
        raise Stop(f"{TOOLCHAIN.as_posix()} has no exact dotnetSdk version.")
    return {"dotnet": sdk}


def file_version(path):
    """The file version of a Windows executable, such as 10.0.26100.7705; empty when it has none."""
    import ctypes
    api = ctypes.windll.version
    size = api.GetFileVersionInfoSizeW(str(path), None)
    if not size:
        return ""
    data = ctypes.create_string_buffer(size)
    pointer, length = ctypes.c_void_p(), ctypes.c_uint()
    if not api.GetFileVersionInfoW(str(path), 0, size, data) or not api.VerQueryValueW(
            data, "\\", ctypes.byref(pointer), ctypes.byref(length)):
        return ""
    fixed = ctypes.cast(pointer, ctypes.POINTER(ctypes.c_uint32 * 13)).contents
    return f"{fixed[2] >> 16}.{fixed[2] & 0xFFFF}.{fixed[3] >> 16}.{fixed[3] & 0xFFFF}"


def probe(sdk_build, environ=os.environ):
    """What the runner has: the .NET SDK, fxc.exe of the pinned Windows SDK and Visual Studio's C++ tools."""
    def command(*argv):
        try:
            result = subprocess.run(list(argv), capture_output=True, text=True, timeout=120, stdin=subprocess.DEVNULL)
        except (OSError, subprocess.TimeoutExpired):
            return ""
        return result.stdout.strip() if result.returncode == 0 else ""
    kits = Path(environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"))
    fxc = kits / "Windows Kits" / "10" / "bin" / sdk_build / "x64" / "fxc.exe"
    vswhere = str(kits / "Microsoft Visual Studio" / "Installer" / "vswhere.exe")
    def visual_studio(component):
        found = lambda name: command(vswhere, "-latest", "-products", "*", "-requires", component, "-property", name)
        return found("catalog_productDisplayVersion") or found("installationVersion")
    return {"dotnet": command("dotnet", "--version"), "fxcPath": str(fxc),
            "fxc": file_version(fxc) if fxc.is_file() else "",
            "visualStudioX64": visual_studio("Microsoft.VisualStudio.Component.VC.Tools.x86.x64"),
            "visualStudioArm64": visual_studio("Microsoft.VisualStudio.Component.VC.Tools.ARM64")}


def toolchain(root=ROOT, found=None):
    """Compares the runner with the pins. Returns (report, problems): the .NET SDK must match exactly, the Windows SDK
    build and the Visual Studio major version must match, and later revisions are reported, not refused."""
    pinned = read_json(root / TOOLCHAIN, TOOLCHAIN.as_posix())
    sdk = re.fullmatch(r"(\d+\.\d+\.\d+)\.(\d+)", pinned.get("windowsSdk", ""))
    studio = re.match(r"(\d+)\.\d+(?:\.\d+)?", pinned.get("visualStudio", ""))
    if not sdk or not studio:
        raise Stop(f"{TOOLCHAIN.as_posix()} has no exact windowsSdk or visualStudio version.")
    found = found if found is not None else probe(f"{sdk.group(1)}.0")
    problems, rows = [], []
    def row(name, want, have, status):
        rows.append((name, want, have or "missing", status))
    if found["dotnet"] == pinned["dotnetSdk"]:
        row(".NET SDK", pinned["dotnetSdk"], found["dotnet"], "same")
    else:
        row(".NET SDK", pinned["dotnetSdk"], found["dotnet"], "DIFFERENT")
        problems.append(f".NET SDK {found['dotnet'] or 'missing'} is not the pinned {pinned['dotnetSdk']}.")
    if not found["fxc"]:
        row("Windows SDK (fxc.exe)", pinned["windowsSdk"], "", "MISSING")
        problems.append(f"fxc.exe of Windows SDK {sdk.group(1)} is missing ({found['fxcPath']}).")
    elif not found["fxc"].startswith(sdk.group(1) + "."):
        row("Windows SDK (fxc.exe)", pinned["windowsSdk"], found["fxc"], "DIFFERENT")
        problems.append(f"fxc.exe {found['fxc']} is not from Windows SDK {sdk.group(1)}.")
    else:
        row("Windows SDK (fxc.exe)", pinned["windowsSdk"], found["fxc"],
            "same" if found["fxc"] == pinned["windowsSdk"] else "later revision of the same SDK")
    for label, key in (("x64", "visualStudioX64"), ("ARM64", "visualStudioArm64")):
        have = found[key]
        name = f"Visual Studio C++ {label} tools"
        if not have:
            row(name, studio.group(0), "", "MISSING")
            problems.append(f"Visual Studio's C++ {label} build tools are missing; Native AOT needs them for the {label} package.")
        elif have.split(".")[0] != studio.group(1):
            row(name, studio.group(0), have, "DIFFERENT")
            problems.append(f"Visual Studio {have} with the C++ {label} tools is not version {studio.group(1)}.")
        else:
            row(name, studio.group(0), have, "same" if have == studio.group(0) else "same major version")
    report = {"pinned": {key: pinned[key] for key in ("dotnetSdk", "windowsSdk", "visualStudio")}, "runner": found}
    return report, problems, rows


# Guards and receipt (Windows runner) -----------------------------------------------------------------------------------

def checkout_head(root=ROOT):
    try:
        result = subprocess.run(["git", "-C", str(root), "rev-parse", "HEAD"], capture_output=True, text=True, timeout=60)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise Stop(f"git could not read the checkout's commit: {error}") from error
    return result.stdout.strip() if result.returncode == 0 else ""


def guards(environ, root=ROOT, out=print, head=checkout_head):
    """--check-store and the usage-count rule of the owner-run publisher. A release or Store build needs both; a test build
    only reports them, and its receipt then says the bundle is not for the Store."""
    required = environ.get("STORE_READY_REQUIRED") == "true"
    candidate = environ.get("CANDIDATE_SHA")
    if candidate is not None and head(root) != candidate:
        raise Stop(f"The checkout is not the candidate {candidate}; the Windows bundle is built only from the exact commit.")
    store_id = environ.get("WINDOWS_STORE_ID") or None
    if store_id and not publish_windows.STORE_ID.fullmatch(store_id):
        raise Stop("The repository variable WINDOWS_STORE_ID is not a 12-character Store ID, such as 9NBLGGH4R315.")
    ready = publish_windows.check_store(root, store_id, out=out)
    try:
        metrics = publish_windows.metrics_url(environ.get("METRICS_URL", ""))
        sending = publish_windows.service_accepts_windows(root)
    except publish_windows.Stop as error:
        raise Stop(str(error)) from error
    if required and not ready:
        raise Stop("A release or Store build needs the Partner Center values. Paste them into Package.appxmanifest as "
                   "docs/maintainers/windows-store.md says, commit them with the candidate and run again.")
    if required and sending and not metrics:
        raise Stop("UsageMetrics.ServiceAcceptsWindows is on, so a Store build needs the usage-count service: set the "
                   "repository variable METRICS_URL to the website's HTTPS root.")
    if not ready:
        out("TEST BUILD: the Store values are not ready, so this bundle is only for testing, not for the Store.")
    return {"store-identity": "ready" if ready else "not-ready", "metrics-url": metrics}


def receipt(packages, output, environ, toolchain_file=None, root=ROOT):
    """Checks package.ps1's report against this run, then copies the bundle and report into a fresh artifact folder with
    SHA256SUMS and windows-release.json."""
    packages, output = Path(packages), Path(output)
    source, version, msix = environ.get("CANDIDATE_SHA", ""), environ.get("EXPECTED_VERSION", ""), environ.get("MSIX", "")
    if not SOURCE.fullmatch(source):
        raise Stop("CANDIDATE_SHA is not a full commit SHA.")
    configured, build = release.configured_version(root)
    if configured != version or publish_windows.msix_version(version, build) != msix:
        raise Stop(f"The checkout's version {configured} ({build}) is not this run's {version}, package {msix}.")
    report = read_json(packages / "package-report.json", "package-report.json")
    metrics = environ.get("METRICS_URL", "")
    store_identity = environ.get("STORE_IDENTITY") == "ready"
    bundle_info = report.get("bundle") or {}
    name = bundle_info.get("file", "")
    problems = []
    if report.get("version") != msix:
        problems.append(f"the packages are version {report.get('version')}, not {msix}")
    if report.get("label") != f"{version} ({build})":
        problems.append(f"the packages say {report.get('label')}, not {version} ({build})")
    if report.get("buildStamp") != f"Release {source[:12]}":
        problems.append(f"the build stamp is {report.get('buildStamp')!r}, not 'Release {source[:12]}'")
    if (report.get("metricsUrl") or "") != metrics:
        problems.append(f"the usage-count service is {report.get('metricsUrl') or 'none'}, not {metrics or 'none'}")
    if report.get("problems"):
        problems.append("package.ps1 listed problems: " + "; ".join(map(str, report["problems"])))
    if sorted(bundle_info.get("architectures") or []) != ARCHITECTURES:
        problems.append("the bundle needs the x64 and ARM64 packages")
    if name != f"Annotator_{msix}.msixbundle":
        problems.append(f"the bundle is {name!r}, not Annotator_{msix}.msixbundle")
    if environ.get("STORE_READY_REQUIRED") == "true" and (not store_identity or report.get("placeholderIdentity")):
        problems.append("a release or Store build cannot carry the PLACEHOLDER Store identity")
    bundle = packages / name
    digest = sha256(bundle) if name and bundle.is_file() else ""
    if not digest or digest != bundle_info.get("sha256") or bundle.stat().st_size != bundle_info.get("bytes"):
        problems.append(f"{bundle} is missing or differs from package-report.json")
    if problems:
        raise Stop("The packages do not match this run: " + "; ".join(problems) + ".")
    if output.exists():
        raise Stop(f"{output} already exists. A built bundle is never replaced; use a new folder.")
    output.mkdir(parents=True)
    shutil.copyfile(bundle, output / name)
    shutil.copyfile(packages / "package-report.json", output / "package-report.json")
    if sha256(output / name) != digest:
        raise Stop("The bundle changed while it was copied into the artifact folder.")
    run = {"id": environ.get("GITHUB_RUN_ID", ""), "attempt": environ.get("GITHUB_RUN_ATTEMPT", ""),
           "url": (f"{environ['GITHUB_SERVER_URL']}/{environ['GITHUB_REPOSITORY']}/actions/runs/{environ['GITHUB_RUN_ID']}"
                   if all(environ.get(key) for key in ("GITHUB_SERVER_URL", "GITHUB_REPOSITORY", "GITHUB_RUN_ID")) else "")}
    data = {"schema": 1, "releaseVersion": version, "build": build, "msixVersion": msix, "source": source,
            "buildStamp": report["buildStamp"], "metricsUrl": metrics,
            "storeReady": store_identity and not report.get("placeholderIdentity"),
            "bundle": {"file": name, "bytes": bundle_info["bytes"], "sha256": digest,
                       "architectures": sorted(bundle_info["architectures"])},
            "packages": [{key: package.get(key) for key in ("architecture", "file", "bytes", "sha256")}
                         for package in report.get("packages") or []],
            "run": run, "toolchain": read_json(toolchain_file, "The toolchain report") if toolchain_file else None}
    write_text(output / RECEIPT, json.dumps(data, indent=2) + "\n")
    write_text(output / "SHA256SUMS", f"{digest}  {name}\n")
    return data


def verify(folder, environ):
    """The Store job's check: the artifact holds this run's Store-ready bundle with the build job's SHA-256."""
    folder = Path(folder)
    data = read_json(folder / RECEIPT, RECEIPT)
    expected = environ.get("BUNDLE_SHA256", "")
    name = (data.get("bundle") or {}).get("file", "")
    problems = []
    if not DIGEST.fullmatch(expected):
        problems.append("the build job gave no SHA-256")
    if data.get("schema") != 1 or data.get("source") != environ.get("CANDIDATE_SHA") or data.get("msixVersion") != environ.get("MSIX"):
        problems.append("the receipt is for another candidate or package version")
    # Resume Store submission sends a bundle from an earlier Release run; resume() checked that run first.
    if (data.get("run") or {}).get("id") != (environ.get("BUNDLE_RUN_ID") or environ.get("GITHUB_RUN_ID")):
        problems.append("the bundle was built in another run")
    if data.get("storeReady") is not True:
        problems.append("the bundle was built as a test build, not for the Store")
    if not re.fullmatch(r"Annotator_[0-9.]+\.msixbundle", name) or not (folder / name).is_file():
        problems.append("the bundle is missing")
    elif not (sha256(folder / name) == data["bundle"].get("sha256") == expected):
        problems.append("the bundle's SHA-256 is not the one the build job recorded")
    try:
        sums = (folder / "SHA256SUMS").read_text(encoding="utf-8")
    except OSError:
        sums = ""
    if sums != f"{expected}  {name}\n":
        problems.append("SHA256SUMS does not list the bundle")
    if problems:
        raise Stop("The Store job will not send this artifact: " + "; ".join(problems) + ".")
    return folder / name


def resume(folder, environ):
    """Resume Store submission's check of an earlier Release run's bundle: it was built in that run, from the commit
    that the run and the published v<version> tag both point at. Returns what verify() and submit() then expect."""
    data = read_json(Path(folder) / RECEIPT, RECEIPT)
    run_id = environ.get("SOURCE_RUN_ID", "")
    run_sha = environ.get("SOURCE_RUN_SHA", "")
    tag_sha = environ.get("TAG_SHA", "")
    bundle = data.get("bundle") or {}
    problems = []
    if not re.fullmatch(r"[0-9]+", run_id) or (data.get("run") or {}).get("id") != run_id:
        problems.append("it was not built in the chosen Release run")
    if not re.fullmatch(r"[0-9a-f]{40}", run_sha) or data.get("source") != run_sha:
        problems.append("it was built from another commit than the chosen run")
    if tag_sha != run_sha:
        problems.append(f"tag v{data.get('releaseVersion')} does not point at the run's commit, so that run did not "
                        "publish this version")
    if data.get("storeReady") is not True:
        problems.append("it was built as a test build, not for the Store")
    if not DIGEST.fullmatch(bundle.get("sha256") or ""):
        problems.append("the receipt has no SHA-256 for it")
    if problems:
        raise Stop("Resume Store submission will not send this bundle: " + "; ".join(problems) + ".")
    return {"candidate": run_sha, "msix": data.get("msixVersion", ""), "sha256": bundle["sha256"],
            "version": data.get("releaseVersion", "")}


# Microsoft Store Developer CLI -----------------------------------------------------------------------------------------

def download(url, path):
    request = urllib.request.Request(url, headers={"User-Agent": "annotator-release"})
    with urllib.request.urlopen(request, timeout=300) as response, open(path, "wb") as stream:
        shutil.copyfileobj(response, stream)


def install_cli(folder, root=ROOT, fetch=download):
    """Downloads the pinned CLI release asset, checks its SHA-256 and unpacks it. Returns the path of msstore.exe."""
    pin = read_json(root / TOOLCHAIN, TOOLCHAIN.as_posix()).get("msstoreCli") or {}
    version, asset, digest = pin.get("version", ""), pin.get("asset", ""), pin.get("sha256", "")
    if not re.fullmatch(r"\d+\.\d+\.\d+", version) or not re.fullmatch(r"MSStoreCLI-win-(x64|arm64)\.zip", asset) \
            or not DIGEST.fullmatch(digest):
        raise Stop(f"{TOOLCHAIN.as_posix()} needs msstoreCli with an exact version, the Windows asset and its sha256.")
    folder = Path(folder)
    if folder.exists():
        raise Stop(f"{folder} already exists; the CLI goes into a new folder.")
    folder.mkdir(parents=True)
    archive = folder / asset
    try:
        fetch(f"https://github.com/microsoft/msstore-cli/releases/download/v{version}/{asset}", archive)
    except OSError as error:
        raise Stop(f"The Microsoft Store Developer CLI {version} could not be downloaded: {error}") from error
    if sha256(archive) != digest:
        raise Stop(f"{asset} {version} is not the file pinned in {TOOLCHAIN.as_posix()}; nothing was unpacked.")
    target = folder / "bin"
    with zipfile.ZipFile(archive) as bundle:
        for member in bundle.namelist():
            parts = PurePosixPath(member.replace("\\", "/")).parts
            if member.startswith(("/", "\\")) or ".." in parts or (parts and ":" in parts[0]):
                raise Stop(f"{asset} holds an unsafe path: {member}")
        bundle.extractall(target)
    executable = target / "msstore.exe"
    if not executable.is_file():
        raise Stop(f"{asset} has no msstore.exe.")
    return executable


class StoreCLI:
    """Runs msstore with a time limit and never shows its arguments in an error, because reconfigure gets credentials."""

    def __init__(self, executable, out=print):
        self.executable = str(executable)
        self.out = out

    def __call__(self, *argv, timeout, label):
        try:
            result = subprocess.run([self.executable, *argv], capture_output=True, text=True, encoding="utf-8",
                                    errors="replace", timeout=timeout, stdin=subprocess.DEVNULL)
        except subprocess.TimeoutExpired as error:
            raise Stop(f"msstore {label} did not finish within {timeout // MINUTES} minutes.") from error
        except OSError as error:
            raise Stop(f"msstore {label} could not start: {error}") from error
        text = (result.stdout or "") + (result.stderr or "")
        if label != "reconfigure":
            self.out(text.rstrip())
        if result.returncode:
            raise Stop(f"msstore {label} failed with exit code {result.returncode}.")
        return text


def submit(folder, executable, environ, run=None, out=print):
    """Signs in with the Partner Center secrets, then sends the bundle: testers go to the package flight when
    WINDOWS_STORE_FLIGHT_ID is set, otherwise to a private-audience submission; public needs the explicit input."""
    credentials = [environ.pop(name, "") for name in CREDENTIALS]
    bundle = verify(folder, environ)
    target = environ.get("STORE_TARGET", "")
    store_id = environ.get("WINDOWS_STORE_ID", "")
    flight = environ.get("WINDOWS_STORE_FLIGHT_ID", "")
    if target not in ("testers", "public"):
        raise Stop(f"windows_store {target!r} sends nothing to the Store.")
    if not publish_windows.STORE_ID.fullmatch(store_id):
        raise Stop("WINDOWS_STORE_ID is not a 12-character Store ID.")
    if flight and not FLIGHT.fullmatch(flight):
        raise Stop("WINDOWS_STORE_FLIGHT_ID is not a flight ID. msstore flights list <Store ID> shows each flight's ID.")
    missing = [name for name, value in zip(CREDENTIALS, credentials) if not value]
    if missing:
        raise Stop("The windows-store environment lacks the secrets " + ", ".join(missing) + ".")
    run = run or StoreCLI(executable, out)
    run("settings", "--enableTelemetry", "false", timeout=5 * MINUTES, label="settings")
    tenant, seller, client, secret = credentials
    try:
        run("reconfigure", "--tenantId", tenant, "--sellerId", seller, "--clientId", client, "--clientSecret", secret,
            timeout=10 * MINUTES, label="reconfigure")
        if target == "testers" and flight:
            run("publish", str(bundle), "-id", store_id, "-f", flight, timeout=45 * MINUTES, label="publish")
            result = {"target": "package flight", "flight": flight, "committed": True}
        elif target == "public":
            run("publish", str(bundle), "-id", store_id, timeout=45 * MINUTES, label="publish")
            result = {"target": "public listing", "committed": True}
        else:
            run("publish", str(bundle), "-id", store_id, "--noCommit", timeout=45 * MINUTES, label="publish")
            draft = run("submission", "get", store_id, timeout=10 * MINUTES, label="submission get")
            visibility = re.findall(r'"visibility"\s*:\s*"([A-Za-z]+)"', draft, re.IGNORECASE)
            if visibility != ["Private"]:
                raise Stop("The draft is not limited to a private audience (Partner Center says "
                           f"{', '.join(visibility) or 'nothing'}), so it was not submitted. Check Pricing and availability "
                           "in Partner Center and submit the draft there, or set WINDOWS_STORE_FLIGHT_ID and run again.")
            run("submission", "publish", store_id, timeout=45 * MINUTES, label="submission publish")
            result = {"target": "private audience", "committed": True}
    finally:
        try:
            run("reconfigure", "--reset", timeout=5 * MINUTES, label="reconfigure")
        except Stop as error:
            out(f"::warning::The CLI kept its Partner Center sign-in ({error}). GitHub deletes the runner after the job.")
    return result


# Command line ----------------------------------------------------------------------------------------------------------

def main(argv=None, environ=None):
    environ = os.environ if environ is None else environ
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("plan")
    commands.add_parser("pins")
    check = commands.add_parser("toolchain")
    check.add_argument("--out", type=Path, required=True, help="Where to write the toolchain report for the receipt.")
    commands.add_parser("guards")
    make = commands.add_parser("receipt")
    make.add_argument("--packages", type=Path, required=True, help="package.ps1's folder for this package version.")
    make.add_argument("--output", type=Path, required=True, help="A new folder for the artifact.")
    make.add_argument("--toolchain", type=Path, help="The toolchain step's report.")
    for name in ("verify", "submit", "resume"):
        command = commands.add_parser(name)
        command.add_argument("--folder", type=Path, required=True, help="The downloaded windows-bundle artifact.")
        if name == "submit":
            command.add_argument("--cli", type=Path, required=True, help="The folder that the cli step made.")
    cli = commands.add_parser("cli")
    cli.add_argument("--folder", type=Path, required=True, help="A new folder for the CLI.")
    args = parser.parse_args(argv)
    try:
        if args.command == "plan":
            outputs(plan(environ), environ)
        elif args.command == "pins":
            outputs(pins(), environ)
        elif args.command == "toolchain":
            report, problems, rows = toolchain()
            write_text(args.out, json.dumps(report, indent=2) + "\n")
            table = ["| Tool | Pinned | Runner | Result |", "| --- | --- | --- | --- |"]
            table += [f"| {name} | {want} | {have} | {status} |" for name, want, have, status in rows]
            print("\n".join(table))
            summary(["### Windows toolchain", ""] + table, environ)
            if problems:
                raise Stop(" ".join(problems))
        elif args.command == "guards":
            outputs(guards(environ), environ)
        elif args.command == "receipt":
            data = receipt(args.packages, args.output, environ, args.toolchain)
            outputs({"bundle": data["bundle"]["file"], "sha256": data["bundle"]["sha256"]}, environ)
            summary(["### Windows bundle", "", f"- {data['bundle']['file']}: {data['bundle']['bytes']:,} bytes, "
                     f"{', '.join(data['bundle']['architectures'])}",
                     f"- SHA-256: `{data['bundle']['sha256']}`",
                     f"- {data['releaseVersion']} ({data['build']}) from `{data['source']}`",
                     "- For the Store: " + ("yes" if data["storeReady"] else "no, a test build")], environ)
        elif args.command == "resume":
            values = resume(args.folder, environ)
            outputs(values, environ)
            print(f"The bundle of Release run {environ.get('SOURCE_RUN_ID')} is {values['version']}, "
                  f"package {values['msix']}, built from {values['candidate']}.")
        elif args.command == "verify":
            print(f"Checked {verify(args.folder, environ)}.")
        elif args.command == "cli":
            print(f"Installed {install_cli(args.folder)}.")
        elif args.command == "submit":
            result = submit(args.folder, args.cli / "bin" / "msstore.exe", environ)
            line = f"Sent to the Microsoft Store: {result['target']}. Certification can take up to three business days."
            print(line)
            summary(["### Microsoft Store", "", line], environ)
    except (Stop, release.ReleaseError, publish_windows.Stop) as error:
        print(f"::error::{error}")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
