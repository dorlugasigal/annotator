import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import pages

SITE = "https://dorlugasigal.github.io/annotator/"
SOURCE = "a" * 40


def fixture(root):
    folder = root / "artifact"
    (folder / "site").mkdir(parents=True)
    (folder / "site/index.html").write_text("<h1>Public site</h1>")
    files = pages.inventory(folder / "site")
    (folder / "receipt.json").write_text(json.dumps({
        "schemaVersion": 1, "siteURL": SITE.rstrip("/"), "source": SOURCE, "files": files
    }))
    return folder


class PagesTests(unittest.TestCase):
    def test_inventory_rejects_source_secrets_symlinks_and_hidden_files(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "index.html").write_text("site")
            for name in ["secret.p8", "App.swift", ".env", "app.zip", "site.js.map", "settings.json"]:
                path = root / name
                path.write_text("private")
                with self.subTest(name=name), self.assertRaises(pages.DistributionError):
                    pages.inventory(root)
                path.unlink()
            link = root / "external.css"
            link.symlink_to(root / "index.html")
            with self.assertRaises(pages.DistributionError):
                pages.inventory(root)
            link.unlink()
            (root / "secret.js").write_text("-----BEGIN PRIVATE KEY-----")
            with self.assertRaises(pages.DistributionError):
                pages.inventory(root)

    def test_receipt_and_asset_hashes_must_match(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = fixture(Path(tmp))
            pages.verify(folder, SITE, SOURCE)
            for source, site in [("b"*40, SITE), (SOURCE, "https://example.com/annotator/")]:
                with self.assertRaises(pages.DistributionError):
                    pages.verify(folder, site, source)
            (folder / "site/index.html").write_text("changed")
            with self.assertRaisesRegex(pages.DistributionError, "checksums"):
                pages.verify(folder, SITE, SOURCE)

    def test_generated_html_cannot_escape_the_selected_site_path(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            path = root / "index.html"
            path.write_text('<a href="/annotator/docs/">Docs</a><img src="/annotator/app/demo.png">')
            pages.verify_paths(root, SITE)
            for url in ["/docs/", "//example.com/asset.js", "/annotator-other/"]:
                path.write_text(f'<a href="{url}">Wrong path</a>')
                with self.assertRaises(pages.DistributionError):
                    pages.verify_paths(root, SITE)

    def test_tree_changes_preserve_other_sites_and_repository_controls(self):
        existing = [
            {"path": "README.md", "type": "blob"},
            {"path": ".github/workflows/pages.yml", "type": "blob"},
            {"path": "Casks/annotator.rb", "type": "blob"},
            {"path": "another-site/index.html", "type": "blob"},
            {"path": "annotator", "type": "tree"},
            {"path": "annotator/index.html", "type": "blob"},
            {"path": "annotator/old.css", "type": "blob"}
        ]
        _, desired, deletes = pages.tree_updates(existing, {"index.html": "hash", "_astro/new.css": "hash2"}, "annotator")
        self.assertEqual(set(desired), {"annotator/index.html", "annotator/_astro/new.css"})
        self.assertEqual([entry["path"] for entry in deletes], ["annotator/old.css"])
        with self.assertRaises(pages.DistributionError):
            pages.tree_updates([{"path": "annotator", "type": "blob"}], {}, "annotator")

    def test_disabled_sync_performs_no_external_action(self):
        with patch.dict(os.environ, {}, clear=True), patch.object(pages, "command") as run:
            with self.assertRaises(pages.DistributionError):
                pages.sync(Path("/unused"), SITE, SOURCE)
            run.assert_not_called()

    def test_sync_has_atomic_non_force_update_and_only_generated_paths(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = fixture(Path(tmp))
            calls = []
            def post(repo, endpoint, payload):
                calls.append((endpoint, payload))
                if endpoint == "git/blobs":
                    import base64
                    data = base64.b64decode(payload["content"])
                    return {"sha": hashlib.sha1(f"blob {len(data)}\0".encode() + data).hexdigest()}
                return {"sha": "c"*40}
            responses = [
                {"private": False, "default_branch": "main"},
                {"object": {"sha": "b"*40}},
                {"tree": {"sha": "d"*40}},
                {"tree": [{"path": "README.md", "type": "blob"}, {"path": "annotator/old.css", "type": "blob"}]}
            ]
            env = {"WEBSITE_DEPLOY_ENABLED": "true", "GITHUB_REF": "refs/heads/main",
                   "GITHUB_REPOSITORY": "dorlugasigal/annotator-code", "WEBSITE_REPOSITORY": "dorlugasigal/annotator"}
            with patch.dict(os.environ, env, clear=True), patch.object(pages, "gh_json", side_effect=responses), \
                 patch.object(pages, "post", side_effect=post), patch.object(pages, "command", return_value="") as command:
                pages.sync(folder, SITE, SOURCE)
            tree = next(payload for endpoint, payload in calls if endpoint == "git/trees")
            self.assertTrue(all(row["path"].startswith("annotator/") for row in tree["tree"]))
            payload = json.loads(command.call_args.kwargs["input"])
            self.assertFalse(payload["force"])

    def test_sync_rejects_private_or_source_destination(self):
        env = {"WEBSITE_DEPLOY_ENABLED": "true", "GITHUB_REF": "refs/heads/main",
               "GITHUB_REPOSITORY": "dorlugasigal/annotator-code", "WEBSITE_REPOSITORY": "dorlugasigal/annotator-code"}
        with patch.dict(os.environ, env, clear=True):
            with self.assertRaises(pages.DistributionError):
                pages.sync(Path("/unused"), SITE, SOURCE)
        env["WEBSITE_REPOSITORY"] = "dorlugasigal/annotator"
        with patch.dict(os.environ, env, clear=True), patch.object(pages, "gh_json", return_value={"private": True}):
            with self.assertRaises(pages.DistributionError):
                pages.sync(Path("/unused"), SITE, SOURCE)

    def test_user_site_requires_scoped_destination(self):
        self.assertEqual(pages.site_prefix(SITE), "annotator")
        with self.assertRaises(pages.DistributionError):
            pages.site_prefix("https://dorlugasigal.github.io")

    def test_local_sync_refuses_actions_wrong_source_and_dirty_source(self):
        with patch.dict(os.environ, {"GITHUB_ACTIONS": "true"}, clear=True), \
             patch.object(pages, "command") as command:
            with self.assertRaisesRegex(pages.DistributionError, "cannot run in Actions"):
                pages.sync(Path("/unused"), SITE, SOURCE, local=True)
            command.assert_not_called()
        for output in [("b" * 40, ""), (SOURCE, " M source.swift")]:
            with self.subTest(output=output), patch.dict(os.environ, {}, clear=True), \
                 patch.object(pages, "command", side_effect=output), patch.object(pages, "gh_json") as api:
                with self.assertRaises(pages.DistributionError):
                    pages.sync(Path("/unused"), SITE, SOURCE, local=True)
                api.assert_not_called()

    def test_local_sync_needs_no_fabricated_github_branch_environment(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = fixture(Path(tmp))
            files = pages.verify(folder, SITE, SOURCE)
            existing = []
            for name in files:
                data = (folder / "site" / name).read_bytes()
                sha = hashlib.sha1(f"blob {len(data)}\0".encode() + data).hexdigest()
                existing.append({"path": "annotator/" + name, "type": "blob", "mode": "100644", "sha": sha})
            responses = [
                {"private": False, "default_branch": "main"},
                {"object": {"sha": "b" * 40}}, {"tree": {"sha": "c" * 40}},
                {"tree": existing},
            ]
            with patch.dict(os.environ, {"WEBSITE_REPOSITORY": "dorlugasigal/annotator"}, clear=True), \
                 patch.object(pages, "command", side_effect=[SOURCE, ""]), \
                 patch.object(pages, "gh_json", side_effect=responses), patch.object(pages, "post") as post:
                self.assertEqual(pages.sync(folder, SITE, SOURCE, local=True), "b" * 40)
                post.assert_not_called()

    def test_project_site_rejects_mismatched_repository_path_before_writes(self):
        env = {"WEBSITE_DEPLOY_ENABLED": "true", "GITHUB_REF": "refs/heads/main",
               "GITHUB_REPOSITORY": "dorlugasigal/annotator-code",
               "WEBSITE_REPOSITORY": "dorlugasigal/other-project"}
        with patch.dict(os.environ, env, clear=True), \
             patch.object(pages, "gh_json", return_value={"private": False, "default_branch": "main"}), \
             patch.object(pages, "post") as post, patch.object(pages, "command") as command:
            with self.assertRaisesRegex(pages.DistributionError, "repository name"):
                pages.sync(Path("/unused"), SITE, SOURCE)
            post.assert_not_called()
            command.assert_not_called()


if __name__ == "__main__":
    unittest.main()
