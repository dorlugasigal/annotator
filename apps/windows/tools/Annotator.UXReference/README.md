# Annotator.UXReference

Compares the Windows toolbar, full properties, arrowhead choices, color popover,
Tools window, Settings with the Toolbar & Shortcuts, Pointer and Break timer pages,
keycaps, the tutorial, region sharing, the break screen, the error window, tray flyout,
drawing canvas and cursors with
the macOS UX reference in
`reference/ux-reference/macos` (specification 013, WIN-PAR-06). It is a development
tool: it is not part of the solution and never ships.

For each reference state and theme it:

1. Starts `Annotator.exe --ux-reference <surface>/<state> --theme <light|dark> --out <folder>`.
   Development builds only (unpackaged) accept the switch; a packaged build
   exits with code 2. The app builds the state through ordinary workspace
   calls on one made-up 2560 x 1440 display at 125%, with an in-memory
   profile. It creates no tray icon, global shortcut, overlay or saved
   setting, skips the single-instance check, and opens the surface just
   beyond the right edge of the desktop without activating it, so it never
   covers or takes focus from other apps. It renders the surface with
   `RenderTargetBitmap`, writes `ready.json` and `layout.txt` (every element's
   laid-out frame and desired size, for finding where a frame difference
   starts) and waits for the tool, at most 90 seconds.
2. Reads the window's UI Automation control view in the reference JSON shape:
   the control type becomes the Mac role (Button `AXButton`, Text
   `AXStaticText`, or `AXHeading` with a heading level, Edit `AXTextField`,
   Slider `AXSlider`, Thumb `AXValueIndicator`, ComboBox `AXPopUpButton`,
   RadioButton `AXRadioButton`, Group `AXGroup`, Image `AXImage`, a named
   Custom control `AXUnknown`, a button with ExpandCollapse
   `AXMenuButton`, a scrolling pane `AXScrollArea`; a group with the Selection
   pattern `AXRadioGroup`, with the Grid pattern `AXOpaqueProviderGroup`, with
   ExpandCollapse `AXDisclosureTriangle`); Name is the label, or the value of a
   static text; AutomationId the identifier; the Value pattern, the Toggle
   state (`On` or `Off`; 1 or 0 for a checkbox), a radio button's selection (1
   or 0), an expander's state (true or false) or the RangeValue the value;
   HelpText the help; and BoundingRectangle the frame in DIPs from the window's
   top-left corner.
   Panes without a name or identifier only structure the tree, so their
   children move up, as SwiftUI's stacks do.
3. Compares the dump with the reference: nodes are aligned in tree order by
   role and words (longest common subsequence, so order matters); labels,
   identifiers, values and help must be equal after the platform terms of
   specification 013 (key symbols become `Ctrl`, `Alt` and `Shift` keycaps,
   `Control-W` becomes `Ctrl+W`, a spoken shortcut such as "Control + Option
   + 2" becomes "Ctrl + Alt + 2" with Command as Win, ⌫ becomes Delete, and a
   version label compares by its shape);
   frames must be within 1 DIP. A static text whose frame differs only in
   width is listed as a text width difference (Segoe UI against SF Pro,
   decision D-4). Missing and extra nodes are listed. The recorder writes a
   Form's switch, pop-up button or segmented picker (`AXRadioGroup`) without a
   label, because AppKit names it by the text before it (its title element),
   so the tool takes that text as its label; a control the Mac source labels
   takes the source's label (the Effect pop-up, `cover-mode`).
4. Saves the Windows surface scaled to 2 pixels per DIP and a sheet with the
   reference, the Windows surface and an even blend of both.

Canvas states (`canvas/<state>`) run a real overlay with its drawing canvas,
presenter and Use apps button for one made-up 900 x 568 point display at 125%,
beyond the desktop's right edge; it never takes focus. The app builds the
state the way `UXReferenceCaptureTests.test07CanvasChrome` does: the sample
scene is submitted like a canvas edit, selections, rotation, boards and the
committed "New note" text follow the Mac's order, and gestures (attach hint,
marquee, eraser) go through the canvas's pointer input and stay in progress.
The canvas renders what it shows with the presenter's renderer at 2x over the
capture's fictional backdrop (drawn at 900 x 560 and stretched to 568, as the
Mac does). The tool then:

- dumps the overlay's own UI Automation element and its children, and compares
  them with the canvas tree the reference records: the group "Annotation
  canvas" with its summary value and help, and the Use apps button. The Mac
  recorder writes that button with its title ("Use apps · Esc") and no label,
  although `DrawingCanvas.swift` labels it "Use apps", so the tool expects the
  source's label and says so in the diff. A reference without a canvas tree
  gets one from the source;
- compares the objects the canvas shows (tool, frame within 1 DIP, rotation,
  lock and selection, in order) with the reference's `elements`;
- writes the usual sheet. Text editing types "New note" through the canvas's
  key input and leaves it in the S-7 editor, so the tree has the edit field.

Color popover states (`pickers/color-stroke`, `color-fill`, `color-gradient`)
open the real stroke or fill popover beyond the desktop's right edge, without
activation. The app builds them as `UXReferenceCaptureTests.test02PropertiesAndPickers`
does: the Rectangle tool with its default style, then Fill shape on, then the
Spectrum gradient on the stroke. The zoom controls (`pickers/zoom-compact` and
`zoom-full`) show `LiveZoomControls` on its own with 12 points of padding, as the
Mac capture does, at screen zoom's default 2x; their group reports the union of
its children's frames, as SwiftUI does.

`cursors --out <folder>` compares every entry of `cursors/cursors.json`: the
pen, highlighter, eraser and rotation cursors that Annotator draws by size,
hot spot and 2x pixels (mean alpha difference at most 2, at most 1% of pixels
off by more than 32, mean color difference at most 8), and the crosshair and
I-beam by kind, since each system draws its own. It writes
`cursors/cursors.sheet.png` and `cursors.diff.txt`. The pointer effects
(cursor halo, spotlight and click effects) have no Windows state yet.

The app's own process is the only one it reads; it stops any process it
started that does not quit in time.

## Running

Build the app (Debug is the default) and run from the repository root:

```powershell
dotnet build apps/windows/src/Annotator.App/Annotator.App.csproj -c Debug -p:Platform=x64
dotnet run --project apps/windows/tools/Annotator.UXReference -- run --out <folder>
```

Options: `--states toolbar/full-properties-*,menu/drawing` (patterns with `*`;
default `toolbar/*,menu/*,canvas/*,properties/*,pickers/*,tools-window/*,settings/*,tutorial/*,dialogs/*,region-sharing/*,break-timer/*`),
`--themes light`
(default both), `--app <path>`
(default the Debug build, else `apps/windows/out/app/Annotator.exe`),
`--reference <folder>` and `--timeout <seconds>`. `list` prints the selected
states. `--usage-counts available` shows Share usage counts in Settings and the
tutorial as available and off, the Mac's states, as Windows will show them once the
owner turns on `UsageMetrics.ServiceAcceptsWindows` (WIN-PRV-01), and compares
them with the Mac's words. By default they show as unavailable, as this build
has them, and the tool expects the unavailable words.
`--contrast aquatic|desert|dusk|nightsky` and `--text-scale <1 to 2.25>`
show the states in a simulated contrast theme or text size (WinUI's own test
hooks; Windows settings never change) and add `-<theme>` or `-text<size>` to
the file names; the comparison with the Mac then shows what changed, and the
sheets show how the windows look.

Each state writes `<surface>/<state>-<theme>.diff.txt` (node by node, and the
objects for a canvas), `.windows.json` (the dump; disabled controls say
`"enabled": false`, which the reference never records), `.layout.txt` (the
app's layout, toolbar and flyout), `.windows.png` (2x) and `.sheet.png`;
`summary.md` and `summary.json` list every state. Each state runs the app with
its own scratch folder (`.app-<state>-<theme>`), so an app that is still quitting
never touches the next state's files. Keep the output outside the
repository.

For a short on-screen check, start one toolbar state where the app would
place it and let it follow its own buttons:
`Annotator.exe --ux-reference toolbar/full-properties-pen --placement docked --out <folder> --timeout 6`.
It shows the toolbar without activating it and quits after the timeout. Its
Stroke and Fill buttons open the real color popover, Pick from screen included.
Toolbar states follow their own buttons off screen too, so UI Automation can
invoke full-properties controls and read what they change; the arrowhead
choices open next to the toolbar without activation.

A development check that changes a state through UI Automation can have the
surface rendered again: setting the named event
`Local\Annotator.UXReference.<process id>.capture` writes `surface-N.bgra` and
then `capture-N.json` (size and status) for the Nth signal, until the tool's
done event or the timeout.

Tools window states (`tools-window/<state>`) show the Tools window's content on
its own, as `UXReferenceCaptureTests.test06WindowsAndDialogs` renders
`ToolLibraryPanel` at its ideal height: 368 points wide with a maximum height
of 420, or 780 by 570 for `shapes-resized`, on the window background, with an
in-memory My shapes library that has loaded (never the person's shapes). The
workspace is drawing, with each section's first tool chosen (Pen for My shapes
and `shapes-resized`): Effects chooses Blur and is the current tile in Focus, as
on the Mac; Paste image is an action that never becomes the current tool (the Mac
capture sets it directly), so the Extras state keeps Pen and shows no current tile. SwiftUI's
lazy grid reports the tiles below the scroll area at a placeholder position and
leaves out the last one (Axes in Shapes); Windows reports where they are. The
docked check (`--placement docked`) opens the real Tools window from the
toolbar's Tool library button, with an in-memory library too.

Settings states with the Toolbar & Shortcuts and Pointer tabs (`settings/tab-toolbar*`,
`settings/tab-pointer*`) show the Settings content as the app's Settings window
makes it, with the pages the app sets through `SettingsWindow.SetPage`, 840
points wide and 700 or 1,500 points high. The capture of `tab-pointer` waits
until the preview has drawn its halo.

The pointer pictures (`pointer/*` and `pointer/motion/*`) have no
accessibility tree; `Annotator.RenderHarness pointer --evidence <folder>` draws
them with the Windows renderer and compares their pixels with the Mac's.

Tutorial states (`tutorial/<state>`) show the card itself, without its window
or the ghost: `welcome`, `coach-<step>` for every step with nothing done,
`coach-arrow-curved`, `coach-shape-done` and `finish`, as the Mac capture does.
The finish card also carries the tray icon and shortcut cards, so it is taller
and 400 points wide (WIN-UI-04, DV-4, DV-10).
`dialogs/canvas-hint` draws the on-screen tip while drawing in the Mac's 540
by 130-point view, with its ✕ and the Turn off tips button over it, and
`dialogs/coffee-reminder` the reminder at the Mac's place in a 460 by 135-point
window. The tip's ✕ takes its label, "Turn off tips", from `WorkspaceHint.swift`,
because the Mac recorder wrote only its title (`HintExpectations`). The ✕ sits
at the end of the text, so its left edge follows the text's width (D-4) and is
reported as a text width difference; its top, size, label and help must match.

Properties states (`properties/<state>`) show the full properties panel on
its own, 16 points from the edges of a window as wide as the reference (648
points, or 932 for `diamond-wide`), as `ContextualInspector().padding(16)` is
captured. The arrowhead choices (`pickers/arrowhead-start` and `-end`) show the
popover for the Arrow tool's default style. SwiftUI reports their lazy grids as
`AXOpaqueProviderGroup`; on Windows they are groups with the Grid pattern, like
the color popover's swatches, which the tool reads as that role.

The error window state (`dialogs/error-window`) shows the error window's content
on its own, 460 by 160 points on the window background, with the message the
Mac capture shows (`Drawing format 9 is not supported by this version.`),
reported the way the app reports errors. `toolbar/full-properties-image` pastes a
24 by 16-pixel image from an in-memory clipboard with Paste image, as the
person would, so the properties row shows the pasted image's properties; the Mac
capture sets the tool directly.

The tray icon state has no accessibility tree in the reference; compare its
sheet. The Mac captures toolbar states in a desktop-style session. The Spotlight
and Zoom menus are toolbar items there only in the `added-buttons` state, as on
Windows.

The Mac reports frames for the content below the fold. Settings' own pages report
them too (`SettingsFrames`); for the Toolbar & Shortcuts and Pointer tabs the tool
scrolls the page with the Scroll pattern and fills in each node's frame from a
view where it shows whole, moved back by the distance scrolled
(`ScrolledFrames`); each diff says how far it scrolled, how many frames it filled
in and why it stopped. Windows limits a window to the screen's height, so the full state's
window may end before 1,500 points; the default state covers its lower part.
`toolbar/full-properties-vertical-arrow` scrolls the same way, because its panel
scrolls on both platforms. A node taller than the view takes its top from one view
and its bottom from another; at the end of the scroll range, content flush with the
view's edge counts as whole; and the view ends at the window's bottom, with 1 DIP to
spare, because a scroll area's reported frame can be a pixel off its clip. A UI
Automation call that fails while the page scrolls is tried again with the scroll
area found again, and the diff says when the app quit before the tool was done.
The tiles' accessibility values and hints come from the Mac source (the
recorder leaves them out for SwiftUI containers), and Hide only expects its
Windows default, Ctrl+Alt+Shift+2 (`ShortcutExpectations`, decision D-5).
Keycaps states (`dialogs/keycaps-<action>`) show one global shortcut's default
keycaps with 8 points of padding; Windows names the keys in words, so they are
wider than the Mac's symbols.
