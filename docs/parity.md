# Mac and Windows parity

Annotator ships as two native apps: Swift on macOS (`apps/macos`) and C# on
Windows (`apps/windows`). The Mac app is the behavioral reference. This page is
the contract between them. Code comments cite its requirement IDs (`WIN-...`),
deviations (`DV-...`) and owner decisions (`D-...`).

## The rule

Every user-visible change ships on both platforms in the same version, with
proper parity. No feature is Mac-only or Windows-only. If one platform truly
cannot do something, record a documented deviation below and say so in help.

## Generated references

The Mac generates two references that the Windows tests read. Never edit
generated files by hand; a Mac change that affects them regenerates them in the
same pull request.

- **Conformance corpus** (`reference/conformance`): data, geometry, motion
  curves and reference images. `make conformance` writes it and
  `make conformance-check` compares. CI runs `make conformance-check-data`,
  which skips the rendered PNGs because their pixels depend on the macOS
  version and GPU. Commit the source change first, then
  export, so the manifest names a clean commit. See
  [its README](../reference/conformance/README.md).
- **UX reference** (`reference/ux-reference`): every surface and state at 2x in
  light and dark, with accessibility trees, motion and design values. Windows
  surfaces match its layout within 1 DIP and its labels, identifiers, values and
  order exactly. Regenerate with
  `TEST_RUNNER_ANNOTATOR_CAPTURE_UX_REFERENCE=1 bash tools/macos/build-signed-dev.sh test -only-testing:NativeAppTests/UXReferenceCaptureTests`
  and `python3 tools/macos/ux_reference.py pack`. See
  [its README](../reference/ux-reference/README.md).

The [parity matrix](parity-matrix.md) gives each Mac behavior its parity level.
Where code cites a former numbered specification, see
[former specification numbers](architecture.md#former-specification-and-decision-numbers).

## Scope

In scope: everything the current macOS release ships, as specified in 001,
002, 003, 005, 008 (current scope), 009, 011 and 012, plus Windows
installation, updates, system integration and accessibility.

Out of scope: retired or superseded behavior (window-following masks in 004,
agent draft files in 010, the image workspace, OCR/QR and Capture screen in
008/009), Windows 10, Windows Server, any embedded browser runtime for app UI, recording, and a direct download
outside the Store (see D-7). Windows in S mode installs Store apps, so the
Store package may run there; it is not a release gate.

## Parity levels

Every Mac behavior is assigned one level in the [parity matrix](parity-matrix.md).

- **Identical (I)**: the same observable behavior and outputs. Data and
  geometry match the conformance corpus within the stated numeric tolerance.
  Rendered pixels match within the WIN-REND-05 tolerance.
- **Platform equivalent (E)**: the same purpose, entry points, states, words
  and results, expressed through the Windows mechanism named here.
- **Documented deviation (D)**: an unavoidable difference listed in
  [Documented deviations](#documented-deviations), explained in Windows help
  where it affects people and approved by the owner.
- **Not applicable (N/A)**: the Mac behavior exists only because of a macOS
  mechanism that Windows does not have, such as the Applications move offer.

No other difference is allowed. A difference found in testing is a bug in the
platform that departs from the specifications, unless an ADR changes them.

## Invariants

- Windows 11 version 22H2 (build 22621) or later, on x64 and ARM64 (D-1).
- Native code only: .NET with Native AOT, WinUI 3 for app windows, and Win32
  with Windows.UI.Composition, Direct2D, DirectWrite and Direct3D 11 for the
  desktop overlay (ADR 0014). No WebView, Electron or browser runtime.
- The macOS app is the behavioral reference. This specification maps
  existing requirements to Windows; it does not restate them. A later change
  to a Mac specification applies to Windows unless that change says otherwise.
- The same privacy promises: drawings, screen pixels and keystrokes stay on the
  computer, metrics stay off until the person turns them on, and no drawing
  content is sent anywhere.
- The same product identity: name, A mark, app icon, colors, words and
  original artwork. No SF Symbols or Apple fonts on Windows; their licenses
  limit them to Apple platforms. No code, assets or tests copied from
  PowerToys, ZoomIt or any other product without an approved provenance record.
- The same offer: all current tools are free, with no account or key.
- Files move between platforms: boards, saved shapes, settings profiles and
  annotation clipboard payloads written on either platform open on the other.

## Principal scenarios

1. A presenter shares the entire screen in Teams, draws on PowerPoint, edits
   the drawing, returns to PowerPoint with Use apps and never loses input.
2. Ctrl+1 zooms around the pointer; clicks reach the control under the pointer
   and meeting viewers see the zoom.
3. A board saved on a Mac opens on Windows with the same objects, styles,
   labels and positions, and opens again on the Mac after a Windows save.
4. A Blur region hides part of the live screen from viewers. Any capture
   failure turns the region opaque; it never shows unfiltered pixels.
5. A presenter shares the left half of a display through the
   **Annotator - Shared Region** window in a meeting app.
6. A new user installs from the Store or `winget` without security warnings,
   starts drawing within a minute and receives updates automatically.

## Requirements

### Parity governance

- **WIN-PAR-01**: Every requirement family in specifications 001-012 and 014 applies
  to Windows unless the parity matrix marks it N/A or out of scope.
- **WIN-PAR-02**: The Mac implementation generates the conformance corpus in
  `reference/conformance` (plan task M-1). Windows tests consume it unchanged.
  Expected outputs are never edited by hand; a behavior change regenerates them
  from the Mac reference in the same change.
- **WIN-PAR-03**: After Windows is released, each user-visible change ships on
  both platforms in the same version. Otherwise the change records a temporary
  deviation with an owner-approved end version.
- **WIN-PAR-04**: Stable identifiers are shared verbatim: tool raw values,
  `ShortcutAction` values, serialized keys, file schema numbers, preference
  keys, clipboard type names, animation style identifiers and accessibility
  identifiers.
- **WIN-PAR-05**: App copy is the Mac copy, changed only where the
  [platform terms](#platform-terms) table requires a Windows word. Copy changes
  follow the repository writing rules (plain B1-B2 English, `no-ai-slop`).
- **WIN-PAR-06**: The Mac generates a UX reference in `reference/ux-reference/macos`
  (plan task M-4): every surface and state at 2x in light and dark, with its
  accessibility tree (labels, identifiers, values and frames in points), motion
  contact sheets and videos, cursors and design values. Windows surfaces match
  its layout within 1 DIP and its labels, values and order exactly; visuals
  follow the parity levels. A change to the macOS interface regenerates the
  reference in the same change. Read `reference/ux-reference/README.md` for its
  limits.

### Annotation model and files

- **WIN-CORE-01**: Port AnnotationCore to a C# library with no Windows UI
  dependency. It builds and its tests run on Windows, macOS and Linux.
- **WIN-CORE-02**: Read and write the same JSON formats: boards (`.annotator`,
  schemas 1-3 and the rules for choosing them), saved shapes, preference
  profiles (schemas 1-2) and the `app.annotator.annotations` clipboard payload.
  Validation, limits, refusal of unknown or newer schemas and every error
  message match the corpus. A file written on one platform and read on the
  other decodes to an equal model (semantic equality; byte-identical JSON is
  not required).
- **WIN-CORE-03**: Transactions, cancellation, undo/redo, coordinated history
  across displays and all budgets match: 64 MiB history, 128 MiB board import,
  8 MiB per saved shape, 64 MiB and 256 entries for the library, names of 1-120
  characters without control characters.
- **WIN-CORE-04**: Geometry is identical within 1e-6 points against the corpus:
  rough shapes, arrowheads and curved heads, diagram and system-design shapes,
  connector anchors and the 18-point attach rule, Shift snapping to 45 degrees,
  Smart Draw recognition and its preview, label layout and hit testing.
  Discrete outcomes (which object is hit, which shape is recognized, which
  anchor is used) are exactly equal.
- **WIN-CORE-05**: Seeded sketch randomness uses the same generator and seed
  rules as Swift, ported exactly. Platform random APIs are not used for
  geometry.
- **WIN-CORE-06**: Coordinates use points, where one point is one Windows
  device-independent pixel (DIP, 1/96 inch at 100% scale). Positions are
  display-local with a top-left origin, as on the Mac. Boards are not rescaled
  when they move between platforms.
- **WIN-CORE-07**: The clipboard uses a registered format named
  `app.annotator.annotations` with the same UTF-8 board payload. Image paste
  accepts PNG and bitmap formats from other apps. Paste order, placement under
  the pointer, the 18-point step and the locked-object rules match 005.

### Rendering

- **WIN-REND-01**: A Direct2D renderer reproduces the Mac scene renderer:
  strokes, caps, joins, widths, dash patterns, roughness, fills and hatching,
  two-color and multicolor gradients in local bounds, independent stroke and
  fill opacity, labels, arrows, counters, rulers, image layers and covers.
- **WIN-REND-02**: Pixel effects reproduce the Mac pipeline order and
  parameters: covered pixels are removed before any filter samples them;
  Blur is a Gaussian blur with radius `amount x backing scale`; Pixelate uses
  cells of `amount x backing scale` aligned to the region center; Distort is a
  twirl centered on the region with radius `max(width, height)` and angle
  `amount / 100 x 2 pi`; strength zero leaves pixels unchanged; solid covers
  win over every effect regardless of order or opacity. Shaders are HLSL
  matched to the Core Image output in the corpus.
- **WIN-REND-03**: Text uses DirectWrite with the font rule chosen in D-4 and
  the Mac layout rules: content-sized frames (width rounded up plus 2 points,
  at least 32), explicit line breaks only, centered shape labels and font
  scaling limits on resize. Glyphs sit where the Mac's do: the first line's
  baseline is one Mac ascender (0.967 em in SF Pro and SF Mono, from the
  corpus) below the top of the frame, or, for a shape label, below the top of
  the Mac's content-sized frame centered on the shape. Frames and the Windows
  fonts' line spacing do not change.
- **WIN-REND-04**: Colors are sRGB with premultiplied alpha. On HDR or
  wide-color displays the overlay renders as SDR content, like other SDR apps.
- **WIN-REND-05**: Rendering tolerance against the Mac reference images, at
  1x and 2x scale: flat color areas within 1/255 per channel; edges within one
  device pixel: each channel lies within the range the other image shows
  within one pixel, plus 32/255 for antialiasing; at least 99.5% of other
  pixels within CIEDE2000 2.0. At most one flat or edge pixel in 10,000 may
  break its rule. Pixel effects meet the tolerance without the 32/255
  allowance or exceptions, and no channel may be more than 2/255 apart.
  Scenes that depend on system fonts compare the layout rules of WIN-REND-03
  and the first baseline (within 0.05 em or one device pixel) instead of
  glyph pixels. Scaled images use high-quality cubic interpolation, which no
  Direct2D mode makes identical to Core Graphics; scenes that depend on it must
  stay within the distance measured in S-13. Spike S-13 calibrated these
  thresholds on WARP, and the owner approved them on 2026-10-19 (see
  [Recorded owner decisions](#recorded-owner-decisions)). They are frozen;
  only an ADR can loosen them.
- **WIN-REND-06**: Every Windows scale factor renders sharply, including 125%,
  150% and 175%. Windows never stretches a bitmap of the overlay or toolbar.
  The process is Per-Monitor DPI Aware v2.

### Desktop overlay and windows

- **WIN-OVR-01**: Each display has its own overlay window covering the whole
  monitor, including the taskbar area. Overlays are borderless, topmost tool
  windows with no taskbar button, no Alt+Tab entry and no system shadow. Their
  content is composited with per-pixel alpha by the GPU, without the layered
  window CPU path.
- **WIN-OVR-02**: Off, Use apps and Draw match 002. Use apps makes every
  overlay click-through at once; no display can keep input alone.
- **WIN-OVR-03**: Focus (E, see DV-1). Entering Draw activates the overlay of
  the target display so keys reach it, and remembers the previously active
  window. As on the Mac, switching to another app does not end Draw; the next
  click on a canvas activates it again. Use apps, Hide and the Escape press
  that releases input return focus to the remembered window if it still
  exists. Toolbar clicks do
  not activate Annotator (`WS_EX_NOACTIVATE`); choosing a drawing tool returns
  focus to the ready canvas, as in 009. Fields that need typing activate the
  toolbar only while they are edited, then return focus.
- **WIN-OVR-04**: Annotator windows keep the Mac stacking order: canvas, then
  effects, selection adornments, HUD and toolbar, then popovers and error
  windows. When another app's always-on-top window covers an overlay,
  Annotator restores its order once per foreground change or display change,
  with no timers or repeated fights. Windows system surfaces (Start,
  notifications, Alt+Tab, Task View, the touch keyboard, UAC and the lock
  screen) can appear above drawings (DV-2).
- **WIN-OVR-05**: Multi-display behavior matches 002 and ADR 0006, using each
  monitor's work area (the area outside the taskbar) where the Mac uses the
  visible frame. Displays with different scale factors keep positions in
  points. Display, scale and taskbar changes cancel pending edits and release
  drawing input, as on the Mac.
- **WIN-OVR-06**: Virtual desktops replace Spaces. Overlays and the toolbar
  stay visible after a desktop switch. A switch runs the WIN-OVR-09 suspension
  without cancelling a pending zoom start, matching the Mac Space-change rule.
- **WIN-OVR-07**: Overlays appear above borderless full-screen apps such as a
  PowerPoint slide show, browsers in full screen and full-screen meeting
  views. Exclusive full-screen games are not supported (DV-6).
- **WIN-OVR-08**: Drawing above elevated (administrator) windows works and
  global shortcuts keep working. Alt+scroll and the global Escape do not reach
  Annotator while an elevated window is in front (DV-3).
- **WIN-OVR-09**: Lock, sleep, sign-out, fast user switching and Remote
  Desktop connect or disconnect run the Mac suspension
  (`suspendPresentation`): stop region sharing with its safety message, cancel
  a pending zoom start, finish any zoom gesture and stop zoom, commit text that
  is being edited, pause capture with effect regions covered opaquely, release
  drawing input and show the same status message.
- **WIN-OVR-10**: Toolbar dragging and docking match 009: docking at the
  outermost point of an edge, the ghost preview, orientation changes and
  clamping inside the work area. The drag is app-owned, so Windows Snap
  layouts never compete with it.
- **WIN-OVR-11**: Drawing cursors show the tool footprint scaled by stroke
  width and zoom, generated for each monitor scale as real system cursors.
  Spike S-5 measures the largest cursor Windows draws without a software
  fallback; footprints beyond it follow the Mac cap.
- **WIN-OVR-12**: Alt+F4 while an Annotator overlay or toolbar is active
  releases drawing input like Use apps. It never closes overlays or quits the
  app; Quit stays in the tray menu, as on the Mac.

### Input

- **WIN-INP-01**: Pointer input uses Windows pointer messages for mouse, pen
  and touch, with the full pointer history so no samples are lost. Ink
  pressure uses the pen pressure value when the pen reports one; mouse, touchpad
  and touch strokes get the Mac's simulated pressure (`InkPressure`, from the
  sample times). Touch draws with one finger like a
  mouse (D-9). Long strokes keep every sample, as in 001.
- **WIN-INP-02**: Local keys follow the [keyboard mapping](#keyboard-mapping):
  Command becomes Ctrl, Control stays Ctrl, Option becomes Alt.
- **WIN-INP-03**: Tool and button keys use the character the active keyboard
  layout produces without modifiers, like the Mac's
  `charactersIgnoringModifiers`. Recording a digit uses the physical
  number-row key, like the Mac key-code digit table. Reserved ink letters,
  Shift rules and refusal messages match 005.
- **WIN-INP-04**: Global shortcuts use `RegisterHotKey`. Modifiers map
  control to Ctrl, option to Alt, command to Win and shift to Shift. Profiles
  keep storing the macOS key code of the same physical key through the
  corpus key table, so profiles move between platforms. Windows refuses a
  chord that types a character in any installed keyboard layout (Ctrl+Alt is
  AltGr on many layouts), and chords that Windows reserves. Registration
  failures keep the previous binding, and a failed profile restores the
  previous set, as on the Mac.
- **WIN-INP-05**: Default global shortcuts (D-5): Show / hide Ctrl+2,
  Resume drawing Ctrl+Shift+2, Live zoom Ctrl+1, Hide only Ctrl+Alt+Shift+2.
  If a default is refused under WIN-INP-04 or owned by another app (for
  example ZoomIt), Annotator keeps running, leaves that action without a
  shortcut and says why in Settings and in the tutorial.
- **WIN-INP-06**: Alt+scroll replaces Option-scroll. A low-level mouse hook
  exists only while the toolbar is visible or zoom is active, the same
  lifetime as the Mac listener. It runs on its own thread with a message loop,
  does no work beyond handing events to the app thread and returns within
  1 ms at p99. It consumes only the scroll events it uses. After a consumed
  scroll, releasing Alt must not open the front app's menu or ribbon key tips.
- **WIN-INP-07**: While zoom is active, a low-level keyboard hook consumes an
  unmodified Escape and exits zoom, exactly as the Mac event tap does. The
  hook does not exist at any other time.
- **WIN-INP-08**: Windows needs no permission for hotkeys or hooks. The
  Permissions tab says so instead of offering Accessibility access. A hook
  that stops working falls back to the toolbar zoom controls and shows the
  same interrupted state as the Mac.
- **WIN-INP-09**: Text entry uses the Windows Text Services Framework through
  the editor chosen in spike S-7. It supports IME composition (Japanese,
  Chinese and Korean at minimum), the emoji panel (Win+.), voice typing
  (Win+H), the touch keyboard, right-to-left text, clipboard and undo inside
  the editor. The committed text matches the edited text within 0.5 points;
  nothing jumps at commit. The first Escape during IME composition cancels the
  composition; later presses follow the 005 and 009 text rules.
- **WIN-INP-10**: Copy, Cut and Paste work in the canvas, the right-click menu
  and the toolbar; text fields keep their own clipboard behavior.

### Capture, zoom, effects and region sharing

- **WIN-CAP-01**: Screen pixels are read only while zoom, the pointer lens, an
  effect or region sharing needs them. Annotator's own windows are never part
  of the pixel source. Frames use bounded newest-frame buffering on the GPU,
  with no per-frame CPU copy. Capture stops when no consumer needs it.
- **WIN-CAP-02**: The pixel source is chosen by Gate 0 spike S-1:
  - Method A (preferred): an in-process Magnification API window renders the
    display at 1x with every Annotator top-level window in its exclusion list,
    kept current whenever Annotator creates a window. Windows.Graphics.Capture
    reads that window. Direct3D 11 shaders apply effects and the result is drawn
    in the overlay's composition tree.
  - Zoom and the pointer lens may show Magnification API windows directly when
    that meets WIN-CAP-05 and the performance budgets.
  - If method A fails, method B renders Blur with a host backdrop brush and a
    Gaussian blur in the overlay's own composition tree, and method C serves
    region sharing from display capture with the limits in DV-9. Pixelate and
    Distort have no fallback; the owner decides under D-3.
- **WIN-CAP-03**: Failures stay closed. A capture or render failure, a denied
  privacy setting, a transparency or power-saving fallback, lock, sleep or a
  display change turns effect regions opaque with the Mac messages. Windows
  never shows unfiltered or stale source pixels in place of an effect.
- **WIN-CAP-04**: Permission states follow the Mac rules: requested only by an
  action that needs pixels, "allowed" kept separate from "working", and a
  clear next step. If the app uses programmatic capture, Windows is expected
  to list it under Settings > Privacy & security > Screenshots and screen
  recording (R-6; S-1 confirms); the Permissions tab then shows that state and
  opens that page. The yellow capture
  border must never appear. Spike S-1 confirms a border-free path; if borderless
  capture needs consent, Annotator asks only when a pixel feature starts.
- **WIN-CAP-05**: Zoom matches 003 and 009: Ctrl+1 starts at 1.5x; Alt+scroll
  changes 1x-16x; ramps take 260 ms in and 200 ms out with the shared curves;
  the viewport origin is `p x (1 - 1/z)` so the pointer stays on the same
  source point; drawings, selection adornments and effects use the same
  viewport in the same frame; text editing freezes the viewport; with
  animation effects off, changes are immediate. Windows' full-screen magnifier
  (`MagSetFullscreenTransform`) is not used: recordings and screen shares do
  not show it (R-4), and its input transform needs UIAccess (R-3).
- **WIN-CAP-06**: The pointer lens matches 003: size, scale, crosshair at the
  real source point and a 120 ms fade. It keeps its own shadow and stays local
  in region sharing.
- **WIN-CAP-07**: When the presenter shares an entire screen in Teams, Zoom or
  Google Meet, viewers see drawings, effects, zoom, Spotlight, the pointer halo
  and the toolbar, as on the Mac. Sharing one window can leave overlays out,
  also as on the Mac. Receiver-side checks are physical gates.
- **WIN-CAP-08**: Region sharing matches 012. The output is an ordinary
  top-level window titled **Annotator - Shared Region**. It opens behind other
  apps without taking focus and keeps updating while covered. Meeting apps
  cannot capture a minimized window, so help says not to minimize it. Resizing
  keeps the region's aspect ratio with letterboxing. Source pixels are cropped
  before filters run. The visible toolbar appears through a borderless capture
  of Annotator's own toolbar window, or a XAML render if S-11 shows that is
  identical. The tray icon shows a red dot while sharing is live. Every state,
  failure and stop rule from 012 applies.
- **WIN-CAP-09**: Spotlight, the pointer halo and click effects draw only on
  the overlay and match 003 and 005, including timings and shapes.

### App windows

- **WIN-UI-01**: An icon in the system tray replaces the menu-bar item: the
  monochrome A glyph, light and dark versions that follow the taskbar's own
  theme, a contrast-theme version and the red dot while region sharing is live.
  A left or right click (and Shift+F10 or the menu key) opens a flyout with
  the Mac popover's content and states. Escape and outside
  clicks dismiss without drawing or stopping zoom. Keyboard and Narrator work.
  Event hooks exist only while the flyout is open. Windows 11 may put new
  icons in the hidden overflow and apps cannot pin themselves; the tutorial's
  finish card shows how to keep the icon visible (DV-4).
- **WIN-UI-02**: The toolbar and properties match 002 and ADR 0012 in points:
  exactly as wide as its tools and buttons on one row (923 with the default
  tools), 72 with the properties hidden, about 146 with them shown, 44 by 48
  tool targets, 34 for Minimal, a 15-point corner radius, and the same
  controls, states and separators. Only a screen narrower than 700 points
  (420 for Minimal) moves the buttons to a second row. The background is the
  theme's window background color rather than Acrylic, so live and snapshot
  appearances are identical. Every one of the toolbar's own buttons can be
  turned off in Settings without leaving a gap. Properties are shown or
  hidden, and stay on one row: the toolbar grows sideways to fit them. After
  the delay chosen in Settings, the toolbar shrinks to one capsule with the
  mark and Draw or Apps (Core `ToolbarMorph` timings for the faces; the
  window resizes at once, DV-12); pointing at it opens it
  at once, and Settings plays a short demo of it. Focus rings appear only
  during keyboard navigation. Pick from screen replaces the Mac's system color
  sampler with a magnifier loupe that follows the pointer; a click reads only
  that pixel of the composed screen and Escape cancels. It needs no
  permission.
- **WIN-UI-03**: Settings is a resizable, nonmodal window opened by Ctrl+, or
  the gear, about 1420 by 700 by default and bounded by the work area. It has the
  same six tabs in the same order: General, Toolbar & Shortcuts, Pointer,
  Permissions, Help and About. It may use Windows' Mica backdrop because it
  never appears in drawing snapshots.
- **WIN-UI-04**: The hands-on tutorial matches the Mac: the welcome card, the
  same ten steps and goals (`AnnotationCore/Tutorial.swift`,
  `Annotator.Core/Tutorial`), the same words with Windows key names shown as
  keycaps in the lines, the same keyboard for shortcuts with modifiers and Esc
  (`TutorialKeyboard`) with the Windows bottom row, the ghost cursor, ring and
  confetti with the same timings, cards that never take the keyboard while
  coaching, no permission requests, and automatic presentation without taking
  focus until the tutorial is finished or Show on launch is off. Start turns
  drawing on. Every step accepts the keyboard or the mouse: it checks the
  workspace's result, never the input. The cards have the Mac's sizes,
  places and accessibility elements (`reference/ux-reference/macos/tutorial`).
  Lines and check boxes are a few points narrower or wider than the Mac's,
  because Segoe UI is narrower than SF (D-4) and Windows keycaps name their
  keys ("Ctrl+2" where the Mac shows ⌃2). The keyboard's bottom row (Ctrl, Win,
  Alt) is shorter than the Mac's (fn, ⌃, ⌥, ⌘), so its frame is narrower. Only
  Windows' finish card adds the tray icon (DV-4) and shortcut (DV-10) cards,
  which make it taller.
- **WIN-UI-05**: The Tools window, the error window, Report a problem, support
  information and What's changed match 005. What's changed is bundled and
  matched to the running Windows version and build.
- **WIN-UI-06**: Icons: tool glyphs use the same vector geometry as the Mac.
  Mac SF Symbols are replaced by the icon set chosen in D-6. One table in the
  plan maps every SF Symbol name to its Windows icon.
- **WIN-UI-07**: System, Light and Dark themes change app windows only. Windows
  contrast themes change app window colors and never annotation colors.
- **WIN-UI-08**: The Applications move offer is N/A: Windows installs MSIX
  packages in a managed location.

### Motion

- **WIN-MOT-01**: Every duration, curve, delay, stagger, budget and
  reduced-motion rule in 003, 005 and 009 applies, including the 2026-10-13
  motion pass. Curves are the same cubic Beziers. Sampled curves match the
  corpus within 1e-4.
- **WIN-MOT-02**: Workspace show and hide styles match 009: Dissolve, Gather,
  Magic, Corners, Pop, Drop, Soft focus, Fade and Off, with per-element
  snapshots, ordering, reversal, the 40-million-pixel snapshot budget, the
  64-element limit and message, particle caps of 96,000 plus 32,000 samples, no
  idle animation loops and no flash at the hand-off to live canvases.
- **WIN-MOT-03**: Windows' Animation effects setting off is Reduce Motion.
- **WIN-MOT-04**: Animations run at the display's native refresh rate,
  including 120 and 144 Hz, driven by the compositor or by presents paced to
  the display.

### System integration

- **WIN-SYS-01**: Launch at sign-in replaces launch at login, using the MSIX
  startup task. It is off by default and only an explicit toggle changes it.
  States map to the 005 states: Disabled is Off, Enabled is On, DisabledByUser
  says it was turned off in Windows and must be turned on in Settings > Apps >
  Startup, and the two policy states say the organization manages it and
  disable the toggle. A startup-task launch is quiet: tray icon and shortcuts
  only.
- **WIN-SYS-02**: Only one instance runs. Opening the app again shows the
  toolbar, like reopening the Mac app.
- **WIN-SYS-03**: Updates come from the Microsoft Store. Check for updates in
  the flyout and Settings asks the Store and offers the update. Annotator
  never closes itself for an update while drawings are visible; it asks first.
  If Windows closes Annotator to install an update, Windows starts it again
  (RegisterApplicationRestart, updates only; the Mac's Sparkle relaunch): as an
  ordinary launch after Check for updates, and quietly, with the tray icon
  only, after an update the Store installed by itself.
  There is no separate updater. The portable copy (WIN-DST-09) checks the
  website's feed (`updates/windows-<track>.json`, written by
  `tools/release/distribution.py`) at launch and when the tray flyout or
  Settings opens, at most once a day while Automatically check for updates is
  on (the default, like the Mac's daily Sparkle check). Opening the tray
  flyout also checks quietly when the last check is at least 6 hours old,
  like the Mac's menu (Sparkle's `checkForUpdateInformation`); a Store copy
  asks the Store the same way. A quiet check never asks anything; it only
  turns the open flyout's row into Update available…, which stays until a
  later check finds no update. It never downloads or
  installs anything: Update available… in the flyout and Settings says where
  to get the release, with Open Downloads page... and Copy winget command, and
  an automatic check asks about a new release once, only when nothing is being
  presented (DV-11). After any update, What's changed shows the new version's
  notes once, as on the Mac.
- **WIN-SYS-04**: `.annotator` files open in Annotator through the package's
  file association, with the same rules as Open board. Open, Save and Export
  use the Windows file dialogs. Saves are atomic.
- **WIN-SYS-05**: Preferences and My shapes live in the package's local data
  folder. Uninstalling removes them (DV-5); Help says to export a profile and
  save boards first.
- **WIN-SYS-06**: A crash removes overlays, as on the Mac. Annotator sends no
  crash reports of its own; Windows Error Reporting stays under Windows
  settings.
- **WIN-SYS-07**: When nothing moves and no input arrives, the app runs no
  timers, polling or presents. Hooks exist only for the lifetimes in WIN-INP-06
  and WIN-INP-07. Energy saver may turn off transparency effects; WIN-CAP-03
  then keeps method B regions opaque. The portable copy's daily update check
  runs at launch and when the tray flyout or Settings opens, never on a timer.

### Accessibility

- **WIN-A11Y-01**: Every Mac accessibility label, value and identifier has a
  UI Automation name, value and AutomationId with the same text. Narrator
  reads the toolbar, properties, flyout, Settings and the tutorial cards.
- **WIN-A11Y-02**: Everything in app windows works from the keyboard. Mac
  VoiceOver actions, such as Move left or Remove from toolbar, become context
  menu items with the same names, reachable with Shift+F10 or the Menu key.
- **WIN-A11Y-03**: Windows contrast themes, text size up to 225% in app
  windows and Animation effects are honored. Larger text never clips controls;
  the short-height scrolling rules from 002 apply.
- **WIN-A11Y-04**: Escape and input release always work, including while the
  toolbar has focus or an IME is composing.

### Privacy and metrics

- **WIN-PRV-01**: Metrics consent, categories, buckets and limits match 011.
  The Windows payload adds `platform: "windows"`, reports `osMajor` 11 and
  `architecture` `arm64` or `x86_64`. Specification 011 and the service accept
  that field since 2026-10-20, and the deployed service since 2026-10-21. Windows
  still sends nothing until the owner turns on
  `UsageMetrics.ServiceAcceptsWindows` in a release; until then the Windows
  Settings say metrics are unavailable.
- **WIN-PRV-02**: Privacy copy also states what the Store and Windows collect
  outside Annotator: Partner Center acquisition and install counts and
  Windows' own diagnostics.
- **WIN-PRV-03**: The app makes no network requests except opt-in metrics,
  Store update checks through Windows, the portable copy's update check (one
  HTTPS request for the website's feed, sending only the app version as its
  user agent; Automatically check for updates turns the daily check off) and
  links the person opens.
- **WIN-PRV-04**: Support information stays coarse: app version and build,
  Windows version and build number, architecture and capability states, with no
  paths, user names or document names.

### Distribution and updates

- **WIN-DST-01**: The Microsoft Store is the only channel at first (D-7):
  an MSIX bundle for x64 and ARM64, signed by the Store, under a free
  individual developer account. The owner completes the account's identity
  check; no agent creates accounts or submits packages.
- **WIN-DST-02**: Website and help give the `winget` command with the Store
  product ID: `winget install --id <ID> --source msstore`. No winget-pkgs
  manifest is needed for the Store channel.
- **WIN-DST-03**: Alpha and beta testers use Store package flights limited to
  the owner's tester groups. The listing stays private until the owner's launch
  decision, matching the Mac prerelease rules.
- **WIN-DST-04**: The MSIX version is `(Major + 1).Minor.Build.0`. Build is the
  shared, always-increasing build number (`CURRENT_PROJECT_VERSION`). The Store
  requires the first number to be at least 1 and reserves the fourth, so
  0.1.0 build 26 becomes 1.1.26.0, and every later release still sorts higher.
  About shows the shared label, such as `0.1.0-beta.8 (26)`.
- **WIN-DST-05**: An owner-run, resumable Windows publisher mirrors the Mac
  release handoff: freeze source, build, run local validation, run the
  Windows App Certification Kit, then submit with the Microsoft Store CLI only
  after explicit owner approval.
- **WIN-DST-06**: Installing needs no administrator rights, restart or UAC
  prompt. Uninstalling from Settings > Apps leaves only files the person saved.
- **WIN-DST-07**: The Store channel costs nothing. Paid signing becomes
  necessary only for a direct download: Azure Artifact Signing (about $9.99 a
  month, individuals only in the USA and Canada) or an OV certificate (about
  $150-300 a year).
- **WIN-DST-08**: Website changes for Windows follow 007 and describe only an
  actually published listing.
- **WIN-DST-09** (owner decision, 2026-10-22): every release also ships the
  Windows app for people without the Microsoft Store: a portable ZIP per
  architecture (`Annotator-<label>-windows-<x64|arm64>.zip`, Annotator.exe at
  the root) on the same public GitHub release as the Mac files, described by
  `annotator-windows.json`, and our own winget package
  (`winget install DorLugasiGal.Annotator`, a portable install from those
  ZIPs). The portable copy is a release, not a development preview: it is built
  with `AnnotatorDistribution=portable`, launches at sign-in through the
  account's Run entry, and says that winget or a new download updates it. A
  release build also gets the website's update feed (`package.ps1
  -UpdateSite`), so it checks for new releases itself (WIN-SYS-03). It is
  not code-signed until the owner chooses a certificate (WIN-DST-07), so a
  direct download can show a SmartScreen warning. winget can't use a plain
  repository as its own source, so the manifests go to microsoft/winget-pkgs
  through an owner-approved pull request.

### Performance

- **WIN-PERF-01**: The 003 workloads and targets apply unchanged: 200 mixed
  elements plus a 20,000-sample stroke, and a 1,000-element, 200,000-sample
  stress scene; p95 app frame work at most 8 ms and input-to-visible at most
  33 ms at 60 Hz. Results use the JSON shape of `tools/macos/benchmark.sh` with
  machine, Windows build and app build recorded. On Windows the 60 Hz
  input-to-visible target is read as recorded on 2026-10-20 (see
  [Recorded owner decisions](#recorded-owner-decisions)): p95 at most two
  display refreshes and the median at most 33 ms.
- **WIN-PERF-02**: Targets to measure, not claims: toolbar visible within
  1 second of a cold start on the reference machine; a quiet startup-task launch
  using under 0.5 seconds of CPU; working set at most 150 MB with the toolbar
  shown and empty boards. Plan task M-3 records the Mac numbers for the same
  workloads.
- **WIN-PERF-03**: With no input and nothing moving, the app uses no CPU or
  GPU time.
- **WIN-PERF-04**: Pen ink may use Windows 11's delegated ink trail when it
  lowers latency without changing committed geometry.
- **WIN-PERF-05**: One capture source per display serves all consumers.
- **WIN-PERF-06**: Measurements use in-app frame timing, PresentMon for
  present-to-display timing, Windows Performance Recorder traces, and a
  high-speed camera for the physical input-to-photon gate.

### AI control

- **WIN-AI-01**: The `annotator` computer-use skill works on Windows through
  UI Automation with the same names. The skill gains Windows sections (Ctrl
  keys, Snipping Tool with Win+Shift+S and its recorder, Windows Settings
  paths) while the phrases pinned for the Mac stay unchanged. The Windows app
  bundles it and exports it from Help, like the Mac app.

## Platform mapping

| macOS | Windows | Level |
| --- | --- | --- |
| Menu-bar item and SwiftUI popover | System tray icon and flyout (left or right click) | E |
| Non-activating `NSPanel` key windows | `WS_EX_NOACTIVATE` toolbar; Draw activates the overlay | E, DV-1 |
| Window levels, `canJoinAllSpaces` | Topmost band with relative order; virtual desktops | E, DV-2 |
| Points at 1x and 2x | DIPs, Per-Monitor DPI Aware v2, 100-500% | I |
| ScreenCaptureKit with app filter | Method A, B or C (S-1) | E, pending |
| Screen Recording permission | Screenshots and screen recording privacy page, if used | E |
| Accessibility permission for event taps | None; low-level hooks | E |
| Carbon hotkeys | `RegisterHotKey` | I |
| Event tap for scroll and Escape | Low-level mouse and keyboard hooks | I, DV-3 |
| `NSTextView` text and IME | TSF editor from S-7 | E |
| Core Graphics and AppKit drawing | Direct2D and DirectWrite | I |
| Core Image filters | HLSL shaders in Direct3D 11 | I |
| Metal particles | Direct3D 11 compute and instancing | I |
| Core Animation | Windows.UI.Composition with cubic Bezier easing | I |
| SwiftUI app windows | WinUI 3 | I in layout, E in control look |
| SF Symbols | Icon set from D-6 | D-6 |
| SF Pro and SF Mono | Font rule from D-4 | D-4 |
| Sparkle | Microsoft Store updates; the portable copy's feed check (DV-11) | E |
| `SMAppService.mainApp` | MSIX startup task | E |
| Applications move offer | None | N/A |
| UserDefaults | JSON settings in the package's local data | E |
| Private pasteboard type | Registered clipboard format, same name | I |
| VoiceOver | Narrator and UI Automation | E |
| Reduce Motion | Animation effects | E |
| Increase Contrast | Contrast themes | E |
| Sleep, lock and Space notifications | Power, session and virtual desktop notifications | E |
| Display reconfiguration | Display, DPI and work-area change messages | E |
| Developer ID and notarization | Store signing | E |
| Homebrew cask | `winget` through `msstore` | E |
| Universal app | MSIX bundle for x64 and ARM64 | E |

## Keyboard mapping

Global shortcuts (Windows defaults; see D-5):

| Action | macOS default | Windows default |
| --- | --- | --- |
| Show / hide | Control-2 | Ctrl+2 |
| Hide only | Control-Option-2 | Ctrl+Alt+Shift+2 |
| Resume drawing | Control-Shift-2 | Ctrl+Shift+2 |
| Live zoom | Control-1 | Ctrl+1 |

Canvas keys, taken from the Mac canvas key handler:

| Action | macOS | Windows |
| --- | --- | --- |
| Commit text, cancel a gesture, stop zoom or lens, then release input | Escape | Escape |
| Undo | Command-Z | Ctrl+Z |
| Redo | Shift-Command-Z | Ctrl+Shift+Z and Ctrl+Y |
| Select all on every display | Command-A | Ctrl+A |
| Duplicate | Command-D | Ctrl+D |
| Group / ungroup | Command-G / Shift-Command-G | Ctrl+G / Ctrl+Shift+G |
| Save / open board | Command-S / Command-O | Ctrl+S / Ctrl+O |
| Copy / cut / paste | Command-C / X / V | Ctrl+C / X / V |
| Tool library | Command-L | Ctrl+L |
| Whiteboard / blackboard | Control-W / Control-B | Ctrl+W / Ctrl+B |
| Nudge 1 / 10 points | Arrow / Shift-Arrow | Arrow / Shift+Arrow |
| Delete selection, or clear every display if none | Delete or Forward Delete | Backspace or Delete |
| Finish a clicked arrow | Return | Enter |
| Commit text | Command-Return | Ctrl+Enter |
| Settings | Command-, | Ctrl+, |
| Change zoom | Option-scroll | Alt+scroll |
| Ink colors, tool and button keys | Configured keys without modifiers | Same |

Windows shows shortcuts as text keycaps (Ctrl, Alt, Shift, Win) rather than
Mac modifier symbols. Ctrl+W and Ctrl+B do not collide with any former
Command shortcut, because the Mac canvas uses no Command-W or Command-B.

## Platform terms

| Mac term | Windows term |
| --- | --- |
| menu bar, menu-bar icon | system tray, tray icon (near the clock) |
| Command, Option, Control | Ctrl, Alt, Ctrl |
| System Settings | Windows Settings |
| Login Items, launch at login | Startup apps, launch at sign-in |
| Screen Recording | Screenshots and screen recording (only if used) |
| Accessibility permission | not needed; say so |
| Finder | File Explorer |
| Screenshot tool (Shift-Command-5) | Snipping Tool (Win+Shift+S), including its recorder |
| VoiceOver | Narrator |
| Reduce motion | Animation effects |
| Spaces | virtual desktops |
| Quit | Quit (same automation identifier) |
| this Mac, your Mac (break timer) | this computer, your computer |
| Control-Command-Q (lock) | Win+L |

## Documented deviations

These need owner approval before Windows release.

- **DV-1 Focus**: while Draw is on, Annotator is the active window, so the
  presenter's app shows as inactive. The Mac keeps the other app active.
- **DV-2 System surfaces**: Start, notifications, Alt+Tab, Task View, Snap
  layouts, the touch keyboard, UAC and the lock screen can appear above
  drawings. UIAccess could raise overlays above some of these, but packaged
  apps probably cannot use it (R-12).
- **DV-3 Elevated apps**: while an administrator app is in front,
  Alt+scroll and the global Escape do not reach Annotator; the toolbar zoom
  controls still work.
- **DV-4 Tray overflow**: Windows can hide the tray icon in the overflow and
  apps cannot pin themselves.
- **DV-5 Uninstall**: uninstalling removes settings and My shapes.
- **DV-6 Games**: exclusive full-screen games may hide overlays.
- **DV-7 Fonts** (D-4): annotation text uses Segoe UI Variable and Cascadia
  Mono, so glyph shapes and widths differ from SF Pro and SF Mono, and frames
  measured on Windows are taller (a Segoe UI Variable line is 1.33 em, an SF
  Pro line 1.18 em). The first baseline sits where the Mac's does
  (WIN-REND-03).
- **DV-8 Icons**: pending D-6.
- **DV-9 Capture fallback**: only if S-1 rejects method A. Method B blurs with
  the system's backdrop rules (opaque when transparency effects are off).
  Method C captures the display including Annotator's windows: the output
  window must not show inside its own region, selection handles and guides
  move to windows hidden from every capture (also from full-screen meeting
  shares), and a blur that crosses the region edge may include blurred pixels
  from just outside it.
- **DV-10 Shortcut conflicts**: Ctrl+1 and Ctrl+2 replace tab switching in
  browsers and other app shortcuts while Annotator runs. The tutorial's
  finish card says how to change them.
- **DV-11 Portable updates** (owner decision, 2026-10-24): the portable copy
  finds new releases like Sparkle but doesn't install them. The copy isn't
  code-signed, so a self-replacing download would meet SmartScreen. Update
  available… says "Annotator 0.1.0-beta.13 is available. Download it from
  getannotator.app, or run winget upgrade DorLugasiGal.Annotator." where the
  Mac says the update is ready to install, and the automatic check's question
  offers Open Downloads page..., Copy winget command and Not now instead of
  Sparkle's install window. A Store copy keeps the Store's install.
- **DV-12 Toolbar frame motion** (owner decision, 2026-10-29): when the
  toolbar shrinks to one button or opens again, the Mac animates the window's
  frame between the two sizes (Core `ToolbarMorph`, 0.3 s and 0.34 s) and
  cross-fades the faces; when the properties row needs more room, it widens
  the window over 0.22 s (`MotionTiming.ToolbarResizeDuration`). A WinUI
  window lays its content out a frame after each resize, so a frame-by-frame
  resize flickers and cuts the buttons off. Windows resizes the toolbar window
  at once and fades the new face or properties in (Motion.fadeIn, or
  Motion.reduced with Animation effects off).
- **DV-13 Off-screen frames** (owner decision, 2026-10-29): while the
  properties row scrolls sideways on a narrow screen, UI Automation gives no
  frame to the properties scrolled out of view (they report IsOffscreen),
  where the Mac's accessibility tree keeps their frames. Their names, values
  and order match.

## Owner decisions

The recommended defaults were adopted on 2026-10-18 (see the records below).

- **D-1 Windows baseline**: Windows 11 22H2 or later, x64 and ARM64
  (recommended). Windows 10 left support on 2025-10-14 and its consumer
  extended updates ended on 2026-10-13; supporting it would remove rounded
  system corners and some backdrop and capture options.
- **D-2 Technology**: C# on .NET with Native AOT, WinUI 3, Win32,
  Windows.UI.Composition, Direct2D and Direct3D 11 (recommended, ADR 0014).
  Alternatives and reasons are in research.md.
- **D-3 If S-1 fails**: (a) release Windows with Blur by method B, Pixelate and
  Distort shown as opaque regions with an explanation, and region sharing by
  method C; or (b) hold the Windows release until a method passes.
- **D-4 Annotation fonts**: (a) system fonts on each platform, with text
  frames measured on the rendering platform without changing the document
  (recommended for the first Windows release; the Mac look stays unchanged); or
  (b) one bundled open-licensed family on both platforms for identical text,
  which changes the Mac's text appearance and needs its own ADR.
- **D-5 Default global shortcuts**: the Mac keys with Ctrl, matching ZoomIt's
  long-standing Ctrl+1 and Ctrl+2 (recommended), or different defaults.
- **D-6 Icons**: Lucide (ISC license, already used by the Mac app for diagram
  shapes) for every Mac SF Symbol (recommended), or Segoe Fluent Icons.
- **D-7 Channel and name**: Store only, free individual account, and the
  publisher name shown in the Store. Recorded 2026-10-20: the owner enrolled
  as an individual developer and reserved **Annotator – Screen Annotation**
  (Store ID `9NMZ345ZJ6JK`, publisher display name Dor Lugasi-Gal); see
  [Recorded owner decisions](#recorded-owner-decisions).
- **D-8 Release rhythm**: Windows joins the shared version once parity is met
  (recommended), or keeps its own prerelease line until then.
- **D-9 Touch**: one-finger touch draws like a mouse (recommended), or touch is
  ignored on canvases.
- **D-10**: withdrawn. There is no paid tier (ADR 0015).
- **D-11 Idle CPU from graphics drivers** (added 2026-10-18 from S-12): count
  only Annotator's own threads and GPU work for WIN-PERF-03 and report graphics
  driver threads separately (recommended), or treat driver threads as a
  failure to fix.

### Recorded owner decisions

- **2026-10-18, start development.** After S-12 and S-1 had run, the owner
  wrote: "lets be real, are the spike needed? cant we just go to development
  here? we know everything is possible, and while developing you'll see the
  results". Applied: Gate 0 closes with the S-12 and S-1 results; the checks of
  S-2 to S-11 become acceptance checks in the phase that builds each feature;
  S-13 still waits for the Mac corpus (M-1); owner-assisted and physical checks
  still apply before release. ADR 0014 is accepted on this instruction.
- **2026-10-18, D-1 to D-11.** The agent asked the owner to confirm the
  recommended defaults; the owner was away and asked the agent to work
  autonomously. Development uses the recommended defaults until the owner
  reviews them: D-1 Windows 11 22H2 or later on x64 and ARM64; D-2 C# with
  Native AOT, WinUI 3, Windows.UI.Composition, Direct2D and Direct3D 11, with
  CsWin32 for Win32 and DirectX interop (S-12); D-3 not needed while method A
  holds (S-1); D-4 system fonts on each platform; D-5 Ctrl+2, Ctrl+Shift+2,
  Ctrl+1 and Ctrl+Alt+Shift+2; D-6 Lucide icons; D-7 Microsoft Store only, with
  the name and account still the owner's steps; D-8 Windows joins the shared
  version once parity is met; D-9 one-finger touch draws; D-11 count Annotator's own threads.
- **2026-10-19, accent color.** After trying the app, the owner wrote:
  "preferences button is red but should be blue". Windows had used the
  Windows accent color (red on the owner's PC) wherever the Mac uses its
  accent color. Applied: Annotator uses the Mac's `controlAccentColor`,
  #007AFF in `reference/ux-reference/macos/design-values.json`, in the light and
  dark themes, everywhere the Mac uses its accent: the Properties button,
  selected tools and toggles, the active board and the mode strip, the color
  palette's selected swatch, the slider and value field, and the canvas's
  selection outline and handles, marquee, attach hints and eraser preview.
  Contrast themes still give app windows the system's highlight color
  (WIN-UI-07).
- **2026-10-19, delegated open items.** About the items waiting for review
  (S-13, scaled images, the text baseline, the Unicode data and the canvas
  reference items), the owner wrote: "i dont get what you need from me. just
  go ahead with common sense and modify". The coordinator decided them;
  applied:
  - S-13 thresholds: WIN-REND-05 takes the thresholds S-13 proposed and is
    frozen: 32/255 for antialiased edges and at most one failing flat or edge
    pixel in 10,000; pixel effects keep the first thresholds, with no channel
    more than 2/255 apart. The corpus tests enforce exactly these values.
  - Scaled images: high-quality cubic interpolation wherever the Mac scales an
    image with Core Graphics (image objects and exported snapshots). No
    Direct2D mode reproduces Core Graphics here, so the corpus `image` scene is
    a regression check at its measured distance: CIEDE2000 p95 / p99 / max of
    7.31 / 10.69 / 25.48 at 1x and 7.34 / 10.70 / 27.06 at 2x.
  - Text baseline: text objects, shape labels, counters, ruler labels and the
    canvas's Use apps button draw their first baseline where the Mac's is
    (WIN-REND-03), without changing documents or frames (D-4 keeps system
    fonts). In the render corpus every text run's baseline is now within one
    device pixel (at most 0.04 em) of the Mac's; system-font text and counters
    sat 0.10 to 0.15 em lower before.
  - Unicode 16 data: the generated Unicode Character Database 16.0.0 tables in
    Annotator.Core are approved (`docs/provenance/components.json`).
  - Canvas reference items: the canvas's Use apps button keeps the accessible
    name "Use apps", the Mac code's label (the Mac capture recorded its title,
    a recorder issue listed in `ux-parity.md`); its accessible frame is the
    reference's 15, 13, 152, 30; and the canvas summary counts an object that
    is still being drawn, as the Mac does.
- **2026-10-20, delegated performance decisions.** The owner had delegated the
  open decisions ("just go ahead with common sense and modify"); the
  coordinator decided two that the capture and latency work left open:
  - WIN-PERF-01 at 60 Hz: on Windows, input-to-visible at 60 Hz is read as at
    most two display refreshes at p95 (33.36 ms at 59.95 Hz), and the median
    must be at most 33 ms. The Windows compositor shows an update one refresh
    after the first frame that catches it, so input to visible is the wait for
    that frame (anywhere in a refresh), one refresh and a lead L that the
    update needs before the frame starts: p50 1.5 refreshes plus L, p95 1.95
    refreshes plus L (25.0 and 32.5 ms plus L at 59.95 Hz). The latency
    analysis measured, at 2,560 x 1,440, 100% and 59.95 Hz: p50 26.08 ms and
    p95 33.35 ms on a quiet machine (L 0.83 ms; the app's own share 0.43 ms
    of input delivery and 0.14 ms of handling at p50); p50 26.0 to 27.2 ms and
    p95 33.7 to 35.2 ms while other agents built, tested and measured (L 1.1
    to 2.2 ms); and the presenter alone, without any input, p95 32.9 to
    33.7 ms. A p95 under 33 ms would need L under 0.47 ms, less than the
    compositor's own lead. Results under load are reported with the quiet
    ones, never left out. At 240 Hz the integrated app measured p50 7.77 ms
    and p95 9.43 ms.
  - The capture pause: a capture whose last consumer stopped stays paused for
    60 s and serves the next effect, zoom or region sharing on its display.
    Windows keeps one frame-sized allocation for every capture session that
    starts until the app quits (15.0 MiB at 2,560 x 1,440; 30 starts and stops
    left 450 MiB in a scratch test), so without the pause every use adds that
    much (6 starts and stops: 90 MiB), and with it uses within a minute add
    nothing (8 cycles: one session, 15.0 MiB once it was let go). The 60 s
    also bound the memory the paused capture holds (103 to 105 MiB of video
    memory) and its cost while paused (no hooks, no frames, 0% of a core).
- **2026-10-20, Store name and identity (D-7).** The owner enrolled in the
  Microsoft Store developer program and reserved the name "Annotator – Screen
  Annotation" as an MSIX or PWA app. Partner Center > Product identity, read by
  the coordinator at 21:00: `Package/Identity/Name`
  `DorLugasi-Gal.AnnotatorScreenAnnotation`, `Package/Identity/Publisher`
  `CN=9BC8A074-AAD1-4E16-8AC2-B68940F537CD`, `PublisherDisplayName`
  Dor Lugasi-Gal, Store ID `9NMZ345ZJ6JK`, package family name
  `DorLugasi-Gal.AnnotatorScreenAnnotation_es65rrkpa6e10`. Applied from the
  0.1.0-beta.8 candidate:
  - `Package.appxmanifest` carries that identity. The package's
    `Properties/DisplayName` and the Start menu name (`uap:VisualElements`
    `DisplayName`) are the reserved name, which Partner Center requires; the
    publisher's `--check-store` also requires the two to be the same. Microsoft
    Learn (checked 2026-10-20) says the manifest must carry the Product
    identity values but does not state the display-name rule; Visual Studio's
    Store association writes the reserved name in both places, so both use it.
  - Where Windows shows the Store name: the Start menu (list and search),
    Windows Settings > Apps > Installed apps and the Store listing. The Start
    tile label (`DefaultTile` `ShortName`) and the startup task's name stay
    Annotator. The installed candidate is the check of what Windows shows.
  - The brand stays Annotator, as on the Mac: every app window title, About,
    the tray icon's name and menu, support information and help text.
  - The website keeps `storeId` null in `website/src/data/windows-listing.json`
    until the listing is public (WIN-DST-08). At launch the owner sets it to
    `9NMZ345ZJ6JK`, which shows `winget install --id 9NMZ345ZJ6JK --source
    msstore` (WIN-DST-02). The owner also sets the GitHub repository variable
    `WINDOWS_STORE_ID` to `9NMZ345ZJ6JK` before any hosted Windows Store run;
    agents do not set it.
- **2026-10-29, toolbar deviations (DV-12, DV-13).** The Windows agent's
  handoff on pull request #77 asked the owner to accept or reject DV-12 and
  DV-13 before a Windows release. The owner then wrote: "i think the windows
  agent is done, they handoff back so go ahead finalize whats needed, merge
  and go for a new release". Applied: both deviations ship with the next
  release; DV-12 stands until WinUI can resize a window without the frame's
  delay, and DV-13 until UI Automation reports frames for content scrolled
  out of view.
