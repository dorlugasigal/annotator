#!/usr/bin/env bash
# Owner-run: records the website demo takes (zoom, library) on the DeskPad virtual display at HiDPI
# (1920x1080 pt, 3840x2160 px). Only DeskPad changes mode, and it is restored on exit. The main display is never
# touched. Build first with the signed build-for-testing command in the demo-recording skill, using the derived
# data path build/WebsiteDemoRecordingSigned. Usage: tools/demo/record_website_demo.sh [zoom|library]
set -uo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
tool="$repo/build/website-demos/display_mode"
screen="${ANNOTATOR_WEBSITE_DEMO_SCREEN:-DeskPad}"
lock="${ANNOTATOR_DESKPAD_LOCK:-$repo/build/annotator-deskpad.lock}"
if [[ ! -x "$tool" ]]; then
  mkdir -p "$repo/build/website-demos"
  TMPDIR="$repo/build/website-demos" swiftc -O "$repo/videos/annotator-reel/tools/display_mode.swift" -o "$tool" || exit 1
fi
if ! mkdir "$lock" 2>/dev/null; then echo "Another recording holds $lock. Wait for it to finish."; exit 1; fi
original="$("$tool" "$screen" get | cut -d' ' -f1)" || { rmdir "$lock"; echo "Open DeskPad first."; exit 1; }
restore() {
  "$tool" "$screen" set "$original" >/dev/null && echo "restored $screen mode $original"
  rmdir "$lock"
}
trap restore EXIT
"$tool" "$screen" set "${WEBSITE_DEMO_HIDPI_MODE:-24}"
sleep 2
export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
take_env=()
if [[ -n "${1:-}" ]]; then take_env=(ANNOTATOR_WEBSITE_DEMO_TAKE="$1" TEST_RUNNER_ANNOTATOR_WEBSITE_DEMO_TAKE="$1"); fi
env ANNOTATOR_RECORD_WEBSITE_DEMO=1 TEST_RUNNER_ANNOTATOR_RECORD_WEBSITE_DEMO=1 \
  ANNOTATOR_WEBSITE_DEMO_SCREEN="$screen" TEST_RUNNER_ANNOTATOR_WEBSITE_DEMO_SCREEN="$screen" "${take_env[@]}" \
  xcodebuild -project "$repo/apps/macos/Annotator.xcodeproj" -scheme Annotator -configuration Debug \
  -destination 'platform=macOS,arch=arm64' -derivedDataPath "$repo/build/WebsiteDemoRecordingSigned" \
  CODE_SIGN_STYLE=Manual CODE_SIGN_IDENTITY="Apple Development" CODE_SIGNING_REQUIRED=YES DEVELOPMENT_TEAM="${DEVELOPMENT_TEAM:-NK959D954Y}" \
  -only-testing:NativeAppTests/WebsiteDemoRecordingTests test-without-building
