#!/usr/bin/env python3
"""Splits a co-op session's BepInEx logs into one small folder per bug marker.

The players press F8 (or type "/bug <text>") in game; every game then writes a "[BUG #n] by <player> in <scene>
===== snapshot of this game" block to its log (see SSMP/Game/Client/BugReport.cs). For each marker this script
writes, in <out>/bug-<n>/:
  - <player>.txt: that player's snapshot, then a window of their log around the marker, without network and save
    noise, with repeated lines collapsed;
  - meta.json: number, reporter, scene, description and the time of the marker in each log.
It also writes <out>/anomalies.txt, known error patterns found in the whole logs even without a marker, and
prints a JSON summary of the new markers on stdout.

Markers already listed in the state file are skipped, since a log sent during a game comes back in the next upload.

Usage: decouper-logs.py --out <dir> --state <file> --log papa=<path> --log jojo=<path> [--before 60] [--after 30]
"""

import argparse
import json
import re
import sys
from collections import Counter, OrderedDict
from datetime import datetime, timedelta
from pathlib import Path

LINE_TIME = re.compile(r"^\[[^\]]*\] (\d{2}:\d{2}:\d{2}\.\d{3}) ")
MARKER = re.compile(r"\[BUG #(?P<n>[^\]]+)\] by (?P<by>.*?)(?:: (?P<desc>.*?))? ===== snapshot of this game")
REPORTER = re.compile(r"^(?P<name>.*?) \(id (?P<id>\d+)\) in (?P<scene>\S*)$")
SNAPSHOT_END = "===== end of snapshot"

NOISE = [
    "Steam P2P", "save update", "Save update from", "Found PlayerData", "SyncType is", "Storing player data",
    "PlayerData value changed", "Compound variable", "HashSet string var", "EncodeUtil", "MusicComponent",
    "Resolved - type", "Found audio snapshot", "for initialization", "Found initialization state",
    "pre-processing FSM",
]

# A message seen more often than this in a window is only counted, at the end of the window
MAX_REPEATS = 3

ANOMALIES = OrderedDict([
    ("exception", re.compile(r"Exception|NullReferenceException")),
    ("death refused on client", re.compile(r"Die was called on client entity$")),
    ("packet read error", re.compile(r"Could not read|Invalid packet length|malformed packet")),
    ("timeout", re.compile(r"timed out")),
    ("spawn failure", re.compile(r"NotImplementedException|Could not spawn")),
    ("warning", re.compile(r"^\[Warning")),
    ("error", re.compile(r"^\[Error")),
])


def parse_time(text):
    return datetime.strptime(text, "%H:%M:%S.%f")


def read_log(path):
    """Returns the lines with the time of each one (lines without a timestamp take the previous one)."""
    lines = []
    last = None
    with open(path, encoding="utf-8", errors="replace") as f:
        for raw in f:
            line = raw.rstrip("\r\n")
            m = LINE_TIME.match(line)
            if m:
                last = parse_time(m.group(1))
            lines.append((last, line))
    return lines


def find_markers(lines):
    markers = []
    for i, (time, line) in enumerate(lines):
        m = MARKER.search(line)
        if not m:
            continue
        end = i
        while end < len(lines) and SNAPSHOT_END not in lines[end][1]:
            end += 1
        markers.append({
            "n": m.group("n"), "by": m.group("by"), "desc": m.group("desc") or "",
            "time": time, "index": i, "snapshot": [l for _, l in lines[i:end + 1]],
        })
    return markers


def strip_prefix(line):
    """Shortens '[Info   :      SSMP] 20:01:49.255 [SSMP.Game.Client.X] msg' to '20:01:49.255 [X] msg'."""
    line = re.sub(r"^\[[^\]]*\] ", "", line)
    return re.sub(r"\[SSMP\.(?:[A-Za-z]+\.)*([A-Za-z`0-9+<>_\[\],. ]+?)\] ", r"[\1] ", line, count=1)


def message_key(line):
    """The line without its level, time and numbers, to recognize repeats of the same message."""
    return re.sub(r"\d+", "N", re.sub(r"^\[[^\]]*\] (?:[\d:.]+ )?", "", line))[:200]


def window(lines, center, before, after):
    """Lines around the marker time, without noise; each message is kept at most MAX_REPEATS times."""
    start, end = center - timedelta(seconds=before), center + timedelta(seconds=after)
    out, counts = [], Counter()
    for time, line in lines:
        if time is None or time < start or time > end:
            continue
        if any(n in line for n in NOISE) or line.startswith("  at "):
            continue
        key = message_key(line)
        counts[key] += 1
        if counts[key] <= MAX_REPEATS:
            out.append(strip_prefix(line))
    repeated = [(count, key) for key, count in counts.items() if count > MAX_REPEATS]
    if repeated:
        out.append("")
        out.append(f"## Messages seen more than {MAX_REPEATS} times in this window (only the first ones kept)")
        out += [f"{count:6d}  {key}" for count, key in sorted(repeated, reverse=True)]
    return out


def anomalies(name, lines):
    """Counts known error patterns per kind and message, with the first and last time seen."""
    found = OrderedDict()
    for time, line in lines:
        for kind, pattern in ANOMALIES.items():
            if pattern.search(line):
                msg = message_key(line)[:160]
                entry = found.setdefault((kind, msg), [0, time, time])
                entry[0] += 1
                entry[2] = time
                break
    rows = [f"== {name}"]
    for (kind, msg), (count, first, last) in sorted(found.items(), key=lambda kv: -kv[1][0]):
        span = f"{first:%H:%M:%S}-{last:%H:%M:%S}" if first else "?"
        rows.append(f"{count:6d}  {span}  {kind}: {msg}")
    return rows


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--state", required=True, type=Path)
    parser.add_argument("--log", action="append", required=True, help="<player>=<path>")
    parser.add_argument("--before", type=int, default=60)
    parser.add_argument("--after", type=int, default=30)
    args = parser.parse_args()

    logs = OrderedDict()
    for spec in args.log:
        name, _, path = spec.partition("=")
        logs[name] = read_log(path)

    state = set(json.loads(args.state.read_text())) if args.state.exists() else set()
    args.out.mkdir(parents=True, exist_ok=True)

    # Group the snapshots of the same marker across logs by number and reporter
    bugs = OrderedDict()
    for name, lines in logs.items():
        for marker in find_markers(lines):
            key = f"{marker['n']}|{marker['by']}"
            bugs.setdefault(key, {})[name] = marker

    new = []
    for key, per_log in bugs.items():
        first = next(iter(per_log.values()))
        session_key = f"{args.out.name}|{key}"
        if session_key in state:
            continue

        reporter = REPORTER.match(first["by"])
        folder = args.out / f"bug-{first['n'].replace(' ', '-')}"
        folder.mkdir(exist_ok=True)

        for name, lines in logs.items():
            marker = per_log.get(name)
            with open(folder / f"{name}.txt", "w", encoding="utf-8") as f:
                if marker is None:
                    f.write(f"No snapshot of this marker in {name}'s log (not connected, or log sent before it).\n")
                    continue
                f.write(f"# {name}: marker at {marker['time']:%H:%M:%S.%f}\n\n## Snapshot\n")
                f.write("\n".join(strip_prefix(l) for l in marker["snapshot"]) + "\n\n")
                f.write(f"## Log from {args.before}s before to {args.after}s after (noise removed)\n")
                f.write("\n".join(window(lines, marker["time"], args.before, args.after)) + "\n")

        meta = {
            "n": first["n"], "reported_by": first["by"],
            "reporter": reporter.group("name") if reporter else first["by"],
            "scene": reporter.group("scene") if reporter else "",
            "description": first["desc"],
            "times": {name: f"{m['time']:%H:%M:%S}" for name, m in per_log.items()},
            "folder": str(folder),
        }
        (folder / "meta.json").write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")
        new.append(meta)
        state.add(session_key)

    rows = []
    for name, lines in logs.items():
        rows += anomalies(name, lines) + [""]
    (args.out / "anomalies.txt").write_text("\n".join(rows), encoding="utf-8")

    args.state.parent.mkdir(parents=True, exist_ok=True)
    args.state.write_text(json.dumps(sorted(state), ensure_ascii=False, indent=2))
    json.dump({"new_bugs": new, "anomalies": str(args.out / "anomalies.txt")}, sys.stdout, ensure_ascii=False,
              indent=2)
    print()


if __name__ == "__main__":
    main()
