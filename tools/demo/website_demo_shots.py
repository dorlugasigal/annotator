#!/usr/bin/env python3
"""Shot list for the website's feature demos (the product stage on the homepage).

The clips are cut from the HiDPI reel takes in build/reel-takes (real ScreenCaptureKit recordings of the
live overlay, see apps/macos/Tests/NativeAppTests/ReelRecordingTests.swift) and framed by the reel's virtual camera:

  videos/annotator-reel/.venv/bin/python videos/annotator-reel/tools/camera.py circle \
      --spec tools/demo/website_demo_shots.py --aspect 16x10 --out build/website-demos/masters
Zoom and Shapes use takes from apps/macos/Tests/NativeAppTests/WebsiteDemoRecordingTests.swift in build/website-demo-takes;
pass --takes with the folder that SOURCES names (build/website-demos/render-all.sh does this).

Every clip follows one grammar, so the stage looks the same whichever tab is open: it opens on the sample
app and the real toolbar while the tool is chosen, moves in on the action, and ends close on the result.
Actions play at about real speed; only pointer travel and waits are shortened. Camera moves are slow and
eased; nothing is drawn over the footage. Rectangles and centres are 3840x2160 capture pixels.

Helpers for review (they write only where you point them):
  videos/annotator-reel/.venv/bin/python tools/demo/website_demo_shots.py stills <shot> <aspect> <t,...> <dir>
  videos/annotator-reel/.venv/bin/python tools/demo/website_demo_shots.py summary
"""
from __future__ import annotations

import sys
from pathlib import Path

# Desktop clips are 16:10 at 1728x1080, which is H.264 level 4.2 at 60 fps and about one output pixel per
# device pixel at the stage's width on a Retina laptop. Phones get their own 4:5 framing at 1080x1350.
ASPECT_SIZE = {"16x10": (1728, 1080), "4x5": (1080, 1350)}
ZMAX = 1.15
REGION = (340, 48, 3500, 2100)  # the sample window and the toolbar below it
WINDOW = (340, 48, 3500, 1428)
DISSOLVE = 0.4  # seconds; the encoder dissolves each clip's last frames into its first for a clean loop

# The folder holding each take's latest-<take>.json and recording.
SOURCES = {"dashboard": "build/reel-takes", "smart": "build/reel-takes", "focus": "build/reel-takes",
           "zoom": "build/website-demo-takes", "library": "build/website-demo-takes"}


def clamp(aspect, pose, region=REGION):
    """Keep the view inside region where it fits; otherwise centre it on the region."""
    width, height = ASPECT_SIZE[aspect]
    z = min(pose["z"], ZMAX)
    vw, vh = width / z, height / z
    x0, y0, x1, y1 = region
    cx = min(max(pose["cx"], x0 + vw / 2), x1 - vw / 2) if vw <= x1 - x0 else (x0 + x1) / 2
    cy = min(max(pose["cy"], y0 + vh / 2), y1 - vh / 2) if vh <= y1 - y0 else (y0 + y1) / 2
    return {"cx": round(cx, 1), "cy": round(cy, 1), "z": round(z, 4)}


def at(aspect, cx, cy, z, region=REGION):
    return clamp(aspect, {"cx": cx, "cy": cy, "z": z}, region)


def fit(aspect, x0, y0, x1, y1, pad=(60, 60), region=REGION, zmax=ZMAX):
    """The pose that shows the source rectangle (x0, y0)-(x1, y1) with padding."""
    width, height = ASPECT_SIZE[aspect]
    px, py = pad
    z = min(width / (x1 - x0 + 2 * px), height / (y1 - y0 + 2 * py), zmax)
    return clamp(aspect, {"cx": (x0 + x1) / 2, "cy": (y0 + y1) / 2, "z": z}, region)


def key(t, pose, ease="inOutCubic"):
    return {"t": t, "cx": pose["cx"], "cy": pose["cy"], "z": pose["z"], "rx": 0.0, "ry": 0.0, "ease": ease}


def drift(pose, dz=1.02, dx=0.0, dy=0.0):
    return {"cx": pose["cx"] + dx, "cy": pose["cy"] + dy, "z": pose["z"] * dz}


# Each retime starts with a near-freeze of the first frame for DISSOLVE seconds. The encoder shows those
# frames only inside the dissolve at the end of the loop, so the action starts from a still, clean state.


def circle(a):
    """Choose Ellipse, then circle the Activation number with a rough teal ellipse."""
    retime = [(1.00, 0.0), (1.03, 0.40), (1.23, 0.62), (1.685, 1.05), (2.28, 1.53), (3.33, 2.63), (3.45, 2.98),
              (3.55, 4.3)]
    if a == "16x10":
        wide = at(a, 1700, 960, 0.80)
        close = fit(a, 800, 180, 2200, 560, (100, 200))  # the page title, Signups and Activation
    else:
        wide = at(a, 1560, 950, 1.0)
        close = at(a, 1660, 600, 1.15)  # centred on the ellipse rather than the whole card
    camera = [
        key(0.0, wide),
        key(1.1, drift(wide, 1.01), "inOut"),
        key(1.75, close),
        key(2.63, drift(close, 1.03), "inOut"),
        key(4.3, clamp(a, drift(close, 1.06)), "inOut"),
    ]
    return {"take": "dashboard", "retime": retime, "camera": camera, "shutter": 0.5}


def arrow(a):
    """Choose Arrow and Blue, click three points, press Return, then drag the middle point to bend it."""
    retime = [(3.40, 0.0), (3.43, 0.40), (3.85, 0.80), (4.63, 1.46), (5.30, 1.94), (5.95, 2.44), (6.63, 2.99),
              (7.14, 3.44), (7.71, 3.92), (8.00, 4.14), (8.57, 4.56), (9.34, 5.32), (9.50, 5.64), (9.62, 6.5)]
    if a == "16x10":
        picker = at(a, 1550, 1000, 0.78)
        bend = fit(a, 1380, 330, 2130, 1210, (300, 110))
    else:
        picker = at(a, 1150, 1090, 1.0)
        bend = fit(a, 1400, 330, 2120, 1200, (80, 120))
    camera = [
        key(0.0, picker),
        key(2.44, drift(picker, 1.015), "inOut"),
        key(3.15, bend),
        key(4.5, drift(bend, 1.02), "inOut"),
        key(5.4, drift(bend, 1.05), "inOut"),
        key(6.5, clamp(a, drift(bend, 1.07)), "inOut"),
    ]
    return {"take": "dashboard", "retime": retime, "camera": camera, "shutter": 0.5}


def highlight(a):
    """Choose Highlighter, then drag across the first note."""
    retime = [(9.60, 0.0), (9.63, 0.40), (10.44, 1.08), (11.08, 1.58), (12.04, 2.58), (12.25, 3.01), (12.62, 4.05)]
    if a == "16x10":
        wide = at(a, 2425, 1070, 0.811)
        close = at(a, 2748, 700, 1.15, WINDOW)
        camera = [key(0.0, wide), key(1.1, drift(wide, 1.01), "inOut"), key(1.75, close),
                  key(2.58, drift(close, 1.0, dx=-10)), key(4.05, clamp(a, drift(close, 1.0, dx=-20)), "inOut")]
    else:
        start = at(a, 1900, 1108, 1.0)
        tool = at(a, 2200, 1108, 1.0)
        close = at(a, 2904, 700, 1.15, WINDOW)
        camera = [key(0.0, start), key(0.5, start), key(1.08, tool, "inOut"), key(1.7, close),
                  key(4.05, drift(close, 1.0, dx=-10), "inOut")]
    return {"take": "dashboard", "retime": retime, "camera": camera, "shutter": 0.5}


def smart(a):
    """Smart Draw: a rough box and a rough arrow snap into clean shapes, then a typed label."""
    retime = [(0.20, 0.0), (0.23, 0.40), (0.32, 0.55), (0.70, 0.93), (1.24, 1.33), (3.03, 2.93), (3.20, 3.23),
              (3.59, 3.51), (4.37, 4.16), (4.55, 4.46), (5.07, 4.86), (5.64, 5.31), (6.49, 6.16), (6.70, 6.51),
              (7.30, 7.45)]
    if a == "16x10":
        wide = at(a, 1925, 915, 0.755)
        work = at(a, 2040, 1225, 1.0)
        close = fit(a, 1540, 860, 2560, 1090, (230, 240))
    else:
        wide = at(a, 1715, 915, 0.93)
        work = at(a, 2050, 1150, 1.0)
        close = fit(a, 1540, 860, 2560, 1090, (20, 300))
    camera = [
        key(0.0, wide),
        key(0.75, drift(wide, 1.01), "inOut"),
        key(1.5, work),
        key(4.95, drift(work, 1.02, dy=-10), "inOut"),
        key(6.2, close),
        key(7.45, clamp(a, drift(close, 1.03)), "inOut"),
    ]
    return {"take": "smart", "retime": retime, "camera": camera, "shutter": 0.5}


def spotlight(a):
    """Turn on the cursor halo and Spotlight from the tool library, then click three pictures."""
    retime = [(0.20, 0.0), (0.23, 0.40), (0.32, 0.55), (0.73, 0.93), (1.10, 1.23), (1.45, 1.51), (2.05, 1.99),
              (2.63, 2.47), (3.38, 3.11), (4.61, 4.16), (5.74, 5.13), (6.25, 5.95)]
    if a == "16x10":
        library = at(a, 1580, 980, 0.818)
        board = fit(a, 380, 250, 2700, 1390, (40, 60))
        close = at(a, 2150, 617, 0.95, WINDOW)
        camera = [key(0.0, library), key(2.45, drift(library, 1.015), "inOut"), key(3.1, board),
                  key(4.3, drift(board, 1.02, dx=40), "inOut"), key(5.2, close),
                  key(5.95, clamp(a, drift(close, 1.03, dx=30, dy=-10)), "inOut")]
    else:
        library = at(a, 1590, 1000, 0.83)
        img0 = at(a, 1000, 760, 0.95)
        img1 = at(a, 1534, 760, 0.95)
        hero = at(a, 2230, 700, 1.0)
        camera = [key(0.0, library), key(2.45, drift(library, 1.01), "inOut"), key(3.05, img0),
                  key(4.1, img1), key(5.1, hero), key(5.95, clamp(a, drift(hero, 1.03)), "inOut")]
    return {"take": "focus", "retime": retime, "camera": camera, "shutter": 0.5}


def blur(a):
    """Choose Effects, then drag a blur region over the busy picture."""
    retime = [(6.90, 0.0), (6.93, 0.40), (7.36, 0.85), (8.02, 1.42), (9.64, 2.82), (9.95, 3.22), (10.19, 4.15)]
    if a == "16x10":
        wide = at(a, 1240, 1210, 0.96)
        close = at(a, 1112, 1107, 1.12)
        camera = [key(0.0, wide), key(0.9, drift(wide, 1.005), "inOut"), key(1.5, close),
                  key(2.82, drift(close, 1.015), "inOut"), key(4.15, clamp(a, drift(close, 1.03)), "inOut")]
    else:
        tool = at(a, 1500, 1300, 0.85)
        close = at(a, 831, 1107, 1.1)
        camera = [key(0.0, tool), key(0.9, drift(tool, 1.005), "inOut"), key(1.5, close),
                  key(4.15, clamp(a, drift(close, 1.03)), "inOut")]
    return {"take": "focus", "retime": retime, "camera": camera, "shutter": 0.5}


def shapes(a):
    """Open the tool library, scroll the shapes, look at Focus and Extras, then draw a Database shape."""
    retime = [(0.45, 0.0), (0.48, 0.40), (0.806, 0.70), (1.545, 1.30), (2.51, 2.10), (3.15, 2.62), (4.29, 3.45),
              (5.37, 4.25), (6.26, 4.95), (7.07, 5.55), (7.55, 5.90), (8.30, 6.60), (8.55, 6.85), (9.38, 7.9)]
    if a == "16x10":
        wide = at(a, 1920, 1000, 0.62)
        library = fit(a, 990, 372, 1866, 1436, (330, 30))
        shape = at(a, 2250, 800, 1.0, WINDOW)
    else:
        wide = at(a, 1650, 1000, 0.62)
        library = fit(a, 990, 372, 1866, 1436, (40, 190))
        shape = at(a, 2280, 820, 1.0, WINDOW)
    camera = [
        key(0.0, wide),
        key(0.4, wide),
        key(1.1, library),
        key(5.5, drift(library, 1.02), "inOut"),
        key(6.25, shape),
        key(7.9, clamp(a, drift(shape, 1.03)), "inOut"),
    ]
    return {"take": "library", "retime": retime, "camera": camera, "shutter": 0.5}


def zoom(a):
    """Choose Screen zoom in the tool library; the zoom follows the pointer, gets deeper with +, then stops.

    Screen zoom opens with two frames of the app's own black startup cover (1.608 and 1.625 s in the take).
    The time map steps over them in one frame; `skips` records that for the manifest.
    """
    retime = [(0.40, 0.0), (0.43, 0.40), (0.76, 0.73), (1.47, 1.375), (1.59, 1.495), (1.66, 1.512),
              (3.16, 2.512), (3.60, 2.805), (5.47, 4.052), (5.85, 4.344), (6.41, 4.792), (7.33, 5.528),
              (7.55, 5.728), (9.70, 7.161), (10.85, 8.119), (12.40, 9.152), (12.75, 9.502), (13.00, 10.05)]
    if a == "16x10":
        wide = at(a, 1920, 854, 0.527)
        library = at(a, 1650, 1030, 0.74)
        screen = at(a, 1920, 1080, 0.5)
        controls = at(a, 1560, 1460, 0.92)
        camera = [
            key(0.0, wide), key(0.45, wide), key(1.0, library), key(1.45, drift(library, 1.01), "inOut"),
            key(2.0, screen), key(4.05, drift(screen, 1.02), "inOut"), key(4.5, controls),
            key(5.55, drift(controls, 1.01), "inOut"), key(6.15, screen), key(9.5, drift(screen, 1.02), "inOut"),
            key(10.05, screen, "inOut"),
        ]
    else:
        wide = at(a, 1700, 1000, 0.62)
        library = at(a, 1640, 1100, 0.74)
        follow_kpi = at(a, 1640, 900, 0.62)
        controls = at(a, 1560, 1400, 0.8)
        follow_note = at(a, 2800, 850, 0.62)
        camera = [
            key(0.0, wide), key(0.45, wide), key(1.0, library), key(1.45, drift(library, 1.01), "inOut"),
            key(2.0, follow_kpi), key(4.05, drift(follow_kpi, 1.02), "inOut"), key(4.5, controls),
            key(5.55, drift(controls, 1.01), "inOut"), key(6.3, follow_note), key(8.2, drift(follow_note, 1.02)),
            key(9.3, wide), key(10.05, wide, "inOut"),
        ]
    return {"take": "zoom", "retime": retime, "camera": camera, "shutter": 0.5,
            "skips": [{"source": [1.59, 1.66], "reason": "the black cover that Screen zoom shows while it starts"}]}

SHOTS = {"circle": circle, "arrow": arrow, "highlight": highlight, "smart": smart, "shapes": shapes,
         "spotlight": spotlight, "zoom": zoom, "blur": blur}


def build(name: str, aspect: str) -> dict:
    if name not in SHOTS:
        raise SystemExit(f"Unknown shot {name}; known: {', '.join(SHOTS)}")
    spec = SHOTS[name](aspect)
    retime = spec["retime"]
    if any(b[0] <= a[0] or b[1] <= a[1] for a, b in zip(retime, retime[1:])):
        raise SystemExit(f"{name}: retime anchors must increase")
    if spec["camera"][-1]["t"] > retime[-1][1] + 1e-6:
        raise SystemExit(f"{name}: the camera outlasts the clip")
    return spec


def _camera_module():
    repo = Path(__file__).resolve().parents[2]
    sys.path.insert(0, str(repo / "videos/annotator-reel/tools"))
    import camera  # noqa: E402  (the reel's renderer, used read-only)
    return camera, repo


def stills(shot: str, aspect: str, times: str, out: str) -> None:
    import json
    import subprocess
    import cv2
    camera, repo = _camera_module()
    spec = build(shot, aspect)
    meta = json.loads((repo / SOURCES[spec["take"]] / f"latest-{spec['take']}.json").read_text())
    tmap, cam = camera.TimeMap(spec["retime"]), camera.Camera(spec["camera"])
    width, height = ASPECT_SIZE[aspect]
    # Captures only add frames when the screen changes, so the file can end before the take's metadata does.
    probe = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0",
                            meta["path"]], capture_output=True, text=True, check=True)
    last = float(probe.stdout) - 0.35
    Path(out).mkdir(parents=True, exist_ok=True)
    for value in times.split(","):
        t = float(value)
        start = min(last, max(0.0, tmap.src(max(0.0, t - 0.05)) - 0.25))
        src = camera.Source(Path(meta["path"]), meta["width"], meta["height"], start=start, limit=90)
        frame = camera.render_frame(src, cam, tmap, t, width, height, spec.get("shutter", 0.5), 32)
        src.close()
        path = Path(out) / f"{shot}-{aspect}-{t:05.2f}.png"
        cv2.imwrite(str(path), frame)
        print(path)


def summary() -> None:
    for name in SHOTS:
        for aspect in ASPECT_SIZE:
            spec = build(name, aspect)
            (s0, c0), (s1, c1) = spec["retime"][0], spec["retime"][-1]
            print(f"{name:10s} {aspect:6s} take={spec['take']:9s} source {s0:5.2f}-{s1:5.2f}s "
                  f"clip {c1 - c0:4.2f}s loop {c1 - c0 - DISSOLVE:4.2f}s average speed {(s1 - s0) / (c1 - c0):.2f}x")


if __name__ == "__main__":
    if len(sys.argv) >= 2 and sys.argv[1] == "stills" and len(sys.argv) == 6:
        stills(*sys.argv[2:6])
    elif len(sys.argv) == 2 and sys.argv[1] == "summary":
        summary()
    else:
        raise SystemExit(__doc__)
