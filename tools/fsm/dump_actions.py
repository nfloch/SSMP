# /// script
# requires-python = ">=3.12"
# dependencies = []
# ///
"""Print the states of an FSM dumped by find_fsm.py, or the parameters of the actions of some states.

usage: uv run dump_actions.py <fsm.json>                  states, actions and transitions
       uv run dump_actions.py <fsm.json> <state> [...]    parameters of each action of these states

Parameters stored in typed lists (FsmGameObject, FsmOwnerDefault, FsmFloat, ...) are resolved, with the name of the
FSM variable they use if any (e.g. the global "Hero"). Integers are decoded from the raw byte data; other raw params
are shown with their size only.
"""
import json
import struct
import sys

# HutongGames.PlayMaker.ParamDataType, in declaration order (PlayMaker.dll)
PARAM_TYPES = (
    "Integer Boolean Float String Color ObjectReference LayerMask Enum Vector2 Vector3 Vector4 Rect Array Character "
    "AnimationCurve FsmFloat FsmInt FsmBool FsmString FsmGameObject FsmOwnerDefault FunctionCall FsmAnimationCurve "
    "FsmEvent FsmObject FsmColor Unsupported GameObject FsmVector3 LayoutOption FsmRect FsmEventTarget FsmMaterial "
    "FsmTexture Quaternion FsmQuaternion FsmProperty FsmVector2 FsmTemplateControl FsmVar CustomClass FsmArray FsmEnum"
).split()

# Param type -> list of actionData holding its value
PARAM_LISTS = {
    "FsmGameObject": "fsmGameObjectParams",
    "FsmOwnerDefault": "fsmOwnerDefaultParams",
    "FsmFloat": "fsmFloatParams",
    "FsmInt": "fsmIntParams",
    "FsmBool": "fsmBoolParams",
    "FsmString": "fsmStringParams",
    "FsmVector2": "fsmVector2Params",
    "FsmVector3": "fsmVector3Params",
    "FsmEnum": "fsmEnumParams",
    "FsmObject": "fsmObjectParams",
    "FsmEventTarget": "fsmEventTargetParams",
    "FsmVar": "fsmVarParams",
    "String": "stringParams",
    "ObjectReference": "unityObjectParams",
}

KEPT_KEYS = ("useVariable", "name", "value", "ownerOption", "gameObject", "target")


def short(value):
    """Keeps the fields that tell which variable or object a param points to."""
    if not isinstance(value, dict):
        return value
    kept = {k: value[k] for k in KEPT_KEYS if k in value}
    if isinstance(kept.get("gameObject"), dict):
        kept["gameObject"] = short(kept["gameObject"])
    if isinstance(kept.get("value"), dict):
        kept["value"] = "<ref>"
    return kept


def print_overview(fsm) -> None:
    print("start:", fsm.get("startState"))
    for state in fsm["states"]:
        actions = [name.split(".")[-1] for name in state["actionData"]["actionNames"]]
        transitions = [(t["fsmEvent"]["name"], t["toState"]) for t in state["transitions"]]
        print(f"== {state['name']}\n   actions: {actions}\n   transitions: {transitions}")
    print("global transitions:", [(t["fsmEvent"]["name"], t["toState"]) for t in fsm.get("globalTransitions", [])])


def print_params(state) -> None:
    data = state["actionData"]
    starts = data["actionStartIndex"] + [len(data["paramName"])]
    raw = bytes(data["byteData"])

    print(f"===== {state['name']}")
    for i, action_name in enumerate(data["actionNames"]):
        enabled = "" if data["actionEnabled"][i] else " (disabled)"
        print(f"  [{i}] {action_name.split('.')[-1]}{enabled}")

        for j in range(starts[i], starts[i + 1]):
            type_id = data["paramDataType"][j]
            type_name = PARAM_TYPES[type_id] if type_id < len(PARAM_TYPES) else str(type_id)
            pos = data["paramDataPos"][j]
            size = data["paramByteDataSize"][j]
            list_name = PARAM_LISTS.get(type_name)

            if list_name and pos < len(data[list_name]):
                value = short(data[list_name][pos])
            elif type_name in ("Integer", "LayerMask", "Enum") and size == 4:
                value = struct.unpack("<i", raw[pos:pos + 4])[0]
            else:
                value = f"<{type_name}, {size} bytes>"
            print(f"      {data['paramName'][j]}: {value}")


def main() -> None:
    if len(sys.argv) < 2:
        sys.exit(__doc__)

    fsm = json.load(open(sys.argv[1]))["fsm"]
    wanted = sys.argv[2:]
    if not wanted:
        print_overview(fsm)
        return

    for state in fsm["states"]:
        if state["name"] in wanted:
            print_params(state)


if __name__ == "__main__":
    main()
