# Native demo recording and camera tracking

For the current fresh feature takes, use the repository-local
`annotator-demo-recording` skill. It documents the verified dark-toolbar,
point-editing, no-Done and colorful-image blur workflow in
`ProductFeatureRecordingTests`, plus pacing and native/video validation.
The commands below describe the earlier full film.

The website no longer embeds recorded takes (its homepage runs a live web demo),
so the old website encoders were removed. Recordings stay in `build/` and the
`annotator-media` repository.

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

## Camera

```sh
python3 tools/demo/recording/test_cursor_camera.py
```

`cursor_camera.py` plans a camera that anticipates the cursor and the visible
control. Do not use it to blur private information; it is an editorial movement
effect.

Cap's local Studio mode was inspected, but its graphical target/editor could
not be addressed because the computer-use connection returned text without
requested screenshots. No existing Cap project was opened or modified.
The delivered film must not be described as Cap-produced or human-recorded.
