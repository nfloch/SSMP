# /// script
# requires-python = ">=3.12"
# dependencies = ["UnityPy==1.25.4"]
# ///
"""Dump the PlayMaker FSMs of the GameObjects whose name contains a given text, from a scene bundle of the
installed game.

usage: uv run find_fsm.py <scene bundle> <object name substring> [FSM name]

Each matching FSM is written as JSON to out/, to be read with dump_actions.py. Without an FSM name, all FSMs of the
matching objects are written.
"""
import json
import pathlib
import sys

import UnityPy
import UnityPy.config

# The game's bundles do not carry their Unity version. Silksong runs on Unity 6000.0.50.
UnityPy.config.FALLBACK_UNITY_VERSION = "6000.0.50f1"

OUT_DIR = pathlib.Path(__file__).resolve().parent / "out"


def main() -> None:
    if len(sys.argv) < 3:
        sys.exit(__doc__)

    bundle, needle = sys.argv[1], sys.argv[2]
    fsm_filter = sys.argv[3] if len(sys.argv) > 3 else None
    OUT_DIR.mkdir(exist_ok=True)

    dumped = 0
    for obj in UnityPy.load(bundle).objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            tree = obj.read_typetree()
        except Exception:  # noqa: BLE001 - scripts without a readable type tree
            continue

        fsm = tree.get("fsm")
        if not isinstance(fsm, dict):
            continue

        try:
            go_name = obj.read().m_GameObject.read().m_Name
        except Exception:  # noqa: BLE001
            go_name = "?"
        if needle not in go_name:
            continue

        name = fsm.get("name")
        print(f"{go_name} / FSM '{name}' / {len(fsm.get('states', []))} states")
        if fsm_filter and name != fsm_filter:
            continue

        path = OUT_DIR / f"{go_name}_{name}_{obj.path_id}.json".replace(" ", "_")
        path.write_text(json.dumps(tree, indent=1, default=str))
        print(f"  -> {path}")
        dumped += 1

    print(f"{dumped} FSM(s) written")


if __name__ == "__main__":
    main()
