# Annotator.Benchmarks

The Windows edition's Release benchmarks (WIN-PERF-01): the 34 workloads of the
Mac suite (`tools/macos/benchmark.sh`, `apps/macos/Sources/PerformanceBenchmarks`) with the same
names, fixtures, seed (`0xA6602026`), checks and JSON shape, followed by the
overlay presenter's frame work. It is not shipped. How to read results and the
targets are in [docs/performance](../../../../docs/performance/README.md).

## Run it

From the repository root, on a quiet machine (other builds and tests change the
numbers):

```powershell
pwsh -NoProfile -File tools/windows/validate.ps1 benchmarks
```

The suite publishes the project with Native AOT, like the app, into
`apps/windows/out/benchmarks` and runs it with 21 samples and 3 warmups. The
report goes to `build/performance/windows.json` (ignored by Git). The
`benchmarks` suite is not part of `validate.ps1 all`. Directly:

```powershell
apps/windows/out/benchmarks/Annotator.Benchmarks.exe --samples 21 --warmup 3 --output build/performance/windows.json
apps/windows/out/benchmarks/Annotator.Benchmarks.exe --only presenter.stress --samples 11 --warmup 2
apps/windows/out/benchmarks/Annotator.Benchmarks.exe --worker renderer.stress.cached --samples 21 --warmup 3
apps/windows/out/benchmarks/Annotator.Benchmarks.exe --list
```

`--only` runs the scenarios whose names start with any of the given prefixes.
`--worker` writes one result to stdout and ignores `--output`. Every worker runs
in a fresh process and is stopped after `--timeout` seconds (default 600); GPU
waits give up after 5 seconds. A failed build, fixture, check, worker or
reading stops the suite with exit code 1, and the report is written only after
every scenario succeeded, so check the exit code, not the file. A Debug build
refuses to run the suite.

## What it measures

- `document.*`: Annotator.Core's document model and board files, as on the Mac:
  one production mutation with history, 100 alternating hit and miss
  selections, one 200,000-sample gesture, a board JSON round trip.
- `renderer.*`: the Mac's CPU bitmap workloads drawn with Direct2D on the
  hardware device (the overlay's renderer) into a 1,920 x 1,080 target: scene
  build, renderer entry (cold only), clear, draw and EndDraw, until a Direct3D
  event query after the draw signals that the GPU finished. The CPU part is the
  `cpuSubmitMicroseconds` counter. Cold means a new scene builder, text measurer
  and renderer; cached means one primed outside timing.
- `image.*` and `export.*`: WIC decoding and PNG encoding, and
  `SnapshotExporter` on WARP, as the app's Export PNG renders.
- `memory.owned-cleanup-idle`: allocate 64 images, a document with 20 history
  edits and a renderer, draw, dispose, collect, then read memory and CPU across
  a 250 ms sleep.
- `presenter.*` (Windows only): `OverlayPresenter` on its own dispatcher thread
  with a compositor and real composition virtual surfaces at the reference
  display's size (5,120 x 1,440 pixels at 125%), without a window, so nothing
  appears on screen. The fixtures are stretched over the display's points, as
  `Annotator.RenderHarness perf` does. One sample is one frame: from the start
  of the presenter call until the overlay thread runs its next work item, which
  includes the composition commit (the app frame work of WIN-PERF-01). The
  presenter call alone (`presenterCallMicroseconds`), the GPU completion of an
  event query after the commit (`gpuCompletionMicroseconds`), the redrawn
  pixels and the managed bytes allocated on the overlay thread are counters.
  Kinds: `full-redraw` (scale or device change), `theme-change` (light and dark
  swapped with one drawing selected), `selection` (another drawing selected),
  `live-sample` (one more sample on the long stroke) and `zoom-pan` (screen
  zoom at 2x, the view 8 points further each frame).

## Differences from the Mac suite

- Renderer durations include GPU completion; the Mac's are CPU bitmap draws.
  Neither is the 8 ms frame budget, which the presenter workloads measure.
- Direct2D on a GPU antialiases an edge pixel or two of merged stroke outlines
  differently from draw to draw (measured on the RTX 3080: up to 5 pixels of
  2 million, up to 16/255; WARP is exact). Renderer samples must therefore match
  a reference draw within WIN-REND-05's edge allowance (32/255) on at most
  0.01% of the pixels, and the differences are recorded
  (`pixelsDifferingFromReference`, `largestDifferenceFromReference`); the Mac's
  CPU bitmaps match exactly, and its cold and cached workers are compared.
- Memory readings keep the Mac's field names: `currentRSSBytes` is the working
  set, `processPeakRSSBytes` the peak working set and `processCPUSeconds` user
  plus kernel time from `GetProcessTimes`, which Windows charges in scheduler
  ticks of about 15.6 ms.
- The cleanup workload disposes and runs the garbage collector where the Mac
  drains autorelease pools.
- The environment records the Windows build, the hardware and WARP devices, the
  app version and build, and a SHA-256 over the measured sources (Core,
  Platform, Rendering, Workspace, this project, `Directory.Build.props` and
  `validate.ps1`); a change during the run fails it.

Not measured: composition and present-to-display timing (PresentMon or Windows
Performance Recorder), input-to-visible time in the running app
(`Annotator.RenderHarness input`), physical input-to-photon time (a high-speed
camera), live capture, hooks, timers and power.
