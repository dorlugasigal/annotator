# Local Release benchmarks

Run from the repository root:

```sh
bash tools/macos/benchmark.sh
bash tools/macos/benchmark.sh --samples 21 --warmup 3 --output build/performance/baseline-21.json
```

The default is 11 measured samples and 2 discarded warmups per workload. The
runner uses a separate Release build directory, deterministic Swift hashing,
and one fresh subprocess per scenario. JSON is written atomically under
`build/performance`; stderr contains a concise duration/RSS table. Failures in
building, fixtures, output verification or memory collection fail the run.
No desktop, clipboard, capture API, app instance, network or native tests are
used. Fixtures are generated locally from a fixed seed; no assets are imported.

This implements the measurable CLI subset of specs 001 and 003 / QUAL-01.
The standard renderer scene has 200 mixed objects plus one 20,000-point stroke;
the stress scene has 1,000 total objects including a 200,000-point stroke.
The gradient scene applies multicolor strokes and translucent gradient fills to
the standard scene, using the same cold/cached measurements and pixel checks.
Separate seeded rough-ellipse cases straddle the renderer's 512-path cache.
An additional 32-ellipse case uses 1,600 x 900 geometry at roughness 2, 6 and 12.
The text case draws 1,000 on-canvas labels. The pressure case draws every segment
of a 200,000-point pen stroke with varying pressure and roughness 6. Its full-path
build counter distinguishes unused geometry allocation from the segment painting
that actually produces pixels. No pressure samples are dropped or combined.
Cold means an empty renderer cache, not cold OS/framework/filesystem caches.
Cached means a pre-drawn, retained renderer; cache-build counters reveal
whether the entire workload actually fits. Bitmap clear, drawing and flush
are timed; context creation, fixture generation, cache priming and pixel
checksum validation are outside timing. The same deterministic pixels must
match the reference render. Document setup is outside timing; add/move/undo
include production transaction/history work. Batched appends represent one
gesture, not individual input events.

Durations use monotonic uptime nanoseconds. Quantiles use nearest rank
(`ceil(p*n)-1`); with 11 samples p95 is the maximum, not a stable population
tail estimate. Raw samples remain in JSON. Raster decode includes touching
decoded pixels; PNG export includes production composition plus PNG encoding,
but no disk write. Model serialization includes encode/decode and validation.

Current RSS is `task_info(MACH_TASK_BASIC_INFO).resident_size`; process
high-water RSS is `getrusage(RUSAGE_SELF).ru_maxrss`, **bytes on Darwin**.
High-water marks include setup and warmup, are cumulative for the isolated
scenario process, and are not per-operation allocation peaks. Before/after RSS
cannot observe transient operation peaks. Cleanup snapshots bracket repeated
owned image/document/renderer allocations, explicit renderer reset, ARC and
autorelease-pool drainage. Idle CPU is process user+system CPU time across a
250 ms sleep after cleanup. Allocator/framework retention is not proof of a
leak; these are harness idle/cleanup numbers, not app lifecycle evidence.

The report fingerprints measured source files and records git revision/dirty
state, hardware, OS, toolchain, sample counts and explicit exclusions. Run on
an otherwise quiet machine for comparisons; CPU affinity, thermal state and
other process activity are not controlled. Only compare matching source,
workload, hardware and toolchain records.

Not measured: native event dispatch, display-link work, live capture, GPU
presentation, input-to-photon latency, frame rate, battery/power, native
observers/timers, or receiver-side protection. The <=8 ms app-frame and <=33 ms
input-to-visible targets cannot be passed or failed using these CPU bitmap
benchmarks. Those require a signed native app with a synthetic scene, event
and display-link signposts, compositor/GPU tracing, and synchronized physical
input/display observation (e.g. instrumented input plus high-speed camera).
Native capture/idle acceptance additionally needs app-owned capture fixtures
and Instruments allocations/energy/lifecycle traces. This report is not a
claim of complete app performance.
