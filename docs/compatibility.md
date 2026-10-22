# Compatibility and protection limits

Release builds after 0.1.0-alpha.6 are universal: one app for macOS 14+ on
Apple Silicon and Intel. Development builds contain only the running Mac's
architecture. No Intel Mac has run the app yet; the Intel code's tests run
under Rosetta. The current
interactive smoke tests were run on macOS 26.6.2 with one display. macOS 14,
mixed-scale multi-display, IME/tablet and receiver-side meeting-app acceptance
are still required.

| Surface | Evidence |
| --- | --- |
| Native drawing and text | AppKit event-path tests on the development machine |
| Input release | Draw/Interact native tests and accessibility button smoke check |
| Annotation-only PNG | Raster tests, PNG signature and opaque pixel assertions |
| Live screen stream and Metal compositor | Signed one-frame stream check passes; production texture orientation/crop regression passes |
| Full-screen zoom | Cursor-anchored viewport and Option-scroll mode implemented; pointer invariants tested, broader system-surface/multi-display acceptance remains open |
| Manually positioned effects | Blur, Pixelate and Distort tested on the desktop. Effects do not follow windows or content. Receiver-side sharing and failure-path checks remain open |
| Teams whole-screen sharing | Worked in a manual test on a Mac; the receiving device wasn't recorded |
| Teams window sharing; Zoom/Meet/OBS capture | Not verified; no universal compatibility claim |

Window-only or app-only sharing can omit another application's overlay.
Covers stay at their canvas positions when windows move or content scrolls.
Blur is not cryptographic or irreversible redaction.

Full-screen zoom moves its viewport with the cursor; it is not a fixed camera.
Option-scroll is intercepted only while that mode is active and the pointer
is on its display. Native/system popup surfaces still require platform
checks.

An application crash or quit removes overlay windows. Use sanitized source
content for genuinely sensitive demos and inspect the remote receiver's view.
Annotation Clear/Delete can remove unlocked covers; Undo restores them.

## Covers stay where you place them

Choose Effects and select Blur, Pixelate or Distort. Drag to place a region,
then use Select to move or resize it. Effects use live screen pixels and need
Screen Recording permission. Zero Blur or Pixelate strength means no filtering.
Use Rectangle with a fill for ordinary solid blocks. These are editable
annotations, not secure redaction. Existing solid covers from saved boards
remain opaque; the app does not silently convert them to rectangles.

Window following is not part of this release. Scrolling, resizing a window or
changing its layout can move private content outside a cover. Remove private
content before sharing rather than relying on a cover to track it.

Hide removes covers from the visible desktop; showing the workspace restores
their saved positions. Quit or an app crash removes them. Editable boards retain
original pasted images, so use a flattened PNG rather than a board when sharing
covered image content.

Sharing one window or app may leave out the cover. System windows and windows
that stay above other apps may also appear above it. Check what your viewers
can see before sharing private content.
