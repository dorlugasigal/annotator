import copy
import datetime
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("release", Path(__file__).with_name("release.py"))
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)

LEGACY = {"version": "0.1.0-beta.17", "build": 35, "date": "2026-10-29",
          "changes": {"Fixed": ["A legacy fix."]}, "knownIssues": ["A known limit."]}
PLAIN = {"version": "0.2.0", "build": 200, "date": "2026-10-30",
         "changes": {"Changed": ["Plain versions."]}, "knownIssues": ["A known limit."]}


def repository(directory, entries, version="0.2.0", build=200, unreleased=None, extra=""):
    root = Path(directory)
    (root / "releases").mkdir(exist_ok=True)
    (root / "apps/macos").mkdir(parents=True, exist_ok=True)
    (root / "apps/macos/project.yml").write_text(
        f"settings:\n  base:\n    MARKETING_VERSION: {version}\n    CURRENT_PROJECT_VERSION: {build}\n{extra}")
    notes = {"schemaVersion": 1, "unreleased": unreleased or {}, "releases": entries}
    (root / "releases/notes.json").write_text(json.dumps(notes))
    return root, notes


class VersionTests(unittest.TestCase):
    def test_release_versions_are_plain_and_legacy_labels_are_history_only(self):
        self.assertEqual(release.parse_version("0.2.0"), (0, 2, 0))
        for value in ["0.2.0-beta.1", "0.2", "01.2.3", "1.2.3+4", "v1.2.3", 7]:
            with self.subTest(value=value), self.assertRaises(release.ReleaseError):
                release.parse_version(value)
        self.assertTrue(release.is_legacy("0.1.0-beta.17"))
        self.assertFalse(release.is_legacy("0.2.0"))
        self.assertEqual(release.parse_any("0.1.0-rc.2"), (0, 1, 0))
        self.assertEqual(release.parse_any("1.4.2"), (1, 4, 2))

    def test_builds_and_windows_packages_are_derived_and_keep_growing(self):
        self.assertEqual(release.derived_build("0.2.0"), 200)
        self.assertEqual(release.derived_build("1.0.0"), 10000)
        self.assertEqual(release.derived_build("2.99.99"), 29999)
        self.assertGreater(release.derived_build("0.2.0"), LEGACY["build"])
        for value in ["0.100.0", "0.2.100"]:
            with self.subTest(value=value), self.assertRaises(release.ReleaseError):
                release.derived_build(value)
        self.assertEqual(release.msix_version("0.2.0"), "1.2.0.0")
        self.assertEqual(release.msix_version("0.1.0-beta.17", 35), "1.1.35.0")
        with self.assertRaises(release.ReleaseError):
            release.msix_version("0.1.0-beta.17")
        ordered = ["0.1.0-beta.17", "0.2.0", "0.2.1", "0.3.0", "1.0.0"]
        packages = [release.version_key(release.msix_version(v, 35 if release.is_legacy(v) else None)) for v in ordered]
        self.assertEqual(packages, sorted(packages))

    def test_plan_suggests_the_next_plain_version(self):
        for current, change, expected in [("0.2.0", "fix", "0.2.1"), ("0.2.0", "feature", "0.3.0"), ("0.2.3", "breaking", "0.3.0"),
                                          ("1.2.3", "fix", "1.2.4"), ("1.2.3", "feature", "1.3.0"), ("1.2.3", "breaking", "2.0.0")]:
            with self.subTest(current=current, change=change):
                result = release.plan(current, change)
                self.assertEqual(result["proposed"], expected)
                self.assertEqual(result["build"], release.derived_build(expected))
                self.assertEqual(result["msix"], release.msix_version(expected))
                self.assertTrue(result["approvalRequired"])
                self.assertFalse(result["publicationAuthorized"])
        self.assertEqual(release.plan("0.2.0", "docs")["action"], "keep")
        for current, change in [("0.1.0-beta.17", "fix"), ("0.2.0", "stage")]:
            with self.subTest(current=current, change=change), self.assertRaises(release.ReleaseError):
                release.plan(current, change)


class ConfigurationTests(unittest.TestCase):
    def test_the_configured_build_is_derived_and_there_is_no_prerelease_setting(self):
        with tempfile.TemporaryDirectory() as directory:
            root, _ = repository(directory, [PLAIN])
            self.assertEqual(release.configured_version(root), ("0.2.0", 200))
            for version, build, extra in [("0.2.0", 35, ""), ("0.2.0", 201, ""), ("0.2.0-beta.1", 200, ""),
                                          ("0.2.0", 200, "    ANNOTATOR_PRERELEASE: beta.1\n")]:
                repository(directory, [PLAIN], version, build, extra=extra)
                with self.subTest(version=version, build=build, extra=extra), self.assertRaises(release.ReleaseError):
                    release.configured_version(root)


class NotesTests(unittest.TestCase):
    def test_notes_keep_legacy_history_below_plain_releases(self):
        with tempfile.TemporaryDirectory() as directory:
            root, original = repository(directory, [copy.deepcopy(PLAIN), dict(copy.deepcopy(LEGACY), source="abcdef0")],
                                        unreleased={"Added": ["A pending feature."]})
            self.assertEqual(release.load_notes(root), original)
            rendered = release.changelog(original)
            self.assertIn("## Unreleased", rendered)
            self.assertIn("## 0.2.0 - 2026-10-30", rendered)
            self.assertIn("## 0.1.0-beta.17 - 2026-10-29", rendered)
            self.assertNotIn("build", rendered)
            self.assertNotIn("Unreleased", release.changelog(original, "0.1.0-beta.17"))
            path = root / "releases/notes.json"
            for mutation in ["duplicate", "version", "derived", "legacy-newer", "package", "date", "category", "source", "field", "latest"]:
                notes = copy.deepcopy(original)
                newest, older = notes["releases"]
                if mutation == "duplicate": notes["releases"].append(copy.deepcopy(older))
                if mutation == "version": newest["version"] = "0.2.0-gamma.1"
                if mutation == "derived": newest["build"] = 36
                if mutation == "legacy-newer": notes["releases"].reverse()
                if mutation == "package": notes["releases"].insert(0, dict(copy.deepcopy(PLAIN), version="0.1.5", build=105))
                if mutation == "date": newest["date"] = "2026-02-31"
                if mutation == "category": newest["changes"] = {"Oops": ["bad"]}
                if mutation == "source": older["source"] = "pending"
                if mutation == "field": newest["candidate"] = "abcdef0"
                if mutation == "latest": notes["releases"].insert(0, dict(copy.deepcopy(PLAIN), version="0.3.0", build=300))
                path.write_text(json.dumps(notes))
                with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                    release.load_notes(root)

    def test_platform_notes_are_optional_validated_and_rendered_separately(self):
        entry = dict(copy.deepcopy(PLAIN), changes={"Fixed": ["A Mac fix."]}, knownIssues=["A Mac limit."],
                     platforms={"windows": {"changes": {"Added": ["The Windows edition."]}, "knownIssues": ["A Windows limit."]}})
        with tempfile.TemporaryDirectory() as directory:
            root, original = repository(directory, [entry, copy.deepcopy(LEGACY)])
            self.assertEqual(release.load_notes(root), original)
            self.assertEqual(release.changelog(original, "0.2.0"), "\n".join([
                "## 0.2.0 - 2026-10-30", "",
                "### Mac", "", "#### Fixed", "", "- A Mac fix.", "", "#### Known limits", "", "- A Mac limit.", "",
                "### Windows", "", "#### Added", "", "- The Windows edition.", "", "#### Known limits", "",
                "- A Windows limit.", ""]))
            path = root / "releases/notes.json"
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


class PrepareTests(unittest.TestCase):
    def test_prepare_writes_the_version_derived_build_and_a_dated_entry(self):
        with tempfile.TemporaryDirectory() as directory:
            root, _ = repository(directory, [copy.deepcopy(PLAIN), copy.deepcopy(LEGACY)],
                                 unreleased={"Fixed": ["A pending fix."]})
            entry = release.prepare("0.3.0", root, datetime.date(2026, 11, 2))
            self.assertEqual((entry["version"], entry["build"], entry["date"]), ("0.3.0", 300, "2026-11-02"))
            self.assertEqual(entry["knownIssues"], PLAIN["knownIssues"])
            self.assertEqual(release.configured_version(root), ("0.3.0", 300))
            notes = release.load_notes(root)
            self.assertEqual(notes["unreleased"], {})
            self.assertEqual([row["version"] for row in notes["releases"]], ["0.3.0", "0.2.0", "0.1.0-beta.17"])
            self.assertEqual((root / "CHANGELOG.md").read_text(), release.changelog(notes))

    def test_prepare_refuses_old_labelled_or_empty_releases(self):
        with tempfile.TemporaryDirectory() as directory:
            for version, unreleased in [("0.2.0", {"Fixed": ["x"]}), ("0.1.9", {"Fixed": ["x"]}), ("0.3.0-beta.1", {"Fixed": ["x"]}),
                                        ("0.3.0", {})]:
                root, _ = repository(directory, [copy.deepcopy(PLAIN), copy.deepcopy(LEGACY)], unreleased=unreleased)
                before = (root / "apps/macos/project.yml").read_text(), (root / "releases/notes.json").read_text()
                with self.subTest(version=version, unreleased=unreleased), self.assertRaises(release.ReleaseError):
                    release.prepare(version, root)
                self.assertEqual(before, ((root / "apps/macos/project.yml").read_text(), (root / "releases/notes.json").read_text()))


class RepositoryTests(unittest.TestCase):
    def test_the_repository_version_is_plain_with_its_derived_build(self):
        version, build = release.configured_version()
        self.assertEqual(build, release.derived_build(version))
        notes = release.load_notes()
        self.assertEqual((notes["releases"][0]["version"], notes["releases"][0]["build"]), (version, build))

    def test_planning_does_not_mutate_version_or_notes(self):
        paths = [release.ROOT / "apps/macos/project.yml", release.ROOT / "releases/notes.json"]
        before = [path.read_bytes() for path in paths]
        release.plan(release.configured_version()[0], "feature")
        self.assertEqual(before, [path.read_bytes() for path in paths])

    def test_packaging_needs_a_clean_candidate(self):
        def result(output):
            return type("GitResult", (), {"returncode": 0, "stdout": output, "stderr": ""})()
        with patch.object(release.subprocess, "run", return_value=result(" M apps/file.swift")):
            with self.assertRaisesRegex(release.ReleaseError, "clean worktree"):
                release.verify_candidate()
        with patch.object(release.subprocess, "run", return_value=result("")):
            release.verify_candidate()


if __name__ == "__main__":
    unittest.main()
