#!/usr/bin/env python3
"""Prepare silent, seekable native interview clips; rendering stays a separate step."""

import argparse
import html
import json
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "videos/annotator-native-promo"
SHOTS = [
    ("Set the problem", "Start with the question.", 6.0),
    ("Sketch the service", "Sketch before you explain.", 9.0),
    ("People and data", "Add people, shapes and color.", 11.0),
    ("Connect the request", "Show how the parts connect.", 10.0),
    ("Name the parts", "Give every part a name.", 10.0),
    ("Color and emphasis", "Make the read path clear.", 9.0),
    ("Blur a sample secret", "Blur a detail before you share.", 6.0),
    ("Move and refine", "Edit without starting over.", 5.0),
]
CHAPTER_LABELS = ["The question", "Smart Draw", "Shapes and color", "Arrow preferences",
                  "Text labels", "Highlighter", "Blur", "Select and edit"]


def run(*args):
    subprocess.run(args, check=True)


def composition(clips, portrait=False):
    width, height = (1080, 1440) if portrait else (1920, 1080)
    pad = 40 if portrait else 64
    frame_top = 230 if portrait else 165
    frame_height = 1040 if portrait else 825
    title_size = 55 if portrait else 64
    duration = clips[-1]["start"] + clips[-1]["duration"]
    parts, motion = [], []
    for index, clip in enumerate(clips):
        start, length = clip["start"], clip["duration"]
        safe_title = html.escape(clip["title"])
        parts.append(
            f'<div id="heading-{index}" class="heading clip" data-start="{start}" '
            f'data-duration="{length}" data-track-index="10">{safe_title}</div>'
            f'<div id="frame-{index}" class="frame"><div id="camera-{index}" class="camera" data-layout-allow-overflow>'
            f'<video id="video-{index}" class="clip" src="assets/{clip["file"]}" '
            f'data-start="{start}" data-duration="{length}" data-track-index="1" muted playsinline></video>'
            '</div></div>'
        )
        motion.extend([
            f'tl.set("#frame-{index}",{{autoAlpha:0}},0);',
            f'tl.set("#frame-{index}",{{autoAlpha:1}},{start});',
            f'tl.set("#frame-{index}",{{autoAlpha:0}},{start + length});',
            f'tl.fromTo("#heading-{index}",{{x:22,opacity:0}},{{x:0,opacity:1,duration:.3,ease:"power2.out",immediateRender:false}},{start});',
        ])
        if index in (1, 4) and not portrait:
            motion.extend([
                f'tl.fromTo("#camera-{index}",{{scale:1,y:0}},{{scale:1.16,y:42,duration:.7,ease:"power2.inOut",immediateRender:false}},{start + .45});',
                f'tl.to("#camera-{index}",{{scale:1,y:0,duration:.6,ease:"power2.inOut"}},{start + length - 1.8});',
            ])
    return f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width={width},height={height}">
<script src="assets/gsap.min.js"></script><style>
@font-face{{font-family:Bricolage;src:url("assets/bricolage.woff2") format("woff2");font-weight:200 800}}
*{{box-sizing:border-box;margin:0;padding:0}}html,body,#main{{width:{width}px;height:{height}px;overflow:hidden}}
body{{background:#101813}}#main{{position:relative;color:#f5f3e9;font-family:Bricolage,sans-serif}}
.ground{{position:absolute;inset:0;background:#101813}}
.brand{{position:absolute;left:{pad}px;top:34px;width:550px;height:42px;font-size:28px;font-weight:650}}
.heading{{position:absolute;left:{pad}px;top:{"120" if portrait else "91"}px;width:{width-pad*2}px;height:{"105" if portrait else "78"}px;font-size:{title_size}px;letter-spacing:-2px;line-height:1.08}}
.frame{{position:absolute;left:{pad}px;top:{frame_top}px;width:{width-pad*2}px;height:{frame_height}px;overflow:hidden;background:#e8ede7;border-radius:18px}}
.camera{{position:absolute;inset:0;transform-origin:50% 35%}}
video{{display:block;width:100%;height:100%;object-fit:contain}}
.disclosure{{position:absolute;left:{pad}px;bottom:32px;width:{width-pad*2}px;height:{"84" if portrait else "38"}px;font-size:{"23" if portrait else "20"}px;line-height:1.35;color:#bdc9bd}}
</style></head><body>
<div id="main" data-composition-id="main" data-width="{width}" data-height="{height}" data-duration="{duration}">
<div class="ground"></div><div class="brand">annotator / for Mac</div>
{''.join(parts)}
<div class="disclosure">Scripted native Mac app footage. Sample data only.{"<span style='display:block'>Private preview. All current tools are free.</span>" if portrait else " Private preview. All current tools are free."}</div>
</div><script>const tl=gsap.timeline({{paused:true}});{''.join(motion)}window.__timelines["main"]=tl;</script>
</body></html>
"""


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--metadata", type=Path, default=PROJECT / "interview-source.json")
    args = parser.parse_args()
    metadata = json.loads(args.metadata.read_text())
    source = Path(metadata["path"])
    if not source.is_absolute():
        source = ROOT / source
    if not source.is_file() or metadata.get("imageWorkspace") is not False:
        raise ValueError("A recorded live-desktop source is required.")
    if metadata.get("gestureSeed") != 20260912:
        raise ValueError("Expected the repeatable interview recording.")
    chapters = metadata["chapters"]
    if [row["name"] for row in chapters] != [row[0] for row in SHOTS]:
        raise ValueError("Source chapter names do not match the interview edit.")
    rect = metadata["contentRect"]
    x, y = int(rect["x"]) // 2 * 2, int(rect["y"]) // 2 * 2
    w, h = int(rect["width"]) // 2 * 2, int(rect["height"]) // 2 * 2
    clips, clock = [], 0.0
    for index, ((name, title, target), chapter) in enumerate(zip(SHOTS, chapters)):
        start = chapter["time"]
        end = chapters[index + 1]["time"] if index + 1 < len(chapters) else metadata["duration"] - .1
        raw = end - start
        if raw <= 0:
            raise ValueError(f"Invalid source range: {name}")
        moving = round(max(target - 1.2, raw / 2), 3)
        length = round(moving + 1.2, 3)
        filename = f"interview-{index + 1:02}.mp4"
        run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-ss", str(start), "-t", str(raw),
            "-i", str(source), "-vf",
            f"crop={w}:{h}:{x}:{y},scale=1440:-2,setpts=(PTS-STARTPTS)/{raw/moving},fps=30,tpad=stop_mode=clone:stop_duration=1.2",
            "-t", str(length), "-an", "-c:v", "libx264", "-preset", "fast", "-crf", "17",
            "-movflags", "+faststart", str(PROJECT / "assets" / filename))
        clips.append(dict(name=name, title=title, file=filename, start=round(clock, 3), duration=length,
                          sourceStart=start, sourceDuration=raw, playbackRate=raw / moving, finalHold=1.2))
        clock += length
    (PROJECT / "index.html").write_text(composition(clips))
    (PROJECT / "mobile/index.html").write_text(composition(clips, True))
    manifest = dict(duration=round(clock, 3), source=str(source.relative_to(ROOT)), clips=clips, audio="none")
    (PROJECT / "interview-edit.json").write_text(json.dumps(manifest, indent=2) + "\n")
    website_data = ROOT / "website/src/data/native-demo.json"
    website_data.parent.mkdir(parents=True, exist_ok=True)
    website_data.write_text(json.dumps({
        "duration": manifest["duration"],
        "chapters": [{"label": label, "time": clip["start"]}
                     for label, clip in zip(CHAPTER_LABELS, clips)]
    }, indent=2) + "\n")
    for folder in (PROJECT, PROJECT / "mobile"):
        (folder / "index.motion.json").write_text(json.dumps({
            "duration": clock,
            "assertions": [{"kind": "appearsBy", "selector": "#heading-0", "bySec": .5}]
                          + [{"kind": "staysInFrame", "selector": f"#heading-{index}"} for index in range(len(clips))]
        }, indent=2) + "\n")
    print(json.dumps({"duration": round(clock, 3), "chapters": len(clips), "audio": "none"}))


if __name__ == "__main__":
    main()
