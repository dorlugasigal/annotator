"""Tests of the owner-run Windows publisher (WIN-DST-05) with a stand-in for every command: nothing builds, installs,
signs or submits. Run: python tools/windows/test_publish_windows.py"""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("publish_windows", HERE / "publish_windows.py")
publish = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publish)

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
START_MANIFEST = """<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
  <Identity Name="{name}" Publisher="{publisher}" Version="0.0.0.0" />
  <Properties><DisplayName>Annotator</DisplayName><PublisherDisplayName>{display}</PublisherDisplayName></Properties>
  <Applications><Application Id="App"><uap:VisualElements DisplayName="{start}" /></Application></Applications>
</Package>
"""
SWITCH = "public static class UsageMetrics\n{{\n    public const bool ServiceAcceptsWindows = {value};\n}}\n"
SERVICE = "https://annotator.example"


class Machine:
    """The commands, git and probes the publisher uses, as records and files in a temporary folder."""

    def __init__(self, root, folder):
        self.root = root
        self.folder = folder
        self.commands = []
        self.head = "0123456789abcdef0123456789abcdef01234567"
        self.dirty = ""
        self.admin = False
        self.wack = "PASS"
        self.fail = None
        self.reported_service = None

    def run(self, argv, timeout, label):
        self.commands.append((label, [str(arg) for arg in argv]))
        self.timeouts = getattr(self, "timeouts", []) + [timeout]
        if self.fail == label:
            raise publish.Stop(f"{label} failed with exit code 1")
        if label == "package.ps1":
            output = Path(argv[argv.index("-Output") + 1]) / "1.2.0.0"
            output.mkdir(parents=True, exist_ok=True)
            bundle = output / "Annotator_1.2.0.0.msixbundle"
            bundle.write_bytes(b"bundle bytes")
            architectures = ["x64", "arm64"] if "-RequireArm64" in argv else ["x64"]
            service = str(argv[argv.index("-MetricsUrl") + 1]) if "-MetricsUrl" in argv else ""
            report = {"version": "1.2.0.0", "problems": [], "metricsUrl": self.reported_service if self.reported_service is not None
                      else service, "bundle": {
                "file": bundle.name, "sha256": hashlib.sha256(b"bundle bytes").hexdigest(), "architectures": architectures}}
            (output / "package-report.json").write_text(json.dumps(report), encoding="utf-8")
        if label == "appcert test":
            report = Path(argv[argv.index("-reportoutputpath") + 1])
            report.write_text(wack_report(self.wack), encoding="utf-8")

    def git(self, *args):
        if args == ("status", "--porcelain"):
            return self.dirty
        if args == ("rev-parse", "HEAD"):
            return self.head
        raise AssertionError(args)


def wack_report(result, version="1.2.0.0"):
    return (f'<?xml version="1.0" encoding="utf-8"?><REPORT OVERALL_RESULT="{result}">'
            f'<APPLICATIONS><Installed_Programs><Program Name="Annotator" Version="{version}" /></Installed_Programs>'
            "</APPLICATIONS></REPORT>")


class PublisherTests(unittest.TestCase):
    def setUp(self):
        # Scratch folders live in the ignored apps/windows/out folder of this checkout, never in the system temp folder.
        scratch = publish.ROOT / "apps" / "windows" / "out" / "publisher-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        base = Path(self.temporary.name)
        self.root = base / "source"
        (self.root / "apps/macos").mkdir(parents=True)
        (self.root / "apps/macos/project.yml").write_text(PROJECT, encoding="utf-8")
        (self.root / publish.MANIFEST).parent.mkdir(parents=True)
        self.identity(STORE)
        self.sending(False)
        self.folder = base / "release"
        self.appcert = base / "appcert.exe"
        self.appcert.write_bytes(b"")
        self.machine = Machine(self.root, self.folder)
        self.lines = []

    def tearDown(self):
        self.temporary.cleanup()

    def identity(self, values):
        (self.root / publish.MANIFEST).write_text(MANIFEST.format(**values), encoding="utf-8")

    def sending(self, on):
        path = self.root / publish.METRICS
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(SWITCH.format(value="true" if on else "false"), encoding="utf-8")

    def publisher(self, **options):
        return publish.Publisher(self.folder, root=self.root, run=self.machine.run, git=self.machine.git,
                                 admin=lambda: self.machine.admin, appcert=self.appcert, platform="nt", environ={},
                                 out=self.lines.append, **options)

    def statuses(self):
        state = json.loads((self.folder / "progress.json").read_text(encoding="utf-8"))
        return {name: entry["status"] for name, entry in state["stages"].items()}

    def labels(self):
        return [label for label, _ in self.machine.commands]

    def test_the_msix_version_is_major_plus_one_minor_patch_zero(self):
        self.assertEqual(publish.msix_version("0.2.0"), "1.2.0.0")
        self.assertEqual(publish.msix_version("2.3.4"), "3.3.4.0")
        # Releases up to 0.1.0-beta.17 used their counted build in the third place.
        self.assertEqual(publish.msix_version("0.1.0-beta.17", 35), "1.1.35.0")
        for version, build in (("0.1.0-beta.17", None), ("0.2.0-rc.1", None), ("0.2", None)):
            with self.subTest(version=version), self.assertRaises(publish.Stop):
                publish.msix_version(version, build)

    def test_the_repository_manifest_has_the_placeholders_or_well_formed_store_values(self):
        # Agents keep the PLACEHOLDER values; the owner replaces them with the Partner Center values (WIN-DST-01).
        identity = publish.package_identity(publish.ROOT / publish.MANIFEST)
        if not identity["placeholder"]:
            self.assertEqual(publish.identity_problems(identity), [])
        self.identity(STORE)
        self.assertFalse(publish.package_identity(self.root / publish.MANIFEST)["placeholder"])

    def listing(self, store_id):
        path = self.root / publish.WEBSITE_LISTING
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps({"storeId": store_id}), encoding="utf-8")

    def test_check_store_shows_each_value_and_changes_nothing(self):
        self.listing(None)
        self.assertTrue(publish.check_store(self.root, "9NBLGGH4R315", out=self.lines.append))
        self.assertIn("  Package/Identity/Name                    12345Owner.Annotator", self.lines)
        self.assertIn("  Package/Identity/Publisher               CN=00000000-1111-2222-3333-444444444444", self.lines)
        self.assertTrue(any("storeId is null" in line for line in self.lines))
        self.assertTrue(self.lines[-1].startswith("READY"))
        self.assertFalse(self.folder.exists())
        self.assertEqual(self.machine.commands, [])

    def test_check_store_finds_placeholders_and_copy_mistakes(self):
        cases = [(PLACEHOLDER, "PLACEHOLDER or empty"),
                 (dict(STORE, publisher="00000000-1111-2222-3333-444444444444"), "must start with CN="),
                 (dict(STORE, name="12345Owner Annotator"), "is not a package name"),
                 (dict(STORE, publisher=STORE["publisher"] + " "), "Publisher has spaces or line breaks"),
                 (dict(STORE, display=" Owner"), "PublisherDisplayName has spaces or line breaks")]
        for values, message in cases:
            with self.subTest(message=message):
                self.identity(values)
                self.lines.clear()
                self.assertFalse(publish.check_store(self.root, out=self.lines.append))
                self.assertTrue(any(line.startswith("PROBLEM") and message in line for line in self.lines), self.lines)
                self.assertTrue(self.lines[-1].startswith("NOT READY"))

    def test_the_start_menu_name_is_the_package_display_name(self):
        manifest = self.root / publish.MANIFEST
        manifest.write_text(START_MANIFEST.format(start="Annotator Draw", **STORE), encoding="utf-8")
        self.assertFalse(publish.check_store(self.root, out=self.lines.append))
        self.assertTrue(any("Use the name you reserved for both" in line for line in self.lines))
        manifest.write_text(START_MANIFEST.format(start="Annotator", **STORE), encoding="utf-8")
        self.assertTrue(publish.check_store(self.root, out=self.lines.append))

    def test_check_store_needs_the_same_store_id_in_every_place(self):
        self.listing("9NBLGGH4R315")
        self.assertTrue(publish.check_store(self.root, "9NBLGGH4R315", out=self.lines.append))
        self.assertFalse(publish.check_store(self.root, "9NBLGGH4R316", out=self.lines.append))
        self.assertTrue(any("is not the Store ID 9NBLGGH4R316" in line for line in self.lines))
        for listing in ({"storeId": "9nblggh4r315"}, {"storeId": "9NBLGGH4R31"}, {"storeId": None, "url": "x"},
                        ["9NBLGGH4R315"]):
            with self.subTest(listing=listing):
                (self.root / publish.WEBSITE_LISTING).write_text(json.dumps(listing), encoding="utf-8")
                self.lines.clear()
                self.assertFalse(publish.check_store(self.root, out=self.lines.append))
                self.assertTrue(any(line.startswith("PROBLEM") for line in self.lines))

    def test_a_store_run_stops_on_a_copy_mistake(self):
        self.identity(dict(STORE, publisher="00000000-1111-2222-3333-444444444444"))
        with self.assertRaisesRegex(publish.Stop, "must start with CN="):
            self.publisher().execute()
        self.assertEqual(self.statuses(), {"Freeze source": "failed"})
        self.assertEqual(self.machine.commands, [])

    def test_check_store_needs_no_release_folder_and_checks_the_store_id(self):
        with redirect_stdout(io.StringIO()) as output, redirect_stderr(io.StringIO()) as error:
            self.assertIn(publish.main(["--check-store"]), (0, 1))
            self.assertEqual(publish.main(["--check-store", "--store-id", "9nblggh4r315"]), 1)
        self.assertIn("Package.appxmanifest (from Partner Center", output.getvalue())
        self.assertIn("--store-id must be the 12-character Store ID", error.getvalue())
        with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            publish.main(["--status"])

    def test_the_plan_changes_nothing(self):
        self.publisher().plan()
        self.assertFalse(self.folder.exists())
        self.assertEqual(self.machine.commands, [])
        self.assertIn("Candidate: 0.2.0, MSIX package 1.2.0.0", self.lines)
        self.assertTrue(any(line.startswith("PLAN ONLY") for line in self.lines))

    def test_a_store_run_needs_the_partner_center_identity(self):
        self.identity(PLACEHOLDER)
        with self.assertRaisesRegex(publish.Stop, "PLACEHOLDER Store identity"):
            self.publisher().execute()
        self.assertEqual(self.statuses(), {"Freeze source": "failed"})
        self.assertEqual(self.machine.commands, [])

    def test_a_changed_checkout_stops_before_anything_runs(self):
        self.machine.dirty = " M apps/windows/src/Annotator.App/AppHost.cs"
        with self.assertRaisesRegex(publish.Stop, "Commit the approved candidate"):
            self.publisher().execute()

    def test_only_on_the_owners_windows_computer(self):
        with self.assertRaisesRegex(publish.Stop, "GitHub Actions"):
            publish.Publisher(self.folder, root=self.root, run=self.machine.run, git=self.machine.git, platform="nt",
                              environ={"GITHUB_ACTIONS": "true"}, out=self.lines.append).execute()
        with self.assertRaisesRegex(publish.Stop, "runs on Windows"):
            publish.Publisher(self.folder / "other", root=self.root, run=self.machine.run, git=self.machine.git,
                              platform="posix", environ={}, out=self.lines.append).execute()

    def test_a_rehearsal_pauses_for_elevated_wack_and_resumes_from_the_report(self):
        self.identity(PLACEHOLDER)
        with self.assertRaises(publish.Pending) as paused:
            self.publisher(rehearsal=True).execute()
        self.assertIn("appcert.exe' test -appxpackagepath", str(paused.exception))
        self.assertEqual(self.labels(), ["release notes and changelog check", "validate.ps1 all", "package.ps1"])
        self.assertNotIn("-RequireArm64", self.machine.commands[2][1])
        self.assertIn("Release 0123456789ab", self.machine.commands[2][1])
        self.assertEqual(self.statuses(), {"Freeze source": "done", "Validate locally": "done", "Build packages": "done",
                                           "Certify with WACK": "paused"})
        self.assertFalse((self.folder / ".publisher.lock").exists())

        # The owner runs WACK in an elevated terminal, then the same command again.
        report = self.folder / "wack" / "report.xml"
        report.parent.mkdir()
        report.write_text(wack_report("PASS"), encoding="utf-8")
        self.publisher(rehearsal=True).execute()
        self.assertEqual(len(self.machine.commands), 3)
        self.assertEqual(set(self.statuses().values()), {"done"})
        self.assertTrue(any("REHEARSAL" in line for line in self.lines))
        self.assertFalse(any("msstore publish" in line for line in self.lines))

    def test_a_store_run_certifies_and_hands_off_without_submitting(self):
        self.machine.admin = True
        self.publisher(store_id="9NBLGGH4R315").execute()
        self.assertEqual(self.labels(), ["release notes and changelog check", "validate.ps1 all", "package.ps1",
                                         "appcert reset", "appcert test"])
        self.assertIn("-RequireArm64", self.machine.commands[2][1])
        self.assertTrue(all("msstore" not in " ".join(argv) for _, argv in self.machine.commands))
        bundle = self.folder / "packages" / "1.2.0.0" / "Annotator_1.2.0.0.msixbundle"
        command = f'msstore publish "{bundle}" -id 9NBLGGH4R315 --noCommit'
        self.assertTrue(any(command in line for line in self.lines))
        state = json.loads((self.folder / "progress.json").read_text(encoding="utf-8"))
        self.assertEqual(state["stages"]["Freeze source"]["detail"]["msix"], "1.2.0.0")
        self.assertEqual(state["stages"]["Certify with WACK"]["detail"]["result"], "PASS")
        self.assertFalse(state["stages"]["Hand off to the Store"]["detail"]["submitted"])
        self.assertTrue((self.folder / "pipeline.log").read_text(encoding="utf-8").count("DONE") >= 5)

    def test_the_store_needs_both_architectures(self):
        self.machine.admin = True

        def x64_only(argv, timeout, label):
            argv = [arg for arg in argv if arg != "-RequireArm64"]
            self.machine.run(argv, timeout, label)
        publisher = self.publisher()
        publisher.run = x64_only
        with self.assertRaisesRegex(publish.Stop, "x64 and ARM64"):
            publisher.execute()

    def test_a_failed_or_foreign_wack_report_stops(self):
        self.machine.admin = True
        self.machine.wack = "FAIL"
        with self.assertRaisesRegex(publish.Stop, "WACK says FAIL"):
            self.publisher().execute()
        report = self.folder / "wack" / "report.xml"
        report.write_text(wack_report("PASS", version="1.1.24.0"), encoding="utf-8")
        with self.assertRaisesRegex(publish.Stop, "for another package"):
            publish.wack_result(report, "1.2.0.0")
        report.write_text(wack_report("WARNING"), encoding="utf-8")
        self.assertEqual(publish.wack_result(report, "1.2.0.0"), "WARNING")

    def test_a_resumed_run_needs_the_frozen_checkout_and_mode(self):
        self.machine.fail = "validate.ps1 all"
        with self.assertRaises(publish.Stop):
            self.publisher().execute()
        self.assertEqual(self.statuses(), {"Freeze source": "done", "Validate locally": "failed"})
        self.machine.fail = None
        self.machine.head = "fedcba9876543210fedcba9876543210fedcba98"
        with self.assertRaisesRegex(publish.Stop, "checkout changed"):
            self.publisher().execute()
        self.machine.head = "0123456789abcdef0123456789abcdef01234567"
        with self.assertRaisesRegex(publish.Stop, "started for the Store"):
            self.publisher(rehearsal=True).execute()

    def test_a_changed_bundle_is_never_handed_off(self):
        self.machine.admin = True
        publisher = self.publisher()
        original = publisher.certify

        def certify_then_change(frozen, built):
            result = original(frozen, built)
            Path(built["bundle"]).write_bytes(b"other bytes")
            return result
        publisher.certify = certify_then_change
        with self.assertRaisesRegex(publish.Stop, "changed after it was built"):
            publisher.execute()

    def test_one_run_at_a_time(self):
        self.folder.mkdir()
        (self.folder / ".publisher.lock").write_text("", encoding="utf-8")
        with self.assertRaisesRegex(publish.Stop, "Another publisher run"):
            self.publisher().execute()
        self.assertEqual(self.machine.commands, [])

    def test_status_reads_saved_progress_only(self):
        self.publisher().status()
        self.assertTrue(self.lines[0].endswith("Freeze source"))
        self.assertIn("NOT STARTED", self.lines[0])
        self.assertEqual(self.machine.commands, [])

    def test_the_release_folder_stays_outside_the_checkout(self):
        with redirect_stderr(io.StringIO()) as error:
            self.assertEqual(publish.main(["--folder", str(publish.ROOT / "release-folder"), "--status"]), 1)
        self.assertIn("outside the source checkout", error.getvalue())
        self.assertFalse((publish.ROOT / "release-folder").exists())

    def test_the_metrics_url_follows_the_mac_and_app_rules(self):
        self.assertEqual(publish.metrics_url(""), "")
        self.assertEqual(publish.metrics_url("https://Annotator.Example/"), SERVICE)
        for wrong in ["http://annotator.example", "https://owner.github.io", "https://annotator.example/api",
                      "https://annotator.example:8443", "https://user@annotator.example", "https://annotator.example/?a=1",
                      "https://annotator.example/#top", "annotator.example", "https://"]:
            with self.assertRaises(publish.Stop, msg=wrong):
                publish.metrics_url(wrong)
        with redirect_stderr(io.StringIO()) as error:
            self.assertEqual(publish.main(["--folder", str(self.folder), "--metrics-url", "https://owner.github.io"]), 1)
        self.assertIn("not GitHub Pages", error.getvalue())

    def test_the_metrics_url_reaches_the_package_build_and_its_report(self):
        self.machine.admin = True
        self.publisher(metrics=SERVICE + "/").execute()
        argv = self.machine.commands[2][1]
        self.assertEqual(argv[argv.index("-MetricsUrl") + 1], SERVICE)
        state = json.loads((self.folder / "progress.json").read_text(encoding="utf-8"))
        self.assertEqual(state["stages"]["Freeze source"]["detail"]["metricsUrl"], SERVICE)

    def test_without_a_metrics_url_the_build_has_no_service(self):
        self.machine.admin = True
        self.publisher().execute()
        self.assertNotIn("-MetricsUrl", self.machine.commands[2][1])
        self.publisher().plan()
        self.assertIn("Usage counts: no service in the build; nothing is sent while UsageMetrics.ServiceAcceptsWindows is off.",
                      self.lines)

    def test_a_package_built_with_another_service_stops(self):
        self.machine.admin = True
        self.machine.reported_service = "https://elsewhere.example"
        with self.assertRaisesRegex(publish.Stop, "another usage-count service"):
            self.publisher(metrics=SERVICE).execute()

    def test_a_resumed_run_keeps_its_metrics_url(self):
        self.machine.fail = "validate.ps1 all"
        with self.assertRaises(publish.Stop):
            self.publisher(metrics=SERVICE).execute()
        self.machine.fail = None
        with self.assertRaisesRegex(publish.Stop, "started with --metrics-url https://annotator.example"):
            self.publisher().execute()

    def test_a_store_run_needs_the_service_once_sending_is_on(self):
        self.sending(True)
        with self.assertRaisesRegex(publish.Stop, "needs the service"):
            self.publisher().execute()
        self.publisher(rehearsal=True).plan()
        self.assertIn("Usage counts: no service in the build; a Store run stops until --metrics-url is given.", self.lines)
        self.machine.admin = True
        publisher = publish.Publisher(self.folder / "with-service", root=self.root, run=self.machine.run, git=self.machine.git,
                                      admin=lambda: True, appcert=self.appcert, platform="nt", environ={},
                                      out=self.lines.append, metrics=SERVICE)
        publisher.execute()
        publisher.plan()
        self.assertIn(f"Usage counts: {SERVICE}; the app sends them once a person turns them on.", self.lines)

    def test_every_wait_is_bounded(self):
        self.machine.admin = True
        self.publisher().execute()
        self.assertTrue(all(isinstance(timeout, int) and 0 < timeout <= 3 * 3600 for timeout in self.machine.timeouts))


if __name__ == "__main__":
    unittest.main()
