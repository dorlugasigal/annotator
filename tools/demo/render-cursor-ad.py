#!/usr/bin/env python3
"""Assemble the cursor-led ad; the source-aware camera is prepared separately."""
import json
import html
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT/"videos/annotator-native-promo"
SPEED = 2
INTRO = 2.0
BROWSER_LENGTH = 6.0
CLOSE_LENGTH = 3.5


def build(phone=False):
    source = json.loads((PROJECT/"native-quality-source.json").read_text())
    length = round(source["duration"]/SPEED*60)/60
    browser_start = INTRO+length
    close_start = browser_start+BROWSER_LENGTH
    total = close_start+CLOSE_LENGTH
    width,height = (1080,1440) if phone else (1920,1080)
    pad = 40 if phone else 60
    film_width,film_height = (1000,750) if phone else (1280,960)
    film_x,film_y = (40,365) if phone else (580,50)
    chapters = source["chapters"]
    text = {
        "Set the problem": ("Start with","the question."),
        "Sketch the service": ("Draw it.","Name it."),
        "People and data": ("Pick the","right shape."),
        "Connect the request": ("Connect","the parts."),
        "Name the parts": ("Make the","path clear."),
        "Color and emphasis": ("Point out","what matters."),
        "Move and refine": ("Move it.","Keep the label."),
    }
    labels=[];animations=[]
    for index,chapter in enumerate(chapters):
        start=INTRO+chapter["time"]/SPEED
        end=INTRO+(chapters[index+1]["time"] if index+1<len(chapters) else source["duration"])/SPEED
        line1,line2=text[chapter["name"]]
        labels.append(f'<div id="label-{index}" class="headline clip" data-start="{start:.6f}" data-duration="{end-start:.6f}" data-track-index="{10+index}">{line1}<span>{line2}</span></div>')
        animations.append(f'tl.fromTo("#label-{index}",{{x:-24,opacity:0}},{{x:0,opacity:1,duration:.22,ease:"power3.out",immediateRender:false}},{start:.6f});')
    auto=html.escape(json.dumps({"version":1,"lanes":[{"target":"volume","points":[
        {"t":0,"v":0},{"t":.1,"v":.72},{"t":total-1.3,"v":.72},{"t":total,"v":0}
    ]}]}),quote=True)
    css_extra = """
.headline{top:130px;left:40px;width:1000px;height:200px;font-size:78px}
.headline span{display:inline;margin-left:18px}
#opening{top:190px;left:40px;width:1000px;font-size:142px;height:330px}
#opening span{display:block;margin-left:0}
#closing{top:240px;left:40px;width:1000px;font-size:150px;height:200px}
#closing-sub{top:470px;left:40px;width:1000px;font-size:44px}
#credit{top:1130px;left:40px;width:1000px;font-size:24px}
""" if phone else """
.headline{top:330px;left:60px;width:480px;height:410px;font-size:88px}
.headline span{display:block}
#opening{top:240px;left:90px;width:1700px;font-size:190px;height:440px}
#opening span{display:block}
#closing{top:270px;left:90px;width:1700px;font-size:200px;height:270px}
#closing-sub{top:570px;left:100px;width:1500px;font-size:52px}
#credit{top:900px;left:90px;width:1740px;font-size:23px}
.source-note{width:450px;height:150px;bottom:25px}
"""
    result=f"""<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width={width},height={height}">
<script src="assets/gsap.min.js"></script><style>
@font-face{{font-family:Bricolage;src:url("assets/bricolage.woff2") format("woff2");font-weight:200 800}}
*{{box-sizing:border-box;margin:0;padding:0}}html,body,#main{{width:{width}px;height:{height}px;overflow:hidden}}
#main{{position:relative;font-family:Bricolage,sans-serif;color:#f4f2f0;background:#101b33}}
.ground{{position:absolute;inset:0;background:#101b33}}
.brand{{position:absolute;left:{pad}px;top:28px;font-size:25px;width:500px;height:40px}}
.headline,#opening,#closing,#closing-sub,#credit{{position:absolute;line-height:1.08;font-weight:600;letter-spacing:-2px}}
.headline span,#opening span,#closing-sub{{color:#ffac77}}
.screen{{position:absolute;left:{film_x}px;top:{film_y}px;width:{film_width}px;height:{film_height}px;overflow:hidden;border-radius:16px;background:#e8ece7;box-shadow:0 18px 45px #0004}}
.screen video{{width:100%;height:100%;object-fit:contain;display:block}}
.source-note{{position:absolute;left:{pad}px;bottom:{"45" if phone else "20"}px;width:{width-pad*2}px;height:70px;font:{"22" if phone else "18"}px/1.5 "Helvetica Neue",sans-serif;color:#b7c2d6}}
#credit{{font-weight:400;line-height:1.5;letter-spacing:0;color:#b7c2d6;height:170px}}
{css_extra}
</style></head><body><div id="main" data-composition-id="main" data-width="{width}" data-height="{height}" data-duration="{total:.6f}">
<div class="ground"></div>
<div id="brand" class="brand clip" data-start="0" data-duration="{close_start:.6f}" data-track-index="25">annotator / for Mac</div>
<div id="opening" class="clip" data-start="0" data-duration="2" data-track-index="2">Make your<span>point.</span></div>
<div id="native-screen" class="screen"><video id="native-video" class="clip" src="assets/cursor-tracked.mp4" data-start="2" data-duration="{length:.6f}" data-track-index="1" muted playsinline></video></div>
{''.join(labels)}
<div id="browser-screen" class="screen"><video id="browser-video" class="clip" src="assets/ad-browser.mp4" data-start="{browser_start:.6f}" data-duration="{BROWSER_LENGTH}" data-track-index="1" muted playsinline></video></div>
<div id="blur-label" class="headline clip" data-start="{browser_start:.6f}" data-duration="{BROWSER_LENGTH}" data-track-index="19">Keep the<span>focus.</span></div>
<div id="source-note" class="source-note clip" data-start="2" data-duration="{close_start-INTRO:.6f}" data-track-index="26">Actual native app footage. Scripted actions and sample data.<span style="display:block">Edited speed. Blur is a visual effect, not secure redaction.</span></div>
<div id="closing" class="clip" data-start="{close_start:.6f}" data-duration="{CLOSE_LENGTH}" data-track-index="3">annotator.</div>
<div id="closing-sub" class="clip" data-start="{close_start:.6f}" data-duration="{CLOSE_LENGTH}" data-track-index="4">Native Mac tools. Free edition.<span style="display:block">Private preview.</span></div>
<div id="credit" class="clip" data-start="{close_start:.6f}" data-duration="{CLOSE_LENGTH}" data-track-index="5">Music: “Chrome Funk” by Punch Deck. Edited excerpt.<span style="display:block">punchdeck.bandcamp.com · CC BY 4.0</span><span style="display:block">creativecommons.org/licenses/by/4.0/</span></div>
<audio id="music-bed" src="assets/audio/chrome-funk.mp3" data-start="0" data-duration="{total:.6f}" data-media-start="32" data-track-index="100" data-automation="{auto}"></audio>
</div><script>
gsap.set("#native-screen,#browser-screen",{{autoAlpha:0}});
const tl=gsap.timeline({{paused:true}});
tl.set("#native-screen",{{autoAlpha:1}},2);tl.set("#native-screen",{{autoAlpha:0}},{browser_start:.6f});
tl.set("#browser-screen",{{autoAlpha:1}},{browser_start:.6f});tl.set("#browser-screen",{{autoAlpha:0}},{close_start:.6f});
tl.fromTo("#opening",{{y:28,opacity:0}},{{y:0,opacity:1,duration:.3,ease:"expo.out",immediateRender:false}},0);
tl.fromTo("#native-screen",{{x:48,opacity:0}},{{x:0,opacity:1,duration:.4,ease:"power3.out",immediateRender:false}},2);
{''.join(animations)}
tl.fromTo("#blur-label",{{x:-24,opacity:0}},{{x:0,opacity:1,duration:.25,ease:"power3.out",immediateRender:false}},{browser_start:.6f});
tl.fromTo("#closing",{{y:35,opacity:0}},{{y:0,opacity:1,duration:.35,ease:"power3.out",immediateRender:false}},{close_start:.6f});
window.__timelines["main"]=tl;
</script></body></html>"""
    return result,total,chapters,length


if __name__=="__main__":
    for folder,phone in [(PROJECT,False),(PROJECT/"mobile",True)]:
        markup,total,chapters,length=build(phone)
        (folder/"index.html").write_text(markup)
        (folder/"index.motion.json").write_text(json.dumps({"duration":total,"assertions":[
            {"kind":"appearsBy","selector":"#opening","bySec":.5},
            {"kind":"staysInFrame","selector":"#closing"},
        ]},indent=2)+"\n")
    names={"Set the problem":"The question","Sketch the service":"Smart Draw and labels","People and data":"System shapes",
           "Connect the request":"Arrow preferences","Name the parts":"Text labels",
           "Color and emphasis":"Highlighter","Move and refine":"Select and edit"}
    data={"duration":total,"chapters":[{"label":names[c["name"]],"time":round(INTRO+c["time"]/SPEED,3)} for c in chapters]+[{"label":"Blur","time":round(INTRO+length,3)}]}
    (ROOT/"website/src/data/native-demo.json").write_text(json.dumps(data,indent=2)+"\n")
    print(json.dumps({"duration":total,"sourceFPS":60,"layout":"1280x960 native picture; never enlarged"}))
