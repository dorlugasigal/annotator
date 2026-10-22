# Native demo recording and camera tracking

For the current fresh feature takes, use the repository-local
`annotator-demo-recording` skill. It documents the verified dark-toolbar,
point-editing, no-Done and colorful-image blur workflow in
`ProductFeatureRecordingTests`, plus pacing and native/video validation.
The commands below describe the earlier full film.

The current feature encoder is `python3 tools/demo/render-product-takes.py`.
It writes `fresh-{draw,arrow,blur}.mp4`, posters and captions to the website
from new source takes, and records hashes/control bounds in `product-takes.json`.
It preserves source timing. Use the skill before a future pacing change.

The recorder is a test-only creator tool, not a product screen recorder.
It uses the actual desktop overlay and native controls with a fixed-seed pointer.
Captured notes, browser data and windows are controlled fixtures.

## Capture

```sh
make generate
ANNOTATOR_RECORD_INTERVIEW=1 TEST_RUNNER_ANNOTATOR_RECORD_INTERVIEW=1 \
ANNOTATOR_RECORD_NATIVE_QUALITY=1 TEST_RUNNER_ANNOTATOR_RECORD_NATIVE_QUALITY=1 \
DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer \
bash tools/macos/build-signed-dev.sh test \
  -only-testing:NativeAppTests/NativeDesktopRecordingTests
```

The native-quality route requests 60 fps at the display's backing-pixel
resolution and uses HEVC. ScreenCaptureKit may emit variable-rate frames;
the final edit normalizes to 60 fps. Source metadata includes cursor positions,
control rectangles/press times, toolbar size changes and notes bounds.

The active committed source and portable metadata are
`videos/annotator-native-promo/assets/native-quality-source.mp4` and
`videos/annotator-native-promo/native-quality-source.json`.

## Camera and edit

```sh
python3 tools/demo/recording/test_cursor_camera.py
python3 tools/demo/render-cursor-demo.py \
  --metadata videos/annotator-native-promo/native-quality-source.json \
  --speed 2 --output build/cursor-demo/tracked.mp4
cp build/cursor-demo/tracked.mp4 \
  videos/annotator-native-promo/assets/cursor-tracked.mp4
python3 tools/demo/render-cursor-ad.py
```

The planner anticipates the cursor and visible control. It retains toolbar
and notes context, limits zoom to 1.10 and verifies every recorded control
press is in frame. The final 1280x960 picture is downsampled, not enlarged.
Camera motion receives a two-frame 7:1 temporal mix only while moving.
Do not use it to blur private information; this is an editorial movement effect.

FFmpeg evaluates the camera in six-second pieces to bound expression size.
Metadata and filter files are retained next to the output for inspection.
This script prepares footage; it does not publish or operate a user's desktop.
HyperFrames then composes the ad with text and attributed music.

Cap's local Studio mode was inspected, but its graphical target/editor could
not be addressed because the computer-use connection returned text without
requested screenshots. No existing Cap project was opened or modified.
The delivered film must not be described as Cap-produced or human-recorded.
