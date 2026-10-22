#!/usr/bin/env python3
"""Render source-resolution cursor framing and a synchronized, sharp toolbar."""
import argparse
import json
import subprocess
from pathlib import Path
from recording.cursor_camera import plan, expression, moving_intervals

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--metadata", type=Path, default=ROOT/"build/native-desktop/latest-interview-native.json")
    parser.add_argument("--seconds", type=float)
    parser.add_argument("--speed", type=float, default=1.75)
    parser.add_argument("--output", type=Path, default=ROOT/"build/cursor-demo/tracked.mp4")
    args = parser.parse_args()
    metadata = json.loads(args.metadata.read_text())
    source = Path(metadata["path"])
    if not source.is_absolute():
        source = ROOT/source
    metadata["path"] = str(source)
    if not metadata.get("nativeQuality") or len(metadata.get("pointerSamples", [])) < 2:
        raise ValueError("Native-resolution source and recorded cursor samples are required.")
    camera = plan(metadata, speed=args.speed)
    if camera["maximumUpscale"] > 1 or camera["missedControlPresses"]:
        raise ValueError(f"Camera failed source-quality/control-visibility gates: {camera['missedControlPresses']}")
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    x, y, width, height = camera["base"]
    duration = min(args.seconds or camera["duration"], camera["duration"])
    if duration <= 0:
        raise ValueError("Output duration must be positive.")
    parts = []
    total_frames = round(duration*60)
    for first in range(0,total_frames,360):
        count = min(360,total_frames-first)
        rows = [{**row,"frame":row["frame"]-first} for row in camera["frames"][first:first+count]]
        zoom = expression(rows, "zoom")
        pan_x, pan_y = expression(rows, "x"), expression(rows, "y")
        motion = "+".join(f"between(n,{a},{b})" for a, b in moving_intervals(rows)) or "0"
        start = first/60
        graph = (
            f"[0:v]setpts=(PTS-STARTPTS)/{args.speed},fps=60,crop={width}:{height}:{x}:{y},"
            f"zoompan=z='{zoom}':x='{pan_x}':y='{pan_y}':d=1:s=1280x960:fps=60,"
            f"tmix=frames=2:weights='7 1':enable='{motion}'[out]"
        )
        part = output.with_name(output.stem+f"-part-{first//360:02}.mp4")
        graph_file = part.with_suffix(".filters.txt")
        graph_file.write_text(graph)
        command = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
                   "-ss",str(start*args.speed),"-i", metadata["path"],
                   "-/filter_complex", str(graph_file), "-map", "[out]", "-frames:v", str(count),
                   "-an", "-c:v", "libx264", "-preset", "fast", "-crf", "14", "-pix_fmt", "yuv420p",
                   "-movflags", "+faststart", str(part)]
        subprocess.run(command, check=True)
        parts.append(part)
    playlist = output.with_suffix(".concat.txt")
    playlist.write_text("".join(f"file '{part.name}'\n" for part in parts))
    subprocess.run(["ffmpeg","-hide_banner","-loglevel","error","-y","-f","concat","-safe","0","-i",
                    str(playlist),"-c","copy","-movflags","+faststart",str(output)],check=True)
    camera["source"] = metadata["path"]
    camera["renderedDuration"] = duration
    output.with_suffix(".camera.json").write_text(json.dumps(camera, indent=2)+"\n")
    print(json.dumps({"output":str(output),"duration":duration,"maximumUpscale":camera["maximumUpscale"],
                      "missedControls":len(camera["missedControlPresses"]),"motionIntervals":len(moving_intervals(camera["frames"]))}))


if __name__ == "__main__":
    main()
