# Publishing from a Mac

The Release workflow is the normal route. When GitHub Actions can't run, a
maintainer with the signing credentials can publish the Mac release from a Mac
with `tools/release/publish_release.py`. It runs the same stages: build and
sign, notarize, staple, disk image, Windows files, GitHub release, Homebrew and
the website.

## Before the first run

- Full Xcode, selected for the command:
  `export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer`.
  The xcodebuild step fails with only the Command Line Tools.
- `npm --prefix services/metrics ci` and `npm --prefix website ci`.
- The disk image tools:
  `python3 -m venv build/dmg-tools && build/dmg-tools/bin/pip install --require-hashes -r tools/release/dmg-requirements.txt`.
- Eject any mounted Annotator disk image.
- A config file at `build/local-publish-config.json` (gitignored, mode 600).
  Required keys: `identity`, `team`, `public_key`, `notary_key`, `key_id`,
  `issuer`, `sparkle_key`, `repository`, `homepage` and `github_user`.
  Optional: `tap_repository`, `website_repository`, `netlify_site_id`,
  `legacy_site_url`, `metrics_url` and `support_url`. `notary_key` and
  `sparkle_key` are paths to the key files, kept outside the repository; the
  file itself never holds a token.

## Run

```sh
python3 tools/release/publish_release.py --config build/local-publish-config.json --folder build/release-<label>            # plan
python3 tools/release/publish_release.py --config build/local-publish-config.json --folder build/release-<label> --execute
python3 tools/release/publish_release.py --folder build/release-<label> --watch                                              # read-only progress
```

Without `--execute` it only prints the plan. Rerun the same command to resume;
saved stages are reused, so Apple never receives the same build twice. Don't
edit the checkout while a release runs; use another worktree for edits.

The Windows ZIPs come from `<folder>/windows` if you copied them there, or
from the `windows-portable` artifact of a finished Release run of the same
commit. Store submissions use `tools/windows/publish_windows.py` on a Windows
PC ([windows-store.md](windows-store.md)).
