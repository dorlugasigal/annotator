# Contributing to Annotator

Thanks for helping. Bug reports, ideas, documentation fixes and code are all
welcome.

## Before you start

- For a bug, open an issue with the bug form. Include the Annotator version
  (Settings > About), your OS version and the exact steps.
- For a new feature or a bigger change, open a feature issue first so we can
  agree on the behavior before you write code.
- Small fixes (typos, clear bugs) can go straight to a pull request.
- Look for issues labelled `good first issue` or `help wanted`.

Issues are public. Use made-up data; never attach credentials or private
screenshots. Report security problems privately ([SECURITY.md](SECURITY.md)).

## The parity rule

Annotator is one product with two native apps: Swift on macOS
(`apps/macos`) and C# on Windows (`apps/windows`). The Mac app is the
behavioral reference.

**Every user-visible change ships on both platforms in the same pull request
or release.** If you can only build one platform, say so in the pull request;
a maintainer or another contributor finishes the other side before release. A
platform that can't do something gets a documented deviation in
[docs/parity.md](docs/parity.md). Open a
["Mac and Windows difference"](https://github.com/dorlugasigal/annotator/issues/new?template=windows-parity-gap.yml)
issue for any difference you find.

## Build and test

See [Build from source](README.md#build-from-source). In short:

| Change | Run |
| --- | --- |
| Swift libraries | `make test` |
| Mac app behavior | `make app`, then the relevant `NativeAppTests` suite |
| Mac change that affects the conformance corpus | `make conformance`, then `make conformance-check` |
| Mac interface change | Regenerate the UX reference (see [docs/parity.md](docs/parity.md#generated-references)) |
| Windows code | `pwsh -NoProfile -File tools/windows/validate.ps1 <suite>` (`core`, `workspace`, `platform`, `rendering`, `conformance`, `app`, `a11y` or `all`) |
| Release tooling | `python3 tools/<folder>/test_<name>.py` |
| Website | `npm --prefix website run typecheck && npm --prefix website run build && npm --prefix website test` |

Warnings are errors on Windows. Native tests on the Mac open real overlays on
your displays; run them when nothing else is using the screen.

## Code rules

- `AnnotationCore` (Mac) stays free of AppKit, SwiftUI and networking.
  `Annotator.Core` and `Annotator.Workspace` (Windows) stay free of Windows and
  UI dependencies.
- `reference/conformance` and `reference/ux-reference` are generated on the
  Mac. Never edit them by hand; regenerate them in the same pull request as
  the Mac change. Windows surfaces match the UX reference within 1 DIP, with
  exact labels, identifiers, values and order.
- Don't list `ICompositorDesktopInterop`, `ICompositorInterop` or
  `CreateDispatcherQueueController` in CsWin32; they break Native AOT. Use the
  hand-written interop in `Annotator.Platform/Interop`.
- Files use LF line endings. On Windows, keep `core.autocrlf=true` in your
  global config and use `git -c core.autocrlf=false` for add, commit, checkout,
  reset and merge. Check with `git ls-files --eol`.
- Annotations live on the live desktop. Don't add a standalone image editor,
  OCR or QR reader, export-area editor or screen recorder.
- Masks fail closed and input is always released. Never weaken either.
- Every feature is free. Don't add licence or paid-feature checks.
- Don't copy code, assets or tests from GPL projects, ZoomIt, PowerToys or any
  source whose licence you haven't checked. Add new third-party material to
  `THIRD_PARTY_NOTICES.md` and `docs/provenance/components.json`.

## Writing

App labels, errors, help and website copy are plain English at a B1-B2 level,
written as if speaking to the person using the app: name the action and the
next step. Mac and Windows use the same words, except for the platform terms
in [docs/parity.md](docs/parity.md#platform-terms).

## Pull requests

- Keep each pull request to one change. Describe what changed, why, and how
  you tested it on each platform.
- Name the issue in the description: `Fixes #123` when the pull request
  finishes it, `Part of #123` when it doesn't. The **Linked issue** check
  fails without one.
- Fill in **Visuals**: screenshots of every change people can see, and a short
  video or GIF for motion and interactions. Show before and after on each
  platform you changed, in light and dark when the look differs. Attach them
  to the pull request, not the repository. Write "No visual change" when
  nothing changes on screen.
- Add user-visible changes to `unreleased` in `releases/notes.json` and run
  `python3 tools/release/release.py changelog --write`. The **Release note**
  check fails when app code changes and this file doesn't. If nothing changes
  for people using Annotator, a maintainer adds the `no-release-note` label.
- CI runs the checks that don't need secrets: the source guards, then the Mac
  and Windows tests when your change touches those apps. Labels such as
  `macos`, `windows` and `website` are added for you.
- A maintainer reviews, then squash-merges. Small fixes can skip the issue if
  a maintainer adds the `no-issue` label. A release pull request (the version
  bump with its notes) needs no issue.

By contributing, you agree that your contribution is licensed under the
[Apache License 2.0](LICENSE), as section 5 of the licence describes.

## Code of conduct

Be kind and constructive. See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
