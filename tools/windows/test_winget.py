#!/usr/bin/env python3
"""Tests for tools/windows/winget.py: the Windows release description and the winget manifests."""
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import winget  # noqa: E402


class WingetTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.folder = Path(self.temp.name)
        self.label, self.build = winget.shared_version()
        for architecture in winget.ARCHITECTURES:
            (self.folder / winget.zip_name(self.label, architecture)).write_bytes(architecture.encode())

    def tearDown(self):
        self.temp.cleanup()

    def test_describe_and_verify_bind_the_zips_to_the_shared_version_and_source(self):
        data = winget.describe(self.folder, "a" * 40)
        self.assertEqual((data["releaseVersion"], data["build"]), (self.label, self.build))
        self.assertEqual(sorted(row["architecture"] for row in data["files"]), ["arm64", "x64"])
        winget.verify(self.folder, "a" * 40)
        with self.assertRaises(winget.WingetError):
            winget.verify(self.folder, "b" * 40)

    def test_a_changed_zip_or_a_missing_architecture_is_refused(self):
        winget.describe(self.folder)
        (self.folder / winget.zip_name(self.label, "x64")).write_bytes(b"changed")
        with self.assertRaises(winget.WingetError):
            winget.verify(self.folder)
        (self.folder / winget.zip_name(self.label, "arm64")).unlink()
        (self.folder / winget.zip_name(self.label, "x64")).write_bytes(b"x64")
        winget.describe(self.folder)
        with self.assertRaises(winget.WingetError):
            winget.verify(self.folder)
        winget.verify(self.folder, require_arm64=False)

    def test_another_version_is_refused(self):
        data = winget.describe(self.folder)
        data["releaseVersion"] = "9.9.9"
        (self.folder / winget.DESCRIPTION_FILE).write_text(json.dumps(data))
        with self.assertRaises(winget.WingetError):
            winget.verify(self.folder)

    def test_manifests_install_the_zip_as_a_portable_app_from_the_release(self):
        data = winget.describe(self.folder)
        out = winget.manifests(data, "dorlugasigal/annotator", f"v{self.label}", self.folder / "out")
        self.assertEqual(out.relative_to(self.folder / "out").as_posix(), f"d/DorLugasiGal/Annotator/{self.label}")
        installer = (out / "DorLugasiGal.Annotator.installer.yaml").read_text()
        self.assertIn("InstallerType: zip\nNestedInstallerType: portable\n", installer)
        self.assertIn("PortableCommandAlias: annotator", installer)
        # Without it winget starts Annotator.exe through a link in another folder, where the Windows App SDK is missing.
        self.assertIn("\nArchiveBinariesDependOnPath: true\n", installer)
        for name in ("DorLugasiGal.Annotator.yaml", "DorLugasiGal.Annotator.installer.yaml", "DorLugasiGal.Annotator.locale.en-US.yaml"):
            text = (out / name).read_text()
            self.assertIn(f"\nManifestVersion: {winget.MANIFEST_VERSION}\n", text)
            self.assertIn(f".{winget.MANIFEST_VERSION}.schema.json\n", text)
        self.assertGreaterEqual(tuple(int(part) for part in winget.MANIFEST_VERSION.split(".")), (1, 9, 0))
        row = next(item for item in data["files"] if item["architecture"] == "x64")
        self.assertIn(f"https://github.com/dorlugasigal/annotator/releases/download/v{self.label}/{row['file']}", installer)
        self.assertIn(row["sha256"].upper(), installer)
        for name in ("DorLugasiGal.Annotator.yaml", "DorLugasiGal.Annotator.locale.en-US.yaml"):
            text = (out / name).read_text()
            self.assertIn(f"PackageVersion: {self.label}\n", text)
        locale = (out / "DorLugasiGal.Annotator.locale.en-US.yaml").read_text()
        self.assertIn("License: Apache-2.0\nLicenseUrl: https://github.com/dorlugasigal/annotator/blob/main/LICENSE\n", locale)
        with self.assertRaises(winget.WingetError):
            winget.manifests(data, "dorlugasigal/annotator", "v0.0.1", self.folder / "out")


if __name__ == "__main__":
    unittest.main()
