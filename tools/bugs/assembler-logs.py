#!/usr/bin/env python3
"""Rebuilds one log per player and per co-op session from the logs sent by SSMP (LogUploader).

Each upload is named "<gokapi id>_ssmp_<session>_<player>_<host|client>_<game start>_<n>.log". A player can send
several logs for one session:
  - several uploads during one game start (disconnected, then reconnected): each one holds the whole log of that game
    start so far, so only the largest one is kept;
  - several game starts (the game was restarted): BepInEx starts a new log each time, so they are put end to end, in
    order, with a separator line.
In each game start's log only the part of this session is kept: from "[Session] Joined session <id>" to the next
"[Session] Joined session" of another session, since one game start can join several sessions.

Writes <out>/<session>/<player>.log and prints a JSON summary: sessions, players, role, game starts, line counts.

Usage: assembler-logs.py --logs <downloaded logs dir> --out <dir> [--session <id>]
"""

import argparse
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

NAME = re.compile(
    r"^(?:[A-Za-z0-9]+_)?ssmp_(?P<session>\d{8}-\d{4}-[0-9a-f]+|unknown-[0-9-]+)_(?P<player>[A-Za-z0-9-]+)_"
    r"(?P<role>host|client)_(?P<start>\d{8}-\d{6})_(?P<n>\d+)\.log$"
)
JOINED = re.compile(r"\[Session\] Joined session (\S+)")


def session_part(lines, session):
    """The lines of one game start's log that belong to the given session."""
    out, inside = [], False
    for line in lines:
        m = JOINED.search(line)
        if m:
            inside = m.group(1) == session
        if inside:
            out.append(line)
    # A log without the marker (older build): keep everything
    return out if out or any(JOINED.search(l) for l in lines) else lines


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--logs", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--session")
    args = parser.parse_args()

    # session -> player -> game start -> (n, path, role)
    found = defaultdict(lambda: defaultdict(dict))
    for path in args.logs.glob("*.log"):
        m = NAME.match(path.name)
        if not m or (args.session and m.group("session") != args.session):
            continue
        starts = found[m.group("session")][m.group("player")]
        n = int(m.group("n"))
        previous = starts.get(m.group("start"))
        if previous is None or n > previous[0]:
            starts[m.group("start")] = (n, path, m.group("role"))

    summary = {}
    for session, players in sorted(found.items()):
        folder = args.out / session
        folder.mkdir(parents=True, exist_ok=True)
        summary[session] = {}
        for player, starts in sorted(players.items()):
            merged, roles = [], set()
            for start, (n, path, role) in sorted(starts.items()):
                roles.add(role)
                lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
                if merged:
                    merged.append(f"===== game restarted, log of the game started {start} =====")
                merged += session_part(lines, session)
            target = folder / f"{player}.log"
            target.write_text("\n".join(merged) + "\n", encoding="utf-8")
            summary[session][player] = {
                "role": "host" if "host" in roles else "client",
                "game_starts": sorted(starts), "lines": len(merged), "log": str(target),
            }

    json.dump(summary, sys.stdout, ensure_ascii=False, indent=2)
    print()


if __name__ == "__main__":
    main()
