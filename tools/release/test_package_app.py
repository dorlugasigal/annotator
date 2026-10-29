import importlib.util
import plistlib
import tempfile
import unittest
from unittest.mock import patch
from pathlib import Path

spec = importlib.util.spec_from_file_location("package_app", Path(__file__).with_name("package-app.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

INFO = dict(CFBundleIdentifier="com.dorlugasigal.annotator", CFBundleShortVersionString="0.3.2",
            CFBundleVersion="302", LSMinimumSystemVersion="14.0")


def fixture_app(directory, info=INFO):
    app = Path(directory) / "Annotator.app"
    (app / "Contents/MacOS").mkdir(parents=True, exist_ok=True)
    (app / "Contents/MacOS/Annotator").write_bytes(b"fixture")
    (app / "Contents/Info.plist").write_bytes(plistlib.dumps(info))
    return app


class PackageTests(unittest.TestCase):
    def test_the_app_has_a_plain_version_and_its_derived_build(self):
        with tempfile.TemporaryDirectory() as directory:
            app = fixture_app(directory)
            metadata = module.inspect_app(app)
            self.assertEqual((metadata["version"], metadata["build"], metadata["releaseVersion"]), ("0.3.2", "302", "0.3.2"))
            self.assertNotIn("prerelease", metadata)
            for change in [dict(CFBundleVersion="5"), dict(CFBundleVersion='#{system("bad")}'), dict(AnnotatorPrerelease="beta.1"),
                           dict(CFBundleShortVersionString="0.3.2-beta.1"), dict(CFBundleShortVersionString="01.3.2"),
                           dict(CFBundleShortVersionString="0.3"), dict(CFBundleShortVersionString="0.3.2.1"),
                           dict(CFBundleIdentifier="com.dorlugasigal.annotator.dev")]:
                fixture_app(directory, {**INFO, **change})
                with self.subTest(change=change), self.assertRaises(module.PackagingError):
                    module.inspect_app(app)

    def test_archive_names_carry_the_version_and_legacy_names_kept_their_build(self):
        self.assertEqual(module.archive_name("annotator", "0.2.0", "200", "arm64 x86_64", "zip"), "annotator-0.2.0-universal.zip")
        self.assertEqual(module.archive_name("annotator", "0.2.0", "200", "arm64", "dmg"), "annotator-0.2.0-arm64.dmg")
        self.assertEqual(module.archive_name("annotator", "0.1.0-beta.17", "35", "arm64 x86_64", "zip"),
                         "annotator-0.1.0-beta.17-35-universal.zip")

    def test_the_archive_url_must_name_the_versioned_archive(self):
        with tempfile.TemporaryDirectory() as directory:
            app = fixture_app(directory)
            # The archive name carries the build's architectures: arm64, or universal for Apple Silicon and Intel.
            for architecture, suffix in (("arm64", "arm64"), ("arm64 x86_64", "universal")):
                with self.subTest(architecture=architecture), \
                     patch.object(module, "architectures", return_value=architecture), \
                     self.assertRaisesRegex(module.PackagingError, rf"annotator-0\.3\.2-{suffix}\.zip"):
                    module.package(app, Path(directory) / "output",
                                   "https://example.com/annotator-0.3.2-302-arm64.zip", "https://example.com")
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

    def test_the_cask_is_pinned_updates_itself_and_keeps_user_data(self):
        metadata = dict(name="Annotator.app", identifier="com.dorlugasigal.annotator",
                        token="annotator", version="0.3.2", build="302", releaseVersion="0.3.2")
        silicon = module.cask(metadata, "https://example.com/a.zip", "a"*64, "https://example.com")
        universal = module.cask({**metadata, "architecture": "arm64 x86_64"}, "https://example.com/a.zip", "a"*64,
                                "https://example.com")
        self.assertIn('version "0.3.2"\n', silicon)
        self.assertIn('sha256 "' + "a"*64 + '"', silicon)
        self.assertIn('app "Annotator.app"', silicon)
        self.assertIn('homepage "https://example.com"\n  auto_updates true\n\n  depends_on arch: :arm64\n  depends_on macos: :sonoma\n', silicon)
        self.assertNotIn("depends_on arch", universal)
        self.assertIn('homepage "https://example.com"\n  auto_updates true\n\n  depends_on macos: :sonoma\n', universal)
        for output in (silicon, universal):
            for forbidden in ["conflicts_with", "prerelease", "Testing", "no_check", "zap ", "system_command", "sudo",
                              "quarantine", "launchctl"]:
                self.assertNotIn(forbidden, output)
        legacy = module.cask({**metadata, "version": "0.1.0", "build": "35", "releaseVersion": "0.1.0-beta.17"},
                             "https://example.com/a.zip", "a"*64, "https://example.com")
        self.assertIn('version "0.1.0-beta.17,35"', legacy)

    def test_release_urls_cannot_embed_secrets_or_mutable_signed_queries(self):
        for url in ["http://host/app.zip", "******host/app.zip",
                    "https://host/app.zip?token=secret", "https://host/app.zip#x",
                    "https://host/app.dmg", "https://host/\napp.zip"]:
            with self.assertRaises(module.PackagingError):
                module.https_url(url, archive=True)
        self.assertEqual(module.https_url("https://downloads.example.com/annotator-0.3.2-arm64.zip", True),
                         "https://downloads.example.com/annotator-0.3.2-arm64.zip")

    def test_ruby_interpolation_is_escaped(self):
        metadata = dict(name="Annotator.app", identifier="com.dorlugasigal.annotator",
                        token="annotator", version="0.3.2", build="302")
        output = module.cask(metadata, "https://example.com/#{not_code}.zip", "a"*64, "https://example.com")
        self.assertIn(r"\#{not_code}", output)

    def test_public_packaging_refuses_development_signing(self):
        with tempfile.TemporaryDirectory() as directory:
            app = fixture_app(directory)
            def commands(*args):
                if args[0] == "lipo": return "arm64"
                if args[:2] == ("codesign", "-d"): return "Authority=Apple Development: Example"
                return ""
            with patch.object(module, "run", side_effect=commands):
                with self.assertRaisesRegex(module.PackagingError, "Developer ID"):
                    module.package(app, Path(directory)/"output",
                                   "https://downloads.example.com/annotator-0.3.2-arm64.zip", "https://example.com")
            self.assertFalse((Path(directory)/"output").exists())


if __name__ == "__main__":
    unittest.main()
