# UX reference

Pictures and layout data of every Annotator surface on macOS, made from the
real app code. The Windows edition copies them (specification 013,
WIN-PAR-06). Open [macos/INDEX.md](macos/INDEX.md) to browse everything, and
[macos-live/INDEX.md](macos-live/INDEX.md) for recordings of the live desktop.

Do not edit these files by hand. Regenerate them on a Mac after any change to
the macOS interface, in the same change.

## What is here

| Path | Contents |
| --- | --- |
| `macos/<surface>/<state>-<light/dark>.png` | The surface at 2x: 2 pixels per point |
| `macos/<surface>/<state>-<light/dark>.json` | Size in points, notes, spec references and the accessibility tree |
| `macos/<surface>/motion/` | Motion: an MP4 at real speed, a contact sheet of up to 12 frames with times, and the frame times |
| `macos/cursors/cursors.json` | Every cursor's size and hot spot in points |
| `macos/design-values.json` | System colors in light and dark, text style sizes and weights, and font sizes |
| `macos/manifest.json` | Source commit, macOS and Xcode versions, and a SHA-256 for every file |
| `macos-live/<take>.mp4` | A real-time recording of the live desktop, re-encoded at 60 fps |
| `macos-live/<take>.json` | What happened when: events, timed segments, the display, scale and durations used |
| `macos-live/<take>.png` | Frames at each segment's start, quarters and end, with times |
| `macos-live/manifest.json` | Source commit, versions and a SHA-256 for every live file |

The surfaces are the toolbar, full properties, pickers, the menu-bar menu and
icon, Settings, the introduction tour, the Tools window, region sharing,
dialogs, canvas chrome (selection, marquee, arrow attachment, eraser, text
editing, boards), pointer effects and cursors.

## Reading the JSON

- Sizes and frames are in points. One macOS point is one Windows DIP.
- `frame` is `[x, y, width, height]` from the image's top-left corner.
- `accessibility` is the tree a screen reader sees. Map `label` to the UI
  Automation Name, `identifier` to AutomationId and `value` to Value
  (WIN-A11Y-01). Use the same words, apart from the platform terms in
  specification 013.
- `enabled` is AppKit's `isAccessibilityEnabled` for every node. On controls,
  `false` means disabled, as for Check for updates… when updates are not set
  up. Containers such as scroll areas, tab groups and the canvas group also
  report `false`; that is AppKit's default for them, not a disabled state.
- `selected` appears on selected items, such as the current tool.
- `notes` explains the state. `spec` names the requirements it shows.

## Using it on Windows

1. Build the same state in the Windows app, at the same size in DIPs.
2. Compare it with the PNG side by side, at 200% scale.
3. Match layout within 1 DIP, using the JSON frames. Match labels, values and
   order exactly.
4. Follow the parity levels in specification 013: layout and copy are
   identical; standard controls may look like Windows controls.
5. For motion, match the durations and curves in `apps/macos/Sources/MotionCore` and
   `reference/conformance/motion`, then check the feel against the videos in
   `macos/<surface>/motion/` and `macos-live/`.

## Live recordings

`macos-live/` shows what offscreen captures cannot: the workspace show and hide
styles (Dissolve, Slide, Magic, Corners, Pop, Drop, Focus, Fade and Off, plus a
reversal), screen zoom and the pointer lens, blur, pixelate and distort over a
desktop, and region sharing with its output window. The real app runs on the
live desktop: the toolbar, overlay, animation windows, zoom and effect windows
and the sharing output are the app's own windows.

- Compare timing with the `segments` in each JSON file. A segment starts when
  the scripted action starts. Show, hide and zoom segments end when the app
  reports that the animation has finished; drag segments end when the pointer
  is released and any effect is ready. Compare the look frame by frame with
  the contact sheet and the video.
- `sharing-output` is the output window, recorded at the same time as
  `sharing-live`; its JSON gives the offset between the two clocks.
- The drawings, the zoom and the effects sit on a fictional sample window over a
  plain backdrop. The app's own capture reads only those two windows, so no
  private screen content is recorded.

## Limits of these captures

- Rendering is offscreen, so system materials are missing. Popovers show the
  window background color instead of their blur. The canvas Use apps button
  shows without its dark material.
- macOS switches draw without their accent fill, and the selected Settings tab
  hides its name. Read their state from the JSON: a switch's `value` is 1 when
  on, and the selected tab is the radio button with `value` 1.
- Captures run in the development test app. Strings like Annotator Dev,
  Local build, Developer preview and the version (0.1.0-beta.7, build 25) come
  from that build. Release builds say Annotator. Windows uses its own version.
- The region chooser lists the capturing Mac's display.
- The canvas uses a fictional backdrop drawn by the test, not a real desktop,
  at the canvas's size (900 x 568 points). Drawings are drawn as vectors at 2x,
  as on the live overlay. The scene has no effect regions; their rendering is
  in the conformance corpus (`reference/conformance`).
- The status-item tree comes from a status button configured by the app but
  outside the menu bar, where macOS reports its role as unknown. Its label,
  value and help are the real ones.
- Live recordings use the display's own scale. On 2026-10-19 that was 1x (an
  Odyssey G95SC at 2560 x 1440 and 120 Hz), so they show timing and behavior,
  not 2x detail; the 2x pictures are in `macos/`.
- In the live recordings the pointer is drawn by the test and moved by script,
  not by a person. The development build's names show, for example "Annotator
  Dev - Shared Region" as the output window title. Drawings use the app's
  default colors.
- The toolbar no longer creates solid covers, so the effects recording adds one
  as an existing board would have it, to show that covers stay above effects.

## Regenerating on a Mac

The capture never shows a window, takes focus, creates a drawing overlay or
reads the screen, so it is safe while the Mac is in use. It needs the
development signing identity used by `tools/macos/build-signed-dev.sh`, Pillow and
ffmpeg.

```sh
export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
make generate
defaults write com.dorlugasigal.annotator.dev AppleShowScrollBars Always
TEST_RUNNER_ANNOTATOR_CAPTURE_UX_REFERENCE=1 bash tools/macos/build-signed-dev.sh test \
  -only-testing:NativeAppTests/UXReferenceCaptureTests
defaults delete com.dorlugasigal.annotator.dev AppleShowScrollBars
python3 tools/macos/ux_reference.py pack
```

The `defaults` lines keep scroll bars always visible in Annotator Dev only.
With the system's automatic setting, a Mac with only a trackpad draws thinner
overlay scroll bars, which moves the Settings, Toolbar and Tools window layouts
by a few points.

`pack` rebuilds `macos/` and then runs `python3 tools/macos/ux_reference.py check`,
which verifies every file against the manifest. Packing the same capture twice
gives identical files. Captures repeat pixel for pixel, with two exceptions:
the build time in About, and tour illustration frames, which can differ by one
color level between runs.

Toolbar, menu, properties and Tools window captures use a desktop-style
drawing session, so they show what the live app shows, including the Focus and
cleanup and Zoom tools menus. Its window is transparent, click-through and far
off screen, and it gets no effects, so it never shows, takes input or reads the
screen. Canvas captures use a test-only canvas with the fictional backdrop.

The capture code is `apps/macos/Tests/NativeAppTests/UXReferenceCaptureTests.swift` and
`apps/macos/Tests/NativeAppTests/Support/UXReferenceRecorder.swift`. Normal test runs
skip it.

### Live recordings

These show windows, draw on the desktop and use the development app's Screen
Recording permission, so run them only when nobody is using the Mac. Each group
takes under a minute.

```sh
export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
ANNOTATOR_RECORD_UX_LIVE=1 TEST_RUNNER_ANNOTATOR_RECORD_UX_LIVE=1 \
  bash tools/macos/build-signed-dev.sh test -only-testing:NativeAppTests/UXLiveRecordingTests
python3 tools/macos/ux_reference.py pack-live
```

Set `TEST_RUNNER_ANNOTATOR_RECORD_UX_LIVE_GROUPS` to `show-hide`, `zoom`,
`effects` or `sharing` (comma-separated) to repeat some of them. Recordings go
to `build/ux-reference/raw-live`; `pack-live` rebuilds `macos-live/` from them
and checks it. Packing the same recordings twice gives identical files; new
recordings differ a little in timing, as any live recording does. The code is
`apps/macos/Tests/NativeAppTests/UXLiveRecordingTests.swift`.
