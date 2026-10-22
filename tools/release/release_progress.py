"""Dependency-free terminal presentation for the local publisher."""
import os
import re
import shutil
import sys
import threading
import time


def text(value):
    value = re.sub(r"\x1b\[[0-?]*[ -/]*[@-~]", "", str(value))
    return re.sub(r"[\x00-\x08\x0b-\x1f\x7f]", "", value).replace("\n", " ")


def duration(seconds):
    minutes, seconds = divmod(max(0, int(seconds)), 60)
    hours, minutes = divmod(minutes, 60)
    return f"{hours}:{minutes:02}:{seconds:02}" if hours else f"{minutes:02}:{seconds:02}"


class TerminalProgress:
    def __init__(self, stages, *, stream=None, plain=False, verbose=False, clock=time.monotonic):
        self.stream = stream if stream is not None else sys.stdout
        self.stages = tuple(stages)
        self.clock = clock
        self.started = clock()
        self.verbose = verbose
        self.live = bool(self.stream.isatty() and not plain and not verbose and os.environ.get("TERM") != "dumb")
        self.color = self.live and "NO_COLOR" not in os.environ
        self.unicode = "utf" in (getattr(self.stream, "encoding", "") or "").lower()
        self.rows = {}
        self.subtitle = "Local build and publishing"
        self.note = "Preparing your release."
        self.log_path = ""
        self.lines = 0
        self.frame = 0
        self.lock = threading.RLock()
        self.stop_event = threading.Event()
        self.thread = None

    def __enter__(self):
        if self.live:
            self.stream.write("\033[?25l")
            self.render()
            self.thread = threading.Thread(target=self.animate, daemon=True)
            self.thread.start()
        else:
            self.stream.write("ANNOTATOR / LOCAL RELEASE\n" + text(self.subtitle) + "\n")
            self.stream.flush()
        return self

    def __exit__(self, *_):
        self.stop_event.set()
        if self.thread:
            self.thread.join()
        if self.live:
            try:
                self.render()
            finally:
                self.stream.write("\033[?25h")
                self.stream.flush()

    def animate(self):
        while not self.stop_event.wait(0.12):
            self.frame += 1
            self.render()

    def message(self, message, *, detail=False):
        message = text(message)
        with self.lock:
            if not detail:
                self.note = message
            if not self.live and (not detail or self.verbose):
                self.stream.write("  " + message + "\n")
                self.stream.flush()

    def stage(self, name, state, seconds=None):
        with self.lock:
            previous = self.rows.get(name)
            started = previous[2] if previous and previous[0] == state else self.clock()
            if state == "running" and seconds is not None:
                started = self.clock() - seconds
            self.rows[name] = (state, seconds, started)
            if state == "running":
                self.note = ("Apple is processing. No percentage or queue estimate is available."
                             if name == "Wait for Apple" else name + "...")
            elif state == "paused":
                self.note = "Paused safely. Keep the saved files and rerun the same command to resume."
            elif state == "failed":
                self.note = "Stopped at this stage. Saved artifacts were kept; see the detailed log."
            if not self.live and not self.verbose and (previous is None or previous[0] != state):
                marker = {"running": "RUN", "done": "OK", "paused": "PAUSED", "failed": "FAILED"}[state]
                elapsed = f"  {duration(seconds)}" if seconds is not None else ""
                self.stream.write(f"[{marker:6}] {self.stages.index(name) + 1:02}/{len(self.stages)}  {name}{elapsed}\n")
                self.stream.flush()
            elif self.live:
                self.render()

    def render(self):
        with self.lock:
            size = shutil.get_terminal_size((80, 24))
            width = max(1, min(size.columns - 1, 88))
            completed = sum(row[0] == "done" for row in self.rows.values())
            bar_width = max(6, min(26, width - 30))
            filled = bar_width * completed // len(self.stages)
            bar = ("━" if self.unicode else "=") * filled + ("─" if self.unicode else "-") * (bar_width - filled)
            rows = [
                ("  ANNOTATOR / LOCAL RELEASE", "1;36"),
                ("  " + text(self.subtitle), "2"),
                ("", ""),
                (f"  {bar}  {completed}/{len(self.stages)} stages done · {duration(self.clock() - self.started)}"
                 if self.unicode else f"  {bar}  {completed}/{len(self.stages)} done | {duration(self.clock() - self.started)}", "36"),
                ("", ""),
            ]
            visible = self.stages
            if size.lines < len(self.stages) + 10:
                visible = tuple(name for name in self.stages if self.rows.get(name, ("",))[0] in ("running", "failed", "paused"))[-1:]
            for name in visible:
                state, seconds, started = self.rows.get(name, ("pending", None, self.started))
                icon = {"pending": "·", "done": "✓", "failed": "✗", "paused": "!"}.get(state)
                if state == "running":
                    icon = ("⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏" if self.unicode else "|/-\\")[self.frame % (10 if self.unicode else 4)]
                elif not self.unicode:
                    icon = {"pending": ".", "done": "+", "failed": "x", "paused": "!"}[state]
                elapsed = duration(self.clock() - started) if state == "running" else duration(seconds) if seconds is not None else ""
                label = f"  {icon}  {name}"
                padding = max(1, width - len(label) - len(elapsed) - 2)
                rows.append((label + " " * padding + elapsed,
                             {"pending": "2", "done": "32", "failed": "31", "paused": "33", "running": "36"}[state]))
            rows += [("", ""), ("  " + text(self.note), ""), ("  Log: " + text(self.log_path), "2")]
            if size.lines < 10:
                rows = [rows[0], rows[3], (text(self.note), "")][:max(1, size.lines - 1)]
            if self.lines:
                self.stream.write(f"\033[{self.lines}A\r\033[J")
            for line, style in rows:
                clipped = line if len(line) <= width else (line[:width - 3] + "..." if width >= 3 else "." * width)
                self.stream.write((f"\033[{style}m{clipped}\033[0m" if self.color and style else clipped) + "\n")
            self.stream.flush()
            self.lines = len(rows)
