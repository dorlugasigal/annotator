# Annotator for Windows

The Windows edition of Annotator: a native C# app (.NET 10, WinUI 3, Native
AOT) that matches the Mac app. The Mac app is the behavioral reference; read
[the parity contract](../../docs/parity.md) and
[architecture](../../docs/architecture.md) first. It is published in the
Microsoft Store and as portable ZIPs on each GitHub release.


## Layout

| Path | What it is |
| --- | --- |
| `Annotator.Windows.slnx` | The solution |
| `Directory.Build.props` | Shared settings: nullable, warnings as errors, invariant globalization |
| `src/Annotator.Core/` | Document model, files, history and geometry; a C# port of `apps/macos/Sources/AnnotationCore` with no Windows or UI dependency |
| `src/Annotator.Workspace/` | The workspace model with no Windows dependency: one document per display, coordinated history, the Off, Use apps and Draw modes with Show / hide, Resume drawing and Hide only, tools, styles and ink keys, immutable snapshots and versioned canvas edits; the canvas controller (gestures, keys, text placement, Smart Draw and live drawing state); also toolbar layout and placement, Windows shortcut labels and the A mark glyph |
| `src/Annotator.Platform/` | Windows services for the desktop overlay: overlay windows per display and input modes, displays and DPI, global shortcuts, the Direct3D 11, Direct2D and DirectWrite device bundle, hand-written Windows.UI.Composition interop, the window registry and the dispatcher thread |
| `src/Annotator.Rendering/` | The Direct2D renderer: executes the Core display lists with cached geometry, brushes, DirectWrite text and WIC images; measures text for the scene builder; renders offscreen and exports PNG snapshots; presents each overlay's committed scene and live gesture (`OverlayPresenter`); draws Blur, Pixelate and Distort with Direct3D 11 shaders that match Core Image (`EffectPipeline`, `EffectsPresenter`). Windows only |
| `src/Annotator.App/` | The WinUI 3 app (Native AOT; unpackaged for development, a single-project MSIX for the Store with `-p:AnnotatorPackage=true`): single instance, tray icon, flyout and menu, the toolbar, overlays on their own thread with one drawing canvas per display (canvas controller, scene and presenter), the clipboard, and global shortcuts |
| `tests/Annotator.Core.Tests/` | xUnit tests for the core; they run on Windows, macOS and Linux |
| `tests/Annotator.Workspace.Tests/` | xUnit tests for the workspace model and the canvas controller, including ports of the Mac ResumeDrawing, MultiDisplayEditing, KeyboardEditing and canvas interaction assertions; any OS |
| `tests/Annotator.Rendering.Tests/` | xUnit tests for the renderer with the WARP device, including the Mac pixel checks that need rasterized output and the S-13 comparison of the corpus `render` and `effects` sections with the Mac images (`Conformance/`, WIN-REND-05); Windows only |
| `tests/Annotator.Platform.Tests/` | xUnit tests for the platform layer; Windows only. They briefly create transparent windows and register an unused global shortcut |
| `tests/Annotator.App.Tests/` | Accessibility checks of the app windows (WIN-A11Y-01 to WIN-A11Y-03): fast tests of the contrast theme resources, and three explicit checks that show every UX reference state off screen and read its UI Automation tree, its keyboard reach, a simulated contrast theme and 225% text; Windows only |
| `tests/Annotator.Conformance.Tests/` | xUnit tests that compare Core and the Windows key table with the Mac conformance corpus in `reference/conformance` (platform, boards, profiles, shortcuts, library, editing, geometry, recognition and hit testing); any OS. Every corpus file is checked against `manifest.json` first, and the differences that remain are listed with their reasons in `Support/OpenDifferences.List.cs` |
| `benchmarks/Annotator.Benchmarks/` | The Release benchmarks (WIN-PERF-01): the Mac suite's 34 workloads with the same names, fixtures and JSON, and the overlay presenter's frame work; Native AOT, never shipped; see its [README](benchmarks/Annotator.Benchmarks/README.md) |
| `tools/Annotator.RenderHarness/` | Manual measurements: the presenter on the real overlay (latency, memory, WIN-PERF-01), input latency into the running app, and Windows.Graphics.Capture screenshots; not part of the solution; see its [README](tools/Annotator.RenderHarness/README.md) |
| `tools/Annotator.UXReference/` | Compares every state of the macOS UX reference (the toolbar, tray flyout and menu, canvas, properties, pickers, Tools window, Settings, tutorial, dialogs and region sharing) and the cursors with the app (UI Automation tree, canvas objects and side-by-side sheets, WIN-PAR-06); not part of the solution; see its [README](tools/Annotator.UXReference/README.md) |
| `tools/Annotator.IconExport/` | Writes the MSIX logos and `Annotator.ico` from the A mark artwork; not part of the solution |


## Build and test

Toolchain versions are pinned in [`tools/windows/toolchain.json`](../../tools/windows/toolchain.json).

```powershell
pwsh tools/windows/validate.ps1 core       # Annotator.Core build and tests
pwsh tools/windows/validate.ps1 workspace  # Annotator.Workspace build and tests
pwsh tools/windows/validate.ps1 platform   # Annotator.Platform build and tests (Windows only)
pwsh tools/windows/validate.ps1 rendering  # Annotator.Rendering build and tests (Windows only, WARP device)
pwsh tools/windows/validate.ps1 conformance # Annotator.Conformance.Tests: the port against the Mac corpus (any OS)
pwsh tools/windows/validate.ps1 app        # Annotator.App build and Native AOT publish to out/app; fails on any warning
pwsh tools/windows/validate.ps1 all        # whole solution, every test project, then the app suite
pwsh tools/windows/validate.ps1 package    # the publisher's tests, then the MSIX packages and bundle (x64; ARM64 with the ARM64 build tools)
python tools/windows/publish_windows.py --folder <release folder>   # maintainer-run Store handoff; plans unless --execute
pwsh tools/windows/validate.ps1 benchmarks # Release benchmarks (Native AOT, about 2 minutes); not part of all
pwsh tools/windows/validate.ps1 a11y       # development app, then the explicit accessibility checks (about 10 minutes)
```

Run the published app with `apps/windows/out/app/Annotator.exe`. It registers the
real global shortcuts (Ctrl+2, Ctrl+Shift+2, Ctrl+1, Ctrl+Alt+Shift+2) while it runs,
installs live zoom's low-level mouse hook while the toolbar shows, and
keeps its settings in `%LOCALAPPDATA%\Annotator Development`. Set
`ANNOTATOR_STARTUP_LOG` to a file path to record startup milestones, global
shortcut registration results and shortcut presses for measurements; nothing is
written otherwise.

Live check with real input, beside a running Annotator (development builds only):

```powershell
dotnet build apps/windows/src/Annotator.App/Annotator.App.csproj -c Debug -p:Platform=x64
pwsh -NoProfile -File tools/windows/live-check.ps1 -Out <folder outside the repository>
```

It starts the app with `--live-check`: the real toolbar, canvas, color popover
and workspace on one made-up display over a band at the top of the primary
display, with no tray icon, global shortcut, single-instance check or saved
setting. The script clicks, drags and presses keys only in that app's windows
(WindowFromPoint first for the pointer; keys only while the app has the
foreground, and every key it presses is released). One script holds every feature's steps, in this order: the toolbar
takes clicks after Draw and strokes, and a new shape is selected with the
Select tool; the keyboard (Shift+F10 on the canvas, Focus drawing tools, Tab in
the toolbar, Escape back to the canvas and Escape releasing input); the Tools window (Ctrl+L, the keyboard on its sections, the Tool
library button, saving the selection to My shapes, inserting, renaming and
deleting it, a tile and Escape, each giving the keyboard back to the canvas; My
shapes is in memory there); the Spotlight menu (its items, and Remove all
censors closing it; the live check adds it and the Zoom menu to its toolbar);
Spotlight and Cursor halo (the yellow ring at the pointer,
the dimming, the Stop buttons); live zoom (the Zoom menu, the lens
window, full-screen zoom's level, both effects on during zoom with the ring at
the pointer, and the Stop buttons; no hook is installed unless `-ZoomMeasure`
asks for the zoom measurements, and `-Shot` with the render harness's path
takes their screen captures with Windows.Graphics.Capture); the color popover
(it stays open after a choice, as on the Mac, and closes with Done or Escape),
tooltips, Use apps and Resume drawing; the text editor (the Text tool opens it
at a click; typing, Backspace and Ctrl+Z, the focused edit field for UI
Automation, the caret's blink and Escape committing); a rectangle's label
(the Text tool edits it; in the stroke color popover Match outline is
unavailable while the label follows the stroke, a palette color gives the
label its own paint, and Match outline makes it follow the stroke again;
Escape closes the popover and commits the label); Settings (Ctrl+, opens it
on General with the keyboard; the share style switch and the theme change the
in-memory profile; Share usage counts shows off and unavailable; About and
Permissions show, and About's Check for updates asks before installing while
the canvas has drawings, with the check's fixed Store answers (it never calls
the Microsoft Store); the Pointer tab's Show halo turns
the halo on and off; on the Toolbar & Shortcuts tab it records, moves with Undo
and deletes tool keys, measures the CPU while a key listens, drags a tool to
More tools, uses Reset, records Ctrl+Alt+Shift+9 for Show / hide (registered
only by the check, never a default shortcut) and sees Draw refuse it through the
recording gate; Alt+F4 hides Settings and the gear opens it again), and the
canvas uses the new key; boards (Save board, Export PNG, a broken board's error
in the error window, Open board and Insert image, with files in the output
folder); the Shift constraints; the on-screen tip after Resume drawing wakes
it, with its Turn off tips button and no status line; the tutorial (welcome
card, Start turning drawing on, steps finished by a key, a drag and an ink key, the click-through layer) and the
coffee reminder after a first-use event; and
region sharing (a custom region, Left half, the output turning live, Cursor halo
drawn in it, the output staying live and following full-screen zoom, a hide and
a show with Show / hide played in the output as copies of the drawings, Stop and
closing; copies of the output window stay in memory); and, at the end, idle work
(over 4 seconds without input, Annotator's own threads and GPU engines use at
most 10 ms a second each; driver threads are reported apart, D-11). The result
checks the pointer watcher's starts (two, without a failure), that the shared
view drew the effects, and that the shared motion played without covers besides
the one each motion starts with. The app writes `result.json` (the objects) and
`canvas.png` (what
the canvas shows, over white) when the script is done. The log says at the
start whether the display shows; while it does, the first click must work on
the stroke popover's first showing, on Settings shown again by the gear, on
the Tools window's File and Extras after Ctrl+L, on the error window's first
OK, on the coffee reminder and on Share a region shown again after a custom
region (`first-click` checks). Each of these clicks waits two compositor
frames after the window shows, as a person aims at what is on screen. While
the display is off a missed first click is noted and retried, because
nothing is composed then.
It takes the keyboard focus for about 100 seconds; run it when that does not disturb anyone, and not while another
live check runs. The
Platform tests' explicit real-input checks
(`-explicit only`, `StackingTests` and `PointerModifierTests`) cover the same
stacking and Shift paths with stand-in windows on a 120-pixel tile.

Dock check with real input, beside a running Annotator (development builds only):

```powershell
dotnet build apps/windows/src/Annotator.App/Annotator.App.csproj -c Debug -p:Platform=x64
pwsh -NoProfile -File tools/windows/dock-check.ps1 -Out <folder outside the repository>
```

It shows the docked toolbar reference state (`--ux-reference toolbar/full-properties-pen
--placement docked`, the app's own toolbar window and placement with an in-memory
profile) and drags its background with SendInput on the display it is on: a free
drop, a drop 3 points inside an edge, and drops at the outermost pixel of the
top, left, bottom and right edges and three quarters along the top. It checks
the ghost (at the edge, gone 4 points inward and after the drop), that each drop
docks at exactly the ghost's frame, flush and centered in the edge's
orientation, and that the toolbar's thread never enters a system move loop
(where Snap layouts appear). Then it presses and holds: Rectangle held still and
let go leaves the docked toolbar where it was, a short press still clicks, a
0.6-second hold then a move picks the toolbar up with the move cursor and never
clicks the button, and the Width slider gets its press at once and never moves
the toolbar. Every press is on the toolbar's background or on the named toolbar
control (WindowFromPoint and UI Automation first), and the release happens only
while the toolbar follows the pointer or over the toolbar. It moves the pointer
for about 20 seconds and takes no keyboard focus.
`-Shot <Annotator.RenderHarness.exe>` also saves the ghost over the toolbar at
the top edge, with everything outside the toolbar's corners blanked.

Icons: `Assets/icon-map.json` maps every Mac SF Symbol name to the Lucide icon
that reads the same: a Lucide name, `{ "lucide": name, "fill": true }` where the
Mac symbol is solid, or `{ "glyph": name }` for one of the original drawings in
its `glyphs` section (Annotator's own artwork on the same 24-unit grid, used where
no Lucide icon reads like the Mac symbol). System-design tools draw their
`SystemDiagramGeometry` outline, as the Mac's `ToolGlyph` does.
After changing the map, regenerate `Icons/LucideIcons.g.cs` from the pinned
`lucide-static` package in a scratch folder outside the repository:

```powershell
npm pack lucide-static@1.59.0; tar -xzf lucide-static-1.59.0.tgz
node tools/windows/update-lucide-icons.mjs --package <scratch>/package
```
