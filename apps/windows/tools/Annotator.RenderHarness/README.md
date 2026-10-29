# Annotator.RenderHarness

A manual harness that measures `OverlayPresenter`, the capture service and the
effect layer on the real overlay of the primary display, with the hardware
device. It is not part of the solution, `validate.ps1` does not build it, and
it is never shipped. The targets it measures are in
[docs/performance](../../../../docs/performance/README.md).

## Run it safely

- Every run ends within `--timeout` seconds (default 120). A watchdog prints
  `TIMEOUT` and ends the process. Each overlay-thread step waits at most 60 s,
  GPU queries give up after 2 s and capture checks after 0.5 to 2 s.
- Modes that need composition frames first draw a small magenta patch and wait
  up to 2 s for a captured frame that shows it. Without one (display off,
  session locked) the run prints `NO FRAMES` and stops.
- The overlay is click-through (Use apps). The harness takes no input and
  registers no shortcuts. Content is on screen only for the seconds a mode runs;
  `perf` draws at 5% opacity by default. `capture` shows a 48-point patch in the
  top-left corner for about 5 s, changing color 2.5 times per second (below the
  three flashes per second that can harm people with photosensitivity);
  `effects` blurs a 640 x 360-pixel area in the top-left corner for about 8 s,
  and `capture-cycles` four times for about 1 s each; `capture-start` shows a
  magenta 48-point patch in the top-left corner for about 10 s while capture
  starts and stops 13 times;
  `motion-live` shows the Mac's desktop, the overlay and the toolbar in a
  1,440 x 900-point area at the bottom of the primary display for about 6 s
  per take (about 17 s for `effects-live` and `sharing-live`, about 30 s
  in Use apps for `effects-measure`, and about 45 s for `sharing-measure` and
  `zoom-measure`). `zoom-screen` zooms the take's made-up display, which
  reaches beyond the area over the primary display, for about 6 s, and
  `zoom-lens` shows the lens for about 4 s. `sharing-live` also shows the output window in a top corner
  of the primary display's work area beside the area (`sharing-measure` in its
  top-left corner); `zoom-measure` zooms the whole primary display at 2x for
  about 25 s, and the measurements sweep colored bars across the stage.
- Only `input` injects input: mouse moves and one button into the running
  Annotator app given by `--pid`, each after `WindowFromPoint` confirms that the
  window under the point is Annotator's. Run it only while nobody uses the
  computer, with the app in Draw and the Pen tool on the whiteboard (Ctrl+W).
  `ink --press` presses and releases the left button once on the harness's own
  overlay, which takes input for those 2 to 3 s, after the same check. When
  another window comes over the point during a stroke (another check started),
  the run stops, and the button's release goes into a small, never-activated
  window of the harness's own under the pointer, so the button never stays
  down and no other app gets input.
- `latency`, `timing` and `race` change the color of a 32-point patch in the
  top-left corner, and `ink` of a 40-pixel square there, every 20 to 35 ms for
  a few seconds; the area is small, so it stays far below the flashing limits
  for photosensitivity, but warn anyone who watches the screen.
- `show` captures only the area under its opaque white backdrop, so
  screenshots never contain other windows; `motion` likewise draws an opaque
  backdrop on its overlay. Keep evidence outside the repository.
- Give the process an outer bound too, and stop only the process you started:

```powershell
dotnet build apps/windows/tools/Annotator.RenderHarness -c Release
$exe = 'apps/windows/tools/Annotator.RenderHarness/bin/Release/net10.0-windows10.0.26100.0/Annotator.RenderHarness.exe'
$run = Start-Process -FilePath $exe -ArgumentList 'latency', '--timeout', '120' -PassThru -NoNewWindow
if (-not $run.WaitForExit(180000)) { Stop-Process -Id $run.Id }
```

Exit codes: 0 done, 1 error (also when the leak check finds the overlay in a
captured frame), 2 usage, 3 no composition frames, 124 time limit.

## Modes

| Mode | What it measures | Time |
| --- | --- | --- |
| `frames` | Whether composition frames reach the screen | under 1 s |
| `latency` | Each layer kind as scene and as live layer: from the start of the submit to the composition time of the first captured frame that shows a new patch color | about 15 s |
| `race` | Two layer kinds (`--scene`, `--live`) updated in one work item, compared in the same captured frames | about 4 s |
| `memory` | Local video memory of each layer combination at the display's size: small and full-display content, cleared, idle release; then the default scene with a swap-chain live layer step by step | about 25 s |
| `surfaces` | One display-sized surface: allocation after small, distant and full updates, after trims and release; creation times of virtual surfaces, graphics devices and swap chains | about 15 s |
| `perf` | The WIN-PERF-01 workloads (`--workload standard`, `stress` or `all`): full redraw, selection change and live samples on a long stroke; CPU submit, GPU completion, redrawn pixels and allocations | about 35 s |
| `show` | The demo board at the display scale, 150% and 175%, with a growing live stroke; screenshots with `--evidence <folder>` | about 14 s |
| `input` | The running app (`--pid`): from a SendInput mouse move that extends a pen stroke to the composition time of the first captured frame that shows the new segment; by where the move fell in the refresh; with `--probe`, split by the app's own times | about 6 s |
| `timing` | What a captured frame's time is in the compositor's own frame statistics, the hardware overlay planes the output supports, and the latency of a live-layer patch (`--live`) by where its submit fell in the refresh, which shows when an update must reach the compositor; `--boost` holds DCompositionBoostCompositorClock meanwhile and `--gpu-priority <n>` sets the presenter device's GPU thread priority (-7 to 7) | about 4 s |
| `ink` | Windows 11's delegated ink trail on a display-sized composition swap chain (`--dcomp` to host it in DirectComposition, `--duplication` to look for it in DXGI desktop duplication, `--press` with the overlay in Draw and the button held), or with `--live SwapChain` a dot presented by the swap chain itself | about 4 s |
| `shot` | Nothing: saves `--area` of the primary monitor as Windows.Graphics.Capture composes it to the PNG file `--evidence`. GDI screen copies can miss the overlays, which are layered windows without a redirection bitmap (S-1) | under 1 s |
| `capture` | The capture service on the primary display: time to the first frame, frame rate, CPU and managed allocations in three 3-second windows, video memory; then the leak check: a patch on the overlay, which a plain monitor capture sees, must never appear in the service's frames | about 20 s |
| `capture-cycles` | Live effects started and stopped four times (`--cycles`, 1 to 100), as when a person adds and deletes a region: one 640 x 360-pixel blur through the effect layer and the capture service as the app creates them. After each stop, the video memory left above the start as Direct3D counts it and as Task Manager does (PDH dedicated), private bytes, handles by type, and the capture sessions opened and reused; `--linger-ms` sets how long the stopped capture stays paused (default the app's 60 s, 0 ends it with each stop) | about 12 s |
| `capture-start` | The first frames of a capture that starts: the overlay shows a magenta patch in the top-left corner, then capture starts `--cycles` times (default 4) with a new service each, on the primary display and on its right half as a made-up display (as the motion run's stage), and once more as a paused capture taken again; every frame of the first 0.4 s is read under the patch. A frame that shows it did not leave Annotator's windows out, or showed the screen's corner instead of the half | about 15 s |
| `effects` | The effect pipeline's GPU time at 5,120 x 1,440 pixels with synthetic frames (nothing on screen), then one blur region on the real overlay from the service's frames: start time, effect frames per second, CPU, allocations, video memory of capture and of the effect layer, Suspend and Resume, release. With `--offscreen`, only the pipeline timings: nothing appears on screen | about 15 s, 3 s offscreen |
| `motion` | The show and hide motion of each style (`--styles`, default all eight) in the app's development-only motion run, which runs beside a running Annotator: every frame's composition time, frame intervals, a 12-frame contact sheet per transition in the Mac reference format, and the CPU and 3D-engine time of the app and of dwm.exe next to an idle window. Writes JSON and PNG files under `--evidence` | about 5 s per style |
| `motion-live` | Replays takes of the Mac's live recordings (`reference/ux-reference/macos-live`) on the Mac's stage and records them at real speed: the ten `show-hide-*` takes by default, `sharing-live` (a shared region with drawings, a blur, Magic's hide and show and Stop sharing; the output window is recorded on its own as `sharing-output`), `sharing-controls` (only that take's toolbar steps, for the control motion), `effects-live` (live effects over the stage, which the app captures), `zoom-screen` (Ctrl+1's zoom ramps in around the pointer, follows it, zooms further and ramps out) and `zoom-lens` (the pointer lens fades in, follows the pointer and turns off). Writes, per take under `--evidence`, a video re-timed to 60 frames per second as the Mac's were packed, with the take's scripted pointer drawn as the Mac's recordings show theirs, a JSON file in the Mac's format with the segments, events, the pointer's path and every frame's time, and the app's report. `effects-measure`, `sharing-measure` and `zoom-measure` are no recordings: they measure the app's effects, region sharing and screen zoom on the stage (see Method) | about 6 s per take, 8 s for `zoom-screen` and `zoom-lens`, 17 s for `effects-live` and `sharing-live`, 30 s for `effects-measure`, 45 s for `sharing-measure` and `zoom-measure` |
| `live-compare` | Nothing on screen: compares `motion-live` takes under `--evidence` (`--takes`, default every take there) with the Mac recordings they replay, frame by frame, both decoded with Media Foundation at the Mac's 1,440 x 900 points. Per take, in its folder: `windows-<take>.png` (a contact sheet at the Mac's offsets), `compare-<take>.png` (Mac and Windows side by side at the same offsets from each segment's start) and `compare-<take>.json` (see Method) | 10 to 60 s per take |
| `pointer` | Nothing on screen: draws the pointer references (`pointer/halo-*`, `pointer/spotlight`, the `pointer/motion` laser trail and click effects) with the Windows renderer on WARP at 2x and compares their pixels with `reference/ux-reference/macos` (`--reference`); writes the images, sheets (Mac, Windows, difference) and `pointer/summary.md` to `--evidence` | about 2 s |
| `video-check` | Nothing on screen: the Media Foundation path that `motion-live` and `live-compare` use instead of ffmpeg. Decodes every Mac recording in `--reference` and checks its size, frame count and frame spacing against its JSON file, then encodes 120 frames of the Mac's desktop with a moving bar and colored corners at the 125% stage size (or `--area`'s size) and decodes them again: frame count, spacing, orientation and the mean error per channel (at most 4) | about 50 s |

Options: `--samples` (latency, timing, ink and race 60, perf 1,000, capture frames checked
for leaks 1,000, effects frames per workload 100), `--scene` and `--live`
(`VirtualSurface`, `DrawingSurface` or `SwapChain`), `--realizations`,
`--opacity` (perf), `--evidence` (show; the PNG file for shot; the CSV samples of
input and timing), `--pid` and `--probe <file>` (input),
`--area x,y,width,height` (shot, motion), `--ink r,g,b` (input), `--offscreen`
(effects), `--boost` and `--gpu-priority` (timing), `--dcomp`, `--duplication` and `--press` (ink), `--timeout`; for `motion` also `--styles`, `--app` (default: the newest
Annotator.exe under `src/Annotator.App/bin`), `--reduce-motion`, `--theme dark|light`
and `--objects` (0 to 64). `motion` shows a 1,600 x 900-pixel area (default: the
middle of the primary monitor) for about 5 s per style: an opaque backdrop on
the overlay, the sample board, the real toolbar and the motion. `motion-live`
takes `--takes`, `--app`, `--reference` (default `reference/ux-reference/macos-live`),
`--ffmpeg` (an ffmpeg to read the Mac's desktop and encode the videos instead of
Media Foundation), `--backdrop` (default a frame of `show-hide-none.mp4`) and `--theme`
(default each take's appearance); it records a 1,440 x 900-point area at the
bottom center of the primary monitor's work area.

## Videos without ffmpeg

`motion-live`, `live-compare` and `video-check` read and write H.264 MP4 files with
Windows' own Media Foundation (`MediaVideo.cs`): the Source Reader decodes with the
built-in H.264 decoder and converts to 32-bit BGR, and the Sink Writer converts BGR
to NV12 and encodes with the built-in H.264 encoder in quality mode (85), at a
constant 60 frames a second. Only software transforms run, so the results do not
depend on the graphics driver. Windows N editions need the Media Feature Pack for
H.264. Media Foundation leaves out an MP4's edit list, so it reports the Mac
recordings' first frame at 33 ms and its own files' at 17 ms; the harness counts
frame times from the first frame, as ffmpeg does (frame n is at n/60 s).
`--ffmpeg <path>` still uses ffmpeg for `motion-live` where one is present.

## Method

- Latency follows the S-1 color-code method: Windows.Graphics.Capture captures
  the whole monitor with no border and a 1 ms update interval, and each frame's
  `SystemRelativeTime` (QPC) is compared with the submit start (`Stopwatch`,
  QPC). It is submit-to-composition time, not input-to-photon time.
- `input` uses the same capture: the time runs from just before `SendInput` to
  the first frame whose pixel just behind the pointer shows the ink (`--ink`,
  default the pen's coral 240,69,56). It
  includes Windows input delivery, the app's processing and composition, but not
  the mouse, USB polling or the display's scan-out.
- A captured frame's `SystemRelativeTime` is the vertical blank at which the
  compositor's frame reaches the display: `timing` finds every frame time on a
  vertical blank (DwmGetCompositionTimingInfo) and one refresh after the start
  of the compositor frame that drew it (DCompositionGetStatistics). So a
  latency covers the wait for the compositor's next frame, which starts at a
  vertical blank, plus that frame's refresh. `latency`, `timing` and `input`
  print the latency by where its start fell in the refresh (eight bins, ms
  after the vertical blank) and `--evidence` keeps every sample as CSV.
- `input --probe <file>`: start the app with `ANNOTATOR_LATENCY_LOG=<file>`
  (development measurement only): the overlay writes each pointer message's
  own time stamp (`POINTER_INFO.PerformanceCount`), the start of its handling
  and the end of the canvas's and the presenter's work (QPC) at the end of
  each stroke, and the harness matches them to its moves by position: send to
  the pointer event, send to the overlay's handling, the handling, and the end
  of the handling to the frame.
- CPU submit is the presenter call on the overlay thread. GPU completion is the
  time from the submit start until a Direct3D 11 event query issued after the
  call signals; it includes waiting in the GPU queue. Timestamp pairs around
  composition-surface draws read near zero on this driver for small updates and
  with realizations while the event query still shows the work, so reports use
  CPU submit and GPU completion.
- Memory is `IDXGIAdapter3::QueryVideoMemoryInfo` (local segment) for the
  process after flushing the immediate context, because Direct3D destroys
  released resources only then.
- `perf` stretches the 1,920 x 1,080-point fixtures of the Mac benchmarks over
  the display and grows the long stroke back across the display one sample at a
  time, sleeping 4 ms between samples (about one frame at 240 Hz).
- `capture` and `effects` report this process's CPU time as a share of one
  core. The compositor (dwm.exe) is shown only when Windows lets the harness
  read it, which normally needs elevation. Each window says whether someone
  used the computer meanwhile (input or foreground changes), because the
  overlay reacts to foreground changes and other work changes the numbers.
  Managed allocations count every thread of the process
  (`GC.GetTotalAllocatedBytes(precise: true)`).
- The leak check reads an area inside the patch from every service frame on
  the capture device. A frame counts as a leak when at least a quarter of the
  area matches a patch color within 24 per channel. A plain Windows.Graphics.
  Capture monitor capture, which includes Annotator's windows, must show the
  patch at the same time, or the run stops.
- `capture-cycles` reads the process's video memory twice: Direct3D's
  `QueryVideoMemoryInfo` (local segment) and PDH `\GPU Process Memory(*)\Dedicated Usage`,
  which Task Manager shows. Only PDH counts the Windows.Graphics.Capture frame
  buffers that the compositor shares with the process, and the allocation that
  Windows keeps for every capture session that started until the process
  ends. Readings come after a garbage collection; each cycle also prints the
  handles gained by type and whether the capture paused (it lingers for the
  app's 60 s, or `--linger-ms`) and how many sessions were opened and reused;
  the last reading comes after the capture service let go of everything.
- The effect pipeline's GPU time is between Direct3D 11 timestamps around one
  `EffectPipeline.Render` on a 5,120 x 1,440 synthetic frame (scale 1 unless
  the workload says otherwise). Live effect CPU time is
  `EffectsStatistics.LastFrameTime`, sampled every 5 ms.
- `motion` runs `Annotator.exe --motion-reference <style>` (development
  builds only) and reads the run's phase from shared memory: idle, hiding,
  showing, and when the overlay started the motion and how long its plan
  runs. Frame times are the `SystemRelativeTime` of every captured frame
  from the motion's start to its planned end; Windows.Graphics.Capture sends
  a frame only when the screen changes, so a gap can also mean nothing moved.
  Contact-sheet frames are GPU copies of the first frame at or after each of
  12 even times (the newest frame when the screen stopped changing). CPU and
  3D-engine time come from PDH (`\GPU Engine(*)\Running Time` per process,
  `\Process(dwm)\% Processor Time`) and the app's process, sampled when the
  phase changes, next to the same counters over the idle 0.6 s before the
  hide.
- `motion-live` first shows the Mac recording's desktop in a click-through
  stage window that never puts itself back on top, and checks that it covers
  the area. Each take then runs `Annotator.exe --motion-reference <style>
  --take show-hide|reversal|controls|effects|sharing|zoom-screen|zoom-lens` (development builds only), which builds
  the stage of `UXLiveRecordingTests`: a 2,560 x 1,440-point display placed so
  that the recorded area is on screen, the toolbar docked at the bottom, the
  seven drawings (none for `effects`, `sharing` and the zoom takes), and the Mac's steps and waits. The
  canvases take only the take's own pointer input: `effects` drags its regions
  through the canvas's pointer input, 54 moves 16 ms apart as the Mac does, and
  its effects read the stage through the app's capture service. Every captured frame is copied
  on the GPU; the last one in each 60 fps slot is read back. A take is deleted
  when a corner of the stage shows another window (not checked while screen
  zoom or the lens shows), or when the toolbar is missing where it should be
  (the stage above it).
- The scripted pointer moves as the Mac recorder's does: its `DemoHandMotion`
  (seeded 2026101901, ported in `Reference/DemoHandMotion.cs`) with the same
  travels in the same order, a move every 16 ms, and a 0.4 s travel before
  each drag. Zoom and the lens follow it as they follow the mouse hook
  (`DrawingCanvasFactory.ReferencePointer`), and the app reports its path.
  The Mac's recordings show its pointer (a recorder window above everything
  that its own capture leaves out); the harness draws the same arrow, and the
  accent circle while pressed, onto each frame where the pointer was then.
  `zoom-lens` starts from the state the Mac's zoom-screen left: Use apps, the
  document's zoom at 2.67x, the pointer where it ended and the path generator
  after zoom-screen's three travels.
- `sharing` shares the Mac's custom region of the sample content through the
  app's capture service, moves the output window to a top corner of the work
  area beside the recorded area, in front of other apps without focus, and
  writes its handle into the shared memory. The harness captures that window
  alone (Windows.Graphics.Capture of the window, without the system pointer,
  as a meeting app's window share) with a second recorder on the take's clock,
  and the take starts only once that recording runs. The drags follow the
  Mac's `drag(points, seconds)`: the travel to the first point, which also
  moves the output's own arrow, then seconds x 60 canvas moves 16 ms apart,
  evenly by distance. `sharing-output` has the Mac's format with
  `startsBeforeSharingLive` 0: both recordings count from the take's start.
- `live-compare` reads frame n of a video as time n/60 s. Show and hide
  (a port of the earlier comparison script): per segment, the frames where the
  drawings above the toolbar's band (y < 740) and anything change (more than
  40 pixels by more than 24), the toolbar's dark box (pixels darker than 60)
  and its swap, pulse, shrink, disappearance and reappearance, from the request
  and from each platform's first moving frame, Windows minus Mac, and the lag
  (in frames, -24 to 24) that best aligns the two takes' change counts.
  `effects-live`: per frame, the share of black pixels in each region (outside
  the cover) and the toolbar's height; at check times on each platform's clock,
  the mean difference per channel inside each region and the cover's level,
  with crops (`crops-effects-live.png`). `sharing-live`: the output window's
  covered frames (92% near black under the title bar) and sheets of the hide
  and the show with the output and the shared region (`sharing-hide.png`,
  `sharing-show.png`). `zoom-screen`: each frame's magnification z, the factor
  whose mapping of the frame before the zoom, x shows p + (x - p) / z around
  the resting pointer p, matches the frame best (half-resolution gray, capped
  differences, so the pointer and toolbar count little), fitted by its start
  to `MotionTiming`'s 260 ms entrance on the enter curve and 200 ms exit on the
  standard curve; the steps of zooming further; and while the pointer moves,
  the point the view magnifies, against the Windows take's pointer path.
  `zoom-lens`: the lens's opacity in each frame (its interior's share of the
  way from the frame without it to the frame with it), fitted to the 120 ms fade
  on the fade curve; when it is gone after turning off; and its magnification.
- `effects-measure`, `sharing-measure` and `zoom-measure` show the stage
  without recording it and run `--take <take>`: in Use apps, the app marks
  phases in the shared memory (5 before; 6 effects-live's three regions,
  sharing-live's region shared into the output, or screen zoom at 2x around the
  stage's pointer, over still content; 7 the same while the harness animates a
  bar across each area they show on every composition frame; 11 while it
  animates bars beside them instead (sharing and zoom); 10 while the app's
  script circles zoom's or the output's pointer, one turn a second; 8 after
  deleting the regions, Stop sharing or stopping zoom) and records its own CPU
  time, managed allocations, frames drawn (effect layer, zoomed screen and
  shared region), the capture service's counters (frames captured, compared
  and unchanged, comparison delays, sessions opened and reused), working set,
  private bytes, threads and whether capture runs at each phase's ends. The
  harness samples the app's and dwm.exe's 3D-engine time and the app's video
  memory (`\GPU Process Memory(*)\Dedicated Usage`) with PDH every 100 ms and
  writes `<take>.json`. The scripted pointer is not input, so capture stays at
  the still rate in phase 10 unless the view moves.