import importlib.util
import json
import plistlib
import tempfile
import unittest
from unittest.mock import patch
from pathlib import Path

spec = importlib.util.spec_from_file_location("package_app", Path(__file__).with_name("package-app.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class PackageTests(unittest.TestCase):
    def test_prerelease_metadata_and_numeric_bundle_version(self):
        with tempfile.TemporaryDirectory() as directory:
            app = Path(directory) / "Annotator Dev.app"
            (app / "Contents/MacOS").mkdir(parents=True)
            (app / "Contents/MacOS/Annotator Dev").write_bytes(b"fixture")
            info = dict(CFBundleIdentifier="com.dorlugasigal.annotator.dev",
                        CFBundleShortVersionString="0.1.0", CFBundleVersion="9",
                        LSMinimumSystemVersion="14.0")
            plist = app / "Contents/Info.plist"
            for phase in ["", "alpha.1", "alpha.12", "beta.1", "rc.2"]:
                info["AnnotatorPrerelease"] = phase
                plist.write_bytes(plistlib.dumps(info))
                metadata = module.inspect_app(app, "preview")
                expected = "0.1.0" + ("-" + phase if phase else "")
                self.assertEqual(metadata["releaseVersion"], expected)
                self.assertEqual(metadata["version"], "0.1.0")
                self.assertEqual(metadata["prerelease"], phase)
                output = module.cask(metadata, "file:///tmp/app.zip", "a"*64, "https://example.com", "preview")
                self.assertIn(f'version "{expected},9"', output)
            for phase in ["alpha", "rc", "alpha.0", "alpha.01", "nightly.1", "beta.-1", "$(ANNOTATOR_PRERELEASE)", 1]:
                info["AnnotatorPrerelease"] = phase
                plist.write_bytes(plistlib.dumps(info))
                with self.assertRaises(module.PackagingError):
                    module.inspect_app(app, "preview")
            info["AnnotatorPrerelease"] = "alpha.1"
            for version in ["0.1.0-alpha.1", "01.1.0", "0.1", "0.1.0.1", "0.1.-1", "0.1.0+9"]:
                info["CFBundleShortVersionString"] = version
                plist.write_bytes(plistlib.dumps(info))
                with self.assertRaises(module.PackagingError):
                    module.inspect_app(app, "preview")

    def test_prerelease_archive_url_must_include_stage_and_build(self):
        with tempfile.TemporaryDirectory() as directory:
            app = Path(directory) / "Annotator.app"
            (app / "Contents/MacOS").mkdir(parents=True)
            (app / "Contents/MacOS/Annotator").write_bytes(b"fixture")
            (app / "Contents/Info.plist").write_bytes(plistlib.dumps(dict(
                CFBundleIdentifier="com.dorlugasigal.annotator",
                CFBundleShortVersionString="0.1.0", CFBundleVersion="9",
                AnnotatorPrerelease="alpha.1", LSMinimumSystemVersion="14.0")))
            # The archive name carries the build's architectures: arm64, or universal for Apple Silicon and Intel.
            for architecture, suffix in (("arm64", "arm64"), ("arm64 x86_64", "universal")):
                with self.subTest(architecture=architecture), \
                     patch.object(module, "architectures", return_value=architecture), \
                     self.assertRaisesRegex(module.PackagingError, rf"annotator-0\.1\.0-alpha\.1-9-{suffix}\.zip"):
                    module.package(app, Path(directory) / "output", "release",
                                   "https://example.com/annotator-0.1.0-9-arm64.zip", "https://example.com")
            self.assertFalse((Path(directory) / "output").exists())

    def test_architectures_accept_apple_silicon_or_universal_only(self):
        app, metadata = Path("/unused/Annotator.app"), dict(name="Annotator.app")
        for output, expected in (("arm64\n", "arm64"), ("x86_64 arm64\n", "arm64 x86_64")):
            with self.subTest(output=output), patch.object(module, "run", return_value=output):
                self.assertEqual(module.architectures(app, metadata), expected)
        for output in ("x86_64\n", "arm64e\n", "arm64 arm64e x86_64\n"):
            with self.subTest(output=output), patch.object(module, "run", return_value=output), \
                 self.assertRaisesRegex(module.PackagingError, "universal"):
                module.architectures(app, metadata)

    def test_universal_cask_installs_on_intel_and_apple_silicon(self):
        metadata = dict(name="Annotator.app", identifier="com.dorlugasigal.annotator",
                        token="annotator", version="0.3.2", build="5")
        silicon = module.cask(metadata, "https://example.com/a.zip", "a"*64, "https://example.com", "release")
        universal = module.cask({**metadata, "architecture": "arm64 x86_64"}, "https://example.com/a.zip", "a"*64,
                                "https://example.com", "release")
        self.assertIn('homepage "https://example.com"\n\n  depends_on arch: :arm64\n  depends_on macos: :sonoma\n', silicon)
        self.assertNotIn("depends_on arch", universal)
        self.assertIn('homepage "https://example.com"\n\n  depends_on macos: :sonoma\n', universal)

    def test_preview_cask_is_pinned_and_does_not_delete_user_data(self):
        metadata = dict(name="Annotator Dev.app", identifier="com.dorlugasigal.annotator.dev",
                        token="annotator-dev", version="0.3.2", build="5")
        output = module.cask(metadata, "file:///tmp/annotator-dev.zip", "a"*64,
                             "https://github.com/dorlugasigal/annotator", "preview")
        self.assertIn('version "0.3.2,5"', output)
        self.assertIn('sha256 "'+"a"*64+'"', output)
        self.assertIn('app "Annotator Dev.app"', output)
        self.assertIn("not notarized", output)
        for forbidden in ["no_check", "zap ", "system_command", "sudo", "quarantine", "launchctl"]:
            self.assertNotIn(forbidden, output)

    def test_release_urls_cannot_embed_secrets_or_mutable_signed_queries(self):
        for url in ["http://host/app.zip", "https://user:secret@host/app.zip",
                    "https://host/app.zip?token=secret", "https://host/app.zip#x",
                    "https://host/app.dmg", "https://host/\napp.zip"]:
            with self.assertRaises(module.PackagingError):
                module.https_url(url, archive=True)
        self.assertEqual(module.https_url("https://downloads.example.com/annotator-0.3.2-5-arm64.zip", True),
                         "https://downloads.example.com/annotator-0.3.2-5-arm64.zip")

    def test_app_identity_version_and_channel_are_validated_before_packaging(self):
        with tempfile.TemporaryDirectory() as directory:
            app = Path(directory)/"Annotator Dev.app"
            (app/"Contents/MacOS").mkdir(parents=True)
            (app/"Contents/MacOS/Annotator Dev").write_bytes(b"fixture")
            info = dict(CFBundleIdentifier="com.dorlugasigal.annotator.dev",
                        CFBundleShortVersionString="0.3.2", CFBundleVersion="5",
                        LSMinimumSystemVersion="14.0")
            (app/"Contents/Info.plist").write_bytes(plistlib.dumps(info))
            self.assertEqual(module.inspect_app(app,"preview")["version"],"0.3.2")
            with self.assertRaises(module.PackagingError):
                module.inspect_app(app,"release")
            info["CFBundleVersion"] = '#{system("bad")}'
            (app/"Contents/Info.plist").write_bytes(plistlib.dumps(info))
            with self.assertRaises(module.PackagingError):
                module.inspect_app(app,"preview")

    def test_ruby_interpolation_is_escaped(self):
        metadata = dict(name="Annotator.app", identifier="com.dorlugasigal.annotator",
                        token="annotator", version="0.3.2", build="5")
        output = module.cask(metadata, "https://example.com/#{not_code}.zip", "a"*64,
                             "https://example.com", "release")
        self.assertIn(r"\#{not_code}", output)

    def test_public_packaging_refuses_development_signing(self):
        with tempfile.TemporaryDirectory() as directory:
            app = Path(directory)/"Annotator.app"
            (app/"Contents/MacOS").mkdir(parents=True)
            (app/"Contents/MacOS/Annotator").write_bytes(b"fixture")
            (app/"Contents/Info.plist").write_bytes(plistlib.dumps({
                "CFBundleIdentifier":"com.dorlugasigal.annotator",
                "CFBundleShortVersionString":"0.3.2","CFBundleVersion":"5",
                "LSMinimumSystemVersion":"14.0",
            }))
            def commands(*args):
                if args[0] == "lipo": return "arm64"
                if args[:2] == ("codesign","-d"): return "Authority=Apple Development: Example"
                return ""
            with patch.object(module,"run",side_effect=commands):
                with self.assertRaisesRegex(module.PackagingError,"Developer ID"):
                    module.package(app,Path(directory)/"output","release",
                                   "https://downloads.example.com/annotator-0.3.2-5-arm64.zip",
                                   "https://example.com")
            self.assertFalse((Path(directory)/"output").exists())


if __name__ == "__main__":
    unittest.main()
