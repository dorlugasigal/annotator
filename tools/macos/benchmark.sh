#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../.."

if [[ $# -eq 1 && "$1" == "--help" ]]; then
    cat <<'EOF'
Usage: bash tools/macos/benchmark.sh [--samples 3..101] [--warmup 1..20]
       [--output PATH] [--worker SCENARIO]

Runs isolated Release CPU/model/bitmap workloads, not native hardware gates.
Defaults: 11 samples, 2 warmups, build/performance/baseline.json.
--worker writes one result to stdout; --output applies only to the full suite.
Set DEVELOPER_DIR for an alternate Xcode installation.
Set ANNOTATOR_BENCHMARK_SCRATCH_PATH for a separate SwiftPM build directory.
See docs/performance/README.md for measurement limits and failure handling.
EOF
    exit 0
fi

if [[ "$(uname -s)" != Darwin ]]; then
    printf '%s\n' 'Benchmarks require macOS and full Xcode (AppKit/CoreGraphics).' >&2
    exit 1
fi
export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"
if [[ ! -x "$DEVELOPER_DIR/usr/bin/xcodebuild" ]]; then
    printf '%s\n' "Full Xcode not found at $DEVELOPER_DIR. Set DEVELOPER_DIR to its Contents/Developer directory." >&2
    exit 1
fi

# A separate build directory avoids contending with native tests and app builds.
scratch="${ANNOTATOR_BENCHMARK_SCRATCH_PATH:-$PWD/build/performance/swift-build}"
swift build --package-path apps/macos -c release --scratch-path "$scratch" --product PerformanceBenchmarks >&2
bin="$(swift build --package-path apps/macos -c release --scratch-path "$scratch" --show-bin-path)"
export SWIFT_DETERMINISTIC_HASHING=1
exec "$bin/PerformanceBenchmarks" "$@"
