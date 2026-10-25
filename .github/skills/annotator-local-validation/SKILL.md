---
name: annotator-local-validation
description: Validate Annotator changes locally for the macOS app, the Windows edition and the website. Use for local checks before a pull request, native, Windows (validate.ps1, live and dock checks, UX reference, render harness) or website validation, CI coverage questions, and deciding which checks run locally versus in GitHub Actions. Preserve release safety and distinguish local evidence from hosted or physical acceptance.
---

# Annotator local-first validation

Run from a checkout of the repository. Contributors can follow every step
here without release secrets.

## Inspect before running

1. Read `AGENTS.md`, `docs/architecture.md`, `docs/parity.md` and the open
   issues for the area.
2. Inspect the worktree and changes. Use the existing Makefile, package scripts
   and validation tools rather than inventing another runner.
3. Read the current workflow definitions before claiming what CI covers.
   An open optimization PR is not the active workflow.
4. Pushing a branch with an open pull request starts CI. Don't dispatch or
   re-run release workflows; those are maintainer actions.
5. Leave unrelated user changes and active runs alone. Use a separate worktree
   when another agent owns the same files.

## Use the local heavy-suite runner

When `tools/ci/validate_local.py` is present, run its `--help` first, then select
the needed suite:

```bash
python3 tools/ci/validate_local.py native
python3 tools/ci/validate_local.py benchmarks
python3 tools/ci/validate_local.py browser
python3 tools/ci/validate_local.py all
```

`all` runs native, benchmarks and browser suites sequentially. It does not
replace release guard tests or metrics service checks. The runner reports missing
prerequisites rather than installing them or skipping a suite.
Check its pinned Xcode/Node requirements; do not silently substitute versions.
If the runner is absent on the current branch, use the existing commands
below and report that the local-first proposal has not been integrated there.
The runner is Mac-only. Its native suites need Xcode, and its browser
suite pins a Node version and starts `npm` without a shell. On Windows, use
`tools/windows/validate.ps1` and run the website scripts one by one.

## Choose the smallest complete local checks

| Change | Starting checks |
| --- | --- |
| Documentation only | Review copy, links and factual claims; no native rebuild |
| Swift libraries | `make test`; use existing test selectors for a narrow fix |
| Native app behavior | `make app` plus `bash tools/macos/build-signed-dev.sh test -only-testing:NativeAppTests/SuiteName`; replace SuiteName with an existing suite, or use `make signed-native-test` for the full suite |
| Mac change to the conformance corpus or UX reference | Regenerate on the Mac in the same change (`make conformance`, then `make conformance-check`; `python3 tools/macos/ux_reference.py pack` or `pack-live`, then `check`), then `validate.ps1 conformance` on Windows |
| Windows code (`apps/windows`) | The `validate.ps1` suite for the project, as in [Windows checks](#windows-checks) |
| Release metadata or packaging | Relevant `tools/release/test_*.py`, `python3 tools/release/release.py check`, and a built-bundle version check |
| Website logic or copy | Website typecheck, build, unit tests and relevant browser cases |
| CI routing | `actionlint` plus local routing/helper tests, if present |
| Performance-sensitive code | Existing Release benchmark workload, without competing builds |

Use `no-ai-slop` for copy review. Surface failed checks; do not replace failures
with skips, retries-until-green, relaxed assertions or smaller workloads.
Install dependencies only after manifest changes or a missing-dependency
failure. Respect the machine-configured package registry.

For a complete candidate, retain the documented coverage:

- Debug and Release Swift package tests.
- Development app build and native tests, with opt-in skips identified.
- Release, packaging, distribution and Pages guard tests.
- Website typecheck/build/unit tests and all four browser modes: root closed,
  root published fixture, `/annotator/` closed, `/annotator/` published fixture,
  plus the Windows Store listing fixture mode (`test:windows`).
- Metrics service typecheck and tests.
- Existing performance workloads when required by the change or release gate.
- When `releases/windows.json` makes Windows part of releases: `validate.ps1
  all` and `a11y`, the live check, and the Windows publisher's package and
  WACK stages on a Windows PC.

The fallback website sequence is:

```bash
npm --prefix website run typecheck
npm --prefix website run build
npm --prefix website test
npm --prefix website run test:e2e
npm --prefix website run test:distribution
npm --prefix website run test:pages
npm --prefix website run test:windows
```

Root `test:e2e` runs every browser engine (Chromium, WebKit, iPhone WebKit).
The fixture, Pages and Windows scripts change URLs, download data and the
Windows Store listing, not rendering, so they default to
`ANNOTATOR_E2E_BROWSERS=chromium`. Set that variable to a
comma-separated project list to widen or narrow a run. Playwright uses four
local workers. Set `ANNOTATOR_E2E_PORT` when port 4178 is busy.
`test:windows` builds with `tests/fixtures/windows-listing.json` at the root with
the published fixture, then under `/annotator/` without releases. Every other mode
keeps the source listing, which stays null until the Store listing is published, and
checks that no page or other build file mentions Windows and that no Windows picture
is built. Playwright's Desktop Chrome reports Windows, so with a listing the page
puts the Windows download first; tests that need one system set the user agent
(`website/tests/e2e/visitors.mjs`).

The same scripts run on a Windows PC. The mode helpers start npm through a
shell (`website/scripts/npm-run.mjs`, because npm is a `.cmd` file there), and
the unit tests read the AI skill ZIP with Windows' built-in `tar` when there is
no `unzip`. Use the npm registry already configured on the machine
(`npm config get registry`); never change npm settings or add an `.npmrc`.
WebKit and iPhone WebKit are not installed on Windows, so set
`ANNOTATOR_E2E_BROWSERS=chromium` there and report those engines as still owed
on a Mac.

Run those browser scripts sequentially in one checkout: fixture scripts mutate
and restore generated output. Parallelize only in independent worktrees.
Never run performance measurements concurrently with builds or other heavy
checks on the same Mac. Do not claim a development-signed test app is a
notarized distribution app.

## Windows checks

The Windows edition (`apps/windows`, C# on .NET 10 with Native AOT) is checked
on a Windows PC. Read the "Build and test" section of
`apps/windows/README.md` first. The toolchain is pinned in
`tools/windows/toolchain.json`; the script warns about a different SDK and
installs nothing. Run from the repository root:

```powershell
pwsh -NoProfile -File tools/windows/validate.ps1 core
```

| Change | Suite |
| --- | --- |
| `Annotator.Core` | `core` and `conformance` |
| `Annotator.Workspace` | `workspace`; add `conformance` for shortcuts, keys, editing or geometry |
| `Annotator.Platform` | `platform` (creates short-lived transparent windows and registers an unused global shortcut) |
| `Annotator.Rendering` | `rendering` (pixel tests on the WARP device, the corpus `render` and `effects` comparisons) |
| `Annotator.App` | `app` (Native AOT publish to `apps/windows/out/app`; fails on any warning), plus the UX reference tool for the surfaces you changed and `live-check.ps1` steps for the behavior |
| App windows, controls, copy or themes | `a11y` (about ten minutes; windows stay off screen) |
| Package manifest, packaging or the Windows publisher | `app`, then `package` (unsigned MSIX packages; nothing installed or uploaded) |
| Performance-sensitive code | `benchmarks` on a quiet machine (not part of `all`), plus the render harness mode that measures it |
| A merge, a handoff or a candidate | `all`: every test project, then the app publish, with 0 warnings |

For a narrow test run, use `dotnet test` from `apps/windows` with `--project
<path>` or `--solution Annotator.Windows.slnx`; `apps/windows/global.json`
selects Microsoft.Testing.Platform. Fix a conformance failure in the port, or
list it with its reason in
`apps/windows/tests/Annotator.Conformance.Tests/Support/OpenDifferences.List.cs`.
The Mac generates `reference/conformance` and `reference/ux-reference`; never edit them.

Checks on the real desktop, with development builds
(`dotnet build apps/windows/src/Annotator.App/Annotator.App.csproj -c Debug -p:Platform=x64`):

- `pwsh -NoProfile -File tools/windows/live-check.ps1 -Out <folder>`: real
  input into Annotator's own windows, one script for every feature. It takes
  the keyboard focus for about 100 seconds. Add short steps for a new feature.
- `pwsh -NoProfile -File tools/windows/dock-check.ps1 -Out <folder>`: toolbar
  drags, docking and the ghost; it moves the pointer for about 15 seconds.
- `dotnet run --project apps/windows/tools/Annotator.UXReference -- run --out <folder>`
  (`--states`, `--themes`; `list`; `cursors --out <folder>`): compares each
  state's UI Automation tree and pictures with `reference/ux-reference/macos`
  (WIN-PAR-06). Frames must match within 1 DIP; labels, identifiers, values,
  help and order exactly. Report missing reference states in an issue so
  someone with a Mac can capture them.
- `apps/windows/tools/Annotator.RenderHarness` (build with
  `dotnet build apps/windows/tools/Annotator.RenderHarness -c Release`; read
  its README first): latency, memory, capture, effects and motion
  measurements; `motion-live` replays the Mac's live recordings in
  `reference/ux-reference/macos-live` and records them with Media Foundation;
  `live-compare`, `video-check` and `pointer` compare without showing
  anything. Exit code 3 means no composition frames (display off or session
  locked); 124 means its time limit.

Rules for anything on screen:

- Check that the display is on first. Never wake it or change power
  settings. While it is off, do code, unit, WARP and off-screen checks, and
  record each on-screen check as pending with its exact command.
- Annotator allows one instance per session. Check `Get-Process Annotator`
  and its path before starting the real app, and never stop an instance you
  did not start. The live check, the dock check and `--ux-reference` run
  beside a running Annotator; otherwise wait with a time limit or report the
  check as pending.
- Give every process you start a bounded lifetime (`Start-Process -PassThru`,
  then `WaitForExit(milliseconds)`, and stop it on timeout). Never wait without
  a timeout for frames, GPU queries, window messages or events.
- Send input only into Annotator's own windows, after `WindowFromPoint`
  confirms the target. Never register the real default shortcuts (Ctrl+1,
  Ctrl+2, Ctrl+Shift+2, Ctrl+Alt+Shift+2) in automated tests.
- Keep runs short, close every window you open and keep evidence outside the
  repository. Before you finish, make sure no Annotator, testhost or harness
  process of yours still runs, and run `dotnet build-server shutdown`.

## What hosted CI covers

`validate.yml` runs on every pull request, including pull requests from forks,
with a read-only token and no credentials. The Mac and Windows jobs run only
while the repository is public:

- **Lightweight source guards** (Linux, always): release, packaging and CI
  guard tests, `release.py check`, and the website and metrics checks when
  their paths change.
- **Mac tests** (`macos-26`, when `apps/macos`, `reference`, `tools/macos`,
  `integrations`, `releases`, the Makefile or the workflow change):
  `make test`, `make conformance-check-data` (every corpus file except the
  rendered PNGs, whose pixels depend on the macOS version) and `make app`.
  Run the full `make conformance-check` on the Mac that generated the corpus.
- **Windows tests** (`windows-2025-vs2026`, when `apps/windows`, `reference`,
  `tools/windows`, `integrations`, `releases` or the workflow change):
  `validate.ps1` `core`, `workspace` and `conformance`, then `app`.

A release run validates the exact candidate with every job. Native app tests
(they need a real display), `platform`, `rendering`, `a11y`, benchmarks, the
browser matrix, WACK and the live and dock checks stay local. Say which of
them you ran.

The Release workflow builds the real artifacts only: it signs and notarizes the
Mac app and builds the Windows bundle. Don't add test suites there, and don't
weaken exact-source, version, signing, timestamp, entitlement,
notarization or immutable-artifact checks. For maintainer-run resumable
artifact work, see `tools/release/local_release.py --help` and
`docs/maintainers/local-fallback.md`. Use `make release-tests` for its mocked
boundaries; don't run production signing, submission or publication as part of
local validation.

Cache safe dependency downloads only, never credentials, keychains,
secret-bearing files or signed artifacts. Never change spending limits,
activate release variables or cancel unrelated runs.

## Treat remote waits as pending

If Apple is still processing, report the submission as pending, not rejected
or accepted. Do not upload duplicates to work around a slow response.
Do not leave a paid macOS runner waiting indefinitely.

Before any maintainer-authorized submission, require a recovery path that retains
the exact signed archive, its hash, source/version and submission ID outside
temporary keychain cleanup. Confirm what artifacts actually survived a failure.
A later Apple acceptance does not restart a stopped workflow, recover deleted
bytes or authorize publishing a newly rebuilt binary.

The local release helper retains the original ZIP before submission and offers
checksum-bound recovery. It does not recover missing historical artifacts or
relax the candidate checks (the exact, clean commit). Leave its submission-attempt marker intact after
an ambiguous failure; use the documented recovery command instead of retrying
the upload.

## Record evidence and hand off

Record the source SHA, dirty/clean state, exact commands, outcomes, skips and
remaining gates. Keep logs local and redact secrets before sharing.
Record meaningful validation results in the pull request.

Distinguish:

- Local tests of the current checkout.
- Hosted tests of a specific commit.
- Verification of the actual signed release artifact.
- Physical installation, capture and update acceptance.

None substitutes for the others. State which checks still need CI or a
physical test. Use
`.github/skills/annotator-release/SKILL.md` for version decisions.
This skill does not authorize production credentials, signing submissions,
publication, deployment or changes to billing.
