# Tools

Run every tool from the repository root.

| Folder | What it holds |
| --- | --- |
| `release/` | Version checks, changelog, packaging, signing, publishing and the website deploy. `python3 tools/release/release.py check` validates the release notes. |
| `macos/` | Mac build and measurement helpers: signed development builds, benchmarks, the UX reference packer and the pinned Xcode toolchain. |
| `windows/` | Windows validation (`validate.ps1`), packaging, live and dock checks, winget and the Store publisher, and the pinned .NET toolchain. |
| `ci/` | The CI path filter, the local validation runner and CI guard tests. |
| `artwork/` | Generators and sources for the system shapes and the Rough.js reference coordinates. |
| `demo/` | Recording, rendering and encoding helpers for website clips and promo videos. Clone `dorlugasigal/annotator-media` into `videos/` first. |

The Mac's Swift tools (`ConformanceExport`, `AnnotatorDiagnostics`) are package
targets in `apps/macos/Tools`. Run them with `make conformance` or
`swift run --package-path apps/macos <name>`.

Tests sit next to the code they test as `test_*.py`. Run one with
`python3 tools/<folder>/test_<name>.py`.
