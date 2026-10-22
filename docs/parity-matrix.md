# Windows parity matrix

One row per shipped Mac behavior, grouped by the former specification that owns
it (see [former specification numbers](architecture.md#former-specification-and-decision-numbers)).
Levels are defined in [the parity contract](parity.md#parity-levels): I identical,
E platform equivalent, D documented deviation, N/A not applicable. "Gate" names
the Gate 0 spike or evidence that covered the row when the Windows edition was built.


## 001 Annotation core

| Behavior | Windows mechanism | Level | Gate |
| --- | --- | --- | --- |
| Elements: UUIDs, finite geometry, tool and style data, groups, locks, anchors, sketch seed | `Annotator.Core` port | I | Corpus |
| One gesture is one undo step; cancellation restores the scene and keeps redo | Core transactions | I | Corpus |
| Locked objects resist editing and erasing | Core | I | Corpus |
| Duplicate and paste remap groups and internal anchors; external anchors detach | Core `insertCopies` | I | Corpus |
| Pen, highlighter, line, straight and curved arrow, rectangle, ellipse, diamond, text, cover, select, transform, group, layers, eraser, connectors | Core plus overlay input | I | Corpus, S-5 |
| Hit testing: visible order, text inside outline shapes wins, solid fills at 50% or more hide text | Core | I | Corpus |
| Connectors attach to fixed anchors within 18 points | Core | I | Corpus |
| A lone unlocked arrow or line shows point handles without a box | Overlay adornments | I | Render corpus |
| Independent stroke and fill opacity; first fill at 40%; Match outline | Core and properties | I | Corpus |
| Two-color and multicolor gradients, 25-degree presets, format 3 | Core and Direct2D gradients | I | Render corpus |
| Labels follow owner paint, inherit gradients while typing | Core, renderer, text editor | I | S-7 |

## 002 Desktop overlay and toolbar design

| Behavior | Windows mechanism | Level | Gate |
| --- | --- | --- | --- |
| One document and transparent overlay per display | Win32 overlay per monitor with composition | I | S-4 |
| Draw prepares input on every display; a click picks the target display | Overlay input | I | S-4 |
| Use apps, Hide and suspension release input on every display | Click-through on all overlays together | I | S-4 |
| Ctrl+A selects across displays with coordinated history | Core and model | I | Corpus |
| Dragging a selection to another display transfers it in one undo step | Model | I | S-4 |
| Display changes cancel edits and release input; toolbar returns to a usable area | Display and work-area messages | E | S-4 |
| Whiteboard and blackboard per display | Model and renderer | I | Render corpus |
| Cursor handoff across displays | Cursor factory per monitor scale | E | S-5 |
| Escape: commit text, cancel gesture, then release | Canvas keys | I | S-6 |
| Native multiline text with IME | TSF editor from S-7 | E | S-7 |
| New text uses saved Text settings | Model | I | Corpus |
| Resume drawing shortcut rules | Hotkey service | I | S-9 |
| Profiles reject collisions by physical key | Key table from the corpus | I | S-9 |
| Toolbar sizes: 1000 wide, 72 tools-only, 126 expanded, Minimal 34 | XAML in points | I | UI tests |
| One Stroke picker, Fill picker for fill tools, inline popover with hex and opacity | XAML color popover | I | UI tests |
| Pick from screen (the Mac's system `NSColorSampler` loupe) | Windows has no system sampler: a magnifier loupe follows the pointer, a click reads that one composed-screen pixel, Escape cancels; no permission in a development build, screen access in a package (WIN-CAP-04) | E | S-3, UI tests |
| Rendered samples for width, pattern, roughness, edges, arrows | Shared renderer in XAML | I | Render corpus |
| Labeled mode control, undo, redo, clear, hide always reachable | XAML | I | UI tests |
| Settings is a persistent nonmodal window | WinUI window | E | UI tests |

## 003 Presentation effects

| Behavior | Windows mechanism | Level | Gate |
| --- | --- | --- | --- |
| Capture only when pixels are needed; own windows excluded | Method A, else B and C | E | S-1 |
| Cursor halo shapes, borders, opacity, glow, persisted identifiers | Overlay renderer | I | Render corpus |
| Click effects Off, Pulse, Ripple, Turn with Mac timings | Composition animation | I | Motion corpus |
| Zoom ramps 260 ms in, 200 ms out | Composition or paced presents | I | S-3 |
| Lens fade 120 ms; Spotlight 180/140 ms and 120 ms radius easing | Composition | I | Motion corpus |
| HUD and hint fades 160/120 ms with a 3-point rise; 140 ms crossfade | Composition | I | Motion corpus |
| No system shadow on overlays; the lens keeps its own shadow | Window styles | I | S-4 |
| Benchmarks: 200 mixed elements and a 20,000-sample stroke; 1,000/200,000 stress | Benchmarks project | I | WIN-PERF |

## 004 Window masks

| Behavior | Windows mechanism | Level | Gate |
| --- | --- | --- | --- |
| Window following | Not built (retired on the Mac) | N/A | None |
| Existing solid covers stay opaque and editable | Core and renderer | I | Corpus |

## 005 Product workflows

| Behavior | Windows mechanism | Level | Gate |
| --- | --- | --- | --- |
| Toolbar & Shortcuts tab: your toolbar, more tools, drag to arrange, keys, reset | Settings tab in XAML | I | UI tests |
| Key recording rules, refusals, moves with Undo | Settings and core | I | Corpus, UI tests |
| Global keys need Ctrl, Alt or Win; Shift with digits recorded correctly | Hotkey service | E | S-9 |
| Expanded and Minimal sizes; Share style between tools; Show tips on screen | XAML and core | I | UI tests |
| Arrange and key motion: lift, slide, settle, pulse, bounce, shake | Composition | I | Motion corpus |
| Four-page tour with illustrations, arrows and Reduce Motion stills | Tour window | I | UI tests |
| Tour shows without taking focus or asking for permissions | `SW_SHOWNOACTIVATE` window | E | S-6 |
| Pointer settings and interactive preview | Settings and overlay renderer | I | UI tests |
| Inline color wheel inside the palette | Custom XAML control | I | UI tests |
| Starting a drawing closes the color picker first | Model | I | UI tests |
| Settings about 840 by 700, resizable | WinUI window | I | UI tests |
| Help, permissions cards, About with the app icon and version | XAML with Windows terms | E | UI tests |
| Coffee reminders, Not now and cadence | Model | I | Corpus |
| Named local profiles; versioned editable boards; atomic saves | Core and file service | I | Corpus |
| Import errors stay in one app-owned error window | Error window | I | UI tests |
| Smart Draw button and setting | XAML and core | I | Corpus |
| Annotation-only PNG export | WIC export | I | Render corpus |
| My shapes: save, thumbnails, insert, delete with confirmation, limits | Library on local data | I | Corpus |
| Empty new text cancels its transaction | Core and editor | I | Corpus, S-7 |
| Applications move offer | Not needed for MSIX | N/A | None |
| Introduction suppression, safe-boundary reopening, Escape explanation | Tour | I | UI tests |
| Report a problem, AI setup explanation, skill export | Help with Windows tools | E | UI tests |
| Launch at login | MSIX startup task | E | S-10 |
| Resizable Tools window that keeps its size | WinUI window | I | UI tests |
| System design catalog shares vector paths with drawings | Core geometry | I | Render corpus |
| Shape labels, Ctrl+C/X/V, `app.annotator.annotations` | Core and clipboard | I | Corpus |
| Arrow attach highlight | Overlay adornments | I | Render corpus |

## 008 Capture utilities (current scope)

| Behavior | Windows mechanism | Level | Gate |
| --- | --- | --- | --- |
| Live magnifier lens | Method A or a magnifier window | E | S-3 |
| Blur, Pixelate and Distort regions | Method A shaders | E | S-1 |
| Image paste and insert as layers, persisted in boards | Clipboard, WIC, core | I | Corpus |
| Counters and rulers | Core and renderer | I | Render corpus |
| Solid covers win; exports flatten covered pixels | Effect pipeline | I | Effects corpus |
| Theme System, Light, Dark for app windows only | WinUI themes | E | UI tests |
| Tool footprint cursors | Cursor factory | I | S-5 |

## 009 Unified interaction and zoom

| Behavior | Windows mechanism | Level | Gate |
| --- | --- | --- | --- |
| Every placed object can be selected, moved and deleted | Core and overlay | I | Corpus |
| Effects above drawings; solid covers above effects | Overlay order | I | S-4 |
| Multi-selection transforms and shared style edits | Core | I | Corpus |
| Selection handles above effect pixels | Adornment layer | I | S-4 |
| Blur 0-20, Pixelate 0-40, Distort 2-100; numeric 0-100 | Properties | I | UI tests |
| Smart Draw preview, recognition rules and 220 ms morph | Core and composition | I | Corpus, motion |
| Eraser outlines, fades over 120 ms and shrinks slightly | Overlay and composition | I | Motion corpus |
| Undo cancels unfinished work first; coordinated clear | Core | I | Corpus |
| Background cycle button; Ctrl+W and Ctrl+B | Toolbar and keys | I | UI tests |
| Edge docking with ghost preview and orientation change | App-owned drag | I | S-4 |
| One Effects tool with three modes | Core and toolbar | I | Corpus |
| 44 by 48 tool targets; Shift-resize; highlighter Shift straightening | XAML and core | I | Corpus |
| Footprint cursor on canvas, system cursors on controls | Cursor factory | I | S-5 |
| Choosing a tool returns focus to the canvas | Focus model | E | S-6 |
| Curved heads and radial Spotlight feather | Core and renderer | I | Render corpus |
| Zoom toggle, 1.5x start, Alt+scroll 1x-16x | Hotkey, hook, zoom service | E | S-3, S-8 |
| Cursor-anchored viewport `p x (1 - 1/z)` | Zoom service | I | S-3 |
| Drawings and adornments use the zoom viewport each frame | Overlay | I | S-3 |
| Escape exits zoom after committing or cancelling edits | Keys and hook | I | S-8 |
| Text editing freezes the viewport | Editor host | I | S-7 |
| Ctrl+2 visibility rules, resume during a show | Model | I | UI tests |
| Dissolve, Gather, Magic, Corners, Pop, Drop, Soft focus, Fade, Off | Motion module | I | Motion corpus |
| 40-million-pixel snapshot budget; 64-element limit; no flash at hand-off | Motion module | I | Native tests |
| Historical style identifiers resolve the same way | Core | I | Corpus |
| Explicit removal and quit have no confirmation dialogs | Model | I | UI tests |

## 011 Product metrics

| Behavior | Windows mechanism | Level | Gate |
| --- | --- | --- | --- |
| Off by default; consent identity lifecycle; daily summary; buckets | Metrics client (`UsageMetrics`) | I | Unit tests |
| Payload fields | Adds `platform`; 011 and the service accept it since 2026-10-20; sending stays off (`UsageMetrics.ServiceAcceptsWindows`) until the owner deploys the service | E | 011 amendment, owner deploy |
| Settings control and plain privacy words | Settings and the tour follow the client; unavailable words until sending is on | I | UI tests |

## 012 Region sharing

| Behavior | Windows mechanism | Level | Gate |
| --- | --- | --- | --- |
| Presets, custom rectangle, display picker, validation errors | Share a region window (WinUI, left out of capture) | I | UI tests |
| Click-through outline with selection-only fill | Topmost click-through guide window in the guide layer | I | S-4 |
| Custom selection across displays, Escape cancels | One selection panel per display; a lost keyboard ends it | I | S-4 |
| Output window titled **Annotator - Shared Region** | Ordinary top-level window, never topmost, left out of capture | E | S-11 |
| Crop before filter; covers win; no unfiltered fallback | Effect pipeline on the region and a one-pixel margin, drawings composited first | I | S-1, effects corpus |
| Toolbar inside the output | Border-free Windows.Graphics.Capture of the toolbar window | E | S-11 |
| Start/Stop, quick halves, red accents and red tray dot | Controls, flyout and tray | E | UI tests |
| Show/hide animations mirrored in the output | Motion module: the overlays' plan copied into the output (`SharedMotion` through `IVisibilityMirror`) | I | Native tests |
| Opens behind other apps, updates while covered, aspect ratio kept | Window service | E | S-11 |
| Stops opaque on failure, lock, sleep or display change | Capture service states and the output's cover | I | S-1 |

## 014 Break timer

Added on the Mac 2026-10-22 (spec) and ported to Windows the same day, because the
requester uses Windows. The shared logic is `Annotator.Workspace`'s `BreakSession`, a port of the Mac's
`BreakTimerController`; the app layer is `Annotator.App/Breaks` and `Settings/BreakTimerPage`.

| Behavior | Windows mechanism | Level | Gate |
| --- | --- | --- | --- |
| Countdown model: rounding, pause, adding and removing minutes, time over, labels | `Annotator.Core` port of `BreakCountdown` and `BreakTimerSettings` | I | Unit tests |
| `breakTimer` and `breakShortcut` in profiles; Ctrl+3 moves to a free key in older profiles | Core preferences and JSON codecs | I | Unit tests; corpus cases still to add on the Mac |
| Break screen on every monitor (or the pointer's), above full-screen apps | A topmost window per monitor at the guide layer; ordinary capture so full-screen meeting shares show it | E | UX reference; live check |
| Lock when someone touches it, after 3 s of arming; jitter under 12 points ignored | `LockWorkStation` (refused under the `DisableLockWorkstation` policy); the window's Activated event, checked again after 150 ms, for another app in front; the app's session watcher (`WTSRegisterSessionNotification`) hides the break screen on a lock and shows it again on unlock; the countdown and its sound (`PlaySound` of the Notification.Default alias) go on while locked | E | Unit tests; live check |
| Keep displays awake; let them sleep 30 minutes after time up | `SetThreadExecutionState` with `ES_SYSTEM_REQUIRED` and `ES_DISPLAY_REQUIRED` while shown, and only `ES_SYSTEM_REQUIRED` while locked until zero | E | Unit tests |
| Controls, keys, Settings tab, tray flyout rows and the Ctrl+3 global key | XAML, hotkey service and the UX reference `break-timer` surface | I | UX reference; a11y |
| Ring and moving colors as compositor animations; Reduce Motion | A `ShapeVisual` ring trimmed by `CompositionEllipseGeometry.TrimEnd`, drifting radial gradients; Animation effects off replaces movement with fades | I | Motion review |
| Blurred desktop background | `DesktopAcrylicController` kept in its active look | E | Owner check |

## Platform integration

| Behavior | Windows mechanism | Level | Gate |
| --- | --- | --- | --- |
| Menu-bar icon and popover | Tray icon and flyout (left or right click) | E | S-6 |
| Show menu-bar icon preference | Show tray icon preference | E | UI tests |
| Updates and What's changed | Store updates, bundled notes | E | S-10 |
| Universal app | MSIX bundle x64 and ARM64 | E | S-10, ARM64 gate |
| Homebrew install | `winget` through `msstore` | E | S-10 |
| VoiceOver, Reduce Motion, Increase Contrast | Narrator, Animation effects, contrast themes | E | Physical gates |
| AI computer-use skill | Same skill with a Windows section (2026-10-20), bundled and exported from Help | E | WIN-AI-01 |
