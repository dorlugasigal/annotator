#!/usr/bin/env python3
"""Prepare the short product ad from verified native recordings."""
import html
import json
import subprocess
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "videos/annotator-native-promo"
ASSETS = PROJECT / "assets"
INK = ASSETS / "native-interview-source.mp4"
BROWSER = ASSETS / "native-browser-blur.mp4"
DURATION = 38.4
NOTE = (714, 642, 1452, 850)
DIAGRAM = (820, 894, 1260, 492)

# Explicit source rectangles keep close-ups attached to the recorded subject.
CLIPS = [
    ("pen", INK, 8.9, 2.3, 2.4, (1170, 918, 370, 180), 2.4),
    ("smart", INK, 12.7, 2.2, 2.4, (1150, 1120, 520, 240), 4.8),
    ("shapes", INK, 15.3, 1.25, 1.8, (1330, 1042, 532, 584), 7.2),
    ("color", INK, 27.9, 1.15, 1.8, (698, 968, 408, 756), 9.0),
    ("filled", INK, 30.05, 1.0, 1.2, DIAGRAM, 10.8),
    ("arrow", INK, 33.9, 1.4, 1.2, (860, 920, 740, 230), 12.0),
    ("heads", INK, 35.9, 1.2, 1.2, (790, 1280, 290, 460), 13.2),
    ("head-result", INK, 37.3, .45, .6, DIAGRAM, 14.4),
    ("curve", INK, 46.7, 1.1, 1.8, (1170, 894, 910, 470), 15.0),
    ("text", INK, 52.95, 1.2, 2.4, DIAGRAM, 16.8),
    ("highlight", INK, 63.15, 2.2, 2.4, (1170, 894, 910, 470), 19.2),
    ("browser", BROWSER, 0, 6.0, 7.2, (140, 264, 1680, 1064), 21.6),
    ("browser-detail", BROWSER, 0, 6.0, 7.2, (1308, 448, 484, 608), 21.6),
    ("edit", INK, 79.4, 4.55, 4.8, DIAGRAM, 28.8),
]


def run(*args):
    subprocess.run(args, check=True)


def prepare():
    for name, source, start, source_duration, duration, crop, _ in CLIPS:
        if not source.is_file():
            raise FileNotFoundError(source)
        if name.startswith("browser"):
            x, y, w, h = crop
            # Omit the recorded layer-startup transition; this edit is not a latency demonstration.
            filters = (
                "[0:v]trim=start=0:end=2,setpts=PTS-STARTPTS[a];"
                "[0:v]trim=start=2.9:end=6,setpts=PTS-STARTPTS[b];"
                f"[a][b]concat=n=2:v=1:a=0,crop={w}:{h}:{x}:{y},fps=30,"
                "tpad=stop_mode=clone:stop_duration=2.1[out]"
            )
            run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(source),
                "-filter_complex", filters, "-map", "[out]", "-t", str(duration), "-an",
                "-c:v", "libx264", "-crf", "16", "-preset", "fast", "-movflags", "+faststart",
                str(ASSETS / f"ad-{name}.mp4"))
            continue
        hold = max(0, duration - source_duration)
        speed = max(1, source_duration / duration)
        x, y, w, h = crop
        filters = f"crop={w}:{h}:{x}:{y},setpts=(PTS-STARTPTS)/{speed},fps=30"
        if hold:
            filters += f",tpad=stop_mode=clone:stop_duration={hold}"
        run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-ss", str(start),
            "-t", str(source_duration), "-i", str(source), "-vf", filters, "-t", str(duration),
            "-an", "-c:v", "libx264", "-crf", "16", "-preset", "fast", "-movflags", "+faststart",
            str(ASSETS / f"ad-{name}.mp4"))
    x, y, w, h = DIAGRAM
    run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-ss", "75.75", "-i", str(INK),
        "-vf", f"crop={w}:{h}:{x}:{y}", "-frames:v", "1", "-q:v", "2", str(ASSETS / "ad-result.jpg"))
    for name in ("hook", "close", "style", "heads", "pen"):
        shutil.copyfile(ASSETS / "ad-result.jpg", ASSETS / f"ad-result-{name}.jpg")


def composition(phone=False):
    w, h = (1080, 1440) if phone else (1920, 1080)
    mx = 48 if phone else 70
    fw, fh, top = (984, 910, 310) if phone else (1780, 790, 210)
    video_nodes, timeline = [], []
    for name, _, _, _, duration, crop, start in CLIPS:
        _, _, sw, sh = crop
        classes = "film"
        if name in ("shapes", "color", "heads"):
            classes += " picker"
        track = 2 if name == "browser-detail" else 1
        video_nodes.append(
            f'<div id="frame-{name}" class="{classes}"><div id="zoom-{name}" class="zoom">'
            f'<div id="pan-{name}" class="pan"><video id="video-{name}" class="clip" '
            f'src="assets/ad-{name}.mp4" data-start="{start}" data-duration="{duration}" '
            f'data-track-index="{track}" muted playsinline></video></div></div></div>'
        )
        timeline += [
            f'tl.set("#frame-{name}",{{autoAlpha:0}},0);',
            f'tl.set("#frame-{name}",{{autoAlpha:{0 if name == "browser-detail" else 1}}},{start});',
            f'tl.set("#frame-{name}",{{autoAlpha:0}},{round(start+duration,4)});',
        ]
        if name in ("shapes", "color", "heads"):
            timeline.append(f'tl.fromTo("#frame-{name}",{{y:36,opacity:0}},{{y:0,opacity:1,duration:.24,ease:"power3.out",immediateRender:false}},{start});')
        if name == "browser-detail":
            timeline.append('tl.fromTo("#frame-browser-detail",{autoAlpha:0,x:55},{autoAlpha:1,x:0,duration:.35,ease:"power3.out",immediateRender:false},22.2);')
    titles = [
        ("pen", "Draw it.", 2.4, 4.8),
        ("style", "Make it yours.", 7.2, 4.8),
        ("connect", "Connect the parts.", 12.0, 4.8),
        ("words", "Put it in words.", 16.8, 4.8),
        ("blur", "Blur the distraction.", 21.6, 7.2),
        ("edit", "Change your mind.", 28.8, 4.8),
    ]
    headings = []
    for index, (name, title, start, length) in enumerate(titles):
        headings.append(f'<div id="title-{name}" class="heading clip" data-start="{start}" data-duration="{length}" data-track-index="{11+index}">{html.escape(title)}</div>')
        timeline.append(f'tl.fromTo("#title-{name}",{{x:-35,opacity:0}},{{x:0,opacity:1,duration:.24,ease:"expo.out",immediateRender:false}},{start});')
    automation = html.escape(json.dumps({"version": 1, "lanes": [{"target": "volume", "points": [
        {"t": 0, "v": 0}, {"t": .15, "v": .6}, {"t": 36.9, "v": .6}, {"t": DURATION, "v": 0}
    ]}]}), quote=True)
    intro_css = (
        "#hook-art{left:48px;top:500px;width:984px;height:620px}#hook-title{left:48px;top:170px;width:984px;font-size:140px}"
        "#close-art{left:100px;top:660px;width:880px;height:520px}#close-title{top:220px;font-size:150px}"
        "#close-sub{top:425px;font-size:48px}#credit{top:1260px;width:984px;font-size:21px}"
        if phone else
        "#hook-art{left:740px;top:320px;width:1100px;height:500px}#hook-title{left:70px;top:290px;width:650px;font-size:132px}"
        "#close-art{left:1030px;top:360px;width:830px;height:400px}#close-title{top:270px;font-size:172px}"
        "#close-sub{top:500px;font-size:45px}#credit{top:905px;width:1450px;font-size:22px}"
    )
    proof = "" if phone else """
<div id="proof-pen" class="proof clip" data-start="2.4" data-duration="4.8" data-track-index="4"><img src="assets/ad-result-pen.jpg" alt="The native diagram built from the pen sketch"></div>
<div id="proof-style" class="proof clip" data-start="7.2" data-duration="3.6" data-track-index="3"><img src="assets/ad-result-style.jpg" alt="The finished native drawing"></div>
<div id="proof-heads" class="proof clip" data-start="13.2" data-duration="1.2" data-track-index="3"><img src="assets/ad-result-heads.jpg" alt="The native diagram with finished connections"></div>"""
    split_css = "" if phone else """
.picker{width:540px}
.proof{position:absolute;left:680px;top:300px;width:1160px;height:650px;overflow:hidden;border-radius:16px}
.proof img{width:100%;height:100%;object-fit:contain}
#frame-pen,#frame-smart{width:740px;height:400px;top:380px}
#proof-pen{left:940px;top:330px;width:900px;height:500px}
#frame-browser{width:1050px;height:665px;top:275px}
#frame-browser-detail{left:1210px;width:640px}
"""
    return f"""<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width={w},height={h}"><script src="assets/gsap.min.js"></script>
<style>
@font-face{{font-family:Bricolage;src:url("assets/bricolage.woff2") format("woff2");font-weight:200 800}}
*{{box-sizing:border-box;margin:0;padding:0}}html,body,#main{{width:{w}px;height:{h}px;overflow:hidden}}
body{{background:#15291f}}#main{{position:relative;color:#f5f2df;font-family:Bricolage,sans-serif}}
.ground{{position:absolute;inset:0;background:#15291f}}
.brand{{position:absolute;left:{mx}px;top:35px;width:800px;height:48px;font-size:29px;font-weight:650}}
.heading{{position:absolute;left:{mx}px;top:110px;width:{w-mx*2}px;height:{"130" if phone else "95"}px;font-size:{"74" if phone else "83"}px;line-height:1.05;letter-spacing:-3px;font-weight:650}}
.film{{position:absolute;left:{mx}px;top:{top}px;width:{fw}px;height:{fh}px;background:#e9ede5;border-radius:18px;overflow:hidden}}
.zoom,.pan{{position:absolute;inset:0;transform-origin:50% 50%}}
video{{display:block;width:100%;height:100%;object-fit:contain}}
.picker{{background:#dce4ca}}
.disclosure{{position:absolute;left:{mx}px;bottom:25px;width:{w-mx*2}px;height:55px;font:{"20" if phone else "18"}px/1.4 "Helvetica Neue",sans-serif;color:#becbbb}}
#hook-title{{position:absolute;height:320px;line-height:1;letter-spacing:-7px;font-weight:650}}
#hook-title span{{display:block;color:#e9f36c}}
#hook-art,#close-art{{position:absolute;overflow:hidden;border-radius:16px;background:#f4f4ee;box-shadow:0 20px 70px #0004}}
#hook-art img,#close-art img{{width:100%;height:100%;object-fit:contain}}
#close-title,#close-sub,#credit{{position:absolute;left:{mx}px}}
#close-title{{width:{w-mx*2}px;height:205px;letter-spacing:-7px;font-weight:650}}
#close-sub{{width:{w-mx*2}px;height:140px;color:#e9f36c}}
#credit{{height:110px;line-height:1.5;color:#d1dac7}}
{intro_css}
{split_css}
</style></head><body><div id="main" data-composition-id="main" data-width="{w}" data-height="{h}" data-duration="{DURATION}">
<div id="ground" class="ground"></div>
<div id="brand" class="brand clip" data-start="0" data-duration="33.6" data-track-index="20">annotator / for Mac</div>
<div id="hook-art" class="clip" data-start="0" data-duration="2.4" data-track-index="2"><img src="assets/ad-result-hook.jpg" alt="A finished native system-design annotation"></div>
<div id="hook-title" class="clip" data-start="0" data-duration="2.4" data-track-index="10">Make your<span>point.</span></div>
{''.join(video_nodes)}{proof}{''.join(headings)}
<div id="close-title" class="clip" data-start="33.6" data-duration="4.8" data-track-index="10">annotator.</div>
<div id="close-sub" class="clip" data-start="33.6" data-duration="4.8" data-track-index="11">Free for Mac.<span style="display:block">Private preview.</span></div>
<div id="close-art" class="clip" data-start="33.6" data-duration="4.8" data-track-index="2"><img src="assets/ad-result-close.jpg" alt="Editable native annotations"></div>
<div id="credit" class="clip" data-start="33.6" data-duration="4.8" data-track-index="12">Music: “Origami” by Scott Buckley · Edited excerpt.<span style="display:block">CC BY 4.0 · scottbuckley.com.au · creativecommons.org/licenses/by/4.0/</span></div>
<div id="disclosure" class="disclosure clip" data-start="0" data-duration="33.6" data-track-index="21">Recorded in the native Mac app. Scripted actions and sample data. Blur is a visual effect.</div>
<audio id="music-bed" src="assets/audio/origami.mp3" data-start="0" data-duration="{DURATION}" data-media-start="24.59"
data-track-index="100" data-automation="{automation}"></audio>
</div><script>
const tl=gsap.timeline({{paused:true}});
tl.fromTo("#hook-title",{{x:-70,opacity:0}},{{x:0,opacity:1,duration:.3,ease:"expo.out",immediateRender:false}},0);
tl.fromTo("#hook-art",{{x:95,rotation:3,scale:.94}},{{x:0,rotation:0,scale:1,duration:.65,ease:"power3.out",immediateRender:false}},0);
{''.join(timeline)}
tl.fromTo("#close-title",{{y:40,opacity:0}},{{y:0,opacity:1,duration:.35,ease:"power3.out",immediateRender:false}},33.6);
tl.fromTo("#close-art",{{x:70,rotation:3}},{{x:0,rotation:0,duration:.65,ease:"power3.out",immediateRender:false}},33.6);
window.__timelines["main"]=tl;
</script></body></html>"""


if __name__ == "__main__":
    prepare()
    for folder, phone in ((PROJECT, False), (PROJECT / "mobile", True)):
        (folder / "index.html").write_text(composition(phone))
        (folder / "index.motion.json").write_text(json.dumps({
            "duration": DURATION,
            "assertions": [{"kind": "appearsBy", "selector": "#hook-title", "bySec": .5},
                           {"kind": "appearsBy", "selector": "#title-blur", "bySec": 22},
                           {"kind": "staysInFrame", "selector": "#close-title"}]
        }, indent=2) + "\n")
    data = {"duration": DURATION, "chapters": [
        {"label": label, "time": time} for label, time in [
            ("The idea", 0), ("Smart Draw", 2.4), ("Shapes and color", 7.2),
            ("Arrow preferences", 12), ("Text labels", 16.8), ("Highlighter", 19.2),
            ("Blur", 21.6), ("Select and edit", 28.8)]
    ]}
    (ROOT / "website/src/data/native-demo.json").write_text(json.dumps(data, indent=2) + "\n")
    print(json.dumps({"duration": DURATION, "source_clips": len(CLIPS), "music": "Origami / Scott Buckley"}))
