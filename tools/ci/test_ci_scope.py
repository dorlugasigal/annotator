import subprocess
import unittest
from unittest.mock import patch

import ci_scope


class ScopeTests(unittest.TestCase):
    def test_paths(self):
        for paths, expected in [
            (["docs/compatibility.md", "apps/macos/Sources/AnnotationCore/Model.swift"], (False, False)),
            (["website/package-lock.json"], (True, True)),
            (["integrations/computer-use/annotator/SKILL.md"], (True, False)),
            (["releases/notes.json"], (True, False)),
            (["tools/ci/ci_scope.py"], (True, True)),
            ([".github/workflows/validate.yml"], (True, True)),
            ([".npmrc"], (True, True)),
        ]:
            with self.subTest(paths=paths):
                result = ci_scope.select(paths)
                self.assertEqual((result["website"], result["metrics"]), expected)
        self.assertEqual(ci_scope.select(["services/metrics/src/collect.ts"]),
                         {"website": False, "metrics": True, "macos": False, "windows": False})
        for paths, expected in [
            (["apps/macos/Sources/AnnotationCore/Element.swift"], (True, False)),
            (["apps/windows/src/Annotator.Core/Element.cs"], (False, True)),
            (["reference/conformance/manifest.json"], (True, True)),
            (["releases/notes.json"], (True, True)),
            ([".github/workflows/validate.yml"], (True, True)),
            (["Makefile"], (True, False)),
            (["docs/parity.md", "website/src/pages/index.astro"], (False, False)),
        ]:
            with self.subTest(paths=paths):
                result = ci_scope.select(paths)
                self.assertEqual((result["macos"], result["windows"]), expected)
        self.assertTrue(ci_scope.select(["website/src/scripts/metrics.ts"])["metrics"])

    @patch.object(ci_scope.subprocess, "run")
    def test_release_and_manual_never_filter(self, run):
        for event, release in [("push", True), ("workflow_dispatch", False)]:
            self.assertTrue(all(ci_scope.scope(event, {}, release).values()))
        run.assert_not_called()

    @patch.object(ci_scope.subprocess, "run", return_value=subprocess.CompletedProcess([], 1))
    def test_shallow_or_new_branch_checks_both(self, _):
        self.assertTrue(all(ci_scope.scope("push", {"before": "0" * 40}, False).values()))

    @patch.object(ci_scope.subprocess, "check_output", return_value=b"website/deleted.ts\0services/metrics/new.ts\0apps/macos/Makefile.old\0apps/windows/x.cs\0")
    @patch.object(ci_scope.subprocess, "run", return_value=subprocess.CompletedProcess([], 0))
    def test_pr_uses_local_merge_parent_and_includes_deletions(self, _, diff):
        self.assertTrue(all(ci_scope.scope("pull_request", {}, False).values()))
        self.assertEqual(diff.call_args.args[0],
                         ["git", "diff", "--name-only", "--no-renames", "-z", "HEAD^1", "HEAD"])

    @patch.object(ci_scope.subprocess, "check_output", side_effect=subprocess.CalledProcessError(1, ["git"]))
    @patch.object(ci_scope.subprocess, "run", return_value=subprocess.CompletedProcess([], 0))
    def test_diff_failure_is_not_an_empty_success(self, *_):
        with self.assertRaises(subprocess.CalledProcessError):
            ci_scope.scope("push", {"before": "a" * 40}, False)

    @patch.object(ci_scope.subprocess, "check_output", return_value=b"docs/compatibility.md\0")
    @patch.object(ci_scope.subprocess, "run", return_value=subprocess.CompletedProcess([], 0))
    def test_push_compares_entire_event_range_not_only_latest_commit(self, _, diff):
        result = ci_scope.scope("push", {"before": "b" * 40}, False)
        self.assertEqual(result, {"website": False, "metrics": False, "macos": False, "windows": False})
        self.assertEqual(diff.call_args.args[0][-2:], ["b" * 40, "HEAD"])

    def test_invalid_event_fails(self):
        with self.assertRaises(ValueError):
            ci_scope.scope("push", {}, False)


if __name__ == "__main__":
    unittest.main()
