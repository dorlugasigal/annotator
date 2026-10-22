# Annotator development

Annotator is open source under Apache-2.0: one product, two native apps.
Swift on macOS in `apps/macos` (the behavioral reference) and C# on Windows in
`apps/windows` (.NET 10, WinUI 3, Native AOT). Read `CONTRIBUTING.md`,
`docs/architecture.md` and `docs/parity.md` before changing behavior.

## Parity

Every user-visible change ships on both platforms in the same piece of work.
No feature is Mac-only or Windows-only. App copy is the Mac copy, with only
the platform terms in `docs/parity.md`. A platform that can't do something
gets a documented deviation there.

The Mac generates `reference/conformance` (`make conformance`, then
`make conformance-check`) and `reference/ux-reference`
(`tools/macos/ux_reference.py`). Never edit them by hand; a Mac behavior or
interface change regenerates them on the Mac in the same change. Windows
surfaces match the UX reference within 1 DIP, with exact labels, identifiers,
values and order.

## Build and test

- Mac: `make test` (Swift package, `swift test --package-path apps/macos`),
  `make app` (development app), `make native-test` or
  `bash tools/macos/build-signed-dev.sh test -only-testing:NativeAppTests/<Suite>`.
  Native tests put real overlays on every display; get the repository root
  from `RepositoryRoot.url`, never by counting `#filePath` levels.
- Windows: `pwsh -NoProfile -File tools/windows/validate.ps1 <suite>`
  (`core`, `workspace`, `platform`, `rendering`, `conformance`, `app`, `a11y`,
  `package`, `benchmarks` or `all`). Warnings are errors.
- Website: `npm --prefix website run typecheck`, `run build` and `test`.
- Tools: `python3 tools/<folder>/test_<name>.py`. Run tools from the
  repository root. `tools/README.md` lists the folders.
- For local checks and the on-screen rules (display on, one instance,
  bounded processes, input only into Annotator's own windows), use
  `.github/skills/annotator-local-validation/SKILL.md`.

## Platform rules

- Keep AnnotationCore free of AppKit, SwiftUI and networking. Keep
  `Annotator.Core` and `Annotator.Workspace` free of Windows and UI
  dependencies. Native UI state is main-actor (Mac) or UI-thread (Windows)
  isolated. Surface errors explicitly.
- Never list ICompositorDesktopInterop, ICompositorInterop or
  CreateDispatcherQueueController in CsWin32 (not AOT-safe); use Platform's
  hand-written interop and `T.IID_Guid`.
- Never weaken mask protection or input release.
- Development builds (Annotator Dev) stay visibly distinguished from
  release builds.
- Keep stable code names, identifiers and historical protocol fields
  unchanged, including in experiments.
- Every feature is free. Never add licence, entitlement or paid-status checks.
- The product edits annotations on the live desktop only. Don't add a
  standalone image editor, OCR/QR reader, export-area UI, image backdrop
  workspace or recorder. Image-backed canvas hosts belong only in tests;
  marketing captures use the real desktop overlay.
- Don't copy code, assets or tests from GPL sources, ZoomIt, PowerToys or the
  neighboring utility repository. Keep `THIRD_PARTY_NOTICES.md` and
  `docs/provenance/components.json` accurate.

## Files and git

Files use LF line endings. On Windows the shared git config sets
`core.autocrlf=true`; leave it, and use `git -c core.autocrlf=false` for add,
commit, checkout, reset and merge, then check `git ls-files --eol`.

## Writing

Write app labels, errors, help and website copy in plain B1-B2 English, as if
speaking to the person using it. Name the action and explain the next step.
Use the `no-ai-slop` skill for copy review if you have it. Keep setup
requirements and privacy limits clear; put developer details in developer
documentation.

AI control uses the `annotator` computer-use skill
(`integrations/computer-use/annotator`) and the normal app UI. Don't add
draft-file or approval workflows.

## Releases

User-visible changes go to `unreleased` in `releases/notes.json`; render
`CHANGELOG.md` with `python3 tools/release/release.py changelog --write`.
Unreleased items are not shipped. Never add signing material, keys or tokens
to the repository. Version changes, signing, publishing,
Store submissions and deploys are maintainer actions (`docs/releasing.md`,
`docs/maintainers/`). Don't claim checks you didn't run; record what you
verified and what's still open in the pull request.
