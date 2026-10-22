import io
import json
import os
from pathlib import Path
import re
import tempfile
import unittest
from unittest.mock import patch

import publish_release as publisher
from release_progress import TerminalProgress, duration, text


class Terminal(io.StringIO):
    @property
    def encoding(self):
        return "utf-8"

    def isatty(self):
        return True


class TerminalProgressTests(unittest.TestCase):
    def test_plain_output_has_transitions_without_raw_logs_or_escapes(self):
        stream = io.StringIO()
        display = TerminalProgress(("Build", "Verify"), stream=stream)
        with display:
            display.stage("Build", "running")
            display.message("private compiler detail", detail=True)
            display.stage("Build", "done", 65)
        output = stream.getvalue()
        self.assertIn("[RUN", output)
        self.assertIn("[OK", output)
        self.assertIn("01:05", output)
        self.assertNotIn("compiler detail", output)
        self.assertNotIn("\033", output)

    def test_explicit_plain_and_verbose_disable_animation(self):
        for options in ({"plain": True}, {"verbose": True}):
            stream = Terminal()
            with patch.dict(os.environ, {"TERM": "xterm"}, clear=True):
                display = TerminalProgress(("Build",), stream=stream, **options)
            with display:
                display.message("compiler detail", detail=True)
            self.assertFalse(display.live)
            self.assertNotIn("\033", stream.getvalue())
            self.assertEqual("compiler detail" in stream.getvalue(), bool(options.get("verbose")))

    def test_live_rows_show_spinner_checkmarks_and_stage_counts(self):
        stream = Terminal()
        now = [10]
        with patch.dict(os.environ, {"TERM": "xterm"}, clear=True), \
             patch("release_progress.shutil.get_terminal_size", return_value=os.terminal_size((80, 24))):
            display = TerminalProgress(("Build", "Wait for Apple"), stream=stream, clock=lambda: now[0])
            display.stage("Build", "done", 50)
            display.stage("Wait for Apple", "running")
            now[0] = 75
            display.render()
            self.assertIn("✓", stream.getvalue())
            self.assertIn("⠋", stream.getvalue())
            self.assertIn("01:05", stream.getvalue())
            self.assertIn("1/2 stages done", stream.getvalue())
            display.frame = 1
            display.render()
            self.assertIn("⠙", stream.getvalue())
            self.assertNotIn("%", stream.getvalue())

    def test_cursor_is_restored_on_exception(self):
        stream = Terminal()
        with patch.dict(os.environ, {"TERM": "xterm"}, clear=True), \
             patch("release_progress.threading.Thread") as thread:
            display = TerminalProgress(("Build",), stream=stream)
            with self.assertRaises(RuntimeError), display:
                raise RuntimeError("fixture")
        self.assertIn("\033[?25l", stream.getvalue())
        self.assertTrue(stream.getvalue().endswith("\033[?25h"))
        thread.return_value.join.assert_called_once()
        self.assertTrue(display.stop_event.is_set())

    def test_no_color_keeps_animation_but_removes_colors(self):
        stream = Terminal()
        with patch.dict(os.environ, {"TERM": "xterm", "NO_COLOR": ""}, clear=True):
            display = TerminalProgress(("Build",), stream=stream)
        self.assertTrue(display.live)
        self.assertFalse(display.color)
        display.stage("Build", "done", 1)
        self.assertNotIn("\033[32m", stream.getvalue())

    def test_compact_render_does_not_wrap_narrow_terminal(self):
        stream = Terminal()
        with patch.dict(os.environ, {"TERM": "xterm"}, clear=True), \
             patch("release_progress.shutil.get_terminal_size", return_value=os.terminal_size((25, 8))):
            display = TerminalProgress(("A long build stage", "A long verification stage"), stream=stream)
            display.log_path = "/a/very/long/path/to/a/log/file"
            display.stage("A long build stage", "running")
        plain = re.sub(r"\x1b\[[0-?]*[ -/]*[@-~]", "", stream.getvalue())
        self.assertTrue(all(len(line) <= 24 for line in plain.splitlines()))

    def test_pause_and_failure_remain_distinct(self):
        stream = Terminal()
        with patch.dict(os.environ, {"TERM": "xterm"}, clear=True):
            display = TerminalProgress(("Wait", "Verify"), stream=stream)
        display.stage("Wait", "paused", 7200)
        display.stage("Verify", "failed", 3)
        self.assertIn("!", stream.getvalue())
        self.assertIn("✗", stream.getvalue())
        self.assertIn("2:00:00", stream.getvalue())
        self.assertIn("saved", stream.getvalue().lower())

    def test_quiet_console_keeps_full_sanitized_log_and_helper_messages(self):
        with tempfile.TemporaryDirectory() as tmp:
            console = io.StringIO()
            display = TerminalProgress(("Build",), stream=console)
            progress = publisher.Progress(Path(tmp).resolve(), ["fixture-secret"], display)
            progress.output = io.StringIO()
            progress.log("compiler says fixture-secret", detail=True)
            helper = publisher.HelperOutput(progress)
            helper.write("Safe helper ")
            helper.write("message\n")
            helper.flush()
            self.assertNotIn("compiler", console.getvalue())
            self.assertIn("compiler says [redacted]", progress.output.getvalue())
            self.assertIn("Safe helper message", console.getvalue())
            self.assertIn("Safe helper message", progress.output.getvalue())
            self.assertNotIn("fixture-secret", progress.output.getvalue())

    def test_display_removes_terminal_control_injection(self):
        self.assertEqual(text("\033[31mred\033[0m\a"), "red")
        self.assertEqual(duration(3661), "1:01:01")

    def test_watch_needs_no_credentials_and_performs_no_release_actions(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp).resolve()
            folder = root / "release"
            journal = root / "release.pipeline"
            journal.mkdir()
            (journal / "progress.json").write_text(json.dumps({
                "schema": 1, "stages": {name: {"status": "done", "seconds": 1} for name in publisher.STAGES}
            }))
            with patch("sys.stdout", new_callable=io.StringIO) as console, \
                 patch.object(publisher.d, "command") as command, \
                 patch.object(publisher, "execute") as execute:
                result = publisher.main(["--folder", str(folder), "--watch", "--plain"])
            self.assertEqual(result, 0)
            self.assertIn("Read-only saved progress", console.getvalue())
            self.assertIn("does not prove", console.getvalue())
            command.assert_not_called()
            execute.assert_not_called()

    def test_saved_progress_rejects_unknown_or_invalid_states(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp).resolve()
            path = root / "progress.json"
            for stages in ({"Unknown step": {"status": "done"}},
                           {publisher.STAGES[0]: {"status": "invalid"}},
                           {publisher.STAGES[0]: {"status": "running", "seconds": -1}}):
                path.write_text(json.dumps({"schema": 1, "stages": stages}))
                with self.subTest(stages=stages), self.assertRaises(publisher.d.DistributionError):
                    publisher.saved_progress(root)

    def test_main_routes_helper_output_without_debug_noise_or_recursion(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp).resolve()
            def execute(args, settings, progress):
                with progress.stage("Preflight"):
                    progress.log("compiler detail", detail=True)
                    print("Helper completed.")
            with patch("sys.stdout", new_callable=io.StringIO) as console, \
                 patch.object(publisher, "config", return_value={
                     # config() always returns the homepage; main() prints it before running.
                     "notary_key": "private.p8", "sparkle_key": "private.key", "homepage": "https://example.test"}), \
                 patch.object(publisher, "execute", side_effect=execute):
                result = publisher.main(["--config", str(root / "config.json"),
                                         "--folder", str(root / "release"), "--execute", "--plain"])
            self.assertEqual(result, 0)
            self.assertIn("[OK", console.getvalue())
            self.assertEqual(console.getvalue().count("Helper completed."), 1)
            self.assertNotIn("compiler detail", console.getvalue())
            log = (root / "release.pipeline/pipeline.log").read_text()
            self.assertIn("compiler detail", log)
            self.assertIn("Helper completed.", log)


if __name__ == "__main__":
    unittest.main()
