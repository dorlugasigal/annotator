#!/usr/bin/env python3
"""Encode fresh native product takes without changing their timing or drawing."""
import argparse
import hashlib
import json
import math
from fractions import Fraction
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[2]

# Settings recordings show a whole window at 1x, so the website frames only the
# controls each take uses. Coordinates are source pixels; a shot eases from the
# previous position to its own over `move` seconds, starting at `at`.
FOCUS = {
    "pointer": {"width": 784, "height": 564, "poster": 5.4, "shots": [{"at": 0, "x": 2168, "y": 440}]},
    # The toolbar strip, More tools down to Presenting, and the confirmation message, in one still frame.
    "shortcuts": {"width": 784, "height": 564, "poster": 6.6, "shots": [{"at": 0, "x": 2168, "y": 448}]},
}


def camera_position(shots, seconds, axis):
    value = shots[0][axis]
    for previous, shot in zip(shots, shots[1:]):
        if seconds <= shot["at"]:
            break
        progress = min(1, (seconds - shot["at"]) / shot["move"])
        value = previous[axis] + (shot[axis] - previous[axis]) * progress * progress * (3 - 2 * progress)
    return value


def camera_expression(shots, axis):
    expression = str(shots[-1][axis])
    for previous, shot in reversed(list(zip(shots, shots[1:]))):
        start, end, delta = shot["at"], shot["at"] + shot["move"], shot[axis] - previous[axis]
        eased = f"{previous[axis]}+{delta}*pow((t-{start})/{shot['move']},2)*(3-2*(t-{start})/{shot['move']})"
        expression = f"if(lt(t,{start}),{previous[axis]},if(lt(t,{end}),{eased},{expression}))"
    return expression


def focus_window(metadata, focus):
    frame = metadata["contentRect"]
    width, height, shots = focus["width"], focus["height"], focus["shots"]
    if shots[0]["at"] != 0 or any(b["at"] < a["at"] + a.get("move", 0) for a, b in zip(shots, shots[1:])):
        raise ValueError("Focus shots must start at zero and not overlap.")
    for shot in shots:
        if (shot["x"] % 2 or shot["y"] % 2 or shot["x"] < frame["x"] or shot["y"] < frame["y"] - 2 or
                shot["x"] + width > frame["x"] + frame["width"] or shot["y"] + height > frame["y"] + frame["height"]):
            raise ValueError("A focus shot leaves the recorded window.")
    for action in metadata["actions"]:
        rect = action.get("rect")
        if not rect:
            continue
        x = camera_position(shots, action["time"], "x")
        y = camera_position(shots, action["time"], "y")
        if not (x <= rect["x"] and y <= rect["y"] and rect["x"] + rect["width"] <= x + width
                and rect["y"] + rect["height"] <= y + height):
            raise ValueError(f"The {action['control']} control is outside the focus frame when used.")
    moving = len(shots) > 1
    crop = (f"crop=w={width}:h={height}:x='{camera_expression(shots, 'x')}':y='{camera_expression(shots, 'y')}'"
            if moving else f"crop={width}:{height}:{shots[0]['x']}:{shots[0]['y']}")
    return width, height, crop


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def encode(metadata_path, output, prefix="fresh"):
    metadata = json.loads(metadata_path.read_text())
    source = Path(metadata["path"])
    take = metadata["take"]
    if take not in ("draw", "arrow", "blur", "style", "tools", "pointer", "shortcuts", "region") or metadata["imageWorkspace"] is not False:
        raise ValueError("Only the fresh live-desktop take types are supported.")
    if metadata.get("toolbarAppearance") != "dark":
        raise ValueError("Use the approved dark-toolbar takes.")
    frame = metadata["contentRect"]
    x, y = int(frame["x"] // 2 * 2), int(frame["y"] // 2 * 2)
    width = math.ceil((frame["x"] + frame["width"] - x) / 2) * 2
    # Keep native browser playback controls below the recorded toolbar.
    clearance = 16 if take == "region" else 64
    height = math.ceil((frame["y"] + frame["height"] - y + clearance) / 2) * 2
    if x < 0 or y < 0 or x + width > metadata["width"] or y + height > metadata["height"]:
        raise ValueError("The take frame is outside its captured source.")
    for action in metadata["actions"]:
        rect = action.get("rect")
        if rect and not (x <= rect["x"] and y <= rect["y"] and
                         rect["x"] + rect["width"] <= x + width and
                         rect["y"] + rect["height"] <= y + height):
            raise ValueError(f"The {action['control']} control is cropped.")
        if action.get("control") in ("Red", "Coral"):
            raise ValueError("The owner rejected red ink for these takes.")
    if prefix == "showcase" and take in ("draw", "arrow", "blur", "style", "tools"):
        samples = metadata.get("toolbarSamples", [])
        if not metadata.get("toolbarAnchorStable") or not samples:
            raise ValueError("Showcase drawing takes need measured stable-toolbar evidence.")
        if metadata.get("toolbarPropertiesMode") != "full":
            raise ValueError("Keep the native tool preferences expanded in the drawing takes.")
        reference = samples[0]["rect"]
        for sample in samples:
            if any(abs(sample["rect"][key] - reference[key]) > 2 for key in ("x", "y", "width")):
                raise ValueError("The native tool rail moved during recording.")
            rect = sample["rect"]
            if not (x <= rect["x"] and y <= rect["y"] and rect["x"]+rect["width"] <= x+width
                    and rect["y"]+rect["height"] <= y+height):
                raise ValueError("The native toolbar leaves the captured frame.")
            if frame["y"] + frame["height"] - rect["y"] - rect["height"] < 80:
                raise ValueError("The native toolbar is too close to the bottom of the take.")
        if metadata.get("toolbarBottomInset", 0) < 80:
            raise ValueError("Leave visible breathing room below the native toolbar.")
        if take == "style":
            typing = [action for action in metadata["actions"] if action.get("gesture") == "type-character"]
            typed = "".join(action["text"] for action in typing)
            times = [action["time"] for action in typing]
            if (typed != "A useful idea." or any(len(action["text"]) != 1 for action in typing)
                    or any(not math.isfinite(value) for value in times)
                    or any(a >= b for a, b in zip(times, times[1:])) or times[-1]-times[0] < 0.8):
                raise ValueError("The label must be typed character by character.")
    focus = FOCUS.get(take) if prefix == "showcase" else None
    video_filter = f"crop={width}:{height}:{x}:{y},fps=30,setsar=1"
    if focus:
        width, height, crop = focus_window(metadata, focus)
        x, y = focus["shots"][0]["x"], focus["shots"][0]["y"]
        video_filter = f"fps=30,{crop},setsar=1"
    source_crop = [x, y, width, height]
    if take == "region":
        required = ["Right half", "Center third", "Top left", "Left half"]
        if [action.get("control") for action in metadata["actions"]] != required:
            raise ValueError("Record the real region controls in the approved order.")
        if metadata.get("recordingSource") != "Real Annotator region chooser; only recorder-owned windows.":
            raise ValueError("Region footage must use the real chooser and owned capture source.")
        ratio = min(1, 1200 / width)
        width = int(width * ratio // 2 * 2)
        height = int(height * ratio // 2 * 2)
        video_filter += f",scale={width}:{height}:flags=lanczos"
    output.mkdir(parents=True, exist_ok=True)
    movie = output / f"{prefix}-{take}.mp4"
    poster = output / f"{prefix}-{take}.jpg"
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(source), "-an",
                    "-vf", video_filter,
                    "-c:v", "libx264", "-profile:v", "main", "-level:v", "3.2",
                    "-tag:v", "avc1", "-g", "60", "-preset", "slow", "-crf", "18",
                    "-threads", "4", "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(movie)], check=True)
    poster_time = (focus or {}).get("poster", max(0, metadata["duration"] - 2.4))
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-ss", str(poster_time),
                    "-i", str(movie), "-frames:v", "1", "-q:v", "2", str(poster)], check=True)
    def stamp(seconds):
        milliseconds = round(seconds * 1000)
        return f"{milliseconds // 60000:02}:{milliseconds // 1000 % 60:02}.{milliseconds % 1000:03}"
    descriptions = {
        "draw": "Choose Ellipse, a rough style and teal ink. Circle the date, then highlight a line.",
        "arrow": "Choose a rough blue curve. Click four points to build the arrow, then drag one point to reshape it.",
        "blur": "Choose Blur and drag over the colorful image. The surrounding text stays sharp.",
        "style": "Draw with a lagoon gradient and a softer fill, then type a label that follows the outline's color.",
        "pointer": "Choose a pointer shape and color, add glow, then click the native preview to try the effect.",
        "shortcuts": "Open the native Shortcuts tab to see the configurable drawing, zoom and tool keys.",
        "tools": "Browse and scroll the real tool library, draw a grid, and explore the Extras, Focus and Draw categories.",
        "region": "Choose Right half, Center third, Top left and Left half in the real region-sharing window.",
    }
    if prefix == "showcase":
        descriptions["draw"] = "Circle the date with a rough teal ellipse, then highlight a line."
        descriptions["arrow"] = "Build a blue curved arrow point by point, then drag one point to reshape it."
        descriptions["blur"] = "Drag a native blur region over the colorful image. The surrounding text stays sharp."
        descriptions["style"] = "Draw a gradient shape, then type its matching label one character at a time."
        descriptions["shortcuts"] = "Drag Spotlight into the toolbar, press its key and choose L, then drag Line out of the toolbar."
    (output / f"{prefix}-{take}.vtt").write_text(
        f"WEBVTT\n\n00:00.000 --> {stamp(metadata['duration'])}\n{descriptions[take]}\n")
    probe = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-show_streams",
                                               "-show_format", "-of", "json", str(movie)], text=True))
    if len(probe["streams"]) != 1 or probe["streams"][0]["codec_type"] != "video":
        raise ValueError("Feature takes must contain one silent video track.")
    stream = probe["streams"][0]
    if (stream.get("codec_name") != "h264" or stream.get("profile") != "Main"
            or stream.get("level") != 32 or stream.get("pix_fmt") != "yuv420p"
            or stream.get("codec_tag_string") != "avc1"
            or Fraction(stream.get("avg_frame_rate", "0")) != 30):
        raise ValueError("Web takes require H.264 Main Level 3.2, avc1, 8-bit YUV 4:2:0 and 30fps.")
    return {
        "file": f"/demo/{movie.name}", "poster": f"/demo/{poster.name}",
        "width": width, "height": height, "duration": float(probe["format"]["duration"]),
        "sha256": digest(movie), "sourceSHA256": digest(source), "sourceFile": source.name,
        "imageWorkspace": False, "toolbarAppearance": "dark", "ink": metadata["ink"],
        "crop": source_crop, "speed": 1,
        "encoding": {"codec": stream["codec_name"], "profile": stream["profile"],
                     "level": stream["level"], "pixelFormat": stream["pix_fmt"], "frameRate": 30},
        "toolbarClearance": clearance,
        **({"focus": focus} if focus else {}),
        "actions": metadata["actions"],
        "input": metadata["input"],
        "preparedControls": metadata.get("preparedControls", []),
        "toolbarAnchorStable": metadata.get("toolbarAnchorStable", False),
        "toolbarPropertiesMode": metadata.get("toolbarPropertiesMode"),
        "toolbarBottomInset": metadata.get("toolbarBottomInset"),
        "toolbarSamples": metadata.get("toolbarSamples", []),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=ROOT / "build/fresh-product")
    parser.add_argument("--output", type=Path, default=ROOT / "website/public/demo")
    parser.add_argument("--showcase", action="store_true", help="Encode product-first recordings and native Settings references.")
    parser.add_argument("--take", help="Encode only this take and keep the other entries.")
    parser.add_argument("--recorded", help="Recording date of --take, as YYYY-MM-DD.")
    parser.add_argument("--region", action="store_true", help="Encode the owned region-chooser take into its separate manifest.")
    args = parser.parse_args()
    if args.region:
        entry = encode(args.source / "latest-region.json", args.output, "showcase")
        metadata = json.loads((args.source / "latest-region.json").read_text())
        entry["recorded"] = metadata["recorded"]
        destination = ROOT / "website/src/data/region-sharing.json"
        destination.write_text(json.dumps({"schemaVersion": 1, "take": entry}, indent=2) + "\n")
        print(f"Encoded native region chooser into {destination}")
        return
    names = ("draw", "arrow", "blur", "style", "tools", "pointer", "shortcuts") if args.showcase else ("draw", "arrow", "blur")
    destination = ROOT / "website/src/data" / ("showcase-takes.json" if args.showcase else "product-takes.json")
    if args.take:
        # Re-encode one take and keep the others, with their own recording dates, unchanged.
        if args.take not in names or not args.recorded:
            raise SystemExit("--take needs a take name from this set and --recorded YYYY-MM-DD.")
        document = json.loads(destination.read_text())
        entry = encode(args.source / f"latest-{args.take}.json", args.output, "showcase" if args.showcase else "fresh")
        entry["recorded"] = args.recorded
        document["takes"][args.take] = entry
        destination.write_text(json.dumps(document, indent=2) + "\n")
        print(f"Encoded {args.take} into {destination}")
        return
    entries = {name: encode(args.source / f"latest-{name}.json", args.output, "showcase" if args.showcase else "fresh") for name in names}
    destination.write_text(json.dumps({"schemaVersion": 1, "recorded": "2026-09-24", "takes": entries}, indent=2) + "\n")
    print(f"Encoded {len(entries)} native takes with full toolbar bounds: {destination}")


if __name__ == "__main__":
    main()
