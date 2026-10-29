import base64
import copy
import hashlib
import importlib.util
import json
import os
import plistlib
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import distribution as d

SUBMISSION = "11111111-2222-3333-4444-555555555555"


def manifest():
    archive = b"test archive"
    return dict(releaseVersion="0.2.0", version="0.2.0", build="200",
                repository="example/annotator-releases", tag="v0.2.0", track="stable",
                archive="annotator-0.2.0-arm64.zip",
                downloadURL="https://github.com/example/annotator-releases/releases/download/v0.2.0/annotator-0.2.0-arm64.zip",
                channel="release", identifier="com.dorlugasigal.annotator", architecture="arm64",
                notarizationVerified=True, homepage="https://annotator.example",
                source="a"*40, sha256=hashlib.sha256(archive).hexdigest(), size=len(archive),
                edSignature=base64.b64encode(b"x"*64).decode())


def legacy_manifest():
    """A release from 0.1.0-beta.17 or earlier: a pre-release label, a counted build and the preview track."""
    archive = b"test archive"
    return dict(releaseVersion="0.1.0-alpha.1", version="0.1.0", build="9", prerelease="alpha.1",
                repository="example/annotator-releases", tag="v0.1.0-alpha.1", track="preview",
                archive="annotator-0.1.0-alpha.1-9-arm64.zip",
                downloadURL="https://github.com/example/annotator-releases/releases/download/v0.1.0-alpha.1/annotator-0.1.0-alpha.1-9-arm64.zip",
                channel="release", identifier="com.dorlugasigal.annotator", architecture="arm64",
                notarizationVerified=True, homepage="https://annotator.example",
                source="a"*40, sha256=hashlib.sha256(archive).hexdigest(), size=len(archive),
                edSignature=base64.b64encode(b"x"*64).decode())


def disk_manifest(app=None):
    app = app or manifest()
    archive = b"test dmg"
    name = d.packager.archive_name("annotator", app["releaseVersion"], str(app["build"]), app.get("architecture", "arm64"), "dmg")
    return dict(schema=1, releaseVersion=app["releaseVersion"], build=str(app["build"]),
                source=app["source"], repository=app["repository"], tag=app["tag"],
                archive=name, downloadURL=f"https://github.com/{app['repository']}/releases/download/{app['tag']}/{name}",
                sha256=hashlib.sha256(archive).hexdigest(), size=len(archive),
                volumeName="Annotator", notarizationID="11111111-2222-3333-4444-555555555555",
                stapled=True, appArchive=app["archive"], appSHA256=app["sha256"])


class DistributionTests(unittest.TestCase):
    def signing_fixture(self, root):
        app = root / "Annotator.app"
        version = app / "Contents/Frameworks/Sparkle.framework/Versions/B"
        (version / "XPCServices/Installer.xpc").mkdir(parents=True)
        (version / "XPCServices/Downloader.xpc").mkdir()
        (version / "Updater.app").mkdir()
        (version / "Autoupdate").write_text("fixture")
        return app

    def test_distribution_signs_nested_helpers_inside_out_and_checks_each(self):
        identity = "Developer ID Application: Example"
        details = f"Authority={identity}\nTeamIdentifier=AAAAAAAAAA\nTimestamp=Sep 23, 2026\nCodeDirectory v=20500 flags=0x10000(runtime)\n"
        with tempfile.TemporaryDirectory() as tmp:
            app = self.signing_fixture(Path(tmp))
            def command(*args, **kwargs):
                if "--verbose=4" in args:
                    self.assertTrue(kwargs["stderr"])
                    return details
                if "--entitlements" in args:
                    return plistlib.dumps({"com.apple.security.app-sandbox": True}).decode()
                return ""
            with patch.object(d, "command", side_effect=command) as run:
                d.sign_distribution_bundle(app, identity, "AAAAAAAAAA")
            signs = [call.args for call in run.call_args_list if "--sign" in call.args]
            self.assertEqual([call[-1].name for call in signs],
                             ["Installer.xpc", "Downloader.xpc", "Autoupdate", "Updater.app", "Sparkle.framework", "Annotator.app"])
            for call in signs:
                self.assertIn("--timestamp", call)
                self.assertIn("runtime", call)
                self.assertNotIn("--deep", call)
                self.assertEqual("--preserve-metadata=entitlements" in call,
                                 call[-1].name in ("Downloader.xpc", "Annotator.app"))
            checks = [call.args for call in run.call_args_list if "--verify" in call.args]
            self.assertEqual(len(checks), 7)
            self.assertTrue(all("--all-architectures" in call for call in checks))
            self.assertIn("--deep", checks[-1])

    def test_distribution_rejects_missing_helpers_before_signing(self):
        with tempfile.TemporaryDirectory() as tmp:
            app = self.signing_fixture(Path(tmp))
            (app / "Contents/Frameworks/Sparkle.framework/Versions/B/Autoupdate").unlink()
            with patch.object(d, "command") as run:
                with self.assertRaisesRegex(d.DistributionError, "missing"):
                    d.sign_distribution_bundle(app, "Developer ID Application: Example", "AAAAAAAAAA")
                run.assert_not_called()

    def test_distribution_rejects_unsafe_signatures_or_entitlements(self):
        identity = "Developer ID Application: Example"
        good = f"Authority={identity}\nTeamIdentifier=AAAAAAAAAA\nTimestamp=Sep 23, 2026\nCodeDirectory v=20500 flags=0x10000(runtime)\n"
        cases = [
            (good.replace(identity, "Apple Development: Example"), {}),
            (good.replace("AAAAAAAAAA", "BBBBBBBBBB"), {}),
            (good.replace("Timestamp=Sep 23, 2026\n", ""), {}),
            (good.replace("Timestamp=Sep 23, 2026", "Timestamp=none"), {}),
            (good.replace("(runtime)", "(adhoc)"), {}),
            (good, {"com.apple.security.get-task-allow": True}),
            (good, []),
        ]
        with tempfile.TemporaryDirectory() as tmp:
            app = self.signing_fixture(Path(tmp))
            for details, entitlements in cases:
                def command(*args, **kwargs):
                    if "--verbose=4" in args:
                        return details
                    if "--entitlements" in args:
                        return plistlib.dumps(entitlements).decode()
                    return ""
                with self.subTest(details=details, entitlements=entitlements), patch.object(d, "command", side_effect=command):
                    with self.assertRaises(d.DistributionError):
                        d.sign_distribution_bundle(app, identity, "AAAAAAAAAA")

    def test_signature_diagnostics_can_read_stderr_without_exposing_failed_output(self):
        result = subprocess.CompletedProcess(["codesign"], 0, stdout="entitlements", stderr="signature metadata")
        with patch.object(d.subprocess, "run", return_value=result):
            self.assertEqual(d.command("codesign"), "entitlements")
            self.assertEqual(d.command("codesign", stderr=True), "signature metadata")
        result.returncode = 1
        result.stderr = "sensitive diagnostic"
        with patch.object(d.subprocess, "run", return_value=result):
            with self.assertRaises(d.DistributionError) as error:
                d.command("codesign", stderr=True)
            self.assertNotIn("sensitive diagnostic", str(error.exception))

    def test_preflight_accepts_only_exact_main_push_or_dispatch(self):
        sha = "a" * 40
        env = dict(GITHUB_REF="refs/heads/main", GITHUB_SHA=sha,
                   DISTRIBUTION_REPOSITORY="owner/annotator",
                   GITHUB_REPOSITORY="owner/annotator-code", WEBSITE_URL="https://owner.github.io/annotator")
        for event in ("push", "workflow_dispatch"):
            with self.subTest(event=event), patch.dict(os.environ, {**env, "GITHUB_EVENT_NAME": event}, clear=True), \
                 patch.object(d, "command", return_value=sha), \
                 patch.object(d, "metadata", return_value=("0.2.0", 200)), \
                 patch.object(d.release, "verify_candidate") as candidate:
                info = d.preflight("0.2.0", sha)
                self.assertEqual((info["source"], info["version"], info["build"]), (sha, "0.2.0", 200))
                candidate.assert_called_once_with()
                with self.assertRaisesRegex(d.DistributionError, "Approved version"):
                    d.preflight("0.2.1", sha)
        for overrides in [
            {"GITHUB_EVENT_NAME": "pull_request"},
            {"GITHUB_EVENT_NAME": "workflow_run"},
            {"GITHUB_EVENT_NAME": "push", "GITHUB_REF": "refs/heads/topic"},
            {"GITHUB_EVENT_NAME": "push", "GITHUB_REF": "refs/tags/v0.1.0"},
            {"GITHUB_EVENT_NAME": "push", "GITHUB_SHA": "b" * 40},
            {"GITHUB_EVENT_NAME": "workflow_dispatch", "GITHUB_SHA": ""},
        ]:
            with self.subTest(overrides=overrides), patch.dict(os.environ, {**env, **overrides}, clear=True), \
                 patch.object(d, "command", return_value=sha), patch.object(d, "metadata") as metadata:
                with self.assertRaises(d.DistributionError):
                    d.preflight("0.2.0", sha)
                metadata.assert_not_called()

    def test_configuration_requires_separate_explicit_destinations(self):
        for name in ["", "../repo", "owner/../repo", "owner/repo;bad"]:
            with self.assertRaises(d.DistributionError):
                d.repository(name)
        for url in ["http://example.com", "https://example.com/project", "https://user:pass@example.com"]:
            if url == "https://example.com/project":
                self.assertEqual(d.website_url(url), url)
            else:
                with self.assertRaises((ValueError, d.packager.PackagingError)):
                    d.website_url(url)
        # The open-source repository publishes its own releases.
        with patch.dict(os.environ, {"DISTRIBUTION_REPOSITORY": "owner/annotator",
                                    "GITHUB_REPOSITORY": "owner/annotator", "WEBSITE_URL": "https://example.com"}):
            self.assertEqual(d.configuration(), ("owner/annotator", "https://example.com"))

    def test_website_url_supports_pages_path_without_traversal(self):
        self.assertEqual(d.website_url("https://dorlugasigal.github.io/annotator/"),
                         "https://dorlugasigal.github.io/annotator")
        for url in ["https://example.com/a/../secret", "https://example.com/a%2Fb",
                    "https://example.com//path", "https://example.com:444/path"]:
            with self.assertRaises(ValueError):
                d.website_url(url)
        self.assertEqual(d.website_url("https://example.com/a_b-1/c"), "https://example.com/a_b-1/c")
        with self.assertRaises(ValueError):
            d.website_url("https://example.com/a//b")
        # A long path that almost matches fails at once instead of backtracking exponentially.
        with self.assertRaises(ValueError):
            d.website_url("https://example.com/" + "-" * 64 + "!")

    def test_signing_material_is_owner_only_from_creation_and_never_reused(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "AuthKey.p8"
            previous = os.umask(0)
            try:
                d.private_file(path, b"key")
            finally:
                os.umask(previous)
            self.assertEqual(path.read_bytes(), b"key")
            self.assertEqual(path.stat().st_mode & 0o777, 0o600)
            with self.assertRaises(FileExistsError):
                d.private_file(path, b"other")
            self.assertEqual(path.read_bytes(), b"key")
            link = Path(folder) / "link"
            link.symlink_to(Path(folder) / "elsewhere")
            with self.assertRaises(FileExistsError):
                d.private_file(link, b"key")
            self.assertFalse((Path(folder) / "elsewhere").exists())

    def test_manifest_fails_closed_and_appcast_uses_build_order(self):
        data = manifest()
        self.assertEqual(d.validate_manifest(data), data)
        xml = d.appcast(data)
        self.assertIn("<sparkle:version>200</sparkle:version>", xml)
        self.assertIn("<sparkle:shortVersionString>0.2.0</sparkle:shortVersionString>", xml)
        for key, value in [("notarizationVerified", False), ("channel", "preview"),
                           ("identifier", "com.dorlugasigal.annotator.dev"), ("track", "preview"), ("build", "35"),
                           ("releaseVersion", "0.2.0-beta.1"),
                           ("archive", "../secret"), ("downloadURL", "https://evil.example/app.zip"),
                           ("source", "main"), ("sha256", "bad"), ("edSignature", ""), ("size", 0)]:
            changed = {**data, key: value}
            with self.subTest(key=key), self.assertRaises(ValueError):
                d.validate_manifest(changed)

    def test_universal_manifests_use_universal_names_and_arm64_ones_stay_valid(self):
        data = manifest()
        self.assertEqual(d.validate_manifest(data)["architecture"], "arm64")
        name = "annotator-0.2.0-universal.zip"
        universal = {**data, "architecture": "arm64 x86_64", "archive": name,
                     "downloadURL": data["downloadURL"].rsplit("/", 1)[0] + "/" + name}
        self.assertEqual(d.validate_manifest(universal), universal)
        self.assertEqual(d.validate_disk_image_manifest(disk_manifest(universal), universal)["archive"],
                         "annotator-0.2.0-universal.dmg")
        # A universal build under an Apple Silicon name, or an Intel-only build, is not a verified artifact.
        for changed in ({**data, "architecture": "arm64 x86_64"}, {**data, "architecture": "x86_64"},
                        {**universal, "archive": data["archive"], "downloadURL": data["downloadURL"]}):
            with self.subTest(architecture=changed["architecture"], archive=changed["archive"]), self.assertRaises(ValueError):
                d.validate_manifest(changed)
        silicon_dmg = {**disk_manifest(data), "appArchive": universal["archive"]}
        with self.assertRaises(ValueError):
            d.validate_disk_image_manifest(silicon_dmg, universal)

    def test_disk_image_manifest_must_match_zip_manifest(self):
        app = manifest()
        data = disk_manifest(app)
        self.assertEqual(d.validate_disk_image_manifest(data, app), data)
        for key, value in [("releaseVersion", "0.2.1"), ("build", "201"),
                           ("source", "b" * 40), ("repository", "other/repo"),
                           ("archive", "../bad.dmg"), ("downloadURL", "https://evil.example/app.dmg"),
                           ("sha256", "bad"), ("size", 0), ("volumeName", "Bad"),
                           ("notarizationID", "bad"), ("stapled", False),
                           ("appArchive", "other.zip"), ("appSHA256", "b" * 64)]:
            changed = {**data, key: value}
            with self.subTest(key=key), self.assertRaises(ValueError):
                d.validate_disk_image_manifest(changed, app)

    def test_notarization_is_never_run_without_explicit_enabled_hosted_gate(self):
        with patch.dict(os.environ, {"RELEASE_AUTOMATION_ENABLED": "false"}, clear=True), patch.object(d, "command") as run:
            with self.assertRaises(d.DistributionError):
                d.sign_and_package("0.2.0", "a"*40, Path("/tmp/unused"))
            run.assert_not_called()

    def test_signing_failure_restores_and_removes_ephemeral_keychain(self):
        with tempfile.TemporaryDirectory() as tmp:
            env = dict(RUNNER_ENVIRONMENT="github-hosted", RELEASE_AUTOMATION_ENABLED="true", RUNNER_TEMP=tmp,
                       APPLE_TEAM_ID="A"*10, DEVELOPER_ID_IDENTITY="Developer ID Application: Example",
                       NOTARY_KEY_ID="B"*10, NOTARY_ISSUER_ID="12345678-1234-1234-1234-123456789012",
                       SPARKLE_PUBLIC_KEY=base64.b64encode(b"x"*32).decode(),
                       DEVELOPER_ID_P12_BASE64=base64.b64encode(b"fixture").decode(),
                       DEVELOPER_ID_P12_PASSWORD="fixture-password",
                       NOTARY_KEY_P8_BASE64=base64.b64encode(b"fixture").decode(), SPARKLE_PRIVATE_KEY="fixture-key")
            calls = []
            def command(*args, **kwargs):
                calls.append(tuple(map(str, args)))
                if args[:3] == ("security", "list-keychains", "-d") and "-s" not in args:
                    return '"login.keychain-db"'
                if args[:2] == ("security", "find-identity"):
                    return env["DEVELOPER_ID_IDENTITY"]
                if args[0] == "xcodebuild":
                    raise d.DistributionError("Synthetic build failure")
                return ""
            info = dict(homepage="https://annotator.example", track="stable", build=200,
                        repository="example/annotator-releases", tag="v0.2.0")
            with patch.dict(os.environ, env, clear=True), patch.object(d, "preflight", return_value=info), patch.object(d, "command", side_effect=command):
                with self.assertRaisesRegex(d.DistributionError, "Synthetic"):
                    d.sign_and_package("0.2.0", "a"*40, Path(tmp)/"output")
            self.assertTrue(any(call[:2] == ("security", "delete-keychain") for call in calls))
            self.assertFalse(any("notarytool" in call for call in calls))
            self.assertEqual(list(Path(tmp).iterdir()), [])

    def test_publication_disabled_or_checksum_mismatch_never_uploads(self):
        with patch.dict(os.environ, {"PUBLICATION_ENABLED": "false"}, clear=True), patch.object(d, "command") as run:
            with self.assertRaises(d.DistributionError):
                d.publish(Path("/tmp/unused"))
            run.assert_not_called()
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            data = manifest()
            (root/"annotator-manifest.json").write_text(json.dumps(data))
            (root/data["archive"]).write_bytes(b"changed")
            env = dict(PUBLICATION_ENABLED="true", CANDIDATE_SHA="a"*40,
                       DISTRIBUTION_REPOSITORY=data["repository"], WEBSITE_URL=data["homepage"])
            with patch.dict(os.environ, env), patch.object(d, "command") as run:
                with self.assertRaisesRegex(d.DistributionError, "checksum"):
                    d.publish(root)
                run.assert_not_called()

    def test_rejected_notarization_never_staples_or_packages(self):
        with tempfile.TemporaryDirectory() as tmp:
            env = dict(RUNNER_ENVIRONMENT="github-hosted", RELEASE_AUTOMATION_ENABLED="true", RUNNER_TEMP=tmp,
                       APPLE_TEAM_ID="A"*10, DEVELOPER_ID_IDENTITY="Developer ID Application: Example",
                       NOTARY_KEY_ID="B"*10, NOTARY_ISSUER_ID="12345678-1234-1234-1234-123456789012",
                       SPARKLE_PUBLIC_KEY=base64.b64encode(b"x"*32).decode(),
                       DEVELOPER_ID_P12_BASE64=base64.b64encode(b"fixture").decode(),
                       DEVELOPER_ID_P12_PASSWORD="fixture-password",
                       NOTARY_KEY_P8_BASE64=base64.b64encode(b"fixture").decode(), SPARKLE_PRIVATE_KEY="fixture-key")
            calls = []
            def command(*args, **kwargs):
                calls.append(tuple(map(str, args)))
                if args[:2] == ("security", "find-identity"): return env["DEVELOPER_ID_IDENTITY"]
                if args[:3] == ("xcrun", "notarytool", "submit"): return f'{{"id":"{SUBMISSION}","message":"Successfully uploaded file"}}'
                if args[:3] == ("xcrun", "notarytool", "info"): return f'{{"id":"{SUBMISSION}","status":"Invalid","name":"notarization.zip"}}'
                return ""
            info = dict(homepage="https://annotator.example", track="stable", build=200,
                        repository="example/annotator-releases", tag="v0.2.0")
            with patch.dict(os.environ, env, clear=True), patch.object(d, "preflight", return_value=info), \
                 patch.object(d, "command", side_effect=command), \
                 patch.object(d, "sign_distribution_bundle") as sign_bundle, \
                 patch.object(d.packager, "inspect_app", return_value={"releaseVersion": "0.2.0", "build": "200"}), \
                 patch.object(d.packager, "package") as package:
                with self.assertRaisesRegex(d.DistributionError, "not accepted"):
                    d.sign_and_package("0.2.0", "a"*40, Path(tmp)/"output")
                package.assert_not_called()
                sign_bundle.assert_called_once()
            self.assertFalse(any("stapler" in call for call in calls))
            self.assertTrue(any(call[:2] == ("security", "delete-keychain") for call in calls))
            self.assertEqual(list(Path(tmp).iterdir()), [])
            # The decision comes from Apple's submission record, after a bounded wait.
            self.assertEqual([call[2] for call in calls if call[:2] == ("xcrun", "notarytool")], ["submit", "wait", "info"])
            self.assertIn("30m", next(call for call in calls if call[2:3] == ("wait",)))

    def signing_environment(self, tmp):
        return dict(RUNNER_ENVIRONMENT="github-hosted", RELEASE_AUTOMATION_ENABLED="true", RUNNER_TEMP=tmp,
                    APPLE_TEAM_ID="A"*10, DEVELOPER_ID_IDENTITY="Developer ID Application: Example",
                    NOTARY_KEY_ID="B"*10, NOTARY_ISSUER_ID="12345678-1234-1234-1234-123456789012",
                    SPARKLE_PUBLIC_KEY=base64.b64encode(b"x"*32).decode(),
                    DEVELOPER_ID_P12_BASE64=base64.b64encode(b"fixture").decode(),
                    DEVELOPER_ID_P12_PASSWORD="fixture-password",
                    NOTARY_KEY_P8_BASE64=base64.b64encode(b"fixture").decode(), SPARKLE_PRIVATE_KEY="fixture-key")

    def sign_until_apple_answers(self, tmp, submit_response, recovery):
        env = self.signing_environment(tmp)
        calls = []
        def command(*args, **kwargs):
            calls.append(tuple(map(str, args)))
            if args[:2] == ("security", "find-identity"): return env["DEVELOPER_ID_IDENTITY"]
            if args[0] == "ditto": Path(args[-1]).write_bytes(b"submitted archive")
            if args[:3] == ("xcrun", "notarytool", "submit"): return submit_response
            if args[:3] == ("xcrun", "notarytool", "wait"):
                raise d.DistributionError("notarytool failed with exit code 1. No later distribution step was run.")
            return ""
        info = dict(homepage="https://annotator.example", track="stable", build=200,
                    repository="example/annotator-releases", tag="v0.2.0")
        with patch.dict(os.environ, env, clear=True), patch.object(d, "preflight", return_value=info), \
             patch.object(d, "command", side_effect=command), patch.object(d, "sign_distribution_bundle"), \
             patch.object(d.packager, "inspect_app", return_value={"releaseVersion": "0.2.0", "build": "200"}), \
             patch.object(d.packager, "package") as package:
            with self.assertRaises(d.DistributionError) as error:
                d.sign_and_package("0.2.0", "a"*40, Path(tmp)/"output", recovery)
            package.assert_not_called()
        self.assertTrue(any(call[:2] == ("security", "delete-keychain") for call in calls))
        return calls, str(error.exception)

    def test_a_notarization_timeout_keeps_the_submitted_archive_and_its_id(self):
        with tempfile.TemporaryDirectory() as tmp:
            recovery = Path(tmp)/"recovery"
            calls, message = self.sign_until_apple_answers(tmp, f'{{"id":"{SUBMISSION}"}}', recovery)
            self.assertIn("notarytool failed", message)
            self.assertEqual(sorted(path.name for path in recovery.iterdir()), ["notarization.zip", "recovery.json"])
            self.assertEqual((recovery/"notarization.zip").read_bytes(), b"submitted archive")
            record = json.loads((recovery/"recovery.json").read_text())
            self.assertEqual(record, {"schema": 1, "releaseVersion": "0.2.0", "build": 200, "source": "a"*40,
                                      "archive": "notarization.zip", "size": len(b"submitted archive"),
                                      "archiveSha256": hashlib.sha256(b"submitted archive").hexdigest(),
                                      "submissionId": SUBMISSION})
            # Only the recovery folder outlives the temporary signing folder; no key material is kept.
            self.assertEqual(sorted(path.name for path in Path(tmp).iterdir()), ["recovery"])
            self.assertFalse(any("stapler" in call for call in calls))

    def test_a_missing_submission_id_never_waits_and_keeps_the_archive(self):
        with tempfile.TemporaryDirectory() as tmp:
            recovery = Path(tmp)/"recovery"
            calls, message = self.sign_until_apple_answers(tmp, '{"message":"unexpected"}', recovery)
            self.assertIn("no submission ID", message)
            self.assertEqual([call[2] for call in calls if call[:2] == ("xcrun", "notarytool")], ["submit"])
            self.assertIsNone(json.loads((recovery/"recovery.json").read_text())["submissionId"])

    def sign_to_the_end(self, tmp, disk_status="Accepted"):
        """Runs hosted signing with every tool replaced, through the disk image, and returns the calls in order."""
        env = self.signing_environment(tmp)
        disk_submission = "66666666-7777-8888-9999-000000000000"
        signature = base64.b64encode(b"x"*64).decode()
        output = Path(tmp)/"output"
        calls = []
        def command(*args, **kwargs):
            calls.append(tuple(map(str, args)))
            if args[:2] == ("security", "find-identity"): return env["DEVELOPER_ID_IDENTITY"]
            if args[:3] == ("xcrun", "notarytool", "submit"):
                ident = disk_submission if str(args[3]).endswith(".dmg") else SUBMISSION
                return json.dumps({"id": ident})
            if args[:3] == ("xcrun", "notarytool", "info"):
                status = disk_status if args[3] == disk_submission else "Accepted"
                return json.dumps({"id": args[3], "status": status})
            if args[:3] == ("xcrun", "stapler", "staple") and str(args[3]).endswith(".dmg"):
                with open(args[3], "ab") as stream: stream.write(b" stapled")
            if str(args[0]).endswith("sign_update") and "-p" in args: return signature
            return ""
        def package(app, folder, url, homepage):
            data = manifest()
            folder.mkdir(parents=True)
            (folder/data["archive"]).write_bytes(b"test archive")
            return {key: data[key] for key in ("releaseVersion", "version", "build", "archive",
                                               "downloadURL", "channel", "identifier", "architecture",
                                               "notarizationVerified", "homepage", "sha256")}
        def build(app, target, identity, name):
            calls.append(("disk_image.build", str(app), identity, name))
            target.mkdir(parents=True)
            (target/name).write_bytes(b"test dmg")
            return {"archive": name, "sha256": hashlib.sha256(b"test dmg").hexdigest(), "size": 8, "volumeName": "Annotator"}
        info = dict(homepage="https://annotator.example", track="stable", build=200,
                    repository="example/annotator-releases", tag="v0.2.0")
        with patch.dict(os.environ, env, clear=True), patch.object(d, "preflight", return_value=info), \
             patch.object(d, "command", side_effect=command), patch.object(d, "sign_distribution_bundle"), \
             patch.object(d.packager, "inspect_app", return_value={"releaseVersion": "0.2.0", "build": "200"}), \
             patch.object(d.packager, "architectures", return_value="arm64"), \
             patch.object(d.packager, "package", side_effect=package), \
             patch.object(d.disk_image, "build", side_effect=build):
            d.sign_and_package("0.2.0", "a"*40, output)
        return calls, output

    def test_hosted_signing_notarizes_and_staples_a_disk_image_of_the_final_zip(self):
        with tempfile.TemporaryDirectory() as tmp:
            calls, output = self.sign_to_the_end(tmp)
            self.assertEqual([call[2] for call in calls if call[:2] == ("xcrun", "notarytool")],
                             ["submit", "wait", "info", "submit", "wait", "info"])
            app_staple = next(i for i, call in enumerate(calls) if call[:3] == ("xcrun", "stapler", "staple"))
            unzip = next(i for i, call in enumerate(calls) if call[:3] == ("ditto", "-x", "-k"))
            build = next(i for i, call in enumerate(calls) if call[0] == "disk_image.build")
            # The image holds the app from the shipped ZIP, which already carries its own ticket.
            self.assertLess(app_staple, unzip)
            self.assertLess(unzip, build)
            self.assertTrue(calls[unzip][3].endswith("annotator-0.2.0-arm64.zip"))
            self.assertEqual(calls[build][2:], ("Developer ID Application: Example", "annotator-0.2.0-arm64.dmg"))
            dmg_calls = [call[:3] for call in calls[build:] if call[-1].endswith(".dmg") or "-v" in call]
            self.assertIn(("xcrun", "stapler", "staple"), dmg_calls)
            self.assertIn(("spctl", "--assess", "--type"), dmg_calls)
            app = json.loads((output/"annotator-manifest.json").read_text())
            disk = json.loads((output/"disk-image/annotator-disk-image.json").read_text())
            d.validate_disk_image_manifest(disk, app)
            image = output/"disk-image"/disk["archive"]
            # The checksum is of the stapled image that will be uploaded.
            self.assertEqual(image.read_bytes(), b"test dmg stapled")
            self.assertEqual((disk["sha256"], disk["size"]), (hashlib.sha256(b"test dmg stapled").hexdigest(), 16))
            self.assertEqual(disk["notarizationID"], "66666666-7777-8888-9999-000000000000")
            self.assertTrue(any(call[:2] == ("security", "delete-keychain") for call in calls))
            self.assertEqual(sorted(path.name for path in Path(tmp).iterdir()), ["output"])

    def test_a_rejected_disk_image_is_never_stapled_or_handed_to_publication(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaisesRegex(d.DistributionError, "Disk image notarization was not accepted"):
                self.sign_to_the_end(tmp, disk_status="Invalid")
            output = Path(tmp)/"output"
            self.assertFalse((output/"disk-image").exists())
            self.assertEqual(sorted(path.name for path in Path(tmp).iterdir()), ["output"])

    def test_an_existing_recovery_folder_is_never_reused(self):
        with tempfile.TemporaryDirectory() as tmp:
            (Path(tmp)/"recovery").mkdir()
            with patch.dict(os.environ, self.signing_environment(tmp), clear=True), \
                 patch.object(d, "preflight", return_value={}), patch.object(d, "command") as run:
                with self.assertRaisesRegex(d.DistributionError, "fresh output"):
                    d.sign_and_package("0.2.0", "a"*40, Path(tmp)/"output", Path(tmp)/"recovery")
                run.assert_not_called()

    def test_publication_uses_draft_then_verification_and_no_source_upload(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            data = manifest()
            (root/data["archive"]).write_bytes(b"test archive")
            for name, text in [("annotator-manifest.json", json.dumps(data)), ("release-notes.md", "Notes\n"),
                               ("SHA256SUMS", "checksum\n"), ("appcast.xml", d.appcast(data))]:
                (root/name).write_text(text)
            calls = []
            bodies = []
            def run(*args, **kwargs):
                calls.append(tuple(map(str, args)))
                if kwargs.get("input"):
                    bodies.append(kwargs["input"])
                if args[:3] == ("gh", "release", "download"):
                    destination = Path(args[args.index("--dir") + 1])
                    (destination/data["archive"]).write_bytes(b"test archive")
                return ""
            env = dict(PUBLICATION_ENABLED="true", CANDIDATE_SHA="a"*40,
                       DISTRIBUTION_REPOSITORY=data["repository"], WEBSITE_URL=data["homepage"])
            with patch.dict(os.environ, env), patch.object(d, "gh_json", side_effect=[{"private": False}, [[]]]), \
                 patch.object(d, "published_manifests", return_value=[]), patch.object(d, "command", side_effect=run):
                d.publish(root)
            actions = [call[2] for call in calls if call[:2] == ("gh", "release")]
            self.assertEqual(actions, ["create", "upload", "download", "edit"])
            # The tag is created at the built candidate, not at the tip of the default branch.
            create = next(call for call in calls if call[:3] == ("gh", "release", "create"))
            self.assertEqual(create[create.index("--target") + 1], data["source"])
            upload = next(call for call in calls if call[:3] == ("gh", "release", "upload"))
            self.assertEqual({Path(path).name for path in upload[6:]},
                             {data["archive"], "annotator-manifest.json", "release-notes.md", "SHA256SUMS", "appcast.xml"})
            self.assertTrue(all("--clobber" not in call for call in calls))
            # Every release is a normal release, never a pre-release, and becomes the latest once verified.
            self.assertNotIn("--prerelease", create)
            edit = next(call for call in calls if call[:3] == ("gh", "release", "edit"))
            self.assertIn("--latest", edit)
            self.assertNotIn("--latest=false", edit)

    def test_a_legacy_pre_release_is_never_published(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            data = legacy_manifest()
            (root/"annotator-manifest.json").write_text(json.dumps(data))
            (root/data["archive"]).write_bytes(b"test archive")
            env = dict(PUBLICATION_ENABLED="true", CANDIDATE_SHA=data["source"],
                       DISTRIBUTION_REPOSITORY=data["repository"], WEBSITE_URL=data["homepage"])
            with patch.dict(os.environ, env), patch.object(d, "gh_json") as api, patch.object(d, "command") as run:
                with self.assertRaisesRegex(d.DistributionError, "plain"):
                    d.publish(root)
            api.assert_not_called()
            run.assert_not_called()

    def test_preflight_refuses_a_labelled_version(self):
        sha = "a" * 40
        env = dict(GITHUB_REF="refs/heads/main", GITHUB_SHA=sha, GITHUB_EVENT_NAME="workflow_dispatch",
                   DISTRIBUTION_REPOSITORY="owner/annotator", WEBSITE_URL="https://owner.github.io/annotator")
        with patch.dict(os.environ, env, clear=True), patch.object(d, "command", return_value=sha), \
             patch.object(d, "metadata", return_value=("0.2.0", 200)), patch.object(d.release, "verify_candidate"):
            for version in ("0.2.0-beta.1", "0.2", "v0.2.0"):
                with self.subTest(version=version), self.assertRaisesRegex(d.DistributionError, "plain"):
                    d.preflight(version, sha)
            self.assertEqual(d.preflight("0.2.0", sha)["track"], "stable")

    def test_publication_uploads_disk_image_when_supplied(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            dmg_root = root / "dmg"
            dmg_root.mkdir()
            data = manifest()
            disk = disk_manifest(data)
            (root/data["archive"]).write_bytes(b"test archive")
            (dmg_root/disk["archive"]).write_bytes(b"test dmg")
            for name, text in [("annotator-manifest.json", json.dumps(data)), ("release-notes.md", "Notes\n"),
                               ("SHA256SUMS", "checksum\n"), ("appcast.xml", d.appcast(data))]:
                (root/name).write_text(text)
            (dmg_root / "annotator-disk-image.json").write_text(json.dumps(disk))
            calls = []
            bodies = []
            def run(*args, **kwargs):
                calls.append(tuple(map(str, args)))
                if kwargs.get("input"):
                    bodies.append(kwargs["input"])
                if args[:3] == ("gh", "release", "download"):
                    destination = Path(args[args.index("--dir") + 1])
                    pattern = args[args.index("--pattern") + 1]
                    (destination/pattern).write_bytes(b"test dmg" if pattern.endswith(".dmg") else b"test archive")
                return ""
            env = dict(PUBLICATION_ENABLED="true", CANDIDATE_SHA=data["source"],
                       DISTRIBUTION_REPOSITORY=data["repository"], WEBSITE_URL=data["homepage"])
            with patch.dict(os.environ, env), patch.object(d, "gh_json", side_effect=[{"private": False}, [[]]]), \
                 patch.object(d, "published_manifests", return_value=[]), patch.object(d, "command", side_effect=run):
                d.publish(root, dmg_root)
            upload = next(call for call in calls if call[:3] == ("gh", "release", "upload"))
            self.assertEqual({Path(path).name for path in upload[6:]},
                             {data["archive"], "annotator-manifest.json", "release-notes.md", "SHA256SUMS",
                              "appcast.xml", disk["archive"], "annotator-disk-image.json"})
            self.assertTrue(any("Disk image SHA-256" in body for body in bodies))

    def test_published_release_selection_excludes_drafts(self):
        with patch.object(d, "gh_json", return_value=[[{"draft": True, "tag_name": "v9.0.0", "assets": []}]]):
            self.assertEqual(d.published_manifests("example/releases"), [])

    def test_published_manifests_accepts_optional_disk_image(self):
        data = manifest()
        disk = disk_manifest(data)
        row = {"draft": False, "tag_name": data["tag"], "prerelease": False,
               "assets": [
                   {"name": "annotator-manifest.json", "id": 1},
                   {"name": data["archive"], "size": data["size"], "browser_download_url": data["downloadURL"]},
                   {"name": "annotator-disk-image.json", "id": 2},
                   {"name": disk["archive"], "size": disk["size"], "browser_download_url": disk["downloadURL"]},
               ]}
        with patch.object(d, "gh_json", side_effect=[[[row]], data, disk]):
            rows = d.published_manifests(data["repository"])
        self.assertEqual(rows[0]["diskImage"], disk)
        row_no_dmg = {**row, "assets": row["assets"][:2]}
        with patch.object(d, "gh_json", side_effect=[[[row_no_dmg]], data]):
            self.assertNotIn("diskImage", d.published_manifests(data["repository"])[0])
        # A plain release marked as a pre-release disagrees with its manifest; legacy releases were pre-releases.
        with patch.object(d, "gh_json", side_effect=[[[{**row_no_dmg, "prerelease": True}]], data]), \
             self.assertRaises(d.DistributionError):
            d.published_manifests(data["repository"])
        legacy = legacy_manifest()
        legacy_row = {"draft": False, "tag_name": legacy["tag"], "prerelease": True, "assets": [
            {"name": "annotator-manifest.json", "id": 1},
            {"name": legacy["archive"], "size": legacy["size"], "browser_download_url": legacy["downloadURL"]}]}
        with patch.object(d, "gh_json", side_effect=[[[legacy_row]], legacy]):
            self.assertEqual(d.published_manifests(legacy["repository"])[0]["releaseVersion"], "0.1.0-alpha.1")

    def test_existing_draft_or_release_cannot_be_replaced_with_different_candidate(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            data = manifest()
            (root/data["archive"]).write_bytes(b"test archive")
            for name, text in [("annotator-manifest.json", json.dumps(data)), ("release-notes.md", "Notes\n"),
                               ("SHA256SUMS", "sum\n"), ("appcast.xml", d.appcast(data))]:
                (root/name).write_text(text)
            env = dict(PUBLICATION_ENABLED="true", CANDIDATE_SHA=data["source"],
                       DISTRIBUTION_REPOSITORY=data["repository"], WEBSITE_URL=data["homepage"])
            row = {"tag_name": data["tag"], "draft": True, "prerelease": False, "body": "Different source", "assets": []}
            with patch.dict(os.environ, env), patch.object(d, "gh_json", side_effect=[{"private": False}, [[row]]]), \
                 patch.object(d, "command") as run:
                with self.assertRaisesRegex(d.DistributionError, "different immutable"):
                    d.publish(root)
                run.assert_not_called()

    def test_canonical_tap_uses_the_newest_release(self):
        legacy, plain = legacy_manifest(), manifest()
        env = dict(PUBLICATION_ENABLED="true", DISTRIBUTION_REPOSITORY=plain["repository"],
                   HOMEBREW_TAP_REPOSITORY=plain["repository"],
                   GITHUB_REPOSITORY="example/private-code", WEBSITE_URL=plain["homepage"])
        for entries, expected, version in [([legacy], legacy, "0.1.0-alpha.1,9"), ([legacy, plain], plain, "0.2.0"),
                                           ([plain, legacy], plain, "0.2.0")]:
            with self.subTest(entries=[row["releaseVersion"] for row in entries]), patch.dict(os.environ, env, clear=True), \
                 patch.object(d, "published_manifests", return_value=entries), \
                 patch.object(d, "gh_json", side_effect=[{"private": False}, {"tree": []}]), \
                 patch.object(d, "command") as run:
                d.update_tap()
                request = json.loads(run.call_args.kwargs["input"])
                content = base64.b64decode(request["content"]).decode()
                self.assertIn('cask "annotator"', content)
                self.assertIn(f'version "{version}"\n', content)
                self.assertIn(expected["downloadURL"], content)
                self.assertNotIn("conflicts_with", content)
                self.assertEqual(request["message"], f"Update annotator to {expected['releaseVersion']}")
                self.assertIn("Casks/annotator.rb", run.call_args.args[4])
                run.assert_called_once()

    def test_canonical_tap_requires_release_and_preserves_identical_cask(self):
        data = manifest()
        env = dict(PUBLICATION_ENABLED="true", DISTRIBUTION_REPOSITORY=data["repository"],
                   HOMEBREW_TAP_REPOSITORY=data["repository"],
                   GITHUB_REPOSITORY="example/private-code", WEBSITE_URL=data["homepage"])
        with patch.dict(os.environ, env, clear=True), \
             patch.object(d, "published_manifests", return_value=[]), \
             patch.object(d, "gh_json", return_value={"private": False}), patch.object(d, "command") as run:
            with self.assertRaisesRegex(d.DistributionError, "No verified published release"):
                d.update_tap()
            run.assert_not_called()
        content = d.packager.cask({**data, "token": "annotator", "name": "Annotator.app"},
                                 data["downloadURL"], data["sha256"], data["homepage"])
        with patch.dict(os.environ, env, clear=True), \
             patch.object(d, "published_manifests", return_value=[data]), \
             patch.object(d, "gh_json", side_effect=[{"private": False},
                 {"tree": [{"path": "Casks/annotator.rb"}]},
                 {"content": base64.b64encode(content.encode()).decode(), "sha": "old"}]), \
             patch.object(d, "command") as run:
            d.update_tap()
            run.assert_not_called()

    def test_tap_waits_until_the_run_s_release_is_listed(self):
        old, new = {"releaseVersion": "0.1.0-beta.12"}, {"releaseVersion": "0.1.0-beta.13"}
        with patch.object(d, "published_manifests", side_effect=[[old], [old], [old, new]]) as listed, \
             patch.object(d.time, "sleep") as sleep:
            self.assertEqual(d.published_release("example/releases", "0.1.0-beta.13"), [old, new])
            self.assertEqual((listed.call_count, sleep.call_count), (3, 2))
        with patch.object(d, "published_manifests", return_value=[old]), patch.object(d.time, "sleep"):
            with self.assertRaisesRegex(d.DistributionError, "not among the published releases"):
                d.published_release("example/releases", "0.1.0-beta.13", attempts=3)
        # Without an expected version (a maintainer's own run), it reads the list once.
        with patch.object(d, "published_manifests", return_value=[old]) as listed, patch.object(d.time, "sleep") as sleep:
            self.assertEqual(d.published_release("example/releases"), [old])
            self.assertEqual((listed.call_count, sleep.call_count), (1, 0))

    def test_dedicated_tap_gets_the_cask_and_the_old_copy_stays_current(self):
        data = manifest()
        env = dict(PUBLICATION_ENABLED="true", DISTRIBUTION_REPOSITORY=data["repository"],
                   HOMEBREW_TAP_REPOSITORY="example/homebrew-annotator",
                   GITHUB_REPOSITORY="example/private-code", WEBSITE_URL=data["homepage"])
        older = base64.b64encode(b'cask "annotator" do\n  version "0.0.1,1"\nend\n').decode()
        for old_copy in (True, False):
            legacy = [{"tree": [{"path": "Casks/annotator.rb"}]}, {"content": older, "sha": "legacy-sha"}] if old_copy \
                else [{"tree": [{"path": "README.md"}]}]
            with self.subTest(old_copy=old_copy), patch.dict(os.environ, env, clear=True), \
                 patch.object(d, "published_manifests", return_value=[data]), \
                 patch.object(d, "gh_json", side_effect=[{"private": False}, {"tree": []}, *legacy]), \
                 patch.object(d, "command") as run:
                content = d.update_tap()
                paths = [call.args[4] for call in run.call_args_list]
                expected = ["repos/example/homebrew-annotator/contents/Casks/annotator.rb"]
                if old_copy:
                    expected.append(f"repos/{data['repository']}/contents/Casks/annotator.rb")
                # The new tap gets a new file; an old copy is replaced in place and never created.
                self.assertEqual(paths, expected)
                self.assertNotIn("sha", json.loads(run.call_args_list[0].kwargs["input"]))
                if old_copy:
                    request = json.loads(run.call_args_list[1].kwargs["input"])
                    self.assertEqual(request["sha"], "legacy-sha")
                    self.assertEqual(base64.b64decode(request["content"]).decode(), content)
        # Inside the release repository's own workflow, the legacy copy there is left alone, and the
        # tap can never be the running repository.
        same = {**env, "GITHUB_REPOSITORY": data["repository"]}
        with patch.dict(os.environ, same, clear=True), patch.object(d, "published_manifests", return_value=[data]), \
             patch.object(d, "gh_json", side_effect=[{"private": False}, {"tree": []}]), patch.object(d, "command") as run:
            d.update_tap()
            self.assertEqual([call.args[4] for call in run.call_args_list],
                             ["repos/example/homebrew-annotator/contents/Casks/annotator.rb"])
        with patch.dict(os.environ, {**same, "HOMEBREW_TAP_REPOSITORY": data["repository"]}, clear=True), \
             patch.object(d, "command") as run:
            with self.assertRaises(d.DistributionError):
                d.update_tap()
            run.assert_not_called()
        self.assertEqual(d.tap_name("example/homebrew-annotator"), "example/annotator")
        self.assertEqual(d.tap_name("example/annotator"), "example/annotator")

    def test_website_metadata_names_the_newest_release_and_mirrors_the_old_feeds(self):
        legacy, plain = legacy_manifest(), manifest()
        older = {**legacy, "build": "8"}
        site = ("example/releases", "https://example.com")
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root/"website/src/data").mkdir(parents=True)
            updates = root/"website/public/updates"
            for published, expected in (([older, plain, legacy], plain), ([older, legacy], legacy)):
                with self.subTest(newest=expected["releaseVersion"]), patch.object(d, "ROOT", root), \
                     patch.object(d, "configuration", return_value=site), \
                     patch.object(d, "published_manifests", return_value=published), \
                     patch.object(d, "appcast", side_effect=lambda entry: f"build:{entry['build']}"):
                    d.website_metadata()
                    payload = json.loads((root/"website/src/data/distribution.json").read_text())
                    # The website states which Macs a release runs on from its verified architectures; it shows no build.
                    self.assertEqual(payload, {"release": {key: expected[key] for key in ("releaseVersion", "downloadURL", "sha256", "architecture")}})
                    # Copies from 0.1.0-beta.17 and earlier read the preview feed, so it carries the same newest release.
                    for name in ("stable.xml", "preview.xml"):
                        self.assertEqual((updates/name).read_text(), f"build:{expected['build']}")
            with patch.object(d, "ROOT", root), patch.object(d, "configuration", return_value=site), \
                 patch.object(d, "published_manifests", return_value=[]):
                d.website_metadata()
            self.assertFalse((updates/"stable.xml").exists())
            self.assertFalse((updates/"preview.xml").exists())
            self.assertEqual(json.loads((root/"website/src/data/distribution.json").read_text()), {"release": None})

    def test_website_metadata_emits_disk_image_keys_only_when_present(self):
        with_dmg = {**manifest(), "diskImage": disk_manifest()}
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root/"website/src/data").mkdir(parents=True)
            for published in ([with_dmg], [manifest()]):
                with patch.object(d, "ROOT", root), patch.object(d, "configuration", return_value=("example/releases", "https://example.com")), \
                     patch.object(d, "published_manifests", return_value=published), \
                     patch.object(d, "appcast", return_value="xml"):
                    d.website_metadata()
                payload = json.loads((root/"website/src/data/distribution.json").read_text())["release"]
                if "diskImage" in published[0]:
                    self.assertEqual(payload["diskImageURL"], with_dmg["diskImage"]["downloadURL"])
                    self.assertEqual(payload["diskImageSHA256"], with_dmg["diskImage"]["sha256"])
                    self.assertEqual(payload["diskImageSize"], with_dmg["diskImage"]["size"])
                else:
                    self.assertNotIn("diskImageURL", payload)

    def test_appcast_shows_the_releases_mac_notes_and_fails_without_them(self):
        data = manifest()
        notes = {"releases": [{"version": "0.2.0", "build": 200, "changes": {"Fixed": ["Pen <strokes> & arrows"], "Added": ["Zoom"]},
                               "knownIssues": ["Effects are not secure redaction."],
                               "platforms": {"windows": {"changes": {"Added": ["Windows only"]}, "knownIssues": []}}}]}
        root = ET.fromstring(d.appcast(data, notes))
        description = root.find("channel/item/description").text
        # Categories keep the notes' order, the text is escaped, and only the Mac notes go to the Mac feed.
        self.assertEqual(description, "<h3>Added</h3><ul><li>Zoom</li></ul><h3>Fixed</h3><ul><li>Pen &lt;strokes&gt; &amp; arrows</li></ul>"
                                      "<h3>Known limits</h3><ul><li>Effects are not secure redaction.</li></ul>")
        self.assertNotIn("Windows only", description)
        without_limits = {"releases": [{**notes["releases"][0], "knownIssues": []}]}
        self.assertNotIn("Known limits", d.update_notes(data, without_limits))
        for missing in ({"releases": []}, {"releases": [{**notes["releases"][0], "build": 199}]},
                        {"releases": [{**notes["releases"][0], "changes": {"Other": ["x"]}}]}):
            with self.subTest(missing=missing), self.assertRaises(d.DistributionError):
                d.appcast(data, missing)
        # Without explicit notes, the feed reads releases/notes.json.
        self.assertIn("<h3>", ET.fromstring(d.appcast(data)).find("channel/item/description").text)

    def windows_description(self, data):
        files = [{"architecture": arch, "file": f"Annotator-{data['releaseVersion']}-windows-{arch}.zip", "size": 10, "sha256": "a"*64}
                 for arch in ("x64", "arm64")]
        description = {"schemaVersion": 1, "releaseVersion": data["releaseVersion"], "build": int(data["build"]), "source": data["source"], "files": files}
        assets = [{"name": row["file"], "size": row["size"]} for row in files]
        return description, assets

    def test_published_windows_files_must_match_the_release(self):
        data = manifest()
        description, assets = self.windows_description(data)
        self.assertEqual(d.validate_windows_description(description, data, assets), description)
        for changed, published in (({**description, "build": 10}, assets), ({**description, "releaseVersion": "0.1.0"}, assets),
                                   ({**description, "schemaVersion": 2}, assets), (description, assets[1:]),
                                   (description, [{**assets[0], "size": 11}, assets[1]]),
                                   ({**description, "files": description["files"][1:]}, assets),
                                   ({**description, "files": [{**description["files"][0], "file": "other.zip"}]}, assets)):
            with self.subTest(changed=changed, published=published), self.assertRaises(d.DistributionError):
                d.validate_windows_description(changed, data, published)

    def test_website_metadata_writes_windows_feeds_only_for_releases_with_windows_files(self):
        plain = manifest()
        newer_mac_only = {**plain, "build": "201", "releaseVersion": "0.2.1", "version": "0.2.1", "tag": "v0.2.1"}
        with_windows = {**plain, "windows": self.windows_description(plain)[0]}
        legacy = {**legacy_manifest(), "windows": self.windows_description(legacy_manifest())[0]}
        site = ("example/releases", "https://example.com")
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root/"website/src/data").mkdir(parents=True)
            updates = root/"website/public/updates"
            with patch.object(d, "ROOT", root), patch.object(d, "configuration", return_value=site), \
                 patch.object(d, "published_manifests", return_value=[newer_mac_only, with_windows, legacy]), \
                 patch.object(d, "appcast", return_value="xml"):
                d.website_metadata()
            # The Mac feed moves on to 0.2.1, but Windows copies only hear about a release that has Windows files. The
            # preview feed, read by copies from 0.1.0-beta.17 and earlier, says the same, and no feed names a track.
            expected = {"schemaVersion": 1, "releaseVersion": "0.2.0", "build": 200,
                        "downloadPage": "https://annotator.example/downloads/",
                        "releaseURL": "https://github.com/example/annotator-releases/releases/tag/v0.2.0"}
            for name in ("windows-stable.json", "windows-preview.json"):
                self.assertEqual(json.loads((updates/name).read_text()), expected)
            with patch.object(d, "ROOT", root), patch.object(d, "configuration", return_value=site), \
                 patch.object(d, "published_manifests", return_value=[newer_mac_only]), patch.object(d, "appcast", return_value="xml"):
                d.website_metadata()
            self.assertFalse((updates/"windows-stable.json").exists())
            self.assertFalse((updates/"windows-preview.json").exists())

    def test_update_verifier_accepts_public_rfc8032_vector_and_rejects_changed_data(self):
        public = bytes.fromhex("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a")
        signature = bytes.fromhex(
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555f"
            "b8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b")
        with tempfile.TemporaryDirectory() as tmp:
            archive = Path(tmp)/"vector"
            archive.write_bytes(b"")
            args = ["xcrun", "swift", str(d.ROOT/"tools/release/verify-update.swift"),
                    base64.b64encode(public).decode(), base64.b64encode(signature).decode(), str(archive)]
            good = subprocess.run(args, capture_output=True, text=True)
            self.assertEqual(good.returncode, 0, good.stderr)
            archive.write_bytes(b"changed")
            bad = subprocess.run(args, capture_output=True, text=True)
            self.assertNotEqual(bad.returncode, 0)
            self.assertIn("does not match", bad.stderr)


if __name__ == "__main__":
    unittest.main()
