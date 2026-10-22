# macOS conformance corpus

This folder holds the reference data that the Windows edition must match
(specification 013, plan task M-1). The Mac writes it with
`make conformance` (`swift run --package-path apps/macos ConformanceExport`); the source is in `apps/macos/Tools/Conformance`. Everything
here except this README is generated, so do not edit those files by hand.

## Regenerate and check

Run from the repository root on a Mac with full Xcode:

```sh
export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
make conformance         # writes reference/conformance
make conformance-check   # compares; exits 1 on any difference
```

Use the default debug build. The exporter reads a few internal renderer values
with `@testable import`, which release builds do not allow.

Commit source changes first, then export, then commit the corpus. The manifest
then names the commit that produced it and `uncommittedChanges` is false.
Three runs, each with a different Swift hash seed, gave byte-identical output.

## Format

- JSON files are UTF-8 with sorted keys and LF line ends. A `.jsonl` file has
  one record per line. Git must not change the bytes: `.gitattributes` marks
  `reference/conformance/**` as `-text`. `manifest.json` lists every file with
  its SHA-256.
- Numbers are written by Swift's `JSONEncoder`, which prints the shortest
  decimal that reads back as the same double. Read them as IEEE doubles.
  `+Infinity`, `-Infinity` and `NaN` appear as strings, only for
  `CGRect.null` and `CGRect.infinite`.
- Points are `[x, y]`. Rectangles are `{x, y, width, height}`. Path elements
  are `["move", [p]]`, `["line", [p]]`, `["quad", [c, p]]`,
  `["cubic", [c1, c2, p]]` and `["close"]`.
- An outcome is `{"ok": value}` or `{"error": error}`. An error has
  `message` (the text people see, which must match exactly), `type` and
  `case`. Decoding errors also have Foundation's `debugDescription`,
  `codingPath` and `underlying` detail. Match those where practical; they are
  developer detail, not user text.
- Fixture IDs look like `00000000-0000-4000-8000-000000000123`. IDs the Mac
  creates at random (copies, groups, geometry tokens) are replaced by
  `FFFFFFFF-0000-4000-8000-00000000000N`, numbered by first appearance. The
  walk visits elements in document order and, in each element, `id`,
  `groupID`, `startAnchor`, `endAnchor`, `geometryToken`, then
  `appendEpoch`. Apply the same renaming to the Windows output before
  comparing. Selections are sorted lists.

## Sections

| Folder | Contents | How to compare |
| --- | --- | --- |
| `platform` | Swift memory layout, Foundation JSON reading and writing, string equality and case mapping, Core Graphics paths, rectangle math, hatch positions, libm samples | Exact. libm values within 1 unit in the last place |
| `boards/valid` | Board files and the bytes the app writes after opening each one (`.written.json`) | Read, write, rename random IDs, compare with `.written.json`: an equal model is required (WIN-CORE-02); equal bytes are the goal of the Windows codecs |
| `boards/invalid` | Board files and the exact error, plus recipes for limits too large to commit | Exact message |
| `profiles` | Preference files, the model after migration (or the error) and after saving | Compare the model as sorted-key JSON; exact messages |
| `shortcuts` | Key parsing and labels, command IDs, global shortcut checks, scripted bind and assign sessions, and `macos-key-codes.json`, the key table of WIN-INP-04 | Exact |
| `library` | Saved-shape files, inserting them, naming rules, invalid files, a scripted store session | Exact; numbers within 1e-9 |
| `editing` | Scripted document sessions: each step's result and the document after it | Exact; numbers within 1e-9 |
| `geometry` | Paths from `SceneRenderer` for every shape and stroke, arrowheads, polygons, connectors, anchors, drag constraints, viewports, transforms, axis ticks, pointer shapes | Same element kinds and counts; numbers within 1e-6 (WIN-CORE-04) |
| `recognition` | Pen strokes and the Smart Draw result | Same tool or null; numbers within 1e-6 |
| `hittest` | Hit grids for single elements and for overlapping scenes | Exact, except points within 1e-9 of a threshold |
| `motion` | Curves, springs, fades and timelines sampled every millisecond; durations | Durations exact; samples within 1e-4 (WIN-MOT-01) |
| `render` | Scenes drawn at 1x and 2x with their board files | WIN-REND-05 tolerance |
| `effects` | Input images through Blur, Pixelate, Distort, covers, image layers and privacy crops | WIN-REND-05 tolerance |

`render` scenes marked `fontDependent` use macOS system fonts, and
`geometry/text.jsonl` records macOS font metrics. Compare the layout rules
there, not the pixels or sizes. `interpolationDependent` scenes scale a small
bitmap; compare them after the Windows interpolation choice is approved.

## Findings for the Windows port (2026-10-19)

These answer the open questions in `apps/windows/README.md` ("Core port notes"
and "Scene geometry notes").

Core:

- `MemoryLayout<Element>.stride` is **528** (size 528, alignment 8), so
  `estimatedBytes` uses 528, not the 512 floor. The history budget in
  `editing/history-limits.jsonl` depends on it.
- Duplicate JSON keys: the **first** value wins, in objects and in
  dictionaries. Dictionary keys that are canonically equivalent (`é` and
  `e` + U+0301) count as duplicates.
- Foundation **accepts trailing commas** in objects and arrays.
- Foundation **reads UTF-16** (either byte order, with or without a byte order
  mark) and UTF-32 big endian without a mark. It refuses UTF-32 little endian
  with a mark, which it reads as UTF-16. A UTF-8 byte order mark is skipped.
- Nesting up to 512 levels is accepted; 513 is refused.
- An `Int` accepts integral values written with a fraction or exponent (`1.0`,
  `1e2`, `100e-2`) and refuses `1.5` and `1e-2`. A `Double` refuses values
  that overflow and values that underflow to zero (`2.4e-324`).
- Messages: a missing key or null value reads "The data couldn’t be read
  because it is missing."; anything else "The data couldn’t be read because it
  isn’t in the correct format." Writing NaN or infinity fails with "The data
  couldn’t be written because it isn’t in the correct format."
- Writing: `/` is escaped as `\/`; DEL and U+2028 are not escaped; control
  characters use lowercase `\u00xx`. Integral doubles below 2^53 have no
  exponent; larger ones may (`1e+16`, `9.007199254740994e+15`).
- Swift `==` compares canonical equivalence (`é` = `e` + U+0301, U+212B =
  `Å`, U+2126 = `Ω`, U+212A = `K`). `<` orders by Unicode scalar after
  normalization. Case mapping is in `platform/strings.jsonl`, for example
  `ß` → `SS` and `ΑΣ` → `ασ`.

Core Graphics:

- `addRect` starts at the minimum corner and runs through the maximum x first;
  negative sizes are standardized. Confirmed.
- `addEllipse` starts at (maxX, midY), runs toward maxY, and uses four cubics
  and a close. The control factor is **0.5522847498**, not the exact
  4(√2 − 1)/3. Confirmed apart from that constant.
- `addRoundedRect` with a zero radius is a plain rectangle. Otherwise it
  starts at (maxX, midY), runs toward maxY and **always adds the line before
  each corner, even when it has zero length** (full radius). This differs from
  the port's assumption.
- Arcs use one cubic per part of at most 90 degrees and add a line from the
  current point to the arc start. `clockwise` is meant for y-up coordinates,
  so in the app's flipped coordinates it runs the other way. An arc with equal
  angles only moves; a zero radius adds a line to the center and a
  zero-length cubic.
- A close with no open subpath is ignored, and so is a second close. A line
  after a close continues from the closed subpath's start without a new move.
  A line with no current point is ignored. A move right after a move replaces
  it.
- `boundingBox` includes move and control points; `boundingBoxOfPath` is the
  tight box. The renderer uses `boundingBox`.
- `integral` rounds the origin down and the far corner up, after
  standardizing. A zero-size rectangle becomes 1 x 1. `CGRect.null` stays
  null.
- Hatch positions are `start + index × spacing` with a fused multiply-add.
  Confirmed: the large frame in `platform/hatch.jsonl` differs otherwise.

Effects: Core Image filters in extended linear sRGB with 16-bit float
channels (`effects/context.json`). Blur, Pixelate and Distort therefore work
on linear values. On Apple silicon the software-renderer option still runs on
Metal, so these outputs equal the app's GPU path on this Mac.

## Not in this corpus

- Live screen capture, the visible show and hide animations, live zoom and
  the region-sharing output. They need a visible screen and are recorded
  separately (plan task M-4).
- Magic flight and Dissolve particle curves, which stay art-directed in the
  app.
- Gesture math that lives in `DrawingCanvas` (resize with Shift, rotation,
  vertex drags). Creation constraints are covered through `DragConstraint`.
