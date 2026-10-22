import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pr_policy  # noqa: E402

TEMPLATE = (Path(__file__).resolve().parents[2] / ".github/PULL_REQUEST_TEMPLATE.md").read_text()


class LinkedIssueTests(unittest.TestCase):
    def check(self, body, labels=(), author="someone"):
        return pr_policy.linked_issue(body, set(labels), author)

    def test_accepts_closing_and_related_references(self):
        for body in ("Fixes #12", "closes #3.", "Resolved: #40", "Part of #16", "Refs #9",
                     "Related to dorlugasigal/annotator#7",
                     "Fixes https://github.com/dorlugasigal/annotator/issues/21"):
            self.assertIsNone(self.check(body), body)

    def test_rejects_missing_or_bare_references(self):
        for body in ("", "Small cleanup.", "See #12", "Fixes #", "Fixes issue 12"):
            self.assertIsNotNone(self.check(body), body)

    def test_the_unedited_template_does_not_pass(self):
        self.assertIsNotNone(self.check(TEMPLATE))

    def test_a_reference_inside_a_comment_does_not_count(self):
        self.assertIsNotNone(self.check("<!-- Fixes #123 -->\nNothing here"))

    def test_label_and_bots_are_exempt(self):
        self.assertIsNone(self.check("", labels=["no-issue"]))
        self.assertIsNone(self.check("", author="dependabot[bot]"))


class ReleaseNoteTests(unittest.TestCase):
    def check(self, files, labels=(), author="someone"):
        return pr_policy.release_note(set(files), set(labels), author)

    def test_app_changes_need_the_notes_file(self):
        for path in ("apps/macos/Annotator/WorkspaceModel.swift", "apps/macos/Sources/AnnotationCore/Ink.swift",
                     "apps/macos/Packages/Sparkle/Package.swift", "apps/windows/src/Annotator.App/App.xaml.cs"):
            self.assertIsNotNone(self.check([path]), path)
            self.assertIsNone(self.check([path, "releases/notes.json"]), path)

    def test_tests_tools_and_docs_need_no_note(self):
        self.assertIsNone(self.check(["apps/macos/Tests/NativeAppTests/X.swift", "apps/windows/tests/A/B.cs",
                                      "tools/ci/ci_scope.py", "docs/architecture.md", "website/src/x.astro"]))

    def test_label_and_bots_are_exempt(self):
        self.assertIsNone(self.check(["apps/windows/src/A.cs"], labels=["no-release-note"]))
        self.assertIsNone(self.check(["apps/windows/src/A.csproj"], author="dependabot[bot]"))


class CommandLineTests(unittest.TestCase):
    def test_reads_the_event_and_the_changed_files(self):
        with tempfile.TemporaryDirectory() as tmp:
            event = Path(tmp) / "event.json"
            files = Path(tmp) / "files.txt"
            event.write_text(json.dumps({"pull_request": {
                "body": "Fixes #5", "labels": [{"name": "macos"}], "user": {"login": "someone"}}}))
            files.write_text("apps/macos/Annotator/A.swift\n")
            self.assertEqual(pr_policy.main(["issue", "--event", str(event)]), 0)
            self.assertEqual(pr_policy.main(["notes", "--event", str(event), "--files", str(files)]), 1)
            files.write_text("apps/macos/Annotator/A.swift\nreleases/notes.json\n")
            self.assertEqual(pr_policy.main(["notes", "--event", str(event), "--files", str(files)]), 0)

    def test_a_missing_body_is_treated_as_empty(self):
        with tempfile.TemporaryDirectory() as tmp:
            event = Path(tmp) / "event.json"
            event.write_text(json.dumps({"pull_request": {"body": None, "labels": [], "user": {"login": "x"}}}))
            self.assertEqual(pr_policy.main(["issue", "--event", str(event)]), 1)


if __name__ == "__main__":
    unittest.main()
