"""Exercise the setup prompt with a synthetic passphrase, never a real secret."""
import os
from pathlib import Path
import pty
import select
import subprocess
import time
import unittest


class PasswordPromptTests(unittest.TestCase):
    def test_passphrase_is_not_echoed(self):
        root = Path(__file__).resolve().parents[2]
        master, slave = pty.openpty()
        process = subprocess.Popen(["node", "scripts/hash-password.ts"],
                                   cwd=root / "services/metrics", stdin=slave,
                                   stdout=subprocess.PIPE, stderr=slave)
        os.close(slave)
        output = b""
        try:
            deadline = time.monotonic() + 10
            while b"characters):" not in output and time.monotonic() < deadline:
                if select.select([master], [], [], 0.2)[0]:
                    output += os.read(master, 4096)
            self.assertIn(b"characters):", output)
            fixture = b"synthetic-passphrase-not-a-secret"
            os.write(master, fixture + b"\r")
            deadline = time.monotonic() + 10
            while process.poll() is None and time.monotonic() < deadline:
                if select.select([master], [], [], 0.1)[0]:
                    try:
                        output += os.read(master, 4096)
                    except OSError:
                        break
            self.assertEqual(process.wait(timeout=2), 0)
            encoded = process.stdout.read()
            self.assertTrue(encoded.startswith(b"scrypt$"))
            self.assertNotIn(fixture, output)
            self.assertNotIn(fixture, encoded)
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=5)
            process.stdout.close()
            os.close(master)



SITE = "00000000-1111-4222-8333-444444444444"
FIXTURE = "synthetic-passphrase-not-a-secret"


class SetAdminPasswordTests(unittest.TestCase):
    """The guided command, with a fake Netlify CLI, a fake publisher and a temporary Git checkout."""

    def setUp(self):
        import json
        import shutil
        import tempfile
        self.metrics = Path(__file__).resolve().parents[2] / "services/metrics"
        self.temp = Path(tempfile.mkdtemp(prefix="set-admin-password-"))
        self.addCleanup(shutil.rmtree, self.temp, ignore_errors=True)
        self.root = self.temp / "repo"
        (self.root / "build").mkdir(parents=True)
        (self.root / "tools/release").mkdir(parents=True)
        (self.root / "build/local-publish-config.json").write_text(json.dumps(
            {"netlify_site_id": SITE, "homepage": "https://example.netlify.app"}))
        self.deploy_status = 0
        (self.root / "tools/release/publish_website.py").write_text(
            "import json, os, sys\n"
            "open(os.environ['FAKE_DEPLOY_LOG'], 'w').write(json.dumps(sys.argv[1:]))\n"
            "sys.exit(int(os.environ.get('FAKE_DEPLOY_STATUS', '0')))\n")
        (self.root / ".gitignore").write_text("build/\n")
        git = ["git", "-C", str(self.root), "-c", "user.name=Test", "-c", "user.email=test@example.invalid"]
        subprocess.run(git[:3] + ["init", "-q"], check=True)
        subprocess.run(git + ["add", "."], check=True)
        subprocess.run(git + ["commit", "-q", "-m", "fixture"], check=True)
        self.cli = self.temp / "netlify"
        self.cli.write_text("#!/usr/bin/env python3\nimport json, os, sys\n"
                            "open(os.environ['FAKE_CLI_LOG'], 'a').write(json.dumps(sys.argv[1:]) + '\\n')\n")
        self.cli.chmod(0o755)
        self.secrets = self.temp / "secrets"
        self.secrets.mkdir()
        self.backup = self.secrets / f"metrics-{SITE}.json"
        self.backup.write_text(json.dumps({"schemaVersion": 1, "siteId": SITE, "adminPassword": "old-generated-value"}))
        self.backup.chmod(0o600)

    def run_command(self, answers, deploy_status=0):
        env = {**os.environ, "ANNOTATOR_REPO_ROOT": str(self.root), "ANNOTATOR_NETLIFY_CLI": str(self.cli),
               "ANNOTATOR_RELEASE_SECRETS_DIR": str(self.secrets), "FAKE_CLI_LOG": str(self.temp / "cli.log"),
               "FAKE_DEPLOY_LOG": str(self.temp / "deploy.log"), "FAKE_DEPLOY_STATUS": str(deploy_status)}
        master, slave = pty.openpty()
        process = subprocess.Popen(["node", "scripts/set-admin-password.ts"], cwd=self.metrics, env=env,
                                   stdin=slave, stdout=slave, stderr=slave)
        os.close(slave)
        output = b""
        try:
            for prompt, answer in answers:
                deadline = time.monotonic() + 15
                while prompt not in output and process.poll() is None and time.monotonic() < deadline:
                    if select.select([master], [], [], 0.1)[0]:
                        try:
                            output += os.read(master, 4096)
                        except OSError:
                            break
                self.assertIn(prompt, output)
                os.write(master, answer + b"\r")
            deadline = time.monotonic() + 20
            while process.poll() is None and time.monotonic() < deadline:
                if select.select([master], [], [], 0.1)[0]:
                    try:
                        output += os.read(master, 4096)
                    except OSError:
                        break
            while select.select([master], [], [], 0.1)[0]:
                try:
                    chunk = os.read(master, 4096)
                except OSError:
                    break
                if not chunk:
                    break
                output += chunk
            return process.wait(timeout=5), output
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=5)
            os.close(master)

    def calls(self):
        import json
        log = self.temp / "cli.log"
        return [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []

    def test_sets_the_hash_redeploys_and_removes_the_old_generated_passphrase(self):
        import json
        fixture = FIXTURE.encode()
        status, output = self.run_command([(b"characters): ", fixture), (b"again: ", fixture), (b"[y/N] ", b"y")])
        self.assertEqual(status, 0, output.decode(errors="replace"))
        self.assertNotIn(fixture, output)
        calls = self.calls()
        self.assertEqual([call[:2] for call in calls], [["env:set", "ADMIN_PASSWORD_HASH"], ["env:set", "ADMIN_SESSION_SECRET"]])
        for call in calls:
            self.assertEqual(call[3:], ["--context", "production", "--secret", "--site", SITE])
            self.assertNotIn(FIXTURE, call[2])
        self.assertTrue(calls[0][2].startswith("scrypt$"))
        self.assertGreaterEqual(len(calls[1][2]), 32)
        # The saved hash accepts the passphrase that was typed.
        check = subprocess.run(["node", "--input-type=module", "-e",
                                "import {verifyPassword} from './src/auth.ts';"
                                "process.exit(verifyPassword(process.argv[1], process.argv[2]) ? 0 : 1)",
                                FIXTURE, calls[0][2]], cwd=self.metrics)
        self.assertEqual(check.returncode, 0)
        self.assertEqual(json.loads((self.temp / "deploy.log").read_text()),
                         ["--config", "build/local-publish-config.json", "--execute"])
        backup = json.loads(self.backup.read_text())
        self.assertNotIn("adminPassword", backup)
        self.assertEqual(backup["siteId"], SITE)
        self.assertIn("adminPasswordChangedAt", backup)
        self.assertEqual(self.backup.stat().st_mode & 0o777, 0o600)
        self.assertIn(b"Sign in at https://example.netlify.app/admin/", output)

    def test_mismatched_passphrases_change_nothing(self):
        status, output = self.run_command([(b"characters): ", FIXTURE.encode()), (b"again: ", b"a-different-passphrase")])
        self.assertEqual(status, 1)
        self.assertIn(b"The two passphrases differ. Nothing was changed.", output)
        self.assertEqual(self.calls(), [])
        self.assertFalse((self.temp / "deploy.log").exists())
        self.assertIn("adminPassword", self.backup.read_text())

    def test_a_six_digit_code_is_accepted_and_five_characters_are_not(self):
        status, output = self.run_command([(b"characters): ", b"12345")])
        self.assertEqual(status, 1)
        self.assertIn(b"Use a passphrase with 6 to 256 characters. Nothing was changed.", output)
        self.assertEqual(self.calls(), [])
        status, output = self.run_command([(b"characters): ", b"482913"), (b"again: ", b"482913"), (b"[y/N] ", b"")])
        self.assertEqual(status, 0, output.decode(errors="replace"))
        self.assertNotIn(b"482913", output)
        self.assertEqual([call[1] for call in self.calls()], ["ADMIN_PASSWORD_HASH"])

    def test_ctrl_c_at_a_prompt_cancels_without_changes(self):
        status, output = self.run_command([(b"characters): ", b"\x03")])
        self.assertEqual(status, 1)
        self.assertIn(b"Cancelled. Nothing was changed.", output)
        self.assertEqual(self.calls(), [])
        self.assertFalse((self.temp / "deploy.log").exists())
        self.assertIn("adminPassword", self.backup.read_text())

    def test_a_dirty_checkout_stops_before_asking(self):
        (self.root / "notes.txt").write_text("uncommitted")
        status, output = self.run_command([])
        self.assertEqual(status, 1)
        self.assertIn(b"Commit or stash your changes first", output)
        self.assertNotIn(b"characters):", output)
        self.assertEqual(self.calls(), [])

    def test_a_failed_redeploy_keeps_the_backup_and_says_how_to_finish(self):
        fixture = FIXTURE.encode()
        status, output = self.run_command([(b"characters): ", fixture), (b"again: ", fixture), (b"[y/N] ", b"")],
                                          deploy_status=1)
        self.assertEqual(status, 1)
        self.assertEqual([call[1] for call in self.calls()], ["ADMIN_PASSWORD_HASH"])
        self.assertIn(b"the redeploy failed, so the old one still works", output)
        self.assertIn(b"tools/release/publish_website.py --config build/local-publish-config.json --execute", output)
        self.assertIn("adminPassword", self.backup.read_text())


if __name__ == "__main__":
    unittest.main()
