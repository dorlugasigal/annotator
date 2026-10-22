import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("release", Path(__file__).with_name("release.py"))
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class ReleaseTests(unittest.TestCase):
    def test_same_stage_candidates_and_docs_only(self):
        for change in ["fix", "feature"]:
            result = release.plan("0.1.0-alpha.1", 9, change)
            self.assertEqual((result["proposed"], result["build"]), ("0.1.0-alpha.2", 10))
            self.assertTrue(result["approvalRequired"])
            self.assertFalse(result["publicationAuthorized"])
        self.assertEqual(release.plan("0.1.0-alpha.1", 9, "docs")["action"], "keep")

    def test_stage_gates_do_not_allow_skips_or_automatic_stable(self):
        for current, target, expected in [
            ("0.1.0-alpha.2", "beta", "0.1.0-beta.1"),
            ("0.1.0-beta.3", "rc", "0.1.0-rc.1"),
            ("0.1.0-rc.2", "stable", "0.1.0"),
        ]:
            self.assertEqual(release.plan(current, 12, "fix", target)["proposed"], expected)
        for target in ["rc", "stable"]:
            with self.assertRaises(release.ReleaseError):
                release.plan("0.1.0-alpha.1", 9, "fix", target)
        self.assertEqual(release.plan("0.1.0-rc.1", 15, "fix")["proposed"], "0.1.0-rc.2")

    def test_stable_and_breaking_work_start_a_new_alpha_line(self):
        for change, expected in [("fix", "1.2.4-alpha.1"), ("feature", "1.3.0-alpha.1"), ("breaking", "2.0.0-alpha.1")]:
            self.assertEqual(release.plan("1.2.3", 30, change)["proposed"], expected)
        self.assertEqual(release.plan("0.1.0-alpha.2", 10, "breaking")["proposed"], "0.2.0-alpha.1")
        with self.assertRaises(release.ReleaseError):
            release.plan("0.1.0-beta.1", 10, "breaking", "rc")

    def test_schema_current_version_immutability_and_rendering(self):
        original = {
            "schemaVersion": 1, "unreleased": {"Added": ["A pending feature."]},
            "releases": [{
                "version": "0.1.0-alpha.1", "build": 9, "date": "2026-09-23",
                "source": "abcdef0", "changes": {"Fixed": ["A verified fix."]}, "knownIssues": []
            }]
        }
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "releases").mkdir()
            (root / "apps/macos").mkdir(parents=True)
            (root / "apps/macos/project.yml").write_text(
                "    MARKETING_VERSION: 0.1.0\n    ANNOTATOR_PRERELEASE: alpha.1\n    CURRENT_PROJECT_VERSION: 9\n")
            path = root / "releases/notes.json"
            path.write_text(json.dumps(original))
            self.assertEqual(release.load_notes(root, require_frozen=True), original)
            rendered = release.changelog(original)
            self.assertIn("## Unreleased", rendered)
            self.assertIn("0.1.0-alpha.1 (build 9)", rendered)
            self.assertNotIn("Unreleased", release.changelog(original, "0.1.0-alpha.1"))
            for mutation in ["duplicate", "version", "build", "date", "category", "source"]:
                notes = copy.deepcopy(original)
                entry = notes["releases"][0]
                if mutation == "duplicate": notes["releases"].append(copy.deepcopy(entry))
                if mutation == "version": entry["version"] = "0.1.0-alpha.01"
                if mutation == "build": entry["build"] = 10
                if mutation == "date": entry["date"] = "2026-02-31"
                if mutation == "category": entry["changes"] = {"Oops": ["bad"]}
                if mutation == "source": entry["source"] = "pending"
                path.write_text(json.dumps(notes))
                with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                    release.load_notes(root, require_frozen=True)

    def test_platform_notes_are_optional_validated_and_rendered_separately(self):
        entry = {
            "version": "0.1.0-beta.8", "build": 26, "date": "2026-10-20", "source": "abcdef0",
            "changes": {"Fixed": ["A Mac fix."]}, "knownIssues": ["A Mac limit."],
            "platforms": {"windows": {"changes": {"Added": ["The Windows edition."]}, "knownIssues": ["A Windows limit."]}},
        }
        original = {"schemaVersion": 1, "unreleased": {}, "releases": [entry]}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "releases").mkdir()
            (root / "apps/macos").mkdir(parents=True)
            (root / "apps/macos/project.yml").write_text(
                "    MARKETING_VERSION: 0.1.0\n    ANNOTATOR_PRERELEASE: beta.8\n    CURRENT_PROJECT_VERSION: 26\n")
            path = root / "releases/notes.json"
            path.write_text(json.dumps(original))
            self.assertEqual(release.load_notes(root, require_frozen=True), original)
            rendered = release.changelog(original, "0.1.0-beta.8")
            self.assertEqual(rendered, "\n".join([
                "## 0.1.0-beta.8 (build 26) - 2026-10-20", "",
                "### Mac", "", "#### Fixed", "", "- A Mac fix.", "", "#### Known limits", "", "- A Mac limit.", "",
                "### Windows", "", "#### Added", "", "- The Windows edition.", "", "#### Known limits", "",
                "- A Windows limit.", ""]))
            plain = copy.deepcopy(original)
            del plain["releases"][0]["platforms"]
            self.assertNotIn("### Mac", release.changelog(plain))
            self.assertIn("### Fixed", release.changelog(plain))
            for mutation in ["empty", "macos", "unknown", "fields", "category", "item", "issues"]:
                notes = copy.deepcopy(original)
                platforms = notes["releases"][0]["platforms"]
                if mutation == "empty": platforms.clear()
                if mutation == "macos": platforms["macos"] = copy.deepcopy(platforms["windows"])
                if mutation == "unknown": platforms["linux"] = copy.deepcopy(platforms["windows"])
                if mutation == "fields": platforms["windows"]["date"] = "2026-10-20"
                if mutation == "category": platforms["windows"]["changes"] = {"Oops": ["bad"]}
                if mutation == "item": platforms["windows"]["changes"] = {"Added": [" "]}
                if mutation == "issues": platforms["windows"]["knownIssues"] = "A limit."
                path.write_text(json.dumps(notes))
                with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                    release.load_notes(root)

    def test_planning_does_not_mutate_version_or_notes(self):
        paths = [release.ROOT / "apps/macos/project.yml", release.ROOT / "releases/notes.json"]
        before = [path.read_bytes() for path in paths]
        version, build = release.configured_version()
        release.plan(version, build, "feature")
        self.assertEqual(before, [path.read_bytes() for path in paths])

    def test_frozen_packaging_rejects_dirty_or_changed_source(self):
        notes = {"releases": [{"source": "abcdef0"}]}
        def result(output):
            return type("GitResult", (), {"returncode": 0, "stdout": output, "stderr": ""})()
        with patch.object(release.subprocess, "run", return_value=result(" M apps/file.swift")):
            with self.assertRaisesRegex(release.ReleaseError, "clean worktree"):
                release.verify_frozen_source(notes)
        with patch.object(release.subprocess, "run", side_effect=[result(""), result(""), result("apps/file.swift")]):
            with self.assertRaisesRegex(release.ReleaseError, "differs"):
                release.verify_frozen_source(notes)
        with patch.object(release.subprocess, "run", side_effect=[result(""), result(""), result("")]):
            release.verify_frozen_source(notes)
        for path in ["website/src/pages/index.astro", "services/metrics/src/http.ts", "netlify.toml",
                     "integrations/computer-use/annotator/SKILL.md"]:
            with self.subTest(path=path), patch.object(release.subprocess, "run", side_effect=[result(""), result(""), result(path)]):
                with self.assertRaisesRegex(release.ReleaseError, "differs"):
                    release.verify_frozen_source(notes)

if __name__ == "__main__":
    unittest.main()
