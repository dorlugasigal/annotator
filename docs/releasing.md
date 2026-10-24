# Releasing

Annotator ships Mac and Windows together, under one version. A release is not
done until every channel below has the new build.

## Version

The shared version lives in `apps/macos/project.yml`. Change it there, then run
`make generate`. Never edit the generated Xcode project or Info.plist.

| Setting | Example | Meaning |
| --- | --- | --- |
| `MARKETING_VERSION` | `0.1.0` | Numeric version. |
| `ANNOTATOR_PRERELEASE` | `beta.11` | `alpha.N`, `beta.N`, `rc.N`, or empty for stable. |
| `CURRENT_PROJECT_VERSION` | `29` | Build number. It always goes up and is never reused. |

The app shows `0.1.0-beta.11 (29)`. The Windows package version is
`(Major + 1).Minor.Build.0`, so `0.1.0` build 29 becomes `1.1.29.0`
(WIN-DST-04). Windows reads the same three settings through
`apps/windows/src/Annotator.App/SharedVersion.props`.

Stages:

- `alpha.N`: early testing; workflows may still change.
- `beta.N`: the planned scope is complete; wider testing.
- `rc.N`: expected to ship if final checks find no blocker.
- No suffix: stable. After stable, follow semantic versioning: patch for
  fixes, minor for features, major for breaking file-format changes.

Every new distributed binary gets a new label and a higher build number. Never
replace a published file or reuse a label. Board file schema numbers change
only when the format changes, never to match the app version.

`python3 tools/release/release.py plan --change fix` (or `--change feature
--stage beta`) proposes the next label and build without changing anything.

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
| Sparkle feed | The signed Mac update, with the release's Mac notes in its update window | Website deploy, from the published release manifest |
| Windows update feed | `updates/windows-<track>.json`: the newest release with Windows ZIPs, which portable copies check daily (they never install it) | Website deploy, from the published `annotator-windows.json` |
| Homebrew | `brew install --cask dorlugasigal/annotator/annotator`; the cask says `auto_updates true`, so `brew upgrade` leaves Sparkle's updates alone unless asked with `--greedy` | `tools/release/distribution.py tap` writes `dorlugasigal/homebrew-annotator` |
| winget | `winget install DorLugasiGal.Annotator` (portable ZIP) | `tools/windows/winget.py manifests`, then a pull request to microsoft/winget-pkgs |
| Microsoft Store | Signed MSIX bundle; Store updates | Release workflow, `windows_store` input |
| Website | https://getannotator.app | Website workflow, after each Release run ([maintainers/website.md](maintainers/website.md)) |

`releases/windows.json` decides whether the Release workflow builds Windows.
It is `partOfReleases: true` now: every run that signs the Mac release builds
the Windows packages from the same commit.

## Checklist

1. **Review**: list the changes since the last release and agree on the next
   label with the maintainer.
2. **Bump**: set the three version settings, move the approved `unreleased`
   items into a new entry with real known issues, run `make generate` and
   `python3 tools/release/release.py changelog --write`.
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
4. **Freeze**: merge the candidate to `main`. Record its SHA as the entry's
   `source` in a follow-up commit; `release.py check --require-frozen` rejects
   app source that changed after that SHA.
5. **Run Release**: Actions > Release > Run workflow on `main`, with the label,
   the candidate SHA, `notarize` and `publish` on, and `windows_store` set to
   `public` (or `testers` for a flight). See
   [maintainers/release-workflow.md](maintainers/release-workflow.md).
6. **Website**: check that the Website workflow that follows the release
   finished, so the site and the Sparkle feeds show it
   ([maintainers/website.md](maintainers/website.md)).
7. **winget**: open the manifest pull request to microsoft/winget-pkgs
   ([maintainers/winget.md](maintainers/winget.md)).
8. **Check** each channel: download the DMG and both ZIPs, run
   `brew upgrade --cask annotator`, check the website and the Store listing.

Only a maintainer with the release secrets can run steps 5 to 7. Anyone can
prepare steps 1 to 4 in a pull request.
