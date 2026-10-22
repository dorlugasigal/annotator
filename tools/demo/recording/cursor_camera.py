"""Offline cursor camera planning in source pixels."""
from bisect import bisect_right
from math import exp


def clamp(value, low, high):
    return max(low, min(high, value))


def pointer_at(samples, times, time):
    index = max(0, min(len(samples) - 2, bisect_right(times, time) - 1))
    a, b = samples[index:index + 2]
    blend = clamp((time - a["time"]) / max(.0001, b["time"] - a["time"]), 0, 1)
    return a["x"] + (b["x"] - a["x"]) * blend, a["y"] + (b["y"] - a["y"]) * blend


def plan(metadata, speed=1.75, fps=60):
    if not 0.5 <= speed <= 3 or fps not in (30, 60):
        raise ValueError("Camera speed must be 0.5..3 and output fps must be 30 or 60.")
    bounds = metadata["contentRect"]
    width = int(max(bounds["width"],bounds["height"]*4/3)/8+1)*8
    height = width*3//4
    base_x = int(clamp(bounds["x"]+bounds["width"]/2-width/2,0,metadata["width"]-width))//2*2
    base_y = int(clamp(bounds["y"]+bounds["height"]-height,0,metadata["height"]-height))//2*2
    samples = metadata["pointerSamples"]
    if len(samples) < 2 or any(a["time"] >= b["time"] for a,b in zip(samples,samples[1:])):
        raise ValueError("Cursor samples must contain strictly increasing capture times.")
    times = [p["time"] for p in samples]
    actions = [a for a in metadata["actions"] if a["kind"] == "control"]
    duration = metadata["duration"] / speed
    frames = round(duration * fps)
    zoom, x, y = 1.0, 0.0, 0.0
    toolbars = metadata["toolbarFrames"]
    toolbar_times = [b["time"] for b in toolbars]
    result = []
    for frame in range(frames):
        source_time = frame / fps * speed
        px, py = pointer_at(samples, times, source_time)
        fx, fy = pointer_at(samples, times, source_time + 1)
        px, py, fx, fy = px - base_x, py - base_y, fx - base_x, fy - base_y
        bar = toolbars[max(0,bisect_right(toolbar_times,source_time)-1)]["rect"]
        required = [min(px-60,fx-60,bar["x"]-base_x-24),min(py-60,fy-60,bar["y"]-base_y-24),
                    max(px+60,fx+60,bar["x"]+bar["width"]-base_x+24),
                    max(py+60,fy+60,bar["y"]+bar["height"]-base_y+24)]
        notes = metadata["notesRect"]
        required[0] = min(required[0],notes["x"]-base_x)
        required[1] = min(required[1],notes["y"]-base_y)
        required[2] = max(required[2],notes["x"]+notes["width"]-base_x)
        nearby = [a for a in actions if a["time"]-1.2 <= source_time <= a["pressTime"]+.6]
        for action in nearby:
            rect=action["rect"]
            required=[min(required[0],rect["x"]-base_x-40),min(required[1],rect["y"]-base_y-40),
                      max(required[2],rect["x"]+rect["width"]-base_x+40),
                      max(required[3],rect["y"]+rect["height"]-base_y+40)]
        goal_zoom = clamp(min(width/(required[2]-required[0]),height/(required[3]-required[1])),1,1.10)
        zoom += (goal_zoom - zoom) * (1 - exp(-1 / fps / .25))
        vw, vh = width / zoom, height / zoom
        goal_x = clamp((required[0]+required[2])/2-vw/2,0,width-vw)
        goal_y = clamp((required[1]+required[3])/2-vh/2,0,height-vh)
        x += (goal_x - x) * (1 - exp(-1 / fps / .25))
        y += (goal_y - y) * (1 - exp(-1 / fps / .25))
        x, y = clamp(x, 0, width-vw), clamp(y, 0, height-vh)
        result.append({"frame": frame, "time": frame/fps, "sourceTime": source_time, "zoom": zoom, "x": x, "y": y})
    misses = []
    for action in actions:
        row = result[min(frames-1, round(action["pressTime"] / speed * fps))]
        rect = action["rect"]
        rx, ry = rect["x"]-base_x, rect["y"]-base_y
        vw, vh = width/row["zoom"], height/row["zoom"]
        if not (row["x"] <= rx and row["y"] <= ry and rx+rect["width"] <= row["x"]+vw and ry+rect["height"] <= row["y"]+vh):
            misses.append({"label":action["label"],"sourceTime":action["pressTime"]})
    return {"base": [base_x, base_y, width, height], "fps": fps, "speed": speed,
            "duration": frames/fps, "frames": result, "missedControlPresses": misses,
            "maximumUpscale": max(p["zoom"]*1280/width for p in result)}


def expression(rows, key, step=8):
    samples = rows[::step]
    if samples[-1] != rows[-1]:
        samples.append(rows[-1])
    terms = [f"{samples[0][key]:.6f}"]
    for before, after in zip(samples, samples[1:]):
        delta = after[key] - before[key]
        if abs(delta) > .000001:
            terms.append(f"({delta:.6f})*clip((on-{before['frame']})/{after['frame']-before['frame']},0,1)")
    return "+".join(terms)


def moving_intervals(rows):
    active = []
    for before, after in zip(rows, rows[1:]):
        distance = abs(after["x"]-before["x"]) + abs(after["y"]-before["y"])
        if distance > 2 or abs(after["zoom"]-before["zoom"]) > .0008:
            active.append(after["frame"])
    groups = []
    for frame in active:
        if groups and frame <= groups[-1][1] + 3:
            groups[-1][1] = frame
        else:
            groups.append([frame, frame])
    return groups
