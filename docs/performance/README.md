# Performance checks

The Release CLI suite verifies deterministic fixtures and records CPU bitmap,
model and memory measurements. It has no numeric timing pass/fail threshold.
Do not treat an exit code of zero as launch approval.

The native targets remain p95 **<=8 ms app frame work** and **<=33 ms
input-to-visible response at 60 Hz**, for the standard scene (200 objects and a 20,000-point stroke) and the stress
scene (1,000 objects and a 200,000-point stroke). The CLI suite doesn't verify
them. Windows runs the same workloads with
`pwsh -NoProfile -File tools/windows/validate.ps1 benchmarks`.

## Run the CPU suite

From the repository root, with full Xcode installed:

```sh
export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
bash tools/macos/benchmark.sh --samples 21 --warmup 3 \
  --output build/performance/local.json
```

The script defaults to that Xcode location without changing `xcode-select`.
Set `DEVELOPER_DIR` for another installation. It builds only
`PerformanceBenchmarks`, in Release, under `build/performance/swift-build`.
`ANNOTATOR_BENCHMARK_SCRATCH_PATH` can select another build directory if a
second benchmark build is needed. Avoid concurrent runs when comparing speed.

Each of the 32 workloads gets a fresh process, three discarded warmups and
21 measured samples with the command above. The report contains all samples,
nearest-rank p50/p95, fixture sizes, checksums, memory readings, source hash,
git revision/dirty state, hardware, OS and toolchain. A dirty revision is not
an immutable source snapshot; retain the report with the reviewed code.
Keep the measured source files unchanged during the run. Other processes,
thermal state and scheduling are uncontrolled.

To reproduce one scenario, use its exact name from the full JSON report:

```sh
bash tools/macos/benchmark.sh --worker renderer.rough-1000.cached \
  --samples 21 --warmup 3 > build/performance/rough-1000-worker.json
```

A worker writes one result to stdout and ignores `--output`. It does not
include the full report's environment/source metadata. Use the full suite for
an evidence record. See the [harness contract](../../apps/macos/Sources/PerformanceBenchmarks/README.md)
for timing boundaries, fixture design and RSS semantics.

## Read results and failures

Cold means an empty renderer cache, not a cold OS. Cached means a renderer
primed outside timing; scenes larger than the 512-path cache can still rebuild
paths. Bitmap clear, production drawing and flush are timed. Fixture creation,
context allocation, priming and checksum verification are excluded.

Selection samples contain 100 alternating hit/miss selections, and the append
sample is one batched 200,000-point gesture. Neither is a single input-event
latency. JSON roundtrip includes encoding, decoding and validation, with no
disk I/O. RSS includes setup/warmup and allocator retention; it is neither
per-operation allocation cost nor proof of a leak.

Build, fixture, checksum, memory-reading and changed-source failures stop the
suite with a nonzero exit. The aggregate JSON is written only after every
workload succeeds. If a run fails, an older file at the same output path can
still exist: check the exit status and report timestamps, not file existence.
Use a fresh output filename and preserve stderr when investigating a failure.
Rerun the named worker to isolate a fixture error. Do not lower targets,
disable checks or report an old JSON file as a successful new run.

## Native and Release checks

Don't run these beside another Xcode build or test. They are separate from
the CLI benchmark.

```sh
export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
make generate
xcodebuild -quiet -project apps/macos/Annotator.xcodeproj \
  -scheme Annotator -configuration Release \
  -derivedDataPath build/UnsignedReleaseDerivedData \
  CODE_SIGN_IDENTITY=- CODE_SIGNING_REQUIRED=NO build
make signed-native-test
```

The unsigned Release app is
`build/UnsignedReleaseDerivedData/Build/Products/Release/Annotator.app`.
It is not a notarized installer. Native tests need the local development
identity and appropriate macOS permissions. For opt-in native GPU measurements:

```sh
ANNOTATOR_BENCHMARK_NATIVE=1 TEST_RUNNER_ANNOTATOR_BENCHMARK_NATIVE=1 \
  bash tools/macos/build-signed-dev.sh test \
  -only-testing:NativeAppTests/NativePixelBenchmarkTests
```

Those GPU tests use synthetic 1080p/4K buffers and real Metal drawables.
Submit-to-completion excludes WindowServer composition and the physical
display. It does not measure a physical external 4K monitor, sustained FPS or
input-to-photon response.

CI doesn't run benchmarks: shared runners give unreliable timings. Run them
on a quiet machine, keep the JSON report with the reviewed code, and attach the
numbers to the pull request. Permission-dependent native tests, real capture
cost, power, mixed-scale displays and receiver-side protection still need
measurements on real hardware.
