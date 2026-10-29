import importlib
import plistlib
import sys
import tempfile
import types
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import disk_image


class DiskImageTests(unittest.TestCase):
    def setUp(self):
        # Fixture apps have no real executable; production reads their architectures with lipo.
        self.enterContext(patch.object(disk_image.packager, "architectures", return_value="arm64"))

    def app(self, root):
        app = root / "Annotator.app"
        (app / "Contents/MacOS").mkdir(parents=True)
        (app / "Contents/Resources").mkdir()
        (app / "Contents/MacOS/Annotator").write_bytes(b"fixture")
        (app / "Contents/Resources/AppIcon.icns").write_bytes(b"icon")
        (app / "Contents/Info.plist").write_bytes(plistlib.dumps({
            "CFBundleIdentifier": "com.dorlugasigal.annotator",
            "LSMinimumSystemVersion": "14.0",
            "CFBundleShortVersionString": "0.2.0",
            "CFBundleVersion": "200",
        }))
        return app

    def test_default_name_uses_release_version_and_build(self):
        with tempfile.TemporaryDirectory() as tmp:
            app = self.app(Path(tmp))
            self.assertEqual(disk_image.default_name(app), "annotator-0.2.0-arm64.dmg")
            with patch.object(disk_image.packager, "architectures", return_value="arm64 x86_64"):
                self.assertEqual(disk_image.default_name(app), "annotator-0.2.0-universal.dmg")

    def test_build_refuses_overwrite_and_passes_layout(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            app = self.app(root)
            out = root / "out"
            out.mkdir()
            background = root / "background.tiff"
            background.write_bytes(b"bg")
            fake = types.SimpleNamespace()
            calls = []
            def build_dmg(path, volume, settings):
                calls.append((Path(path), volume, settings))
                Path(path).write_bytes(b"dmg")
            fake.build_dmg = build_dmg
            tempdir_before = tempfile.tempdir
            with patch.dict(sys.modules, {"dmgbuild": fake}), \
                 patch.object(disk_image, "add_dmgbuild_path"), \
                 patch.object(disk_image, "background_tiff", return_value=background), \
                 patch.object(disk_image, "verify_contents") as verify, \
                 patch.object(disk_image, "command", return_value="") as command:
                report = disk_image.build(app, out, "-", verify=True)
            self.assertEqual(report["archive"], "annotator-0.2.0-arm64.dmg")
            self.assertEqual(report["volumeName"], "Annotator")
            self.assertEqual(report["iconLocations"], disk_image.ICON_LOCATIONS)
            self.assertEqual(calls[0][1], "Annotator")
            settings = calls[0][2]
            self.assertEqual(settings["symlinks"], {"Applications": "/Applications"})
            self.assertEqual(settings["icon_locations"], disk_image.ICON_LOCATIONS)
            self.assertEqual(settings["background"], str(background))
            self.assertEqual(settings["filesystem"], "HFS+")
            self.assertEqual(settings["format"], "UDZO")
            self.assertIn(".background.tiff", settings["hide"])
            self.assertTrue(any(call.args[:3] == ("codesign", "--force", "--sign") for call in command.call_args_list))
            verify.assert_called_once()
            # The image was built in a scratch folder beside the output and moved into place whole.
            self.assertNotEqual(calls[0][0].parent, out.resolve())
            self.assertEqual(calls[0][0].parent.parent, out.resolve())
            self.assertEqual(sorted(path.name for path in out.iterdir()), [report["archive"]])
            self.assertEqual((out / report["archive"]).read_bytes(), b"dmg")
            self.assertEqual(tempfile.tempdir, tempdir_before)
            with self.assertRaisesRegex(disk_image.DiskImageError, "already exists"):
                disk_image.build(app, out, verify=False)

    def test_missing_dmgbuild_names_setup_command(self):
        with tempfile.TemporaryDirectory() as tmp:
            app = self.app(Path(tmp))
            with patch.object(disk_image, "DMG_VENV", Path(tmp) / "missing"):
                with self.assertRaisesRegex(disk_image.DiskImageError, "pip install --require-hashes"):
                    disk_image.add_dmgbuild_path()


    def test_failed_tool_reports_its_last_message(self):
        with self.assertRaisesRegex(disk_image.DiskImageError, "hdiutil failed with exit code 1: resource busy"):
            with patch.object(disk_image.subprocess, "run", return_value=types.SimpleNamespace(
                    returncode=1, stdout="", stderr="hdiutil: detach: first line\nresource busy\n")):
                disk_image.command("hdiutil", "detach", "/Volumes/Annotator")

    def test_detach_retries_before_forcing(self):
        calls = []
        def fake(*args, stderr=False):
            calls.append(args)
            if "-force" not in args:
                raise disk_image.DiskImageError("busy")
            return ""
        with patch.object(disk_image, "command", side_effect=fake), patch.object(disk_image.time, "sleep"):
            disk_image.detach(Path("/Volumes/Annotator"))
        self.assertEqual(len(calls), 4)
        self.assertEqual(calls[-1], ("hdiutil", "detach", "-force", Path("/Volumes/Annotator")))


if __name__ == "__main__":
    unittest.main()
