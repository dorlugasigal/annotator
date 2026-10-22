import io
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import publish_website as w

SOURCE = "a" * 40
SITE = "https://annotator-example.netlify.app"
IDENT = "11111111-2222-3333-4444-555555555555"


def settings():
    return {"repository": "owner/public", "homepage": SITE, "github_user": "owner",
            "netlify_site_id": IDENT, "metrics_url": SITE}


class PublishWebsiteTests(unittest.TestCase):
    def setUp(self):
        environment = patch.dict(os.environ)
        environment.start()
        self.addCleanup(environment.stop)
        os.environ.pop("GITHUB_ACTIONS", None)

    def test_actions_runner_is_refused_before_any_command(self):
        with tempfile.TemporaryDirectory() as temporary:
            progress = w.publisher.Progress(Path(temporary).resolve(), stages=w.STAGES)
            args = SimpleNamespace(interval=15, site_timeout=30)
            with patch.dict(os.environ, {"GITHUB_ACTIONS": "true"}), patch.object(w.d, "command") as command, \
                 self.assertRaisesRegex(w.d.DistributionError, "locally"):
                w.execute(args, settings(), progress)
            command.assert_not_called()

    def test_plan_prints_website_without_commands_or_outputs(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            config = root / "config.json"
            config.write_text(json.dumps(settings()))
            folder = root / "publication"
            with patch.object(w.d, "command") as command, patch("sys.stdout", new_callable=io.StringIO) as output:
                self.assertEqual(w.main(["--config", str(config), "--folder", str(folder)]), 0)
            self.assertIn(f"Website: {SITE}", output.getvalue())
            command.assert_not_called()
            self.assertFalse(folder.exists())

    def test_existing_release_config_does_not_pass_signing_fields_to_website(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary).resolve() / "config.json"
            path.write_text(json.dumps({**settings(), "notary_key": "/not/read.p8", "sparkle_key": "/not/read.key",
                                        "identity": "unused", "team": "unused"}))
            self.assertEqual(w.config(path), settings())
            for change in [{"metrics_url": "https://elsewhere.test"}, {"repository": "dorlugasigal/annotator-code"},
                           {"netlify_token": "forbidden"}, {"homepage": "https://example.test/subpath"}]:
                path.write_text(json.dumps({**settings(), **change}))
                with self.subTest(change=change), self.assertRaises(w.d.DistributionError):
                    w.config(path)

    def test_execution_uses_only_website_boundaries_and_resumes_saved_artifact(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            progress = w.publisher.Progress(root, stages=w.STAGES)
            args = SimpleNamespace(interval=15, site_timeout=30)
            def command(*argv, **kwargs):
                if argv[:2] == ("gh", "auth"): return "fixture-token"
                return "" if "status" in argv else SOURCE
            def build(folder, source, site, config):
                self.assertEqual((folder, source, site, config), (root, SOURCE, SITE, settings()))
                (folder / "package").mkdir()
                return folder / "package"
            with patch.object(w.d, "command", side_effect=command), \
                 patch.object(w.d, "gh_json", return_value={"private": False}), \
                 patch.object(w.netlify, "check_remote"), \
                 patch.object(w.publisher, "build_website", side_effect=build) as builder, \
                 patch.object(w.netlify, "deploy", return_value="deployment") as deploy, \
                 patch.object(w.publisher, "verify_site") as verify, \
                 patch.object(w.netlify, "verify") as verify_artifact, \
                 patch.object(w.netlify, "verify_service") as verify_service, \
                 patch.object(w.local, "credentials", side_effect=AssertionError("No app signing")):
                w.execute(args, settings(), progress)
                w.execute(args, settings(), progress)
                self.assertEqual(builder.call_count, 1)
                self.assertEqual(deploy.call_count, 1)
                self.assertEqual(verify.call_count, 2)
                verify_artifact.assert_called_once()
                verify_service.assert_called_once()
            self.assertEqual(json.loads((root / "deployment.json").read_text())["deploy_id"], "deployment")

    def test_dirty_checkout_and_changed_resume_destination_stop_before_upload(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            progress = w.publisher.Progress(root, stages=w.STAGES)
            args = SimpleNamespace(interval=15, site_timeout=30)
            with patch.object(w.d, "command", side_effect=[SOURCE, " M website/page.astro"]), \
                 patch.object(w.netlify, "deploy") as deploy, self.assertRaisesRegex(w.d.DistributionError, "Commit"):
                w.execute(args, settings(), progress)
            deploy.assert_not_called()
            (root / "website.json").write_text(json.dumps({"source": "b" * 40, **settings()}))
            with patch.object(w.d, "command", side_effect=[SOURCE, ""]), \
                 patch.object(w.netlify, "deploy") as deploy, self.assertRaisesRegex(w.d.DistributionError, "differs"):
                w.execute(args, settings(), progress)
            deploy.assert_not_called()


if __name__ == "__main__":
    unittest.main()
