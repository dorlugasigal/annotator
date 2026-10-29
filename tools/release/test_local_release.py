import argparse
import base64
import contextlib
import io
import json
from pathlib import Path
import plistlib
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import local_release as local
import distribution as d

SOURCE = "a" * 40
ID = "11111111-2222-3333-4444-555555555555"
PUBLIC = base64.b64encode(b"p" * 32).decode()
SIGNATURE = base64.b64encode(b"s" * 64).decode()


class LocalReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.enterContext(patch.object(local, "ROOT", self.root.parent / "source-repo"))
        # Fixture apps have no real executable; production reads their architectures with lipo.
        self.architecture = "arm64"
        self.enterContext(patch.object(d.packager, "architectures", side_effect=lambda *_: self.architecture))
        self.folder = self.root / "release"
        self.folder.mkdir()
        self.key = self.root / "private-key"
        self.key.write_text("SECRET-KEY-MATERIAL")
        self.notes = {"releases": [dict(version="0.2.0", build=200,
                                      date="2026-09-23", changes={"Fixed": ["A fix."]},
                                      knownIssues=[])]}
        self.app(self.folder / "Annotator.app")
        (self.folder / "notarization.zip").write_bytes(b"original submission bytes")
        (self.folder / "sign_update").write_bytes(b"fixture signer")
        (self.folder / "release-notes.md").write_text(local.release.changelog(self.notes, "0.2.0"))
        self.receipt = dict(schema=1, version="0.2.0", build=200, source=SOURCE,
                            repository="example/binaries", homepage="https://example.com",
                            track="stable", publicKey=PUBLIC,
                            identity="Developer ID Application: Example", team="AAAAAAAAAA",
                            metadata=d.packager.inspect_app(self.folder / "Annotator.app"),
                            appHash=local.tree_hash(self.folder / "Annotator.app"),
                            archiveHash=local.digest(self.folder / "notarization.zip"),
                            signerHash=local.digest(self.folder / "sign_update"),
                            notesHash=local.digest(self.folder / "release-notes.md"))
        self.save_receipt()
        # Every process call in this suite is mocked. No signing or network access.
        self.run = self.enterContext(patch.object(d, "command", side_effect=self.command))
        self.verify = self.enterContext(patch.object(d, "verify_distribution_bundle"))
        self.state = "Accepted"
        self.log_hash = self.receipt["archiveHash"]
        self.notary_name = "notarization.zip"

    def app(self, path):
        (path / "Contents/MacOS").mkdir(parents=True)
        (path / "Contents/MacOS/Annotator").write_bytes(b"fixture")
        (path / "Contents/Info.plist").write_bytes(plistlib.dumps({
            "CFBundleIdentifier": "com.dorlugasigal.annotator",
            "LSMinimumSystemVersion": "14.0", "CFBundleShortVersionString": "0.2.0",
            "CFBundleVersion": "200",
            "SUPublicEDKey": PUBLIC, "SUFeedURL": "https://example.com/updates/stable.xml",
            "AnnotatorBuildStamp": f"Release {SOURCE}",
            "AnnotatorWebsiteURL": "https://example.com", "AnnotatorMetricsURL": "",
        }))

    def save_receipt(self):
        (self.folder / "receipt.json").write_text(json.dumps(self.receipt))
        (self.folder / "receipt.sha256.json").write_text(json.dumps({
            "sha256": local.digest(self.folder / "receipt.json")}))

    def command(self, *args, **kwargs):
        if args[:2] == ("xcrun", "notarytool"):
            action = args[2]
            if action == "submit":
                self.assertNotIn("--wait", args)
                self.assertNotIn("--timeout", args)
                target = Path(args[3])
                marker = self.folder / ("disk-image/submit-attempt.json" if target.suffix == ".dmg" else "submit-attempt.json")
                self.assertTrue(marker.exists())
                return json.dumps({"id": ID})
            if action == "info":
                return json.dumps({"id": ID, "name": self.notary_name, "status": self.state})
            if action == "log":
                return json.dumps({"jobId": ID, "archiveFilename": self.notary_name,
                                   "sha256": self.log_hash, "status": self.state,
                                   "issues": ["SECRET-KEY-MATERIAL"]})
        if args[0] == "ditto":
            shutil.copytree(args[1], args[2], symlinks=True)
            return ""
        if Path(args[0]).name == "sign_update":
            return SIGNATURE if "-p" in args else ""
        if args[:2] == ("xcrun", "stapler") or args[0] == "spctl" or args[:2] == ("xcrun", "swift"):
            return ""
        raise AssertionError(f"Unexpected process in mock test: {args[0]}")

    def attach(self):
        local.submit(self.folder, self.receipt, ())

    def test_load_checks_all_saved_artifacts(self):
        self.assertEqual(local.load(self.folder), self.receipt)
        for name in ("notarization.zip", "sign_update", "release-notes.md", "receipt.json",
                     "receipt.sha256.json", "Annotator.app/Contents/MacOS/Annotator"):
            path = self.folder / name
            data = path.read_bytes()
            for missing in (True, False):
                with self.subTest(name=name, missing=missing):
                    if missing:
                        path.unlink()
                    else:
                        path.write_bytes(b"changed")
                    with self.assertRaises((ValueError, OSError)):
                        local.load(self.folder)
                    path.write_bytes(data)

    def test_symlinks_and_hardlinks_fail_closed(self):
        path = self.folder / "notarization.zip"
        data = path.read_bytes()
        path.unlink()
        path.symlink_to(self.key)
        with self.assertRaisesRegex(local.Error, "Symlink"):
            local.load(self.folder)
        path.unlink()
        path.hardlink_to(self.key)
        with self.assertRaises(local.Error):
            local.load(self.folder)
        path.unlink()
        path.write_bytes(data)
        (self.folder / "Annotator.app/escape").symlink_to(self.key)
        with self.assertRaisesRegex(local.Error, "unsafe symlink"):
            local.load(self.folder)

    def test_internal_framework_links_are_supported(self):
        app = self.folder / "Annotator.app"
        (app / "Versions").mkdir()
        (app / "Versions/B").mkdir()
        (app / "Versions/Current").symlink_to("B")
        self.assertEqual(len(local.tree_hash(app)), 64)

    def test_metadata_and_public_settings_checked_even_with_updated_hashes(self):
        self.receipt["version"] = "0.2.1"
        self.save_receipt()
        with self.assertRaisesRegex(local.Error, "metadata"):
            local.load(self.folder)
        self.receipt["version"] = "0.2.0"
        self.receipt["publicKey"] = base64.b64encode(b"z" * 32).decode()
        self.save_receipt()
        with self.assertRaisesRegex(local.Error, "update settings"):
            local.load(self.folder)

    def test_new_receipt_binds_website_and_metrics_destination(self):
        self.receipt.update(websiteURL="https://example.com", metricsURL="")
        self.save_receipt()
        self.assertEqual(local.load(self.folder)["metricsURL"], "")
        self.receipt["metricsURL"] = "https://different.example"
        self.save_receipt()
        with self.assertRaisesRegex(local.Error, "website or metrics"):
            local.load(self.folder)

    def test_submit_retains_archive_and_prevents_duplicates(self):
        self.attach()
        self.assertEqual(local.attached(self.folder, self.receipt), ID)
        with self.assertRaisesRegex(local.Error, "already attempted"):
            self.attach()
        self.assertEqual(local.digest(self.folder / "notarization.zip"), self.receipt["archiveHash"])

    def test_ambiguous_submit_cannot_retry_and_can_recover(self):
        with patch.object(local, "notary", side_effect=local.Error("Transport failed")):
            with self.assertRaises(local.Error):
                self.attach()
        self.assertTrue((self.folder / "submit-attempt.json").exists())
        self.assertFalse((self.folder / "submission.json").exists())
        with self.assertRaisesRegex(local.Error, "already attempted"):
            self.attach()
        local.recover(self.folder, self.receipt, (), ID)
        self.assertEqual(local.attached(self.folder, self.receipt), ID)

    def test_bad_submit_response_is_also_ambiguous(self):
        with patch.object(local, "notary", return_value={"id": "not-an-id"}):
            with self.assertRaises(local.Error):
                self.attach()
        with self.assertRaisesRegex(local.Error, "already attempted"):
            self.attach()

    def test_recovery_requires_attempt_and_matching_terminal_archive(self):
        with self.assertRaises(local.Error):
            local.recover(self.folder, self.receipt, (), ID)
        local.write_once(self.folder / "submit-attempt.json", {
            "receiptHash": local.digest(self.folder / "receipt.json"),
            "archiveHash": self.receipt["archiveHash"]})
        for state, checksum in (("In Progress", self.log_hash), ("Accepted", "b" * 64)):
            self.state, self.log_hash = state, checksum
            with self.subTest(state=state), self.assertRaises(local.Error):
                local.recover(self.folder, self.receipt, (), ID)
            self.assertFalse((self.folder / "submission.json").exists())
        self.state, self.log_hash = "Invalid", self.receipt["archiveHash"]
        local.recover(self.folder, self.receipt, (), ID)
        self.assertEqual(local.attached(self.folder, self.receipt), ID)

    def test_status_does_not_need_current_head_and_pending_is_success(self):
        self.attach()
        args = ["status", "--folder", str(self.folder), "--notary-key", str(self.key),
                "--key-id", "ABCDEFGHIJ", "--issuer", ID]
        with patch.object(local, "candidate", side_effect=AssertionError("must not inspect HEAD")):
            for state, expected in (("In Progress", 0), ("Invalid", 1), ("Accepted", 0)):
                self.state = state
                with self.subTest(state=state):
                    self.assertEqual(local.main(args), expected)
        self.assertEqual(local.digest(self.folder / "notarization.zip"), self.receipt["archiveHash"])

    def test_status_timeout_keeps_saved_files(self):
        self.attach()
        with patch.object(local, "notary", side_effect=TimeoutError):
            with self.assertRaises(TimeoutError):
                local.info(ID, self.folder, self.receipt, ())
        self.assertEqual(local.load(self.folder), self.receipt)

    def test_pending_invalid_and_wrong_hash_cannot_finalize(self):
        self.attach()
        with patch.object(local, "candidate", return_value=(200, self.notes)):
            for state, checksum in (("In Progress", self.log_hash), ("Invalid", self.log_hash),
                                    ("Accepted", "f" * 64)):
                self.state, self.log_hash = state, checksum
                with self.subTest(state=state), self.assertRaises(local.Error):
                    local.finalize(self.folder, self.receipt, (), self.key)
                self.assertFalse((self.folder / "final").exists())
        self.assertFalse(any("staple" in call.args for call in self.run.call_args_list))

    def package(self, app, output, url, homepage):
        output.mkdir()
        name = url.rsplit("/", 1)[-1]
        (output / name).write_bytes(b"stapled final archive")
        return dict(self.receipt["metadata"], channel="release", downloadURL=url, homepage=homepage,
                    archive=name, sha256=local.digest(output / name), architecture=self.architecture,
                    notarizationVerified=True)

    def test_universal_app_finalizes_under_universal_names(self):
        self.attach()
        self.architecture = "arm64 x86_64"
        with patch.object(local, "candidate", return_value=(200, self.notes)), \
             patch.object(d.packager, "package", side_effect=self.package):
            local.finalize(self.folder, self.receipt, (), self.key)
        report = d.validate_manifest(json.loads((self.folder / "final" / "annotator-manifest.json").read_text()))
        self.assertEqual(report["archive"], "annotator-0.2.0-universal.zip")
        self.assertTrue(report["downloadURL"].endswith("/v0.2.0/annotator-0.2.0-universal.zip"))
        self.assertEqual(report["architecture"], "arm64 x86_64")

    def test_accepted_finalizes_copy_and_preserves_original(self):
        self.attach()
        with patch.object(local, "candidate", return_value=(200, self.notes)), \
             patch.object(d.packager, "package", side_effect=self.package):
            local.finalize(self.folder, self.receipt, (), self.key)
            with self.assertRaisesRegex(local.Error, "already exists"):
                local.finalize(self.folder, self.receipt, (), self.key)
        self.assertEqual(local.load(self.folder), self.receipt)
        output = self.folder / "final"
        report = d.validate_manifest(json.loads((output / "annotator-manifest.json").read_text()))
        self.assertEqual(report["source"], SOURCE)
        self.assertEqual(report["notarizationID"], ID)
        self.assertEqual((output / "SHA256SUMS").read_text(), f"{report['sha256']}  {report['archive']}\n")
        self.assertTrue((output / "appcast.xml").is_file())
        self.assertTrue(any(call.args[:2] == ("xcrun", "swift") for call in self.run.call_args_list))
        self.assertFalse(any(call.args[0] == "xcodebuild" for call in self.run.call_args_list))

    def test_finalize_failure_can_retry_without_overwriting(self):
        self.attach()
        with patch.object(local, "candidate", return_value=(200, self.notes)), \
             patch.object(d.packager, "package", side_effect=self.package):
            original = self.command
            def fail(*args, **kwargs):
                if args[:2] == ("xcrun", "swift"):
                    raise local.Error("Signature mismatch")
                return original(*args, **kwargs)
            self.run.side_effect = fail
            with self.assertRaisesRegex(local.Error, "Signature mismatch"):
                local.finalize(self.folder, self.receipt, (), self.key)
            self.assertFalse((self.folder / "final").exists())
            self.assertFalse(list(self.folder.glob(".finalize-*")))
            self.run.side_effect = original
            local.finalize(self.folder, self.receipt, (), self.key)

    def final_zip(self):
        final = self.folder / "final"
        final.mkdir(exist_ok=True)
        archive = final / "annotator-0.2.0-arm64.zip"
        archive.write_bytes(b"stapled zip")
        data = dict(self.receipt["metadata"], channel="release", downloadURL="https://github.com/example/binaries/releases/download/v0.2.0/annotator-0.2.0-arm64.zip",
                    homepage="https://example.com", archive=archive.name, sha256=local.digest(archive),
                    architecture="arm64", notarizationVerified=True, repository="example/binaries",
                    tag="v0.2.0", track="stable", source=SOURCE, size=archive.stat().st_size,
                    edSignature=SIGNATURE)
        (final / "annotator-manifest.json").write_text(json.dumps(data))
        return data

    def test_disk_image_build_receipt_and_submit_resume(self):
        manifest = self.final_zip()
        name = "annotator-0.2.0-arm64.dmg"
        def make_dmg(app, out, identity, requested):
            self.assertEqual(requested, name)
            path = out / requested
            path.write_bytes(b"dmg")
            return {"archive": requested, "sha256": local.digest(path),
                    "size": path.stat().st_size, "volumeName": "Annotator"}
        def command(*args, **kwargs):
            if args[:3] == ("ditto", "-x", "-k"):
                self.app(args[4] / "Annotator.app")
                return ""
            return self.command(*args, **kwargs)
        self.run.side_effect = command
        with patch.object(local, "tree_hash", return_value="tree-hash"), \
             patch.object(local.disk_image, "build", side_effect=make_dmg):
            receipt = local.build_disk_image(self.folder, self.receipt)
        self.assertEqual(receipt["appArchive"], manifest["archive"])
        self.assertEqual(receipt["appSHA256"], manifest["sha256"])
        self.assertEqual(receipt["sha256"], local.digest(self.folder / "disk-image" / name))
        self.notary_name = receipt["archive"]
        self.log_hash = receipt["sha256"]
        local.submit_disk_image(self.folder, self.receipt, ())
        self.assertTrue((self.folder / "disk-image/submit-attempt.json").is_file())
        with self.assertRaisesRegex(local.Error, "already attempted"):
            local.submit_disk_image(self.folder, self.receipt, ())
        self.state = "In Progress"
        self.assertEqual(local.info_disk_image(ID, self.folder, self.receipt, ()), "In Progress")

    def test_disk_image_attempt_can_recover_without_resubmitting(self):
        self.final_zip()
        target = self.folder / "disk-image"
        target.mkdir()
        name = "annotator-0.2.0-arm64.dmg"
        (target / name).write_bytes(b"dmg")
        dmg_receipt = dict(schema=1, releaseVersion="0.2.0", build="200", source=SOURCE,
                           repository="example/binaries", tag="v0.2.0", archive=name,
                           sha256=local.digest(target / name), size=(target / name).stat().st_size,
                           volumeName="Annotator", sourceAppHash="tree-hash",
                           appArchive="annotator-0.2.0-arm64.zip",
                           appSHA256=local.digest(self.folder / "final" / "annotator-0.2.0-arm64.zip"))
        local.write_once(target / "disk-image-receipt.json", dmg_receipt)
        local.write_once(target / "submit-attempt.json", {
            "receiptHash": local.digest(target / "disk-image-receipt.json"),
            "archiveHash": dmg_receipt["sha256"]})
        self.notary_name = name
        self.state = "Accepted"
        self.log_hash = dmg_receipt["sha256"]
        local.recover_disk_image(self.folder, self.receipt, (), ID)
        self.assertEqual(local.attached_disk_image(self.folder, self.receipt), ID)
        with self.assertRaisesRegex(local.Error, "already attached"):
            local.recover_disk_image(self.folder, self.receipt, (), ID)

    def test_disk_image_accepted_staples_final_and_rejects_tampering(self):
        self.final_zip()
        target = self.folder / "disk-image"
        target.mkdir()
        name = "annotator-0.2.0-arm64.dmg"
        (target / name).write_bytes(b"dmg")
        dmg_receipt = dict(schema=1, releaseVersion="0.2.0", build="200", source=SOURCE,
                           repository="example/binaries", tag="v0.2.0", archive=name,
                           sha256=local.digest(target / name), size=(target / name).stat().st_size,
                           volumeName="Annotator", sourceAppHash="tree-hash",
                           appArchive="annotator-0.2.0-arm64.zip",
                           appSHA256=local.digest(self.folder / "final" / "annotator-0.2.0-arm64.zip"))
        local.write_once(target / "disk-image-receipt.json", dmg_receipt)
        self.notary_name = name
        self.log_hash = dmg_receipt["sha256"]
        local.submit_disk_image(self.folder, self.receipt, ())
        local.finalize_disk_image(self.folder, self.receipt, ())
        manifest = d.validate_disk_image_manifest(json.loads((target / "final/annotator-disk-image.json").read_text()),
                                                  json.loads((self.folder / "final/annotator-manifest.json").read_text()))
        self.assertTrue(manifest["stapled"])
        self.assertEqual(manifest["notarizationID"], ID)
        self.assertTrue((target / "final" / name).is_file())
        with self.assertRaisesRegex(local.Error, "already exists"):
            local.finalize_disk_image(self.folder, self.receipt, ())
        (target / name).write_bytes(b"changed")
        with self.assertRaisesRegex(local.Error, "bytes changed"):
            local.load_disk_image_receipt(self.folder, self.receipt)

    def test_finalize_requires_original_candidate_and_notes(self):
        self.attach()
        with patch.object(local, "candidate", side_effect=local.Error("wrong source")):
            with self.assertRaisesRegex(local.Error, "wrong source"):
                local.finalize(self.folder, self.receipt, (), self.key)
        with patch.object(local, "candidate", return_value=(11, self.notes)):
            with self.assertRaisesRegex(local.Error, "metadata"):
                local.finalize(self.folder, self.receipt, (), self.key)

    def test_candidate_requires_the_exact_clean_commit(self):
        with patch.object(d, "command", return_value=SOURCE), \
             patch.object(local.release, "configured_version", return_value=("0.2.0", 200)), \
             patch.object(local.release, "load_notes", return_value=self.notes), \
             patch.object(local.release, "verify_candidate") as clean:
            self.assertEqual(local.candidate("0.2.0", SOURCE), (200, self.notes))
            clean.assert_called_once_with(local.ROOT)
            with self.assertRaisesRegex(local.Error, "exact original"):
                local.candidate("0.2.0", "b" * 40)
            with self.assertRaisesRegex(local.Error, "version"):
                local.candidate("0.2.1", SOURCE)
            clean.side_effect = local.release.ReleaseError("Packaging requires a clean worktree.")
            with self.assertRaisesRegex(local.release.ReleaseError, "clean worktree"):
                local.candidate("0.2.0", SOURCE)

    def test_prepare_rejects_stale_source_before_build_or_directory_creation(self):
        folder = self.root / "new-release"
        args = argparse.Namespace(folder=folder, version="0.2.0", candidate=SOURCE)
        with patch.object(local, "candidate", side_effect=local.release.ReleaseError("stale source")):
            with self.assertRaisesRegex(local.release.ReleaseError, "stale source"):
                local.prepare(args)
        self.assertFalse(folder.exists())
        self.run.assert_not_called()

    def test_prepare_builds_and_persists_before_any_submit(self):
        folder = self.root / "new-release"
        args = argparse.Namespace(folder=folder, version="0.2.0", candidate=SOURCE,
                                  repository="example/binaries", homepage="https://example.com",
                                  identity=self.receipt["identity"], team=self.receipt["team"],
                                  public_key=PUBLIC, support_url="")
        def build(*cmd, **kwargs):
            if cmd[0] == "xcodebuild":
                derived = cmd[cmd.index("-derivedDataPath") + 1]
                self.app(derived / "Build/Products/Release/Annotator.app")
                signer = derived / "SourcePackages/artifacts/sparkledistribution/Sparkle/bin/sign_update"
                signer.parent.mkdir(parents=True)
                signer.write_bytes(b"signer fixture")
                return ""
            if cmd[:2] == ("ditto", "-c"):
                cmd[-1].write_bytes(b"new submission archive")
                return ""
            if cmd[:2] == ("ditto", "-x"):
                shutil.copytree(folder / "Annotator.app", cmd[-1] / "Annotator.app")
                return ""
            return self.command(*cmd, **kwargs)
        self.run.side_effect = build
        with patch.object(local, "candidate", return_value=(200, self.notes)), \
             patch.object(d, "sign_distribution_bundle") as sign:
            local.prepare(args)
        sign.assert_called_once()
        receipt = local.load(folder)
        self.assertEqual(receipt["source"], SOURCE)
        self.assertFalse((folder / "submit-attempt.json").exists())
        self.assertFalse(any(call.args[:2] == ("xcrun", "notarytool") for call in self.run.call_args_list))
        with self.assertRaisesRegex(local.Error, "fresh release"):
            local.prepare(args)

    def test_secret_material_and_raw_responses_are_not_saved_or_printed(self):
        capture = io.StringIO()
        with contextlib.redirect_stdout(capture), contextlib.redirect_stderr(capture):
            self.attach()
            local.info(ID, self.folder, self.receipt, ())
            with patch.object(d, "command", return_value="SECRET-KEY-MATERIAL"):
                with self.assertRaises(local.Error) as error:
                    local.notary("info", ID, ())
                self.assertNotIn("SECRET", str(error.exception))
        self.assertNotIn("SECRET", capture.getvalue())
        for path in self.folder.rglob("*"):
            if path.is_file():
                self.assertNotIn(b"SECRET-KEY-MATERIAL", path.read_bytes())

    def test_keys_must_stay_outside_output_and_source(self):
        args = argparse.Namespace(notary_key=self.folder / "sign_update", key_id="ABCDEFGHIJ", issuer=ID)
        with self.assertRaisesRegex(local.Error, "outside"):
            local.credentials(args, self.folder)
        args.notary_key = self.key
        self.assertIn(self.key, local.credentials(args, self.folder))

    def test_changed_attempt_or_submission_cannot_attach(self):
        self.attach()
        (self.folder / "submission.json").write_text(json.dumps({"id": ID, "archiveHash": "x"}))
        with self.assertRaisesRegex(local.Error, "does not match"):
            local.attached(self.folder, self.receipt)
        (self.folder / "submit-attempt.json").write_text("{}")
        with self.assertRaisesRegex(local.Error, "does not match"):
            local.attached(self.folder, self.receipt)

    def test_write_once_and_lock_refuse_overwrite_or_parallel_work(self):
        path = self.folder / "state.json"
        local.write_once(path, {"saved": True})
        with self.assertRaises(FileExistsError):
            local.write_once(path, {"saved": False})
        self.assertEqual(local.read_json(path), {"saved": True})
        self.assertFalse(list(self.folder.glob(".state-*")))
        with local.locked(self.folder):
            with self.assertRaisesRegex(local.Error, "Another local release"):
                with local.locked(self.folder):
                    self.fail("Concurrent lock must not succeed")


if __name__ == "__main__":
    unittest.main()
