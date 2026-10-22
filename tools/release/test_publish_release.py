import base64
import contextlib
import io
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import publish_release as p
from test_distribution import manifest

SOURCE = "a" * 40
IDENT = "11111111-2222-3333-4444-555555555555"


def settings(root):
    return dict(identity="Developer ID Application: Example", team="AAAAAAAAAA",
                public_key=base64.b64encode(b"x" * 32).decode(),
                notary_key=str(root / "notary.p8"), key_id="BBBBBBBBBB", issuer=IDENT,
                sparkle_key=str(root / "sparkle.key"), repository="owner/public",
                homepage="https://owner.github.io/public", github_user="owner",
                tap_repository="owner/public", website_repository="owner/public", support_url="")


class PublishReleaseTests(unittest.TestCase):
    def test_plan_has_no_commands_or_output_directories(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp).resolve()
            cfg = settings(root)
            cfg.pop("support_url")
            path = root / "config.json"
            path.write_text(json.dumps(cfg))
            with patch.object(p.d, "command") as run, patch("sys.stdout", new_callable=io.StringIO) as output:
                result = p.main(["--config", str(path), "--folder", str(root / "release")])
            self.assertEqual(result, 0)
            run.assert_not_called()
            self.assertFalse((root / "release").exists())
            self.assertFalse((root / "release.pipeline").exists())
            self.assertIn(f"Website: {cfg['homepage']}", output.getvalue())

    def test_status_reads_saved_progress_without_commands(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp).resolve()
            cfg = settings(root)
            cfg.pop("support_url")
            path = root / "config.json"
            path.write_text(json.dumps(cfg))
            journal = root / "release.pipeline"
            journal.mkdir()
            (journal / "progress.json").write_text(json.dumps(
                {"schema": 1, "stages": {"Wait for Apple": {"status": "paused"}}}))
            with patch.object(p.d, "command") as run, patch("sys.stdout", new_callable=io.StringIO) as out:
                result = p.main(["--config", str(path), "--folder", str(root / "release"), "--status"])
            self.assertEqual(result, 0)
            self.assertIn("PAUSED", out.getvalue())
            run.assert_not_called()

    def test_config_rejects_private_destination_and_unknown_secret_field(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp).resolve()
            for update in ({"repository": "dorlugasigal/annotator-code"}, {"password": "must-not-be-used"}):
                path = root / "config.json"
                path.write_text(json.dumps({**settings(root), **update}))
                with self.subTest(update=update), self.assertRaises(p.d.DistributionError):
                    p.config(path)

    def test_netlify_config_requires_one_origin_and_keeps_tokens_out_of_config(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp).resolve()
            path = root / "config.json"
            cfg = {**settings(root), "homepage":"https://owner-site.netlify.app",
                   "netlify_site_id":IDENT, "metrics_url":"https://owner-site.netlify.app/",
                   "legacy_site_url":"https://owner.github.io/public"}
            cfg.pop("support_url")
            path.write_text(json.dumps(cfg))
            self.assertEqual(p.config(path)["metrics_url"], cfg["homepage"])
            for update in [{"metrics_url":"https://other.netlify.app"}, {"netlify_token":"secret"}]:
                path.write_text(json.dumps({**cfg, **update}))
                with self.subTest(update=update), self.assertRaises(p.d.DistributionError):
                    p.config(path)

    def test_apple_timeout_keeps_existing_submission_without_resubmitting(self):
        with tempfile.TemporaryDirectory() as tmp:
            progress = p.Progress(Path(tmp).resolve())
            with patch.object(p.local, "attached", return_value=IDENT), \
                 patch.object(p.local, "info", return_value="In Progress"), \
                 patch.object(p.local, "submit") as submit, \
                 patch.object(p.time, "monotonic", side_effect=[0, 2]), \
                 patch.object(p.time, "sleep") as sleep:
                with self.assertRaisesRegex(p.Pending, "same command"):
                    p.wait_for_apple(Path(tmp), {}, (), progress, 60, 1)
                submit.assert_not_called()
                sleep.assert_not_called()

    def test_apple_requires_accepted_and_records_pause(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp).resolve()
            for state in ("Accepted", "Invalid", "Rejected"):
                with self.subTest(state=state), patch.object(p.local, "attached", return_value=IDENT), \
                     patch.object(p.local, "info", return_value=state):
                    if state == "Accepted":
                        p.wait_for_apple(root, {}, (), p.Progress(root), 60, 1)
                    else:
                        with self.assertRaisesRegex(p.d.DistributionError, state):
                            p.wait_for_apple(root, {}, (), p.Progress(root), 60, 1)
            progress = p.Progress(root)
            with self.assertRaises(p.Pending), progress.stage("Wait for Apple"):
                raise p.Pending("pending")
            self.assertEqual(json.loads((root / "progress.json").read_text())["stages"]["Wait for Apple"]["status"], "paused")

    def test_logs_redact_known_keys_and_tokens(self):
        with tempfile.TemporaryDirectory() as tmp:
            progress = p.Progress(Path(tmp), ["/private/secret.p8", "sensitive-value"])
            progress.output = io.StringIO()
            with patch("sys.stdout", new_callable=io.StringIO) as console:
                progress.log("/private/secret.p8 sensitive-value github_pat_abc123 ghp_abcdef")
            for output in (console.getvalue(), progress.output.getvalue()):
                self.assertNotIn("secret.p8", output)
                self.assertNotIn("sensitive-value", output)
                self.assertNotIn("abc123", output)
            self.assertEqual(progress.clean("-----BEGIN PRIVATE KEY-----"), "[private-key output omitted]")
            self.assertEqual(progress.clean("encoded-secret"), "[private-key output omitted]")
            self.assertEqual(progress.clean("-----END PRIVATE KEY-----"), "[private-key output omitted]")
            self.assertEqual(progress.clean("public result"), "public result")

    def test_environment_restores_existing_values(self):
        with patch.dict(os.environ, {"PUBLICATION_ENABLED": "false"}, clear=True):
            with p.environment({"PUBLICATION_ENABLED": "true", "GH_TOKEN": "temporary"}):
                self.assertEqual(os.environ["PUBLICATION_ENABLED"], "true")
            self.assertEqual(os.environ["PUBLICATION_ENABLED"], "false")
            self.assertNotIn("GH_TOKEN", os.environ)

    def test_command_logging_hides_captured_credentials_and_streams_build_output(self):
        with tempfile.TemporaryDirectory() as tmp:
            progress = p.Progress(Path(tmp).resolve(), ["fixture-token"])
            progress.output = io.StringIO()
            process = SimpleNamespace(stdout=io.StringIO("building\nfixture-token\n"), wait=lambda: 0)
            with patch.object(p.d, "command", return_value="fixture-token"), \
                 patch.object(p.subprocess, "Popen", return_value=process) as launch, \
                 patch.dict(os.environ, {"GH_TOKEN": "fixture-token", "NETLIFY_AUTH_TOKEN":"netlify-fixture", "ADMIN_SESSION_SECRET":"session-fixture"}), \
                 patch("sys.stdout", new_callable=io.StringIO), p.command_logs(progress):
                self.assertEqual(p.d.command("gh", "auth", "token", "--user", "owner"), "fixture-token")
                p.d.command("xcodebuild", "-quiet", "build")
            self.assertNotIn("fixture-token", progress.output.getvalue())
            self.assertIn("building", progress.output.getvalue())
            self.assertNotIn("GH_TOKEN", launch.call_args.kwargs["env"])
            self.assertNotIn("NETLIFY_AUTH_TOKEN", launch.call_args.kwargs["env"])
            self.assertNotIn("ADMIN_SESSION_SECRET", launch.call_args.kwargs["env"])
            self.assertNotIn("-quiet", launch.call_args.args[0])

    def test_live_site_wait_requires_matching_content_not_just_http_success(self):
        import hashlib
        progress = p.Progress(Path("/unused"))
        expected = hashlib.sha256(b"new site").hexdigest()
        with patch.object(p.pages, "verify", return_value={"index.html": expected}), \
             patch.object(p, "urlopen") as fetch, patch.object(p.time, "monotonic", side_effect=[0, 2]):
            fetch.return_value.__enter__.return_value.read.return_value = b"old site"
            with self.assertRaisesRegex(p.Pending, "live delivery is not confirmed"):
                p.verify_site(Path("/unused"), "https://owner.github.io/public", SOURCE, progress, 60, 1)
        with patch.object(p.pages, "verify", return_value={"index.html": expected}), \
             patch.object(p, "urlopen") as fetch:
            fetch.return_value.__enter__.return_value.read.return_value = b"new site"
            p.verify_site(Path("/unused"), "https://owner.github.io/public", SOURCE, progress, 60, 1)

    def test_failed_prepare_stops_before_any_publication(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp).resolve()
            cfg = settings(root)
            Path(cfg["sparkle_key"]).write_text("fixture")
            journal = root / "journal"
            journal.mkdir()
            args = SimpleNamespace(folder=root / "release", interval=60, apple_timeout=1, site_timeout=1)
            def command(*args, **kwargs):
                return SOURCE if args[0] == "git" else "private-fixture-token"
            with patch.object(p.d, "command", side_effect=command), \
                 patch.object(p.release, "configured_version", return_value=("0.1.0-alpha.2", 10)), \
                 patch.object(p.local, "ROOT", Path("/unrelated-repo")), \
                 patch.object(p.local, "candidate"), patch.object(p, "check_published"), \
                 patch.object(p.local, "credentials", return_value=()), \
                 patch.object(p.local, "prepare", side_effect=p.d.DistributionError("build failed")), \
                 patch.object(p.d, "publish") as publish:
                with self.assertRaisesRegex(p.d.DistributionError, "build failed"):
                    p.execute(args, cfg, p.Progress(journal))
                publish.assert_not_called()
            state = json.loads((journal / "progress.json").read_text())
            self.assertEqual(state["stages"]["Build and sign"]["status"], "failed")
            self.assertNotIn("private-fixture-token", (journal / "candidate.json").read_text())

    def test_resume_reuses_app_submission_and_final_archive(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp).resolve()
            cfg = settings(root)
            Path(cfg["sparkle_key"]).write_text("fixture")
            folder = root / "release"
            (folder / "final").mkdir(parents=True)
            (folder / "submission.json").write_text("{}")
            journal = root / "journal"
            journal.mkdir()
            args = SimpleNamespace(folder=folder, interval=60, apple_timeout=1, site_timeout=1)
            receipt = dict(source=SOURCE, version="0.1.0-alpha.2", repository=cfg["repository"],
                           homepage=cfg["homepage"], publicKey=cfg["public_key"],
                           identity=cfg["identity"], team=cfg["team"])
            def command(*args, **kwargs):
                return SOURCE if args[0] == "git" else "fixture-token"
            with contextlib.ExitStack() as stack:
                stack.enter_context(patch.object(p.d, "command", side_effect=command))
                stack.enter_context(patch.object(p.release, "configured_version", return_value=("0.1.0-alpha.2", 10)))
                stack.enter_context(patch.object(p.local, "ROOT", Path("/unrelated-repo")))
                stack.enter_context(patch.object(p, "windows_part_of_release", return_value=False))
                stack.enter_context(patch.object(p.local, "candidate"))
                stack.enter_context(patch.object(p, "check_published"))
                stack.enter_context(patch.object(p.local, "credentials", return_value=()))
                stack.enter_context(patch.object(p.local, "load", return_value=receipt))
                stack.enter_context(patch.object(p.local, "attached", return_value=IDENT))
                prepare = stack.enter_context(patch.object(p.local, "prepare"))
                submit = stack.enter_context(patch.object(p.local, "submit"))
                finalize = stack.enter_context(patch.object(p.local, "finalize"))
                stack.enter_context(patch.object(p, "wait_for_apple"))
                stack.enter_context(patch.object(p, "verify_final", return_value={**manifest(), "tag": "v0.1.0-alpha.2"}))
                stack.enter_context(patch.object(p.local, "build_disk_image"))
                stack.enter_context(patch.object(p.local, "submit_disk_image"))
                stack.enter_context(patch.object(p, "wait_for_disk_image"))
                stack.enter_context(patch.object(p.local, "finalize_disk_image"))
                stack.enter_context(patch.object(p, "verify_disk_image_final", return_value={"archive": "annotator.dmg"}))
                publish = stack.enter_context(patch.object(p.d, "publish"))
                stack.enter_context(patch.object(p.d, "update_tap", return_value="cask"))
                stack.enter_context(patch.object(p.d, "published_cask", return_value="cask"))
                stack.enter_context(patch.object(p, "build_website", return_value=root / "package"))
                sync = stack.enter_context(patch.object(p.pages, "sync", return_value="b" * 40))
                stack.enter_context(patch.object(p, "verify_site"))
                p.execute(args, cfg, p.Progress(journal))
                prepare.assert_not_called()
                submit.assert_not_called()
                finalize.assert_not_called()
                publish.assert_called_once_with(folder / "final", folder / "disk-image" / "final", None)
                self.assertTrue(sync.call_args.kwargs["local"])

    def test_cask_check_covers_the_tap_and_any_copy_in_the_release_repository(self):
        cfg = {"repository": "owner/public", "tap_repository": "owner/homebrew-annotator"}
        with patch.object(p.d, "published_cask", side_effect=["cask", None]) as read:
            p.verify_casks(cfg, "cask")
        self.assertEqual([call.args[0] for call in read.call_args_list], ["owner/homebrew-annotator", "owner/public"])
        with patch.object(p.d, "published_cask", side_effect=["cask", "older cask"]):
            with self.assertRaisesRegex(p.d.DistributionError, "cask in owner/public differs"):
                p.verify_casks(cfg, "cask")
        with patch.object(p.d, "published_cask", return_value=None):
            with self.assertRaisesRegex(p.d.DistributionError, "cask in owner/homebrew-annotator differs"):
                p.verify_casks(cfg, "cask")
        with patch.object(p.d, "published_cask", return_value="cask") as read:
            p.verify_casks({"repository": "owner/public", "tap_repository": "owner/public"}, "cask")
        read.assert_called_once_with("owner/public")

    def test_preflight_rejects_published_version_reuse_before_build(self):
        cfg = settings(Path("/unused"))
        data = manifest()
        with patch.object(p.d, "gh_json", return_value={"private": False}), \
             patch.object(p.d, "published_manifests", return_value=[data]):
            p.check_published(cfg, data["source"], data["releaseVersion"], int(data["build"]))
            with self.assertRaisesRegex(p.d.DistributionError, "different source"):
                p.check_published(cfg, "b" * 40, data["releaseVersion"], int(data["build"]))
            with self.assertRaisesRegex(p.d.DistributionError, "exceed"):
                p.check_published(cfg, SOURCE, "0.1.0-alpha.2", int(data["build"]))

    def test_resume_rejects_source_change_before_network_or_publication(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp).resolve()
            journal = root / "journal"
            journal.mkdir()
            (journal / "candidate.json").write_text(json.dumps({"source": "b" * 40}))
            args = SimpleNamespace(folder=root / "release")
            with patch.object(p.d, "command", return_value=SOURCE) as command, \
                 patch.object(p.release, "configured_version", return_value=("0.1.0-alpha.2", 10)), \
                 patch.object(p.d, "publish") as publish:
                with self.assertRaisesRegex(p.d.DistributionError, "source or destinations changed"):
                    p.execute(args, settings(root), p.Progress(journal))
                self.assertEqual(command.call_count, 1)
                self.assertEqual(command.call_args.args[0], "git")
                publish.assert_not_called()

    def test_final_archive_and_auxiliary_tampering_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp).resolve()
            final = root / "final"
            final.mkdir()
            data = {**manifest(), "notarizationID": IDENT}
            (final / "annotator-manifest.json").write_text(json.dumps(data))
            (final / data["archive"]).write_bytes(b"test archive")
            (root / "release-notes.md").write_text("notes")
            (final / "release-notes.md").write_text("notes")
            (final / "SHA256SUMS").write_text(f"{data['sha256']}  {data['archive']}\n")
            (final / "appcast.xml").write_text(p.d.appcast(data))
            receipt = dict(repository=data["repository"], source=data["source"], version=data["releaseVersion"],
                           build=int(data["build"]), homepage=data["homepage"], publicKey="public")
            with patch.object(p.local, "attached", return_value=IDENT), patch.object(p.d, "command") as run:
                self.assertEqual(p.verify_final(root, receipt), data)
                self.assertIn("verify-update.swift", str(run.call_args.args[2]))
                (final / "SHA256SUMS").write_text("changed")
                with self.assertRaisesRegex(p.d.DistributionError, "SHA256SUMS"):
                    p.verify_final(root, receipt)
                (final / data["archive"]).write_bytes(b"changed")
                with self.assertRaisesRegex(p.d.DistributionError, "checksum"):
                    p.verify_final(root, receipt)

    def test_saved_progress_accepts_old_stage_lists(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp).resolve()
            (root / "progress.json").write_text(json.dumps({"schema": 1, "stages": {
                "Preflight": {"status": "done"},
                "Staple and package": {"status": "done"},
                "Publish release": {"status": "paused"},
            }}))
            rows = p.saved_progress(root)
            self.assertEqual(rows["Preflight"][0], "done")
            self.assertEqual(rows["Notarize disk image"][0], "not started")


if __name__ == "__main__":
    unittest.main()
