"""Package a verified live-desktop capture, with a closer phone edit."""
import json
import math
import subprocess
from pathlib import Path

root = Path(__file__).resolve().parents[2]
metadata = json.loads((root / "build/native-desktop/latest.json").read_text())
source = Path(metadata["path"]).resolve()
if metadata.get("imageWorkspace") is not False or not source.is_relative_to(root / "build/native-desktop"):
    raise ValueError("Only the live-desktop recording is allowed.")
rect = metadata["contentRect"]
if not all(math.isfinite(rect[key]) and rect[key] >= 0 for key in ["x", "y", "width", "height"]):
    raise ValueError("Invalid capture bounds.")
x, y = int(rect["x"]) // 2 * 2, int(rect["y"]) // 2 * 2
w = min(math.ceil(rect["width"] / 2) * 2, metadata["width"] - x)
h = min(math.ceil(rect["height"] / 2) * 2, metadata["height"] - y)
if w < 100 or h < 100:
    raise ValueError("The live desktop crop is too small.")
target = root / "website/public/demo"
target.mkdir(parents=True, exist_ok=True)

def encode(filters, output):
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(source),
                    "-vf", filters, "-an", "-c:v", "libx264", "-crf", "18", "-preset", "medium",
                    "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(output)], check=True)

desktop_height = round(1440 * h / w / 2) * 2
encode(f"crop={w}:{h}:{x}:{y},scale=1440:{desktop_height}", target / "native-desktop.mp4")
phone_width = min(w, int(h * .75) // 2 * 2)
camera = max(0, min(w - phone_width, metadata["pointerSamples"][0]["x"] - x - phone_width / 2))
commands = []
last_time = 0
for sample in metadata["pointerSamples"]:
    local = sample["x"] - x
    desired = camera
    if local < camera + phone_width * .18:
        desired = local - phone_width * .32
    elif local > camera + phone_width * .82:
        desired = local - phone_width * .68
    desired = max(0, min(w - phone_width, desired))
    dt = max(.01, sample["time"] - last_time)
    camera += (desired - camera) * (1 - math.exp(-dt / .13))
    commands.append(f'{sample["time"]:.6f} crop@phone x {int(camera)//2*2};')
    last_time = sample["time"]
command_file = root / "build/native-desktop/phone-camera.txt"
command_file.write_text("\n".join(commands))
encode(f"crop={w}:{h}:{x}:{y},sendcmd=f='{command_file}',crop@phone={phone_width}:{h}:0:0,scale=720:960",
       target / "native-desktop-mobile.mp4")
for name in ["native-desktop", "native-desktop-mobile"]:
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-ss", "9",
                    "-i", str(target / f"{name}.mp4"), "-frames:v", "1", "-update", "1", "-q:v", "2",
                    str(target / f"{name}-poster.jpg")], check=True)
print(json.dumps({"duration": metadata["duration"], "width": 1440, "height": desktop_height,
                  "chapters": metadata["chapters"], "desktop_bytes": (target / "native-desktop.mp4").stat().st_size,
                  "mobile_bytes": (target / "native-desktop-mobile.mp4").stat().st_size}))
