import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hosted_website as h  # noqa: E402

SOURCE = "a" * 40
SITE = "https://annotator.example"


class HostedWebsiteTests(unittest.TestCase):
    def test_build_seals_the_site_then_the_bridge_from_it(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp) / "out"
            with patch.object(h.netlify, "prepare", return_value=folder / "netlify") as prepare, \
                 patch.object(h.netlify, "prepare_bridge") as bridge:
                h.build(folder, SITE, "https://owner.github.io/annotator", SOURCE)
            prepare.assert_called_once_with(h.local.ROOT, folder / "netlify", SITE, SOURCE)
            bridge.assert_called_once_with(folder / "netlify", folder / "bridge",
                                           "https://owner.github.io/annotator", SITE, SOURCE)

    def test_build_refuses_a_used_folder_or_a_short_source(self):
        with tempfile.TemporaryDirectory() as temp:
            with self.assertRaises(h.d.DistributionError):
                h.build(Path(temp), SITE, h.netlify.LEGACY_SITE, SOURCE)
            with self.assertRaises(h.d.DistributionError):
                h.build(Path(temp) / "new", SITE, h.netlify.LEGACY_SITE, "abc")

    def test_deploy_needs_the_switch_main_and_a_token(self):
        good = {"WEBSITE_DEPLOY_ENABLED": "true", "GITHUB_REF": "refs/heads/main", "NETLIFY_AUTH_TOKEN": "fixture"}
        for missing in ("WEBSITE_DEPLOY_ENABLED", "GITHUB_REF", "NETLIFY_AUTH_TOKEN"):
            env = {key: value for key, value in good.items() if key != missing}
            with self.subTest(missing=missing), patch.object(h.netlify, "deploy") as deploy:
                with self.assertRaises(h.d.DistributionError):
                    h.deploy("out", SITE, SOURCE, "site-id", env)
                deploy.assert_not_called()
        with patch.object(h.netlify, "deploy", return_value="deploy-1") as deploy, \
             patch.object(h.publisher, "verify_site") as live:
            self.assertEqual(h.deploy("out", SITE, SOURCE, "site-id", good), "deploy-1")
        deploy.assert_called_once_with(Path("out") / "netlify", SITE, SOURCE, "site-id",
                                       h.local.ROOT / "services/metrics/node_modules/.bin/netlify")
        # The deploy is not done until the live files match the sealed ones.
        live.assert_called_once_with(Path("out") / "netlify", SITE, SOURCE, h.Log, h.INTERVAL, h.TIMEOUT,
                                     netlify_host=True)

    def test_a_deploy_that_never_goes_live_fails(self):
        good = {"WEBSITE_DEPLOY_ENABLED": "true", "GITHUB_REF": "refs/heads/main", "NETLIFY_AUTH_TOKEN": "fixture"}
        with patch.object(h.netlify, "deploy", return_value="deploy-1"), \
             patch.object(h.publisher, "verify_site", side_effect=h.publisher.Pending("not live")):
            with self.assertRaises(h.d.DistributionError):
                h.deploy("out", SITE, SOURCE, "site-id", good)

    def test_bridge_verification_checks_the_legacy_host(self):
        with patch.object(h.publisher, "verify_site") as live:
            h.verify_bridge("out", "https://owner.github.io/annotator/", SOURCE)
        live.assert_called_once_with(Path("out") / "bridge", "https://owner.github.io/annotator", SOURCE,
                                     h.Log, h.INTERVAL, h.TIMEOUT)

    def test_errors_exit_with_one(self):
        with patch.dict(os.environ, {}, clear=True):
            self.assertEqual(h.main(["deploy", "--folder", "out", "--site", SITE, "--source", SOURCE,
                                     "--site-id", "x"]), 1)


if __name__ == "__main__":
    unittest.main()
