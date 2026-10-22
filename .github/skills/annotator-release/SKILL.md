---
name: annotator-release
description: Prepare and guide Annotator releases for macOS and the Windows edition. Use for local release readiness, version bumps, source freezing, signing/notarization handoff on a maintainer's Mac, the Windows package build, WACK and Microsoft Store handoff on a maintainer's Windows PC, progress monitoring, release recovery, Homebrew and website publication, or release notes. Prefer local checks and preserve exact artifacts. Ask before advancing versions/stages; do not infer production or Store authorization.
---

# Annotator release checkpoint

Run from the Annotator repository root. Keep this skill repository-local.
Do not install it into the user-facing computer-use skill or app resources.

Contributors can follow the review, release-notes and planning steps without
any secret. Signing, notarization, publishing, Store submissions and deploys
are maintainer actions; their setup is in `docs/maintainers/`.

Both platforms share one version and one set of release notes. The Mac steps
below run on a maintainer's Mac; the [Windows edition](#windows-edition)
section covers a maintainer's Windows PC and the Microsoft Store. On Windows, write
`python` where this skill writes `python3`.

## Inspect before recommending

1. Read `AGENTS.md`, `docs/releasing.md`, open release issues and
   `releases/notes.json`.
   For signing, publication, Homebrew, website or support-link work, also read
   `docs/maintainers/README.md`, `docs/maintainers/release-workflow.md`,
   `docs/maintainers/website.md` and `docs/maintainers/local-fallback.md`.
   For Windows packages or Store work, also read `releases/windows.json`,
   `docs/maintainers/winget.md`, `docs/maintainers/windows-store.md` and
   `docs/maintainers/release-workflow.md`.
   Use `.github/skills/annotator-local-validation/SKILL.md` for local checks.
   Never enable external gates
   just because validation or version approval passed.
2. Run `python3 tools/release/release.py check` and inspect the worktree.
3. Use the current release's `source` commit from the notes as the baseline.
   Inspect the log and diff since that commit, plus uncommitted files. If the
   source is unavailable, report that and identify an explicitly verified
   candidate commit; do not silently use HEAD or an arbitrary tag.
4. Separate behavior changes, compatible fixes, breaking changes, documentation
   and infrastructure-only changes. Read relevant code/specs; commit subjects
   alone are not evidence. Include regressions, actual test results, skipped
   tests and unresolved tester feedback.
5. Update only `unreleased` notes for verified user-visible changes, in plain
   English. Use `no-ai-slop` for copy review. Preserve recorded release entries.
   Run `python3 tools/release/release.py changelog --write` and `check`.

## Recommend and ask

Use `python3 tools/release/release.py plan --change fix` (or `feature`, `breaking`,
`docs`) to calculate numbering. This is read-only and does not judge readiness.
For a stage proposal add `--stage beta`, `rc` or `stable`; do not skip stages.
Consult the stage definitions and checklist in `docs/releasing.md`.

Present one short decision:

- Current version/build and verified baseline.
- Meaningful changes and the strongest remaining blocker.
- Recommendation: keep accumulating work, make the next candidate in the same
  stage, or promote one stage; include the exact proposed label/build.
- Timing: ready for another test build now, wait for specified feedback, or
  blocked by a named validation/installation requirement.

Ask with `ask_user`, offering **Keep current version**, **Prepare recommended
candidate**, and **Choose another target**. An explicit user request naming the
target already supplies version approval; do not ask for it again.
If approval is unavailable, leave a recommendation and Unreleased notes only.
Autopilot is not authorization to promote a stage. Do not repeat this prompt
after every small edit or after a maintainer already chose to wait.

## Prepare only after version approval

1. Verify the approved target against the read-only plan. Bump the three
   settings in `apps/macos/project.yml`; never edit generated files directly.
   Build numbers must exceed every existing release entry. Never reuse a label.
   These settings are the shared version. The Windows build reads the same
   file (`apps/windows/src/Annotator.App/SharedVersion.props`), so never set a
   Windows version anywhere else.
2. Move the applicable Unreleased items into a new notes entry with date and
   known issues. Mark its source `pending` until the candidate source is frozen;
   this is a preparation state, not a releasable record.
3. Regenerate with `make generate`; render the changelog and run the complete
   validation matrix in the pipeline guide. Fix errors, record skip reasons,
   and record actual results in the release pull request. `make generate` and the
   Mac matrix need the Mac; on the Windows PC, say that they are still owed
   rather than skipping them silently.
4. Checkpoint the source only with commit authorization. Replace `pending` in
   the new record with that source commit in a separate evidence change,
   without altering the candidate code. Run
   `python3 tools/release/release.py check --require-frozen` before packaging. A clean
   package commit may include that evidence change; record its exact SHA too.
5. Build the appropriate identity and signing channel. Verify the extracted
   signature, embedded version/stage/build, absence of test fixtures, source
   stamp and checksum. Use a fresh output directory. Never rename an old ZIP.
6. Run local validation first; the candidate's pull request then runs CI.
   Local and hosted results are distinct; do not claim an earlier run covers
   new source.
7. Generate release-update text with
   `python3 tools/release/release.py changelog --version VERSION`.
   Keep it local until distribution/publication is separately approved.
   Include known limits and install/signing status; omit
   credentials, local paths and personal data from public copy.
8. Update the test card and the release pull request. Preserve old artifacts, labels,
   history and tags. Report what is prepared versus actually installed or sent.

## Use the resumable local publishing flow (macOS)

Use `tools/release/publish_release.py` rather than recreating signing/upload commands.
Read its `--help` and the handoff before running it. Configure existing
identity and key-file paths locally; never put private key contents or tokens
in source, command arguments, a skill or chat.

1. Confirm `python3 tools/release/release.py check --require-frozen` passes from a clean
   candidate. A freeze records the exact source; it is not Apple approval.
   Never overwrite a published version or rewrite its historical source.
2. Plan without production actions:

   ```bash
   python3 tools/release/publish_release.py \
     --config build/local-publish-config.json \
     --folder "$HOME/Releases/annotator-VERSION"
   ```

3. For maintainer-authorized execution, a maintainer adds `--execute`. Respect current
   production-action restrictions: this skill does not grant permission to
   execute signing, submission, deployment or publication.
4. Use the live terminal display for stage statuses, not a guessed Apple
   percentage. Detailed output stays in the sibling `.pipeline/pipeline.log`.
   `--verbose` shows it in the terminal; `--plain` disables animation and color.
5. Observe an existing run without touching it:

   ```bash
   python3 tools/release/publish_release.py \
     --folder "$HOME/Releases/annotator-VERSION" --watch
   ```

   This only reads saved status and never queries Apple, submits, signs or
   publishes. Use `--status` for a single snapshot. Saved running state can be
   stale after a crash; do not claim liveness from the file alone.
6. Leave the release's original checkout unchanged while it runs or is paused.
   Make further edits in a separate worktree. Do not pull new tool/app changes
   into the frozen checkout to improve its display.
7. On timeout or interruption, keep the release directory and rerun the same
   command from the same source/config. It verifies the receipt and original
   archive before resuming; it does not rebuild or resubmit completed stages.
   If submission lost its response, use the documented `recover` command
   instead of removing the attempt marker.
8. Report success only after the actual signed artifact, published bytes,
   canonical `annotator` cask and live website checks succeed. The app can be
   published even if a later website stage fails; inspect the stage statuses.
   Keep physical installation and signed-update acceptance separate.

The app and website build locally; Apple processing and public GitHub Pages
hosting remain remote. Do not dispatch release Actions or spoof runner
environment variables to publish locally.

## Windows edition

The Windows edition (`apps/windows`, specification 013) ships only through the
Microsoft Store. Nothing is on the Store yet. Signing, notarization, the disk
image, Homebrew casks, `make generate` and `tools/release/publish_release.py` stay
Mac-only; the Windows flow never signs anything, because the Store signs what
it publishes.

### Version and the Windows switch

- The label and build are the shared ones, such as `0.1.0-beta.7 (25)`. The MSIX
  package version is `(Major + 1).Minor.Build.0` (WIN-DST-04): 0.1.0 build 26
  becomes `1.1.26.0`. `package.ps1`, the publisher and the hosted job all
  check it. Every Store package needs a higher version, so the shared build
  must keep rising.
- `releases/windows.json` `partOfReleases` is `true`: every Release run that
  signs the Mac app also builds the Windows packages from the same commit.
  Turning it off needs a maintainer's decision.
- Each user-visible change ships on both platforms in the same version, or
  records a deviation with a maintainer-approved end version (WIN-PAR-03).

### Build and check a Windows candidate (a maintainer's Windows PC)

`tools/windows/publish_windows.py` is the maintainer-run handoff (needs Python 3).
Read its `--help` and the Windows section of the release handoff first. It
plans by default; a maintainer adds `--execute`. Keep the release folder outside
the checkout:

```powershell
python tools/windows/publish_windows.py --folder C:\Releases\annotator-windows-1.1.26.0
python tools/windows/publish_windows.py --folder C:\Releases\annotator-windows-1.1.26.0 --status
python tools/windows/publish_windows.py --check-store --store-id <Store ID>
```

- `--execute` runs five resumable stages: freeze the source (clean checkout,
  shared version, `tools/release/release.py check`, the Store identity), validate
  (`tools/windows/validate.ps1 all`), build (`tools/windows/package.ps1
  -RequireArm64`: x64 and ARM64 packages and the bundle, compared with the
  validated publish), certify with WACK, and hand off. Rerun the same command
  to resume; finished stages are kept.
- WACK needs an elevated (administrator) terminal. Without one the stage
  pauses and prints the two `appcert.exe` commands; a maintainer runs them as
  administrator, then reruns the publisher, which reads the report. Never
  elevate on a maintainer's behalf.
- `--status` reads saved progress only. `--check-store` only checks the
  Partner Center values in `Package.appxmanifest` and the website's Windows
  listing; it exits 1 while they are placeholders. `--rehearsal` allows the
  placeholder identity and an x64-only bundle and submits nothing.
- For a package check without a release folder, use
  `pwsh tools/windows/validate.ps1 package` (run `validate.ps1 app` first). It
  builds unsigned packages and installs, signs and uploads nothing.
- The last stage prints the submission steps and submits nothing. Report the
  bundle, its SHA-256 and WACK result as prepared, not as submitted.

GitHub's Release workflow can also build the bundle on a Windows runner
(`windows-package`) when `partOfReleases` is `true`, or on a maintainer's manual
run with **windows_packages**. That job runs no test suite; WACK and
`validate.ps1` stay on a maintainer's PC. Do not dispatch it.

### Portable ZIPs and winget

Since beta.11 every release also carries the portable Windows ZIPs (WIN-DST-09). The Release workflow builds them
with the bundle (`windows-portable` artifact); `tools/release/publish_release.py` fetches them in **Get Windows files** and
uploads them with the Mac files. `tools/windows/winget.py manifests` writes the winget-pkgs manifests for
`DorLugasiGal.Annotator`; only a maintainer opens that pull request. See `docs/maintainers/winget.md`.

### Store steps only the account owner can do

- Enroll in Partner Center as an individual developer, create the product and
  reserve the name.
- Copy the product identity (`Package/Identity/Name`,
  `Package/Identity/Publisher`, `Package/Properties/PublisherDisplayName`)
  into `apps/windows/src/Annotator.App/Package.appxmanifest`, check it with
  `--check-store --store-id <Store ID>`, and commit it with the candidate.
  Agents may run the check; they never invent these values.
- Install Visual Studio's ARM64 build tools and the Microsoft Store Developer
  CLI, then run `msstore reconfigure` once.
- Make the first submission by hand in Partner Center, to a private
  audience; the Store CLI can't make it. Create the tester group and package
  flights (WIN-DST-03), which exist only after a published submission.
- Set up the `windows-store` GitHub environment with the four
  `PARTNER_CENTER_*` secrets and `WINDOWS_STORE_FLIGHT_ID`, and the repository
  variables `WINDOWS_STORE_ID` and `STORE_SUBMISSION_ENABLED` (`true` only
  after the first submission is published). Secrets go only into GitHub's
  secret fields, never into chat, files or command arguments.

`docs/maintainers/windows-store.md` has every Partner Center step and
the text to paste.

## Publication is a separate decision

This skill does not authorize Developer ID material creation, notarization
uploads, public tags/releases, feed changes, live checkout or deployment.
For Windows, version approval never authorizes a Store submission (a Partner
Center upload, `msstore publish`, or a Release run with **windows_store**),
a package flight for testers, a public listing, `STORE_SUBMISSION_ENABLED`,
the website's `storeId` or turning off `partOfReleases`.
Never override a Gatekeeper rejection, alter quarantine or change MDM policy.
Ask for the specific external action when it becomes necessary. Do not merge
or push main, create a public release, or contact testers merely because a local
candidate was approved.

What's changed in the app is a manual, offline view of bundled release notes.
Keep it tied to the running version; do not show Unreleased changes as shipped.
