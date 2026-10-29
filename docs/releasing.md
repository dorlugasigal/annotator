# Releasing

Annotator ships Mac and Windows together, under one version. A release is not
done until every channel below has the new build.

## Version

Every release has a plain semantic version, `MAJOR.MINOR.PATCH`, such as
`0.2.0`. There are no alpha, beta or release candidate versions, and the app
shows only the version. Releases up to `0.1.0-beta.17` had pre-release labels
and counted build numbers; their notes, tags and files keep them.

| Change | Next version | Example |
| --- | --- | --- |
| Fixes only | Patch | `0.2.0` to `0.2.1` |
| New features | Minor | `0.2.1` to `0.3.0` |
| Breaking change, such as a board file format older apps can't open | Major (minor before 1.0.0) | `1.4.2` to `2.0.0` |

The version lives in `apps/macos/project.yml`, and the Windows app reads it
through `apps/windows/src/Annotator.App/SharedVersion.props`. Two numbers come
from it, and nobody counts them by hand:

- **Build** (`CURRENT_PROJECT_VERSION`): `major * 10000 + minor * 100 + patch`,
  so `0.2.0` is `200`. Sparkle, What's changed and the portable Windows update
  check compare it, and it is above every older build (the last was `35`).
  Keep minor and patch at 99 or lower.
- **Windows package** (WIN-DST-04): `(major + 1).minor.patch.0`, so `0.2.0`
  is `1.2.0.0`, above `1.1.35.0`, the last package before it.

`python3 tools/release/release.py plan --change fix` (or `feature`,
`breaking`, `docs`) suggests the next version and changes nothing.
`python3 tools/release/release.py prepare --version X.Y.Z` writes it with its
build, moves `unreleased` into a dated entry and renders `CHANGELOG.md`; then
run `make generate`. Never edit the generated Xcode project or Info.plist, and
never reuse a version or replace a published file. Board file schema numbers
change only when the format changes, never to match the app version.

## Release notes

`releases/notes.json` is the source of the changelog, the GitHub release text
and the app's **What's changed** view.

- Add each user-visible change to `unreleased` in the same pull request.
- Never edit an entry that has shipped.
- An entry may add `platforms.windows` with its own `changes` and
  `knownIssues`; the Windows app then shows those instead.
- Render the changelog with `python3 tools/release/release.py changelog --write`
  and commit `CHANGELOG.md`. `python3 tools/release/release.py check` must pass.

Unreleased items are not shipped. Do not describe them as available.

## Channels

Every release ships all of these:

| Channel | What it gets | How |
| --- | --- | --- |
| GitHub release | Mac DMG and ZIP; Windows ZIPs for x64 and ARM64 with `annotator-windows.json` | Release workflow, publish job |
| Sparkle feed | `updates/stable.xml`: the signed Mac update, with the release's Mac notes in its update window | Website deploy, from the published release manifest |
| Windows update feed | `updates/windows-stable.json`: the newest release with Windows ZIPs, which portable copies check daily (they never install it) | Website deploy, from the published `annotator-windows.json` |
| Homebrew | `brew install --cask dorlugasigal/annotator/annotator`; the cask says `auto_updates true`, so `brew upgrade` leaves Sparkle's updates alone unless asked with `--greedy` | Release workflow, publish job: `tools/release/distribution.py tap` writes `dorlugasigal/homebrew-annotator` once the new release is listed |
| winget | `winget install DorLugasiGal.Annotator` (portable ZIP) | `tools/windows/winget.py manifests`, then a pull request to microsoft/winget-pkgs |
| Microsoft Store | Signed MSIX bundle; Store updates | Release workflow: every release goes to the public listing (`windows_store` is `public` by default) |
| Website | https://getannotator.app | Website workflow, after each Release run ([maintainers/website.md](maintainers/website.md)) |

`releases/windows.json` decides whether the Release workflow builds Windows.
It is `partOfReleases: true` now: every run that signs the Mac release builds
the Windows packages from the same commit.

## Update feeds

Every copy reads `updates/stable.xml` (Mac) or `updates/windows-stable.json`
(portable Windows). Copies of `0.1.0-beta.17` and earlier read
`updates/preview.xml` and `updates/windows-preview.json`; the website writes
those with the same newest release, so they update too. Keep writing them
while those copies are around.

## Checklist

1. **Review**: list the changes since the last release and agree on the next
   version with the maintainer (`release.py plan`).
2. **Prepare**: in one pull request, run
   `python3 tools/release/release.py prepare --version X.Y.Z`, review the new
   entry's known limits, and run `make generate`. A release pull request
   changes only the version, the Xcode project's version lines,
   `releases/notes.json` and `CHANGELOG.md`, so it needs no issue.
3. **Validate** on a Mac:

   ```sh
   make test
   swift test --package-path apps/macos -c release
   make signed-native-test
   make conformance-check
   python3 tools/release/release.py check
   python3 tools/ci/validate_local.py browser
   ```

   And on Windows: `pwsh -NoProfile -File tools/windows/validate.ps1 all`.
4. **Merge** the release pull request. Releases build only from `main`, so the
   merged commit is the release: the Release run checks that it is clean and
   that its version and notes agree (`release.py check --candidate`), and the
   published tag `vX.Y.Z` and manifest record it. Releases up to
   0.1.0-beta.13 also recorded it as the entry's `source`; new entries don't.
5. **Run Release**: Actions > Release > Run workflow on `main`, with the
   version, the SHA of the head of `main`, and `notarize` and `publish` on.
   `windows_store` stays `public`: every release goes to the Store. The run
   publishes a normal GitHub release, marked latest. See
   [maintainers/release-workflow.md](maintainers/release-workflow.md).
6. **Website**: check that the Website workflow that follows the release
   finished, so the site and the update feeds show it
   ([maintainers/website.md](maintainers/website.md)).
7. **winget**: open the manifest pull request to microsoft/winget-pkgs
   ([maintainers/winget.md](maintainers/winget.md)).
8. **Check** each channel: download the DMG and both ZIPs, run
   `brew upgrade --cask annotator`, check the website and the Store listing.

Only a maintainer with the release secrets can run steps 5 to 7. Anyone can
prepare steps 1 to 3 in a pull request.
