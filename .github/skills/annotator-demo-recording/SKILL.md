---
name: annotator-demo-recording
description: Record and refine real Annotator product demonstrations from this source repository, on macOS and Windows. Use for fresh website clips, the Windows app's website screenshots, Windows replays of the Mac's live recordings, visible toolbar interactions, point-by-point curved arrows, rough shapes, native blur, pacing changes and recording quality checks. Reuse the isolated native recorders, preserve the user's workspace and distinguish scripted footage from human or computer-use acceptance.
---

# Record Annotator demos

Use this repository-only creator workflow for an explicitly requested native
recording. Do not install it as the public `annotator` computer-use skill.
Do not operate the user's live workspace or bypass a denied permission.
The sections below cover the Mac recorder; see
[Windows captures](#windows-captures) for the Windows app.

## Start with the existing recorder

The promo and reel projects live in `dorlugasigal/annotator-media`. For reel,
promo or website-demo work, clone it into `videos/` at the repository root
(`git clone https://github.com/dorlugasigal/annotator-media videos`); that
folder is ignored here, and the tools and recording tests read it there.

Read these before changing a take:

- `AGENTS.md`
- `docs/releasing.md`
- `apps/macos/Tests/NativeAppTests/ProductFeatureRecordingTests.swift`
- `apps/macos/Tests/NativeAppTests/Support/DemoHandMotion.swift`
- `apps/macos/Tests/NativeAppTests/Support/RealtimeDesktopMovie.swift`
- `tools/demo/render-product-takes.py`
- `website/src/data/product-takes.json`

The original film and new feature takes use a real `OverlaySession` on the
live desktop, native toolbar accessibility actions and timed canvas events.
ScreenCaptureKit records only test-owned windows. The pointer is scripted in
the recording; it is not a human performance or proof that a computer-use
provider completed the same workflow.

Do not substitute SVG recreations, old-film excerpts, a browser drawing app,
image-backed canvas fixtures or artificial pointer paths laid over old video.
An ordinary AppKit sample-content window underneath the live overlay is fine.
Keep it clearly fictional and free of private screen content.

## Build one readable action

Keep the full dark toolbar visible throughout, including expanded Properties
and color pickers. Frame the content and toolbar together before recording.
Use the existing bounds assertions; fail a take that crops a control.

Prefer a simple sequence:

1. Choose the tool and style in the real toolbar.
2. Choose a color last, then draw immediately. Do not click Done unnecessarily.
3. Hold the result, make one meaningful edit, then choose Use apps.

Current visual choices are a rough teal ellipse, blue curved arrow and colorful
landscape blur. Do not use red or change arrowheads unless requested.
Measure actual text bounds before circling or highlighting words; guessed
coordinates produced a misaligned first take.

For curved arrows, choose Curved, click each point, and finish with Return.
During travel between points, send native `mouseMoved` events so the preview
follows the pointer. Drag a selected interior point to demonstrate editing.
Assert the same arrow ID, changed point, preserved head style and expected
point count afterward.

For blur, use detailed colorful content so the change is visible. Drag over
the requested image, not nearby text. Wait for `effectsReady`, and update the
recording's owned-window filter when the native compositor window appears.
An unchanged background can mean that new compositor was omitted from capture.
Blur remains a visual effect, not secure redaction.

## Record safely

Keep the user's installed app running. Use a separate derived-data directory and isolated
UserDefaults. The existing Apple Development identity can retain the app's
normal capture grant; an unsigned identity may not have it. Never manufacture
a grant, change quarantine or generate signing material.

Require macOS 15 or later and the pinned Xcode version. Confirm the recorder
has its Screen Recording grant; a skipped test is not a new recording.
The following records all three takes. Set `DEVELOPMENT_TEAM` to the team of
an existing Apple Development identity:

```bash
export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
: "${DEVELOPMENT_TEAM:?Set the team of an existing Apple Development identity}"
make generate
ANNOTATOR_RECORD_PRODUCT_FEATURES=1 \
TEST_RUNNER_ANNOTATOR_RECORD_PRODUCT_FEATURES=1 \
xcodebuild -project apps/macos/Annotator.xcodeproj \
  -scheme Annotator -configuration Debug \
  -destination 'platform=macOS,arch=arm64' \
  -derivedDataPath build/ProductRecordingSigned \
  CODE_SIGN_STYLE=Manual CODE_SIGN_IDENTITY="Apple Development" \
  CODE_SIGNING_REQUIRED=YES DEVELOPMENT_TEAM="$DEVELOPMENT_TEAM" \
  -only-testing:NativeAppTests/ProductFeatureRecordingTests test
```

Set both `ANNOTATOR_RECORD_PRODUCT_TAKE=arrow` and
`TEST_RUNNER_ANNOTATOR_RECORD_PRODUCT_TAKE=arrow` to repeat only that take.
The other supported values are `draw` and `blur`. Normal test runs skip recording.

Do not run competing native builds or benchmarks while recording. Stop on
capture failure or an unavailable native control instead of replacing the
action with a model mutation that looks successful.

## Pace without fake motion

Use `DemoHandMotion` for bounded, repeatable easing and curved pointer travel.
It already varies speed while preserving endpoints. Avoid random targets,
shaking, decorative laps and slow repeated palette tours.

The recorder's `travel`, `pause`, `drag` and `arrowPoint` helpers own timing.
When asked for faster clips, shorten transition/inspection pauses
first. Preserve a short visible press and a readable final hold. Confirm a
target pace on one take before repeating all three.

The current encoder preserves recorded speed. If a later edit intentionally
changes playback speed, record that factor, retime captions and action timestamps,
and inspect the actual encoded result. Do not silently call sped-up footage
real-time. Leave pacing unchanged when asked to defer it.

## Encode and verify

The recorder writes unique originals and `latest-{draw,arrow,blur}.json` under
`build/fresh-product/`. Keep originals and failed iterations. Confirm the latest
metadata names the files from this run, not a previous successful take. For a
later recording date, update the encoder's `recorded` label; do not reuse the
current campaign's date. Encode only after native assertions pass:

```bash
python3 tools/demo/render-product-takes.py
npm --prefix website run typecheck
npm --prefix website run build
npm --prefix website test
ANNOTATOR_E2E_PORT=4397 npm --prefix website run test:e2e
```

The encoder checks capture/crop bounds, dark toolbar, prohibited color actions
and silent video output. It retains source hashes, control rectangles and
timestamps in `product-takes.json`. It leaves native backdrop below the toolbar
so browser playback controls do not cover the app's controls.

Inspect a labeled early/middle/late contact sheet and a moving clip. Confirm:

- Every tool, color and style choice visibly causes the native result.
- The whole toolbar stays in frame and popovers close at the intended step.
- Ellipse/highlight geometry fits the content; arrow points remain editable.
- Blur actually changes the captured image, while surrounding content is sharp.
- Playback is silent, finite and paused offscreen; reduced-motion/no-JavaScript
  visits retain the real poster and native controls.
- No fake window headers, numbered demo panels or repeated captions are added
  around the website videos. Put provenance in one recording-details disclosure.

Use `no-ai-slop` for any caption or page-copy edit. Record the result in the pull request
with actual recording results and limits, then use `annotator-release` for
candidate preparation. Recording does not authorize deployment, Apple submission,
publication or a hosted CI run.

## Windows captures

The Windows edition has no video takes for the website yet. Its captures follow
the same rules: the real app, its real overlay and toolbar, over a made-up
window that the script owns, with input only into Annotator's own windows.
UX reference sheets and render harness comparisons are parity evidence, not
marketing footage. Follow the on-screen rules in the Windows section of
`.github/skills/annotator-local-validation/SKILL.md`: display on, one
Annotator per session, bounded processes, evidence outside the repository.

### Website screenshots

`tools/windows/website-shots.ps1` takes the six pictures (`draw`, `properties`,
`color`, `blur`, `zoom`, `settings`; choose with `-Shots`). Read its header
first. It opens the sample window of `tools/windows/website-backdrop.ps1` over
an area at the top of the primary display, starts the development app with
`--live-check` (in-memory profile, no tray icon, global shortcut or saved
setting), drives the real toolbar, canvas, color popover, effects, zoom and
Settings, and saves the area with the render harness's Windows.Graphics.Capture
`shot` mode, without the system pointer:

```powershell
dotnet build apps/windows/src/Annotator.App/Annotator.App.csproj -c Debug -p:Platform=x64
dotnet build apps/windows/tools/Annotator.RenderHarness -c Release
pwsh -NoProfile -File tools/windows/website-shots.ps1 -Out <folder outside the repository>
python tools/windows/encode_website_shots.py <that folder> --commit <source SHA>
```

- It refuses to start while another Annotator or render harness runs, while
  the PC had input in the last `-IdleSeconds` (60) or while the display is
  off. It skips a shot when another app's window lies over the sample window
  in the area. It takes the keyboard focus for about a minute; run it only
  when nobody is using the PC.
- The encoder crops each shot to 16:10, writes `website/media/windows/` and
  `website/src/data/windows-shots.json` (sizes, checksums, crops and how each
  shot was made). The site uses them only while
  `website/src/data/windows-listing.json` names a published Store listing.
  Run the website checks afterwards, including `test:windows`.
- The pictures show a development build at the display's scale and the PC's
  theme. Say so in the pull request, and take them again when the
  toolbar or Settings change.

### Replays of the Mac's live recordings

`Annotator.RenderHarness motion-live` replays the takes in
`reference/ux-reference/macos-live` on the Mac's stage with the real app and
records them at real speed, with the take's scripted pointer drawn in.
`live-compare` then compares them with the Mac recordings frame by frame.
Both read and write H.264 MP4 with Windows' own Media Foundation, so ffmpeg is
optional (`--ffmpeg`); Windows N editions need the Media Feature Pack.
`video-check` tests that path without showing anything. Read the harness
README's "Run it safely" section and give each run an outer time limit. These
videos are parity evidence; keep them in the evidence folder,
not in the repository or on the website.
