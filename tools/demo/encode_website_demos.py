#!/usr/bin/env python3
"""Encode the website demo masters into silent looping web clips, posters and a manifest.

Run after the masters exist (see tools/demo/website_demo_shots.py):
  videos/annotator-reel/.venv/bin/python tools/demo/encode_website_demos.py

For each shot it writes website/public/demo/stage-<shot>.{mp4,jpg} (16:10 desktop) and
stage-<shot>-phone.{mp4,jpg} (4:5 phone), then website/src/data/stage-demos.json with checksums, sizes,
source takes, retiming and the native actions that each clip shows. The last DISSOLVE seconds of every
clip dissolve into its first frame, so the loop restarts from a clean state without a jump. The poster is
the finished result, just before that dissolve. Files are H.264 High 4.2, yuv420p, bt709, fast start and
silent.
"""
from __future__ import annotations

import hashlib
import json
import subprocess
import sys
from pathlib import Path

import cv2
import numpy as np

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "demo"))
sys.path.insert(0, str(REPO / "videos/annotator-reel/tools"))
import website_demo_shots as shots  # noqa: E402
import camera  # noqa: E402  (the reel's renderer, used read-only for its time map)

MASTERS = REPO / "build/website-demos/masters"
CHECKS = REPO / "build/website-demos/checks"
PUBLIC = REPO / "website/public/demo"
MANIFEST = REPO / "website/src/data/stage-demos.json"
FPS = 60
VARIANTS = {"desktop": ("16x10", ""), "phone": ("4x5", "-phone")}
# The body starts two frames after the dissolve length, so the loop ends on two pure frames of the start.
LEAD = round(shots.DISSOLVE * FPS) + 2


def run(cmd: list[str]) -> str:
    return subprocess.run(cmd, check=True, capture_output=True, text=True).stdout


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def frame(video: Path, seconds: float, width: int, height: int) -> np.ndarray:
    raw = subprocess.run(["ffmpeg", "-v", "error", "-ss", f"{seconds:.4f}", "-i", str(video), "-frames:v", "1",
                          "-vf", "scale=in_color_matrix=bt709:in_range=tv:out_range=pc,format=bgr24",
                          "-f", "rawvideo", "-pix_fmt", "bgr24", "-"], check=True, capture_output=True).stdout
    return np.frombuffer(raw, np.uint8).reshape(height, width, 3)


def encode(master: Path, out: Path, frames: int) -> None:
    dissolve = shots.DISSOLVE
    offset = (frames - LEAD) / FPS - dissolve
    graph = (f"[0:v]split=2[a][b];[a]trim=start_frame={LEAD},setpts=PTS-STARTPTS[body];"
             f"[b]trim=end_frame={LEAD},setpts=PTS-STARTPTS[head];"
             f"[body][head]xfade=transition=fade:duration={dissolve}:offset={offset:.5f},format=yuv420p[v]")
    run(["ffmpeg", "-v", "error", "-y", "-i", str(master), "-filter_complex", graph, "-map", "[v]", "-an",
         "-c:v", "libx264", "-preset", "slow", "-crf", "20", "-profile:v", "high", "-level:v", "4.2",
         "-pix_fmt", "yuv420p", "-g", "60", "-maxrate", "9M", "-bufsize", "18M", "-r", str(FPS),
         "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
         "-movflags", "+faststart", str(out)])


def probe(video: Path) -> dict:
    info = json.loads(run(["ffprobe", "-v", "error", "-show_streams", "-show_format", "-count_frames",
                           "-of", "json", str(video)]))
    streams = info["streams"]
    if [s["codec_type"] for s in streams] != ["video"]:
        raise SystemExit(f"{video.name} must have exactly one video track and no audio")
    s = streams[0]
    if (s["codec_name"], s["profile"], s["pix_fmt"], s["level"]) != ("h264", "High", "yuv420p", 42):
        raise SystemExit(f"{video.name}: unexpected encoding {s['codec_name']} {s['profile']} {s['pix_fmt']} {s['level']}")
    return {"frames": int(s["nb_read_frames"]), "duration": float(info["format"]["duration"]),
            "width": int(s["width"]), "height": int(s["height"])}


def clip_time(tmap: camera.TimeMap, source: float) -> float:
    """Inverse of the monotone time map: the clip time that shows this source time."""
    low, high = float(tmap.o[0]), float(tmap.o[-1])
    for _ in range(60):
        mid = (low + high) / 2
        if tmap.src(mid) < source:
            low = mid
        else:
            high = mid
    return (low + high) / 2


def speeds(retime: list, skips: list) -> dict:
    """Playback speed of each segment (source seconds per clip second), ignoring the opening freeze and any
    declared skip. Segments under half speed are holds, not playback, so `moving` leaves them out."""
    skipped = [tuple(skip["source"]) for skip in skips]
    rates = [(b[0] - a[0]) / (b[1] - a[1]) for a, b in zip(retime[1:], retime[2:])
             if not any(lo <= a[0] and b[0] <= hi for lo, hi in skipped)]
    moving = [rate for rate in rates if rate >= 0.5]
    return {"slowest": round(min(rates), 2), "fastest": round(max(rates), 2),
            "moving": [round(min(moving), 2), round(max(moving), 2)]}


def main() -> None:
    CHECKS.mkdir(parents=True, exist_ok=True)
    takes = {}
    demos = {}
    for shot in shots.SHOTS:
        entry = {}
        for variant, (aspect, suffix) in VARIANTS.items():
            spec = shots.build(shot, aspect)
            take = spec["take"]
            if take not in takes:
                meta = json.loads((REPO / shots.SOURCES[take] / f"latest-{take}.json").read_text())
                path = Path(meta["path"])
                takes[take] = {"meta": meta, "file": path.name, "sha256": sha256(path)}
            master = MASTERS / f"{shot}-{aspect}.mp4"
            info = json.loads((MASTERS / f"{shot}-{aspect}.json").read_text())
            if info["retime"] != [list(pair) for pair in spec["retime"]] or info["camera"] != spec["camera"]:
                raise SystemExit(f"{master.name} is older than tools/demo/website_demo_shots.py; render it again")
            width, height = shots.ASPECT_SIZE[aspect]
            out = PUBLIC / f"stage-{shot}{suffix}.mp4"
            encode(master, out, info["frames"])
            facts = probe(out)
            expected = info["frames"] - round(shots.DISSOLVE * FPS)
            if (facts["width"], facts["height"]) != (width, height) or abs(facts["frames"] - expected) > 1:
                raise SystemExit(f"{out.name}: {facts} does not match {width}x{height}, {expected} frames")
            duration = facts["frames"] / FPS
            # The loop is seamless when the last frame matches the first.
            first, last = frame(out, 0, width, height), frame(out, duration - 1 / FPS, width, height)
            seam = float(np.mean(np.abs(first.astype(np.int16) - last.astype(np.int16))))
            if seam > 1.5:
                raise SystemExit(f"{out.name}: the loop jumps (mean difference {seam:.2f})")
            poster_time = duration - shots.DISSOLVE - 3 / FPS  # the last frame before the dissolve
            poster = PUBLIC / f"stage-{shot}{suffix}.jpg"
            image = frame(out, poster_time, width, height)
            cv2.imwrite(str(poster), image, [cv2.IMWRITE_JPEG_QUALITY, 84, cv2.IMWRITE_JPEG_PROGRESSIVE, 1,
                                             cv2.IMWRITE_JPEG_OPTIMIZE, 1])
            cv2.imwrite(str(CHECKS / f"{shot}-{aspect}-first.png"), first)
            cv2.imwrite(str(CHECKS / f"{shot}-{aspect}-poster.png"), image)
            tmap = camera.TimeMap(spec["retime"])
            source = [spec["retime"][0][0], spec["retime"][-1][0]]
            actions = []
            for action in takes[take]["meta"]["actions"]:
                if not source[0] <= action["time"] <= source[1]:
                    continue
                shown = clip_time(tmap, action["time"]) - LEAD / FPS
                if shown < 0:
                    raise SystemExit(f"{shot}: {action} happens inside the loop dissolve")
                item = {key: action[key] for key in ("control", "gesture", "text", "target") if key in action}
                item.update({"sourceTime": round(action["time"], 3), "clipTime": round(shown, 3)})
                actions.append(item)
            entry[variant] = {
                "file": f"/demo/{out.name}", "poster": f"/demo/{poster.name}", "width": width, "height": height,
                "duration": round(duration, 4), "fps": FPS, "posterTime": round(poster_time, 4),
                "sha256": sha256(out), "bytes": out.stat().st_size, "loopSeam": round(seam, 3),
                "encoding": {"codec": "h264", "profile": "High", "level": 42, "pixelFormat": "yuv420p",
                             "frameRate": FPS},
            }
            entry.update({"take": take, "sourceFile": takes[take]["file"], "sourceSHA256": takes[take]["sha256"],
                          "source": [round(value, 3) for value in source],
                          "speed": speeds(spec["retime"], spec.get("skips", [])),
                          "skipped": spec.get("skips", []), "actions": actions})
            print(f"{out.name}: {facts['frames']} frames, {out.stat().st_size / 1e6:.2f} MB, seam {seam:.2f}")
        demos[shot] = entry
    manifest = {
        "schemaVersion": 1,
        "recorded": "2026-09-27",
        "shotList": "tools/demo/website_demo_shots.py",
        "encoder": "tools/demo/encode_website_demos.py",
        "renderer": "videos/annotator-reel/tools/camera.py",
        "input": "Timed native UI actions and canvas events with a scripted pointer, recorded by "
                 "apps/macos/Tests/NativeAppTests/ReelRecordingTests.swift (circle, arrow, highlight, smart, spotlight, blur) and "
                 "apps/macos/Tests/NativeAppTests/WebsiteDemoRecordingTests.swift (shapes, zoom). Not a human or "
                 "computer-use-provider recording.",
        "source": "Real-time ScreenCaptureKit recordings of the live Annotator overlay and toolbar on a 3840x2160 "
                  "HiDPI virtual display. Test-owned windows and fictional sample apps only.",
        "edits": "Virtual-camera framing, shortened pointer travel and waits, and a dissolve back to the first "
                 "frame at the end of each loop. The zoom clip steps over the two black frames that Screen zoom "
                 "shows while it starts. Nothing is drawn over the recording.",
        "dissolve": shots.DISSOLVE,
        "audio": False,
        "demos": demos,
    }
    MANIFEST.write_text(json.dumps(manifest, indent=2) + "\n")
    print(MANIFEST)


if __name__ == "__main__":
    main()
