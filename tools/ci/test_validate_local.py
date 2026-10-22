import subprocess
import unittest
from unittest.mock import patch

import validate_local as local


class LocalValidationTests(unittest.TestCase):
    @patch.object(local.platform, "system", return_value="Darwin")
    @patch.object(local.Path, "is_dir", return_value=True)
    @patch.object(local.subprocess, "check_output", return_value="v26.8.1\n")
    @patch.object(local, "run")
    def test_full_sequence(self, run, *_):
        local.validate("all")
        commands = [call.args[0] for call in run.call_args_list]
        self.assertEqual(commands[0], ["python3", "tools/demo/recording/test_cursor_camera.py"])
        commands = commands[1:]
        self.assertEqual(commands[:3], [["make", "test"], ["swift", "test", "--package-path", "apps/macos", "-c", "release"],
                                       ["make", "app"]])
        builds = [c for c in commands if c[0] == "xcodebuild"]
        self.assertEqual([c[c.index("-configuration") + 1] for c in builds], ["Debug", "Release"])
        self.assertTrue(all(c[-2:] == ["build", "test"] for c in builds))
        self.assertIn("--samples", commands[5])
        self.assertEqual(commands[5][3:7], ["21", "--warmup", "3", "--output"])
        self.assertEqual([c[-1] for c in commands[6:]],
                         ["typecheck", "build", "test", "test:e2e", "test:distribution", "test:pages",
                          "test:windows"])
        self.assertEqual(run.call_args_list[-1].kwargs["env"]["SITE_URL"], "")
        self.assertEqual(run.call_args_list[-1].kwargs["env"]["DISTRIBUTION_METADATA_FILE"], "")
        self.assertEqual(run.call_args_list[-1].kwargs["env"]["WINDOWS_LISTING_FILE"], "")

    @patch.object(local.platform, "system", return_value="Linux")
    def test_wrong_platform_fails(self, _):
        with self.assertRaisesRegex(RuntimeError, "Xcode"):
            local.validate("native")

    @patch.object(local.subprocess, "check_output", return_value="v22.0.0")
    def test_wrong_node_fails(self, _):
        with self.assertRaisesRegex(RuntimeError, "Node"):
            local.validate("browser")

    @patch.object(local.subprocess, "check_output", return_value="v26.8.1")
    @patch.object(local, "run", side_effect=subprocess.CalledProcessError(23, ["npm"]))
    def test_failure_stops_without_success(self, run, _):
        with self.assertRaises(subprocess.CalledProcessError):
            local.validate("browser")
        self.assertEqual(run.call_count, 1)

    @patch.object(local.sys, "argv", ["validate_local.py", "all"])
    @patch.object(local, "validate", side_effect=subprocess.CalledProcessError(23, ["swift"]))
    def test_exit_status_is_preserved(self, _):
        self.assertEqual(local.main(), 23)


if __name__ == "__main__":
    unittest.main()
