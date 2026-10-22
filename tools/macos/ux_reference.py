#!/usr/bin/env python3
"""Pack the macOS UX reference for the Windows edition (specification 013, WIN-PAR-06).

The native test `UXReferenceCaptureTests` renders every Annotator surface offscreen into
build/ux-reference/raw. This script turns that output into reference/ux-reference/macos:
optimized PNGs, JSON sidecars, contact sheets and short videos for motion, a manifest
with SHA-256 hashes and an INDEX.md gallery. `UXLiveRecordingTests` records the live
desktop into build/ux-reference/raw-live; `pack-live` turns those recordings into
reference/ux-reference/macos-live.

    python3 tools/macos/ux_reference.py pack       # raw output -> reference/ux-reference/macos
    python3 tools/macos/ux_reference.py pack-live  # live recordings -> reference/ux-reference/macos-live
    python3 tools/macos/ux_reference.py check      # verify both folders against their manifests

`pack` needs Pillow and ffmpeg. `check` needs only the standard library.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
RAW = ROOT / "build/ux-reference/raw"
OUT = ROOT / "reference/ux-reference/macos"
RAW_LIVE = ROOT / "build/ux-reference/raw-live"
OUT_LIVE = ROOT / "reference/ux-reference/macos-live"
LIVE_GROUPS = [
    ("show-hide", "Show and hide", "Each workspace show and hide style with seven drawings and the toolbar, and a reversal mid-way."),
    ("zoom", "Live zoom", "Screen zoom ramping in, following the pointer, zooming further and ramping out; the pointer lens."),
    ("effects", "Live effects", "Blur, pixelate and distort over the owned sample desktop, a strength change, an existing solid cover and Use apps."),
    ("sharing", "Region sharing", "The desktop while a region is shared, and the output window people share in a call."),
]

SURFACES = [
    ("toolbar", "Toolbar", "The floating toolbar in every layout, size, mode and tool state. Compare with WIN-UI-02."),
    ("properties", "Full properties", "The properties panel for each tool, 648 points wide unless noted."),
    ("pickers", "Pickers and zoom controls", "Color popover, arrowhead choices and zoom controls."),
    ("menu", "Menu-bar menu and icon", "The menu-bar popover (the Windows tray flyout) and the menu-bar icon. Compare with WIN-UI-01."),
    ("settings", "Settings", "Every Settings tab at its default size, the same tab stretched to show all of its content, and the launch-at-login states."),
    ("tour", "Introduction tour", "The four tour pages, setup permission states and illustration motion."),
    ("tools-window", "Tools window", "Each section of the Tools window and a wider window."),
    ("region-sharing", "Region sharing", "The Share a region window, outlines and the opaque output while it starts."),
    ("break-timer", "Break timer", "The full-screen break countdown in each state, the five color sets and the plain background (specification 014)."),
    ("dialogs", "Dialogs and small parts", "Report a problem, Share feedback, What's changed, coffee reminder, keycaps, error window and the on-screen tip."),
    ("canvas", "Canvas chrome", "Selection handles, marquee, arrow attachment, eraser preview, text editing and boards on a fictional backdrop."),
    ("pointer", "Pointer effects", "Spotlight, cursor halo shapes and click effects."),
    ("cursors", "Cursors", "Tool cursors with their hot spots, at three stroke widths."),
]


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def run(*command: str) -> str:
    try:
        return subprocess.run(command, cwd=ROOT, check=True, capture_output=True, text=True).stdout.strip()
    except (OSError, subprocess.CalledProcessError):
        return ""


def write_json(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, indent=1, sort_keys=True, ensure_ascii=False) + "\n", encoding="utf-8")


def background(appearance: str, values: dict) -> tuple[int, int, int]:
    color = values.get("colors", {}).get(appearance, {}).get("windowBackgroundColor", "#ECECECFF")
    return tuple(int(color[i:i + 2], 16) for i in (1, 3, 5))


def save_png(image, path: Path, icc: bytes | None) -> None:
    options = {"optimize": True}
    if icc:
        options["icc_profile"] = icc
    image.save(path, "PNG", **options)


def pack_sequence(folder: Path, destination: Path, values: dict, Image, ImageDraw, ImageFont) -> dict:
    meta = json.loads((folder / "sequence.json").read_text())
    frames = sorted(folder.glob("frame-*.png"))
    times = meta["times"]
    if len(frames) != len(times) or len(frames) < 2:
        raise SystemExit(f"{folder}: frame count and times do not match.")
    fps = round(1 / (times[1] - times[0]))
    fill = background(meta["appearance"], values)
    stem = f"{meta['state']}-{meta['appearance']}"
    destination.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory() as temporary:
        flattened = Path(temporary)
        for index, frame in enumerate(frames):
            with Image.open(frame) as source:
                canvas = Image.new("RGB", source.size, fill)
                canvas.paste(source.convert("RGBA"), mask=source.convert("RGBA"))
                canvas.save(flattened / f"frame-{index:04d}.png")
        video = destination / f"{stem}.mp4"
        subprocess.run([
            "ffmpeg", "-y", "-loglevel", "error", "-framerate", str(fps), "-i", str(flattened / "frame-%04d.png"),
            "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18",
            "-threads", "1", "-map_metadata", "-1", "-fflags", "+bitexact", "-flags:v", "+bitexact",
            "-movflags", "+faststart", str(video)], check=True)

        count = min(12, len(frames))
        picks = sorted({round(i * (len(frames) - 1) / (count - 1)) for i in range(count)})
        with Image.open(flattened / "frame-0000.png") as first:
            cell = (first.width // 2, first.height // 2)
        columns = 4 if cell[0] < 400 else 2
        rows = -(-len(picks) // columns)
        label_height, gap = 26, 12
        sheet = Image.new("RGB", (columns * (cell[0] + gap) + gap, rows * (cell[1] + label_height + gap) + gap), fill)
        draw = ImageDraw.Draw(sheet)
        try:
            font = ImageFont.truetype("/System/Library/Fonts/SFNSMono.ttf", 15)
        except OSError:
            font = ImageFont.load_default()
        ink = (40, 40, 40) if meta["appearance"] == "light" else (220, 220, 220)
        for position, index in enumerate(picks):
            column, row = position % columns, position // columns
            x = gap + column * (cell[0] + gap)
            y = gap + row * (cell[1] + label_height + gap)
            with Image.open(flattened / f"frame-{index:04d}.png") as frame:
                sheet.paste(frame.resize(cell, Image.LANCZOS), (x, y))
            draw.text((x, y + cell[1] + 5), f"{times[index] * 1000:.0f} ms", fill=ink, font=font)
        save_png(sheet, destination / f"{stem}.png", None)

    meta.update({"fps": fps, "frames": len(frames), "video": f"{stem}.mp4", "contactSheet": f"{stem}.png",
                 "contactSheetFrames": [times[i] for i in picks],
                 "contactSheetScale": 1})
    write_json(destination / f"{stem}.json", meta)
    return meta


def pack(raw: Path, out: Path) -> None:
    try:
        from PIL import Image, ImageDraw, ImageFont
    except ImportError:
        raise SystemExit("pack needs Pillow: python3 -m pip install pillow")
    if shutil.which("ffmpeg") is None:
        raise SystemExit("pack needs ffmpeg on PATH.")
    if not raw.is_dir():
        raise SystemExit(f"No capture output at {raw}. Run UXReferenceCaptureTests first; see reference/ux-reference/README.md.")
    values = json.loads((raw / "design-values.json").read_text())
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)

    entries, motion = [], []
    for surface, _, _ in SURFACES:
        source = raw / surface
        if not source.is_dir():
            raise SystemExit(f"Missing surface {surface} in {raw}.")
        target = out / surface
        target.mkdir()
        for sidecar in sorted(source.glob("*.json")):
            if sidecar.name == "cursors.json":
                write_json(target / sidecar.name, json.loads(sidecar.read_text()))
                continue
            meta = json.loads(sidecar.read_text())
            png = sidecar.with_suffix(".png")
            with Image.open(png) as image:
                image.load()
                save_png(image, target / png.name, image.info.get("icc_profile"))
                meta["pixels"] = [image.width, image.height]
            meta["image"] = f"{surface}/{png.name}"
            write_json(target / sidecar.name, meta)
            entries.append(meta)
        for folder in sorted(path for path in source.iterdir() if path.is_dir()):
            meta = pack_sequence(folder, target / "motion", values, Image, ImageDraw, ImageFont)
            meta["video"] = f"{surface}/motion/{meta['video']}"
            meta["contactSheet"] = f"{surface}/motion/{meta['contactSheet']}"
            motion.append(meta)
    write_json(out / "design-values.json", values)
    write_index(out, entries, motion)

    files = sorted(path for path in out.rglob("*") if path.is_file() and path.name != "manifest.json")
    head = run("git", "rev-parse", "HEAD")
    app_changes = run("git", "status", "--porcelain", "--", "apps/macos/Annotator", "apps/macos/Sources", "apps/macos/Packages")
    manifest = {
        "about": "macOS UX reference for the Windows edition. Generated by tools/macos/ux_reference.py; do not edit by hand.",
        "generated": datetime.now(timezone.utc).strftime("%Y-%m-%d"),
        "source": {
            "commit": head, "appSourcesClean": app_changes == "",
            "macOS": run("sw_vers", "-productVersion"),
            "xcode": run("xcodebuild", "-version").splitlines()[0] if run("xcodebuild", "-version") else "",
            "renderScale": 2, "accentColor": values["colors"]["light"].get("controlAccentColor"),
            "method": "Offscreen AppKit and SwiftUI rendering into sRGB bitmaps at 2x, independent of the display.",
        },
        "counts": {"images": len(entries), "motion": len(motion), "files": len(files)},
        "files": {str(path.relative_to(out)): sha256(path) for path in files},
    }
    write_json(out / "manifest.json", manifest)
    total = sum(path.stat().st_size for path in out.rglob("*") if path.is_file())
    print(f"Packed {len(entries)} images and {len(motion)} motions into {out.relative_to(ROOT)} ({total / 1e6:.1f} MB).")


def write_index(out: Path, entries: list[dict], motion: list[dict]) -> None:
    lines = ["# macOS UX reference gallery", "",
             "Generated by `tools/macos/ux_reference.py`. Read [the README](../README.md) first. Images are 2x;",
             "sizes below are in points (one point is one Windows DIP). Each image has a JSON file with the same",
             "name holding its accessibility tree.", ""]
    for surface, title, description in SURFACES:
        lines += [f"## {title}", "", description, ""]
        states: dict[str, dict[str, dict]] = {}
        for entry in entries:
            if entry["surface"] == surface:
                states.setdefault(entry["state"], {})[entry["appearance"]] = entry
        if states:
            lines += ["| State | Light | Dark |", "| --- | --- | --- |"]
            for state in sorted(states):
                pair = states[state]
                any_entry = pair.get("light") or next(iter(pair.values()))
                width = 420 if any_entry["size"][0] > 200 else min(200, int(any_entry["size"][0] * 2))
                cells = []
                for appearance in ("light", "dark"):
                    entry = pair.get(appearance)
                    cells.append(f'<img src="{entry["image"]}" width="{width}">' if entry else "")
                size = f'{any_entry["size"][0]:g} x {any_entry["size"][1]:g} pt'
                notes = any_entry.get("notes", "").replace("|", "/")
                lines.append(f"| `{state}`<br>{size}<br>{notes} | {cells[0]} | {cells[1]} |")
            lines.append("")
        clips = [item for item in motion if item["surface"] == surface]
        if clips:
            lines += ["Motion (contact sheet; the video plays at real speed):", ""]
            for item in sorted(clips, key=lambda value: (value["state"], value["appearance"])):
                lines += [f'- `{item["state"]}` ({item["appearance"]}, {item["frames"]} frames at {item["fps"]} fps): '
                          f'[video]({item["video"]}). {item["notes"]}', "",
                          f'  <img src="{item["contactSheet"]}" width="640">', ""]
    (out / "INDEX.md").write_text("\n".join(lines).rstrip() + "\n", encoding="utf-8")


def extract_frame(video: Path, time: float, target: Path) -> None:
    target.unlink(missing_ok=True)
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-ss", f"{time:.4f}", "-i", str(video), "-frames:v", "1",
                    "-fflags", "+bitexact", str(target)], check=True)
    if not target.is_file():
        raise SystemExit(f"Could not read a frame at {time:.3f} s from {video}.")


def pack_live(raw: Path, out: Path) -> None:
    try:
        from PIL import Image, ImageDraw, ImageFont
    except ImportError:
        raise SystemExit("pack-live needs Pillow: python3 -m pip install pillow")
    if shutil.which("ffmpeg") is None:
        raise SystemExit("pack-live needs ffmpeg on PATH.")
    takes = sorted(path for path in raw.iterdir() if (path / "meta.json").is_file()) if raw.is_dir() else []
    if not takes:
        raise SystemExit(f"No live recordings in {raw}. Run UXLiveRecordingTests first; see reference/ux-reference/README.md.")
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)
    try:
        font = ImageFont.truetype("/System/Library/Fonts/SFNSMono.ttf", 14)
    except OSError:
        font = ImageFont.load_default()
    entries = []
    with tempfile.TemporaryDirectory() as temporary:
        scratch = Path(temporary)
        for folder in takes:
            meta = json.loads((folder / "meta.json").read_text())
            name = meta["take"]
            video = out / f"{name}.mp4"
            subprocess.run([
                "ffmpeg", "-y", "-loglevel", "error", "-i", str(folder / meta["video"]),
                "-vf", "fps=60,pad=ceil(iw/2)*2:ceil(ih/2)*2", "-c:v", "libx264", "-preset", "slow", "-pix_fmt", "yuv420p",
                "-crf", "20", "-threads", "1", "-an", "-map_metadata", "-1", "-fflags", "+bitexact", "-flags:v", "+bitexact",
                "-movflags", "+faststart", str(video)], check=True)
            duration = meta["duration"]
            last = max(0.0, (round(duration * 60) - 2) / 60)
            picks = [("start", 0.0)]
            for segment in meta["segments"]:
                span = segment["end"] - segment["start"]
                for share in (0, 0.25, 0.5, 0.75, 1):
                    time = min(last, segment["start"] + span * share)
                    picks.append((f"{segment['label']} +{span * share * 1000:.0f} ms", time))
            picks.append(("end", last))
            frames = []
            for index, (label, time) in enumerate(picks):
                time = round(time * 60) / 60
                target = scratch / f"{name}-{index:03d}.png"
                extract_frame(video, time, target)
                frames.append((label, time, target))
            with Image.open(frames[0][2]) as first:
                width, height = first.size
            divisor = 4 if width >= 1000 else 2
            cell = (width // divisor, height // divisor)
            columns = 4
            rows = -(-len(frames) // columns)
            label_height, gap = 22, 10
            sheet = Image.new("RGB", (columns * (cell[0] + gap) + gap, rows * (cell[1] + label_height + gap) + gap), (236, 236, 236))
            draw = ImageDraw.Draw(sheet)
            for position, (label, time, target) in enumerate(frames):
                x = gap + (position % columns) * (cell[0] + gap)
                y = gap + (position // columns) * (cell[1] + label_height + gap)
                with Image.open(target) as frame:
                    sheet.paste(frame.convert("RGB").resize(cell, Image.LANCZOS), (x, y))
                draw.text((x, y + cell[1] + 4), f"{time:6.3f} s  {label}", fill=(40, 40, 40), font=font)
            save_png(sheet, out / f"{name}.png", None)
            meta.update({
                "video": f"{name}.mp4", "contactSheet": f"{name}.png",
                "contactSheetFrames": [{"label": label, "time": time} for label, time, _ in frames],
                "packedFps": 60,
                "packing": "Re-encoded at a constant 60 fps (H.264, CRF 20). The recording itself has a variable frame rate: ScreenCaptureKit delivers a frame when the screen changes, so times are real.",
            })
            write_json(out / f"{name}.json", meta)
            entries.append(meta)
    write_live_index(out, entries)
    files = sorted(path for path in out.rglob("*") if path.is_file() and path.name != "manifest.json")
    manifest = {
        "about": "Live macOS UX recordings for the Windows edition. Generated by tools/macos/ux_reference.py pack-live; do not edit by hand.",
        "generated": datetime.now(timezone.utc).strftime("%Y-%m-%d"),
        "source": {
            "commit": run("git", "rev-parse", "HEAD"),
            "appSourcesClean": run("git", "status", "--porcelain", "--", "apps/macos/Annotator", "apps/macos/Sources") == "",
            "macOS": run("sw_vers", "-productVersion"),
            "xcode": run("xcodebuild", "-version").splitlines()[0] if run("xcodebuild", "-version") else "",
            "method": "UXLiveRecordingTests: real-time ScreenCaptureKit recordings of the test host's own windows on the live desktop, with an owned backdrop, an owned sample window and a scripted pointer.",
        },
        "counts": {"recordings": len(entries), "files": len(files)},
        "files": {str(path.relative_to(out)): sha256(path) for path in files},
    }
    write_json(out / "manifest.json", manifest)
    total = sum(path.stat().st_size for path in out.rglob("*") if path.is_file())
    shown = out.relative_to(ROOT) if out.is_relative_to(ROOT) else out
    print(f"Packed {len(entries)} live recordings into {shown} ({total / 1e6:.1f} MB).")


def write_live_index(out: Path, entries: list[dict]) -> None:
    lines = ["# Live macOS recordings", "",
             "Generated by `tools/macos/ux_reference.py pack-live`. Read [the README](../README.md) first. These are real-time",
             "recordings of the live desktop at the display's own scale (1x here), played back at real speed. Each video has",
             "a JSON file with its events and timed segments, and a contact sheet of frames at each segment's start, quarters",
             "and end.", ""]
    for group, title, description in LIVE_GROUPS:
        lines += [f"## {title}", "", description, ""]
        for entry in sorted((item for item in entries if item["take"].startswith(group)), key=lambda item: item["take"]):
            pixels = entry["pixels"]
            segments = ", ".join(f"{item['label']} {item['start']:.2f}-{item['end']:.2f} s" for item in entry["segments"])
            lines += [f"### `{entry['take']}`", "",
                      f"{entry['notes']} {entry['duration']:.2f} s, {pixels[0]} x {pixels[1]} pixels at {entry['scale']:g}x. "
                      f"[Video]({entry['video']}) · [details]({entry['take']}.json)", "",
                      f"Segments: {segments}." if segments else "", "",
                      f'<img src="{entry["contactSheet"]}" width="960">', ""]
    (out / "INDEX.md").write_text("\n".join(lines).rstrip() + "\n", encoding="utf-8")


def check(out: Path) -> int:
    manifest_path = out / "manifest.json"
    if not manifest_path.is_file():
        print(f"No manifest at {manifest_path}.")
        return 1
    manifest = json.loads(manifest_path.read_text())
    problems = []
    for name, digest in manifest["files"].items():
        path = out / name
        if not path.is_file():
            problems.append(f"missing {name}")
        elif sha256(path) != digest:
            problems.append(f"changed {name}")
    listed = set(manifest["files"]) | {"manifest.json"}
    for path in out.rglob("*"):
        if path.is_file() and str(path.relative_to(out)).replace(os.sep, "/") not in listed:
            problems.append(f"unlisted {path.relative_to(out)}")
    for problem in problems:
        print(problem)
    print(f"{len(manifest['files'])} files checked, {len(problems)} problems.")
    return 1 if problems else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=["pack", "pack-live", "check"])
    parser.add_argument("--raw", type=Path, default=None)
    parser.add_argument("--out", type=Path, default=None)
    arguments = parser.parse_args()
    if arguments.command == "pack":
        out = (arguments.out or OUT).resolve()
        pack((arguments.raw or RAW).resolve(), out)
        return check(out)
    if arguments.command == "pack-live":
        out = (arguments.out or OUT_LIVE).resolve()
        pack_live((arguments.raw or RAW_LIVE).resolve(), out)
        return check(out)
    if arguments.out:
        return check(arguments.out.resolve())
    return max(check(folder) for folder in (OUT, OUT_LIVE) if folder.exists())


if __name__ == "__main__":
    sys.exit(main())
