import json
import io
import os
from pathlib import Path
import shutil
import tempfile
import unittest
import zipfile
from unittest.mock import patch

import netlify_release as n
import pages

SOURCE = "a" * 40
SITE = "https://annotator-example.netlify.app"
IDENT = "11111111-2222-3333-4444-555555555555"


class NetlifyReleaseTests(unittest.TestCase):
    def artifact(self, root):
        folder = root / "artifact"
        (folder / "site/admin").mkdir(parents=True)
        (folder / "site/updates").mkdir()
        (folder / "site/index.html").write_text('<a href="/downloads/">Download</a>')
        (folder / "site/admin/index.html").write_text("owner")
        (folder / "site/updates/preview.xml").write_text("<rss>signed-update</rss>")
        functions = folder / "functions"
        functions.mkdir()
        entries = []
        for name in sorted(n.FUNCTIONS):
            path = functions / (name + ".zip")
            with zipfile.ZipFile(path, "w") as archive:
                archive.writestr("index.mjs", "export default ()=>new Response('fixture');")
                archive.writestr("NETLIFY-LICENSE.txt", "Fixture notice")
            entry = {"name": name, "path": str(path),
                     "buildData": {"runtimeAPIVersion": 2, "bootstrapVersion": "2.18.0"},
                     "invocationMode": "stream", "runtimeVersion": "nodejs24.x"}
            if name in n.ROUTES:
                entry["routes"] = [{"pattern": n.ROUTES[name]}]
            if name not in ("admin-logout", "aggregate-daily"):
                entry["trafficRules"] = {"action": {"type": "rate_limit"}}
            if name == "aggregate-daily":
                entry["schedule"] = "15 0 * * *"
            entries.append(entry)
        (functions / "manifest.json").write_text(json.dumps({"functions": entries}))
        (folder / "netlify.toml").write_text("[build]\npublish='site'\n")
        n.local.write_once(folder / "receipt.json", {
            "schemaVersion": 1, "source": SOURCE, "siteURL": SITE, "files": n.files_in(folder),
        })
        return folder

    def test_root_host_and_site_id_are_explicit(self):
        self.assertEqual(n.site_url(SITE + "/"), SITE)
        self.assertEqual(n.site_id(IDENT), IDENT)
        for value in ["https://dorlugasigal.github.io/annotator", "https://example.test/path",
                      "http://example.test", "https://user:pass@example.test"]:
            with self.subTest(value=value), self.assertRaises(n.d.DistributionError):
                n.site_url(value)
        with self.assertRaises(n.d.DistributionError):
            n.site_id("create-a-site")

    def test_receipt_covers_site_functions_and_config(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = self.artifact(Path(temp).resolve())
            self.assertIn("index.html", n.verify(folder, SITE, SOURCE))
            for relative in ["site/index.html", "functions/collect-app.zip", "netlify.toml"]:
                path = folder / relative
                original = path.read_bytes()
                path.write_bytes(b"modified")
                with self.subTest(relative=relative), self.assertRaisesRegex(n.d.DistributionError, "checksums"):
                    n.verify(folder, SITE, SOURCE)
                path.write_bytes(original)
            with self.assertRaises(n.d.DistributionError):
                n.verify(folder, SITE, "b" * 40)
            with self.assertRaises(n.d.DistributionError):
                n.verify(folder, "https://other.netlify.app", SOURCE)

    def test_prepare_allows_generated_feeds_but_rejects_other_source_changes(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp).resolve()
            original = self.artifact(root)
            checkout = root / "checkout"
            shutil.copytree(original / "site", checkout / "website/dist")
            for name in ("admin.js", "admin.css"):
                (checkout / "website/dist/admin" / name).write_text("fixture")
            shutil.copy2(original / "netlify.toml", checkout / "netlify.toml")
            notice = checkout / "services/metrics/NETLIFY-LICENSE.txt"
            notice.parent.mkdir(parents=True)
            notice.write_text("Fixture notice")
            changes = (" M website/src/data/distribution.json\n"
                       "?? website/public/updates/preview.xml\n"
                       "?? website/public/updates/stable.xml\n"
                       "?? website/public/updates/windows-preview.json\n")

            def command(*args, **kwargs):
                if args[0] == "git":
                    if "status" in args:
                        self.assertIn("--untracked-files=all", args)
                        return changes
                    return SOURCE
                self.assertIn("functions:build", args)
                folder = Path(args[args.index("--functions") + 1])
                folder.mkdir()
                manifest = json.loads((original / "functions/manifest.json").read_text())
                for entry in manifest["functions"]:
                    destination = folder / (entry["name"] + ".zip")
                    with zipfile.ZipFile(entry["path"]) as source, zipfile.ZipFile(destination, "w") as bundle:
                        for name in source.namelist():
                            if name != "NETLIFY-LICENSE.txt":
                                bundle.writestr(name, source.read(name))
                    entry["path"] = str(destination)
                (folder / "manifest.json").write_text(json.dumps(manifest))
                return ""

            with patch.object(n.d, "command", side_effect=command):
                output = n.prepare(checkout, root / "prepared", SITE, SOURCE)
                self.assertIn("updates/preview.xml", n.verify(output, SITE, SOURCE))
                for path in ("website/public/updates/extra.xml", "website/public/updates/windows-extra.json",
                             "website/src/pages/index.astro",
                             "services/metrics/netlify/functions/collect-app.ts", ".env"):
                    changes = f" M website/src/data/distribution.json\n?? {path}\n"
                    with self.subTest(path=path), self.assertRaisesRegex(n.d.DistributionError, "Commit source"):
                        n.prepare(checkout, root / "rejected", SITE, SOURCE)
                    self.assertFalse((root / "rejected").exists())

    def test_function_paths_cannot_escape_and_schedule_cannot_disappear(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = self.artifact(Path(temp).resolve()) / "functions"
            manifest = json.loads((folder / "manifest.json").read_text())
            manifest["functions"][0]["path"] = "/tmp/outside.zip"
            (folder / "manifest.json").write_text(json.dumps(manifest))
            with self.assertRaises(n.d.DistributionError):
                n.verify_functions(folder)

    def test_missing_function_metadata_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = self.artifact(Path(temp).resolve()) / "functions"
            original = json.loads((folder / "manifest.json").read_text())
            for field in ("buildData", "invocationMode", "runtimeVersion", "routes", "trafficRules", "schedule"):
                manifest = json.loads(json.dumps(original))
                name = "aggregate-daily" if field == "schedule" else "admin-login"
                del next(entry for entry in manifest["functions"] if entry["name"] == name)[field]
                (folder / "manifest.json").write_text(json.dumps(manifest))
                with self.subTest(field=field), self.assertRaises(n.d.DistributionError):
                    n.verify_functions(folder)

    def test_bridge_preserves_feed_bytes_and_uses_only_public_stub(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp).resolve()
            folder = self.artifact(root)
            bridge = n.prepare_bridge(folder, root / "bridge", "https://owner.github.io/annotator", SITE, SOURCE)
            files = pages.verify(bridge, "https://owner.github.io/annotator", SOURCE)
            self.assertEqual(set(files), {
                "index.html", "docs/index.html", "downloads/index.html", "ai/index.html",
                "privacy/index.html", "support/index.html", "updates/preview.xml"
            })
            self.assertEqual((bridge / "site/updates/preview.xml").read_bytes(),
                             (folder / "site/updates/preview.xml").read_bytes())
            self.assertIn(SITE, (bridge / "site/index.html").read_text())
            self.assertIn(SITE + "/privacy/", (bridge / "site/privacy/index.html").read_text())

    FILES = ("README.md", "SECURITY.md", ".github/FUNDING.yml", ".github/readme/icon.png")
    PAGES = {"root": "packaging/pages", "files": FILES, "reporting": True}

    def repository_fixture(self, root):
        folder = root / self.PAGES["root"]
        for name in self.FILES:
            path = folder / name
            path.parent.mkdir(parents=True, exist_ok=True)
            if name.endswith((".png", ".jpg")):
                path.write_bytes(b"\x89binary https://dorlugasigal.github.io/annotator stays")
            else:
                path.write_text(f"{name}: [Help](https://dorlugasigal.github.io/annotator/docs/)")
        (folder / "repository.json").write_text(json.dumps({"description": "Draw on your Mac.", "topics": ["macos", "drawing"]}))
        return folder

    def repository_api(self, current, topics=("drawing", "macos"), reporting=True, homepage=None):
        tree = [{"path": name, "type": "blob", "sha": sha} for name, sha in current.items()]
        responses = {
            "repos/owner/public": {"private": False, "default_branch": "main", "description": "Draw on your Mac.",
                                   "homepage": homepage or SITE + "/", "topics": list(topics)},
            "repos/owner/public/git/ref/heads/main": {"object": {"sha": "head-sha"}},
            "repos/owner/public/git/commits/head-sha": {"tree": {"sha": "tree-sha"}},
            "repos/owner/public/git/trees/tree-sha?recursive=1": {"truncated": False, "tree": tree},
            "repos/owner/public/private-vulnerability-reporting": {"enabled": reporting},
        }
        return lambda *args: responses[args[1]]

    def test_repository_files_are_published_as_one_compare_and_swap_commit(self):
        import base64
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            self.repository_fixture(root)
            with patch.object(n.local, "ROOT", root):
                files = n.repository_files(SITE, self.PAGES)
            # README already matches; everything else is new or different.
            current = {"README.md": n.git_blob(files["README.md"]), "SECURITY.md": "stale-sha", "Casks/annotator.rb": "cask-sha"}
            writes = []
            def command(*args, **kwargs):
                if args[0] == "git":
                    return "" if "status" in args else SOURCE
                writes.append((args[3] if args[2] == "--method" else args[2], args[4], json.loads(kwargs.get("input") or "{}")))
                return json.dumps({"sha": f"sha-{len(writes)}"})
            with patch.object(n.local, "ROOT", root), patch.object(n.d, "command", side_effect=command), \
                 patch.object(n.d, "gh_json", side_effect=self.repository_api(current, topics=("macos",), reporting=False,
                                                                              homepage="https://owner.github.io/annotator/")):
                changed = n.publish_repository("owner/public", SITE, SOURCE, self.PAGES)
            self.assertEqual(changed, sorted(set(self.FILES) - {"README.md"}))
            blobs = [body for method, path, body in writes if path.endswith("git/blobs")]
            self.assertEqual(len(blobs), len(self.FILES) - 1)
            texts = [base64.b64decode(body["content"]) for body in blobs]
            # Website links point at the approved host in text files; images are uploaded byte for byte.
            self.assertTrue(any(f"[Help]({SITE}/docs/)".encode() in text for text in texts))
            self.assertIn(b"\x89binary https://dorlugasigal.github.io/annotator stays", texts)
            tree = next(body for method, path, body in writes if path.endswith("git/trees"))
            self.assertEqual(tree["base_tree"], "tree-sha")
            self.assertNotIn("Casks/annotator.rb", [entry["path"] for entry in tree["tree"]])
            commit = next(body for method, path, body in writes if path.endswith("git/commits"))
            self.assertEqual(commit["parents"], ["head-sha"])
            self.assertIn(SOURCE[:12], commit["message"])
            ref = next((method, body) for method, path, body in writes if path.endswith("git/refs/heads/main"))
            # Blobs, then the tree, then the commit: the ref moves to the new commit without forcing.
            self.assertEqual(ref, ("PATCH", {"sha": f"sha-{len(blobs) + 2}", "force": False}))
            settings = next(body for method, path, body in writes if path == "repos/owner/public")
            self.assertEqual(settings, {"description": "Draw on your Mac.", "homepage": SITE + "/"})
            self.assertIn(("PUT", "repos/owner/public/topics", {"names": ["drawing", "macos"]}), writes)
            self.assertIn(("PUT", "repos/owner/public/private-vulnerability-reporting", {}), writes)

    def test_matching_repository_is_left_unchanged_and_private_source_is_refused(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            self.repository_fixture(root)
            with patch.object(n.local, "ROOT", root):
                current = {name: n.git_blob(data) for name, data in n.repository_files(SITE, self.PAGES).items()}
            def command(*args, **kwargs):
                if args[0] == "git":
                    return "" if "status" in args else SOURCE
                self.fail(f"Unexpected write: {args}")
            with patch.object(n.local, "ROOT", root), patch.object(n.d, "command", side_effect=command), \
                 patch.object(n.d, "gh_json", side_effect=self.repository_api(current)):
                self.assertEqual(n.publish_repository("owner/public", SITE, SOURCE, self.PAGES), [])
            with patch.object(n.local, "ROOT", root), \
                 patch.object(n.d, "command", side_effect=lambda *args, **kwargs: " M README.md" if "status" in args else SOURCE):
                with self.assertRaisesRegex(n.d.DistributionError, "clean candidate source"):
                    n.publish_repository("owner/public", SITE, SOURCE, self.PAGES)
            with patch.object(n.local, "ROOT", root), patch.object(n.d, "gh_json", return_value={"private": True}):
                with self.assertRaisesRegex(n.d.DistributionError, "must be public"):
                    n.repository_changes("owner/public", SITE, self.PAGES)

    def test_tap_pages_publish_only_the_readme_and_leave_reporting_alone(self):
        import base64
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            folder = root / "packaging/homebrew-tap"
            folder.mkdir(parents=True)
            (folder / "README.md").write_text("Get [Annotator](https://dorlugasigal.github.io/annotator/).")
            (folder / "repository.json").write_text(json.dumps({"description": "Homebrew tap.", "topics": ["macos", "homebrew-tap"]}))
            responses = {
                "repos/owner/homebrew-x": {"private": False, "default_branch": "main", "description": None,
                                           "homepage": None, "topics": []},
                "repos/owner/homebrew-x/git/ref/heads/main": {"object": {"sha": "head-sha"}},
                "repos/owner/homebrew-x/git/commits/head-sha": {"tree": {"sha": "tree-sha"}},
                "repos/owner/homebrew-x/git/trees/tree-sha?recursive=1": {"truncated": False, "tree": [
                    {"path": "README.md", "type": "blob", "sha": "generated-readme"},
                    {"path": "Casks/annotator.rb", "type": "blob", "sha": "cask-sha"}]},
            }
            writes = []
            def command(*args, **kwargs):
                if args[0] == "git":
                    return "" if "status" in args else SOURCE
                writes.append((args[3], args[4], json.loads(kwargs.get("input") or "{}")))
                return json.dumps({"sha": f"sha-{len(writes)}"})
            # Asking about vulnerability reporting would raise KeyError: the tap never touches it.
            with patch.object(n.local, "ROOT", root), patch.object(n.d, "command", side_effect=command), \
                 patch.object(n.d, "gh_json", side_effect=lambda *args: responses[args[1]]):
                self.assertEqual(n.publish_repository("owner/homebrew-x", SITE, SOURCE, n.TAP_PAGES), ["README.md"])
            blob = next(body for method, path, body in writes if path.endswith("git/blobs"))
            self.assertEqual(base64.b64decode(blob["content"]).decode(), f"Get [Annotator]({SITE}/).")
            tree = next(body for method, path, body in writes if path.endswith("git/trees"))
            # The cask stays in the base tree; only the README changes.
            self.assertEqual((tree["base_tree"], [entry["path"] for entry in tree["tree"]]), ("tree-sha", ["README.md"]))
            self.assertIn(("PATCH", "repos/owner/homebrew-x", {"description": "Homebrew tap.", "homepage": SITE + "/"}), writes)
            self.assertIn(("PUT", "repos/owner/homebrew-x/topics", {"names": ["homebrew-tap", "macos"]}), writes)

    def test_remote_site_must_match_before_deploy(self):
        with patch.dict(os.environ, {"NETLIFY_AUTH_TOKEN": "not-a-real-token"}):
            with patch.object(n.d, "command", return_value=json.dumps({"id": IDENT, "ssl_url": SITE})):
                n.check_remote("netlify", IDENT, SITE)
            with patch.object(n.d, "command", return_value=json.dumps({"id": IDENT, "ssl_url": "https://elsewhere.test"})):
                with self.assertRaises(n.d.DistributionError):
                    n.check_remote("netlify", IDENT, SITE)
            # A custom domain becomes the primary URL; the site's netlify.app address still identifies it.
            primary = {"id": IDENT, "url": "https://example.app", "ssl_url": "https://example.app",
                       "custom_domain": "example.app", "default_domain": SITE.removeprefix("https://")}
            with patch.object(n.d, "command", return_value=json.dumps(primary)):
                n.check_remote("netlify", IDENT, SITE)
                n.check_remote("netlify", IDENT, "https://example.app")
                with self.assertRaises(n.d.DistributionError):
                    n.check_remote("netlify", IDENT, "https://www.example.app")

    def test_remote_check_accepts_existing_cli_login_without_environment_token(self):
        with patch.dict(os.environ, {}, clear=True), \
             patch.object(n.d, "command", return_value=json.dumps({"id": IDENT, "ssl_url": SITE})) as command:
            n.check_remote("netlify", IDENT, SITE)
        command.assert_called_once_with("netlify", "api", "getSite", "--data", json.dumps({"site_id": IDENT}))

    def test_deploy_uses_env_token_and_does_not_modify_sealed_artifact(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = self.artifact(Path(temp).resolve())
            commands = []
            def command(*args, **kwargs):
                commands.append(args)
                if args[0] == "git":
                    return "" if "status" in args else SOURCE
                if "api" in args:
                    return json.dumps({"id": IDENT, "ssl_url": SITE})
                self.assertIn("--no-build", args)
                self.assertIn("--skip-functions-cache", args)
                self.assertNotIn("--auth", args)
                self.assertEqual(args[args.index("--functions") + 1],
                                 n.local.ROOT / "services/metrics/netlify/functions")
                self.assertEqual(kwargs["cwd"], n.local.ROOT)
                return json.dumps({"site_id": IDENT, "deploy_id": "deploy-fixture"})
            with patch.dict(os.environ, {"NETLIFY_AUTH_TOKEN":"fixture"}), \
                 patch.object(n.d, "command", side_effect=command), patch.object(n, "verify_service") as verify_service:
                self.assertEqual(n.deploy(folder, SITE, SOURCE, IDENT, "netlify"), "deploy-fixture")
                verify_service.assert_called_once_with("netlify", IDENT, "deploy-fixture", SITE)
            n.verify(folder, SITE, SOURCE)
            self.assertFalse((folder / ".netlify").exists())

    def test_live_service_requires_the_schedule_and_json_sign_in_response(self):
        scheduled = {"site_id": IDENT, "function_schedules": [
            {"name": "aggregate-daily", "cron": "15 0 * * *"}]}
        for status, body, valid in [
            (401, '{"error":"Sign in to view metrics."}', True),
            (401, '<html>not JSON</html>', False),
            (401, '{"error":"Different service"}', False),
            (404, "not found", False), (502, "bad gateway", False),
        ]:
            error = n.HTTPError(SITE, status, "fixture", {}, io.BytesIO(body.encode()))
            with self.subTest(status=status, body=body), \
                 patch.object(n.d, "command", return_value=json.dumps(scheduled)), \
                 patch.object(n, "urlopen", side_effect=error):
                if valid:
                    n.verify_service("netlify", IDENT, "deployment", SITE)
                else:
                    with self.assertRaises(n.d.DistributionError):
                        n.verify_service("netlify", IDENT, "deployment", SITE)
        for deployed in [{**scheduled, "function_schedules": []}, {**scheduled, "site_id": "wrong"}]:
            with patch.object(n.d, "command", return_value=json.dumps(deployed)), \
                 patch.object(n, "urlopen") as request, self.assertRaises(n.d.DistributionError):
                n.verify_service("netlify", IDENT, "deployment", SITE)
            request.assert_not_called()

    def test_local_links_accept_root_without_relaxing_pages_subpath(self):
        pages.LocalLinks("").feed('<a href="/docs/">Help</a>')
        with self.assertRaises(n.d.DistributionError):
            pages.LocalLinks("").feed('<script src="//outside.test/x.js"></script>')
        with self.assertRaises(n.d.DistributionError):
            pages.LocalLinks("annotator").feed('<a href="/docs/">Wrong base</a>')


if __name__ == "__main__":
    unittest.main()
