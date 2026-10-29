"""Tests of the Windows steps of the Release workflow (tools/windows/hosted_release.py) with stand-ins for the runner, the
downloads and the Microsoft Store Developer CLI: nothing builds, installs or submits.
Run: python tools/windows/test_hosted_release.py"""
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
import zipfile

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("hosted_release", HERE / "hosted_release.py")
hosted = importlib.util.module_from_spec(spec)
spec.loader.exec_module(hosted)

PROJECT = "settings:\n  base:\n    MARKETING_VERSION: 0.2.0\n    CURRENT_PROJECT_VERSION: 200\n"
MANIFEST = """<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
  <Identity Name="{name}" Publisher="{publisher}" Version="0.0.0.0" />
  <Properties><DisplayName>Annotator</DisplayName><PublisherDisplayName>{display}</PublisherDisplayName></Properties>
</Package>
"""
STORE = {"name": "12345Owner.Annotator", "publisher": "CN=00000000-1111-2222-3333-444444444444", "display": "Owner"}
PLACEHOLDER = {"name": "PLACEHOLDER.Annotator", "publisher": "CN=PLACEHOLDER-PARTNER-CENTER-PUBLISHER-ID",
               "display": "PLACEHOLDER Partner Center publisher name"}
METRICS = "public static class UsageMetrics\n{{\n    public const bool ServiceAcceptsWindows = {value};\n}}\n"
SOURCE = "0123456789abcdef0123456789abcdef01234567"
STORE_ID = "9NBLGGH4R315"
BUNDLE = b"bundle bytes"
SECRETS = {"PARTNER_CENTER_TENANT_ID": "tenant-secret", "PARTNER_CENTER_SELLER_ID": "seller-secret",
           "PARTNER_CENTER_CLIENT_ID": "client-secret-id", "PARTNER_CENTER_CLIENT_SECRET": "very-secret-value"}


class Fixture(unittest.TestCase):
    def setUp(self):
        # Scratch folders live in the ignored apps/windows/out folder of this checkout, never in the system temp folder.
        scratch = hosted.ROOT / "apps" / "windows" / "out" / "hosted-release-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.base = Path(self.temporary.name)
        self.root = self.base / "source"
        (self.root / "apps/macos").mkdir(parents=True)
        (self.root / "apps/macos/project.yml").write_text(PROJECT, encoding="utf-8")
        (self.root / "releases").mkdir()
        self.switch(False)
        (self.root / "tools/windows").mkdir(parents=True)
        (self.root / "tools/windows/toolchain.json").write_text(
            (hosted.ROOT / "tools/windows/toolchain.json").read_text(encoding="utf-8"), encoding="utf-8")
        manifest = self.root / hosted.publish_windows.MANIFEST
        manifest.parent.mkdir(parents=True)
        self.identity(STORE)
        self.sending(False)

    def tearDown(self):
        self.temporary.cleanup()

    def switch(self, value, **extra):
        data = {"$comment": "fixture", "partOfReleases": value, **extra}
        (self.root / "releases/windows.json").write_text(json.dumps(data), encoding="utf-8")

    def identity(self, values):
        (self.root / hosted.publish_windows.MANIFEST).write_text(MANIFEST.format(**values), encoding="utf-8")

    def sending(self, on):
        path = self.root / hosted.publish_windows.METRICS
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(METRICS.format(value="true" if on else "false"), encoding="utf-8")


class SwitchAndPlanTests(Fixture):
    def environ(self, **values):
        base = {"GITHUB_EVENT_NAME": "workflow_dispatch", "RELEASE_PROCEEDS": "false", "BUILD_WINDOWS": "false",
                "STORE_TARGET": "none", "STORE_SUBMISSION_ENABLED": "", "WINDOWS_STORE_ID": "",
                "TRIGGERING_ACTOR": "owner", "REPOSITORY_OWNER": "owner"}
        return {**base, **values}

    def plan(self, **values):
        with redirect_stdout(io.StringIO()):
            return hosted.plan(self.environ(**values), self.root)

    def test_the_checked_in_switch_is_valid(self):
        self.assertIsInstance(hosted.part_of_releases(), bool)

    def test_the_switch_holds_only_a_boolean(self):
        self.assertFalse(hosted.part_of_releases(self.root))
        self.switch(True)
        self.assertTrue(hosted.part_of_releases(self.root))
        for wrong in ("true", 1, None):
            self.switch(wrong)
            with self.subTest(value=wrong), self.assertRaises(hosted.Stop):
                hosted.part_of_releases(self.root)
        self.switch(True, storeId=STORE_ID)
        with self.assertRaises(hosted.Stop):
            hosted.part_of_releases(self.root)
        (self.root / "releases/windows.json").write_text("{", encoding="utf-8")
        with self.assertRaises(hosted.Stop):
            hosted.part_of_releases(self.root)

    def test_windows_costs_nothing_until_the_switch_is_on_and_a_release_proceeds(self):
        for event in ("push", "workflow_dispatch"):
            for proceeds in ("true", "false"):
                with self.subTest(event=event, proceeds=proceeds):
                    values = self.plan(GITHUB_EVENT_NAME=event, RELEASE_PROCEEDS=proceeds,
                                       BUILD_WINDOWS="" if event == "push" else "false",
                                       STORE_TARGET="" if event == "push" else "none")
                    self.assertEqual((values["windows"], values["store-ready"], values["store"]), (False, False, "none"))
        self.switch(True)
        self.assertEqual(self.plan(GITHUB_EVENT_NAME="push", RELEASE_PROCEEDS="true", BUILD_WINDOWS="", STORE_TARGET="")
                         ["windows"], True)
        self.assertTrue(self.plan(GITHUB_EVENT_NAME="push", RELEASE_PROCEEDS="true", BUILD_WINDOWS="", STORE_TARGET="")
                        ["store-ready"])
        self.assertFalse(self.plan(RELEASE_PROCEEDS="false")["windows"])

    def test_the_package_version_follows_win_dst_04(self):
        self.assertEqual(self.plan()["msix"], "1.2.0.0")

    def test_a_manual_test_build_is_not_for_the_store(self):
        values = self.plan(BUILD_WINDOWS="true")
        self.assertEqual((values["windows"], values["store-ready"], values["store"]), (True, False, "none"))

    def test_a_store_submission_builds_store_ready_packages_for_the_owner_only(self):
        ready = dict(STORE_TARGET="public", STORE_SUBMISSION_ENABLED="true", WINDOWS_STORE_ID=STORE_ID, RELEASE_PUBLISHES="true")
        values = self.plan(**ready)
        self.assertEqual((values["windows"], values["store-ready"], values["store"]), (True, True, "public"))
        # Every release goes to the public listing with its published release; a run that publishes nothing cannot.
        with self.assertRaisesRegex(hosted.Stop, "publish"):
            self.plan(**{**ready, "STORE_TARGET": "public", "RELEASE_PUBLISHES": "false"})
        for change, message in [({"TRIGGERING_ACTOR": "someone-else"}, "owner"),
                                ({"REPOSITORY_OWNER": "", "TRIGGERING_ACTOR": ""}, "owner"),
                                ({"STORE_SUBMISSION_ENABLED": "false"}, "by hand"),
                                ({"WINDOWS_STORE_ID": "9nblggh4r315"}, "WINDOWS_STORE_ID"),
                                ({"STORE_TARGET": "everyone"}, "windows_store"),
                                ({"STORE_TARGET": "testers"}, "windows_store")]:
            with self.subTest(change=change), self.assertRaisesRegex(hosted.Stop, message):
                self.plan(**{**ready, **change})

    def test_only_a_manual_run_asks_for_windows_inputs(self):
        for change in ({"BUILD_WINDOWS": "true"}, {"STORE_TARGET": "public", "STORE_SUBMISSION_ENABLED": "true",
                                                   "WINDOWS_STORE_ID": STORE_ID}):
            with self.subTest(change=change), self.assertRaisesRegex(hosted.Stop, "manual run"):
                self.plan(GITHUB_EVENT_NAME="push", RELEASE_PROCEEDS="true", **change)

    def test_plan_writes_step_outputs(self):
        output = self.base / "output.txt"
        environ = self.environ(BUILD_WINDOWS="true", GITHUB_OUTPUT=str(output))
        with redirect_stdout(io.StringIO()):
            hosted.outputs(hosted.plan(environ, self.root), environ)
        self.assertEqual(output.read_bytes(), b"windows=true\nstore-ready=false\nstore=none\nmsix=1.2.0.0\n")


class ToolchainTests(Fixture):
    RUNNER = {"dotnet": "10.0.402", "fxcPath": "fxc.exe", "fxc": "10.0.26100.7705",
              "visualStudioX64": "18.5.1", "visualStudioArm64": "18.5.1"}

    def check(self, **changes):
        report, problems, rows = hosted.toolchain(self.root, {**self.RUNNER, **changes})
        return problems, {name: status for name, _, _, status in rows}

    def test_pins_give_the_exact_dotnet_sdk(self):
        self.assertEqual(hosted.pins(self.root), {"dotnet": "10.0.402"})

    def test_the_pinned_toolchain_passes(self):
        problems, statuses = self.check()
        self.assertEqual(problems, [])
        self.assertEqual(set(statuses.values()), {"same"})

    def test_later_revisions_of_the_same_sdk_and_visual_studio_are_reported(self):
        problems, statuses = self.check(fxc="10.0.26100.8249", visualStudioX64="18.10.0",
                                        visualStudioArm64="18.10.12224.181")
        self.assertEqual(problems, [])
        self.assertEqual(statuses["Windows SDK (fxc.exe)"], "later revision of the same SDK")
        self.assertEqual(statuses["Visual Studio C++ ARM64 tools"], "same major version")

    def test_a_different_or_missing_toolchain_stops_the_build(self):
        for change, message in [({"dotnet": "10.0.401"}, ".NET SDK"), ({"dotnet": ""}, ".NET SDK"),
                                ({"fxc": ""}, "fxc.exe"), ({"fxc": "10.0.22621.755"}, "fxc.exe"),
                                ({"visualStudioArm64": ""}, "ARM64"), ({"visualStudioX64": ""}, "x64"),
                                ({"visualStudioX64": "17.14.4"}, "version 18")]:
            with self.subTest(change=change):
                problems, _ = self.check(**change)
                self.assertTrue(any(message in problem for problem in problems), problems)

    @unittest.skipUnless(os.name == "nt", "reads a Windows executable's version")
    def test_file_version_reads_a_windows_executable(self):
        self.assertRegex(hosted.file_version(sys.executable), r"^\d+\.\d+\.\d+\.\d+$")


class GuardTests(Fixture):
    def guards(self, **environ):
        lines = []
        return hosted.guards(environ, self.root, out=lines.append), lines

    def test_a_test_build_reports_the_placeholder_identity(self):
        self.identity(PLACEHOLDER)
        values, lines = self.guards()
        self.assertEqual(values, {"store-identity": "not-ready", "metrics-url": ""})
        self.assertTrue(any(line.startswith("TEST BUILD") for line in lines))

    def test_a_release_or_store_build_needs_the_partner_center_values(self):
        self.identity(PLACEHOLDER)
        with self.assertRaisesRegex(hosted.Stop, "Partner Center values"):
            self.guards(STORE_READY_REQUIRED="true")
        self.identity(STORE)
        values, _ = self.guards(STORE_READY_REQUIRED="true", WINDOWS_STORE_ID=STORE_ID)
        self.assertEqual(values["store-identity"], "ready")

    def test_a_store_id_that_is_not_one_stops(self):
        with self.assertRaisesRegex(hosted.Stop, "WINDOWS_STORE_ID"):
            self.guards(WINDOWS_STORE_ID="not-an-id")

    def test_the_build_runs_only_from_the_candidate_commit(self):
        lines = []
        values = hosted.guards({"CANDIDATE_SHA": SOURCE}, self.root, out=lines.append, head=lambda root: SOURCE)
        self.assertEqual(values["store-identity"], "ready")
        with self.assertRaisesRegex(hosted.Stop, "exact commit"):
            hosted.guards({"CANDIDATE_SHA": SOURCE}, self.root, out=lines.append, head=lambda root: "f" * 40)
        with self.assertRaisesRegex(hosted.Stop, "exact commit"):
            hosted.guards({"CANDIDATE_SHA": SOURCE}, self.root, out=lines.append)

    def test_the_checkout_head_comes_from_git(self):
        self.assertRegex(hosted.checkout_head(), r"^([0-9a-f]{40})?$")

    def test_usage_counts_follow_the_owner_run_publisher(self):
        self.sending(True)
        with self.assertRaisesRegex(hosted.Stop, "METRICS_URL"):
            self.guards(STORE_READY_REQUIRED="true")
        values, _ = self.guards(STORE_READY_REQUIRED="true", METRICS_URL="https://Annotator.Example/")
        self.assertEqual(values["metrics-url"], "https://annotator.example")
        with self.assertRaisesRegex(hosted.Stop, "GitHub Pages"):
            self.guards(METRICS_URL="https://owner.github.io/")


class BundleFixture(Fixture):
    def setUp(self):
        super().setUp()
        self.packages = self.base / "packages" / "1.2.0.0"
        self.packages.mkdir(parents=True)
        (self.packages / "Annotator_1.2.0.0.msixbundle").write_bytes(BUNDLE)
        self.report = {"version": "1.2.0.0", "label": "0.2.0", "buildStamp": f"Release {SOURCE[:12]}",
                       "metricsUrl": "", "placeholderIdentity": False, "problems": [],
                       "packages": [{"architecture": "x64", "file": "Annotator_1.2.0.0_x64.msix", "bytes": 3,
                                     "sha256": "a" * 64, "files": 99, "payload": "not compared"}],
                       "bundle": {"file": "Annotator_1.2.0.0.msixbundle", "bytes": len(BUNDLE),
                                  "sha256": hashlib.sha256(BUNDLE).hexdigest(), "architectures": ["x64", "arm64"]}}
        self.write_report()
        self.environ = {"CANDIDATE_SHA": SOURCE, "EXPECTED_VERSION": "0.2.0", "MSIX": "1.2.0.0",
                        "STORE_IDENTITY": "ready", "STORE_READY_REQUIRED": "true", "METRICS_URL": "",
                        "GITHUB_RUN_ID": "42", "GITHUB_RUN_ATTEMPT": "1", "GITHUB_SERVER_URL": "https://github.com",
                        "GITHUB_REPOSITORY": "owner/annotator-code"}

    def write_report(self):
        # package.ps1 writes UTF-8 without a byte order mark; the reader accepts one as well.
        (self.packages / "package-report.json").write_text(json.dumps(self.report), encoding="utf-8-sig")

    def receipt(self, output="artifact", **environ):
        return hosted.receipt(self.packages, self.base / output, {**self.environ, **environ}, root=self.root)


class ReceiptTests(BundleFixture):
    def test_the_receipt_records_the_bundle_and_its_sha256(self):
        data = self.receipt()
        digest = hashlib.sha256(BUNDLE).hexdigest()
        artifact = self.base / "artifact"
        self.assertEqual(sorted(path.name for path in artifact.iterdir()),
                         ["Annotator_1.2.0.0.msixbundle", "SHA256SUMS", "package-report.json", "windows-release.json"])
        self.assertEqual((artifact / "SHA256SUMS").read_bytes(), f"{digest}  Annotator_1.2.0.0.msixbundle\n".encode())
        self.assertEqual((artifact / "Annotator_1.2.0.0.msixbundle").read_bytes(), BUNDLE)
        saved = json.loads((artifact / "windows-release.json").read_text(encoding="utf-8"))
        self.assertEqual(saved, data)
        self.assertEqual((data["releaseVersion"], data["build"], data["msixVersion"], data["source"]),
                         ("0.2.0", 200, "1.2.0.0", SOURCE))
        self.assertEqual(data["bundle"], {"file": "Annotator_1.2.0.0.msixbundle", "bytes": len(BUNDLE), "sha256": digest,
                                          "architectures": ["arm64", "x64"]})
        self.assertTrue(data["storeReady"])
        self.assertEqual(data["run"]["url"], "https://github.com/owner/annotator-code/actions/runs/42")
        self.assertNotIn(b"\r\n", (artifact / "windows-release.json").read_bytes())

    def test_a_test_build_is_marked_as_not_for_the_store(self):
        self.report["placeholderIdentity"] = True
        self.write_report()
        data = self.receipt(STORE_IDENTITY="not-ready", STORE_READY_REQUIRED="false")
        self.assertFalse(data["storeReady"])
        with self.assertRaisesRegex(hosted.Stop, "PLACEHOLDER"):
            self.receipt("second", STORE_IDENTITY="not-ready", STORE_READY_REQUIRED="true")

    def test_packages_that_do_not_match_this_run_stop(self):
        changes = [("version", "1.1.35.0", "version"), ("label", "0.2.0 (200)", "say"),
                   ("buildStamp", "Release other", "build stamp"), ("metricsUrl", "https://annotator.example", "usage-count"),
                   ("problems", ["x64 package lacks resources.pri."], "listed problems")]
        for key, value, message in changes:
            with self.subTest(key=key):
                saved = self.report[key]
                self.report[key] = value
                self.write_report()
                with self.assertRaisesRegex(hosted.Stop, message):
                    self.receipt(f"artifact-{key}")
                self.report[key] = saved
                self.write_report()
        for key, value, message in [("architectures", ["x64"], "ARM64"), ("sha256", "b" * 64, "differs"),
                                    ("bytes", 1, "differs"), ("file", "Other.msixbundle", "Annotator_1.2.0.0")]:
            with self.subTest(bundle=key):
                saved = self.report["bundle"][key]
                self.report["bundle"][key] = value
                self.write_report()
                with self.assertRaisesRegex(hosted.Stop, message):
                    self.receipt(f"artifact-bundle-{key}")
                self.report["bundle"][key] = saved
                self.write_report()
        with self.assertRaisesRegex(hosted.Stop, "version"):
            self.receipt("artifact-version", EXPECTED_VERSION="0.2.1")
        with self.assertRaisesRegex(hosted.Stop, "commit SHA"):
            self.receipt("artifact-source", CANDIDATE_SHA="main")
        self.assertFalse(any(path.name.startswith("artifact") for path in self.base.iterdir()))

    def test_a_built_bundle_is_never_replaced(self):
        self.receipt()
        with self.assertRaisesRegex(hosted.Stop, "never replaced"):
            self.receipt()


class StoreTests(BundleFixture):
    def setUp(self):
        super().setUp()
        self.receipt()
        self.artifact = self.base / "artifact"
        self.store = {"CANDIDATE_SHA": SOURCE, "MSIX": "1.2.0.0", "GITHUB_RUN_ID": "42",
                      "BUNDLE_SHA256": hashlib.sha256(BUNDLE).hexdigest(), "WINDOWS_STORE_ID": STORE_ID,
                      "STORE_TARGET": "public", **SECRETS}
        self.calls = []

    def run_cli(self, *argv, timeout, label):
        self.calls.append(list(argv))
        self.assertGreater(timeout, 0)
        return ""

    def submit(self, **environ):
        lines = []
        result = hosted.submit(self.artifact, Path("msstore.exe"), {**self.store, **environ}, run=self.run_cli,
                               out=lines.append)
        return result, lines

    def commands(self):
        return [call[0] if call[0] != "submission" else " ".join(call[:2]) for call in self.calls]

    def test_verify_accepts_only_this_runs_store_ready_bundle(self):
        self.assertEqual(hosted.verify(self.artifact, self.store), self.artifact / "Annotator_1.2.0.0.msixbundle")
        for change, message in [({"BUNDLE_SHA256": "c" * 64}, "SHA-256"), ({"BUNDLE_SHA256": ""}, "no SHA-256"),
                                ({"GITHUB_RUN_ID": "43"}, "another run"), ({"CANDIDATE_SHA": "f" * 40}, "another candidate"),
                                ({"MSIX": "1.1.27.0"}, "another candidate")]:
            with self.subTest(change=change), self.assertRaisesRegex(hosted.Stop, message):
                hosted.verify(self.artifact, {**self.store, **change})
        (self.artifact / "Annotator_1.2.0.0.msixbundle").write_bytes(b"changed")
        with self.assertRaisesRegex(hosted.Stop, "SHA-256"):
            hosted.verify(self.artifact, self.store)

    def test_resume_accepts_only_the_bundle_that_the_chosen_run_published(self):
        source = {"SOURCE_RUN_ID": "42", "SOURCE_RUN_SHA": SOURCE, "TAG_SHA": SOURCE}
        values = hosted.resume(self.artifact, source)
        self.assertEqual(values, {"candidate": SOURCE, "msix": "1.2.0.0", "sha256": hashlib.sha256(BUNDLE).hexdigest(),
                                  "version": "0.2.0"})
        # The later Store job runs with another run ID; BUNDLE_RUN_ID names the run that built the bundle.
        store = {**self.store, "GITHUB_RUN_ID": "99", "BUNDLE_RUN_ID": "42"}
        self.assertEqual(hosted.verify(self.artifact, store), self.artifact / "Annotator_1.2.0.0.msixbundle")
        with self.assertRaisesRegex(hosted.Stop, "another run"):
            hosted.verify(self.artifact, {**store, "BUNDLE_RUN_ID": "41"})
        for change, message in [({"SOURCE_RUN_ID": "43"}, "not built in the chosen"),
                                ({"SOURCE_RUN_ID": "42; rm"}, "not built in the chosen"),
                                ({"SOURCE_RUN_SHA": "f" * 40, "TAG_SHA": "f" * 40}, "another commit"),
                                ({"TAG_SHA": "e" * 40}, "did not publish"), ({"TAG_SHA": ""}, "did not publish")]:
            with self.subTest(change=change), self.assertRaisesRegex(hosted.Stop, message):
                hosted.resume(self.artifact, {**source, **change})
        data = json.loads((self.artifact / "windows-release.json").read_text(encoding="utf-8"))
        data["storeReady"] = False
        (self.artifact / "windows-release.json").write_text(json.dumps(data), encoding="utf-8")
        with self.assertRaisesRegex(hosted.Stop, "test build"):
            hosted.resume(self.artifact, source)

    def test_verify_refuses_a_test_build(self):
        data = json.loads((self.artifact / "windows-release.json").read_text(encoding="utf-8"))
        data["storeReady"] = False
        (self.artifact / "windows-release.json").write_text(json.dumps(data), encoding="utf-8")
        with self.assertRaisesRegex(hosted.Stop, "test build"):
            hosted.verify(self.artifact, self.store)

    def test_the_public_listing_needs_the_explicit_input(self):
        result, _ = self.submit()
        self.assertEqual(result["target"], "public listing")
        self.assertEqual(self.calls[2], ["publish", str(self.artifact / "Annotator_1.2.0.0.msixbundle"), "-id", STORE_ID])
        self.assertEqual(self.commands(), ["reconfigure", "settings", "publish", "reconfigure"])
        self.assertEqual(self.calls[-1], ["reconfigure", "--reset"])
        # There is no package flight or private audience any more: only the public listing.
        for target in ("none", "testers", ""):
            with self.subTest(target=target), self.assertRaisesRegex(hosted.Stop, "sends nothing"):
                self.submit(STORE_TARGET=target)

    def test_credentials_go_only_to_the_cli_sign_in(self):
        environ = {**self.store}
        hosted.submit(self.artifact, Path("msstore.exe"), environ, run=self.run_cli, out=lambda line: None)
        for name in SECRETS:
            self.assertNotIn(name, environ)
        # The CLI is signed in first: a fresh install refuses every other command until then.
        sign_in = self.calls[0]
        self.assertEqual(sign_in, ["reconfigure", "--tenantId", "tenant-secret", "--sellerId", "seller-secret",
                                   "--clientId", "client-secret-id", "--clientSecret", "very-secret-value"])
        for call in self.calls[1:]:
            self.assertFalse(set(call) & set(SECRETS.values()))

    def test_missing_secrets_or_ids_stop_before_the_cli_runs(self):
        for change, message in [({"PARTNER_CENTER_CLIENT_SECRET": ""}, "PARTNER_CENTER_CLIENT_SECRET"),
                                ({"WINDOWS_STORE_ID": "annotator"}, "WINDOWS_STORE_ID")]:
            with self.subTest(change=change), self.assertRaisesRegex(hosted.Stop, message):
                self.submit(**change)
        self.assertEqual(self.calls, [])

    def test_the_cli_never_shows_its_sign_in_arguments(self):
        lines = []
        cli = hosted.StoreCLI(sys.executable, out=lines.append)
        with self.assertRaises(hosted.Stop) as error:
            cli("-c", "import sys; print('signed in'); sys.exit(3)", "very-secret-value", timeout=60, label="reconfigure")
        self.assertNotIn("very-secret-value", str(error.exception))
        self.assertEqual(lines, [])
        self.assertIn("ok", cli("-c", "print('ok')", timeout=60, label="settings"))


class CliTests(Fixture):
    def archive(self, members):
        path = self.base / "cli.zip"
        with zipfile.ZipFile(path, "w") as bundle:
            for name in members:
                bundle.writestr(name, b"tool")
        return path

    def pin(self, digest):
        path = self.root / "tools/windows/toolchain.json"
        data = json.loads(path.read_text(encoding="utf-8"))
        data["msstoreCli"]["sha256"] = digest
        path.write_text(json.dumps(data), encoding="utf-8")

    def test_the_checked_in_pin_names_an_exact_release(self):
        pin = json.loads((hosted.ROOT / "tools/windows/toolchain.json").read_text(encoding="utf-8"))["msstoreCli"]
        self.assertRegex(pin["version"], r"^\d+\.\d+\.\d+$")
        self.assertEqual(pin["asset"], "MSStoreCLI-win-x64.zip")
        self.assertRegex(pin["sha256"], r"^[0-9a-f]{64}$")

    def test_the_cli_is_unpacked_only_when_its_sha256_matches(self):
        archive = self.archive(["msstore.exe", "msalruntime.dll"])
        urls = []
        def fetch(url, path):
            urls.append(url)
            Path(path).write_bytes(archive.read_bytes())
        self.pin(hashlib.sha256(archive.read_bytes()).hexdigest())
        executable = hosted.install_cli(self.base / "cli", self.root, fetch)
        self.assertEqual(executable, self.base / "cli" / "bin" / "msstore.exe")
        self.assertEqual(urls, ["https://github.com/microsoft/msstore-cli/releases/download/v0.4.3/MSStoreCLI-win-x64.zip"])
        self.pin("0" * 64)
        with self.assertRaisesRegex(hosted.Stop, "not the file pinned"):
            hosted.install_cli(self.base / "other", self.root, fetch)
        self.assertFalse((self.base / "other" / "bin").exists())
        with self.assertRaisesRegex(hosted.Stop, "new folder"):
            hosted.install_cli(self.base / "cli", self.root, fetch)

    def test_unsafe_archive_paths_are_refused(self):
        for index, member in enumerate(("../msstore.exe", "/msstore.exe", "C:/msstore.exe")):
            archive = self.archive(["msstore.exe", member])
            self.pin(hashlib.sha256(archive.read_bytes()).hexdigest())
            with self.subTest(member=member), self.assertRaisesRegex(hosted.Stop, "unsafe path"):
                hosted.install_cli(self.base / f"cli-{index}", self.root,
                                   lambda url, path: Path(path).write_bytes(archive.read_bytes()))
            self.assertFalse((self.base / f"cli-{index}" / "bin").exists())


class CommandLineTests(Fixture):
    def test_errors_become_workflow_errors(self):
        out = io.StringIO()
        with redirect_stdout(out):
            code = hosted.main(["plan"], {"GITHUB_EVENT_NAME": "push", "BUILD_WINDOWS": "true"})
        self.assertEqual(code, 1)
        self.assertIn("::error::Only a manual run", out.getvalue())


if __name__ == "__main__":
    unittest.main()
