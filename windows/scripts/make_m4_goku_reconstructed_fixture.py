#!/usr/bin/env python3
"""
Writes the Goku regression fixture (IKEMENLab.Tests/Fixtures/m4_goku_reconstructed_plan.json, m4_goku_reconstructed_trace.jsonl).

RECONSTRUCTED FIXTURE, kept only because the verifier tests need a PLAN and the real Goku plan is unavailable (the real trace is m4_goku_real_trace.jsonl). It is RECONSTRUCTED from the frame numbers a user reported from a real
playback (input a reported at frame 290; P1 enters State 200 / Anim 200 / MoveType A at frame 291; returns to neutral at 308; the driver times out
at frame 338 saying "expected 17200, saw 0"). Everything else (life, distance, intro) is filler consistent with the Funny Valentine trace. Replace the
two files with the real ones when available; the tests assert only the reported facts.
"""
import json, os

here = os.path.dirname(os.path.abspath(__file__))
fx = os.path.join(here, "..", "IKEMENLab.Tests", "Fixtures")

edge1 = "cand:neutral>state:17200@state:-1/ctrl:114#0"
edge2 = "cand:state:17200>state:17210@state:17200/ctrl:3#0"
route = edge1 + ">" + edge2

plan = {
    "schema": "ikemenlab.xray.plan/1", "character": "char:Goku", "route": route,
    "approachDistance": 60, "neutralFrames": 20, "tailFrames": 20, "maxFrames": 1800,
    "steps": [
        {"index": 1, "edge": edge1, "kind": "Start", "from": "neutral", "to": "state:17200", "fromState": None, "toState": 17200,
         "command": "a", "contact": None, "earliestTick": None, "timeoutFrames": 45, "input": [["a"], []], "notes": []},
        {"index": 2, "edge": edge2, "kind": "Cancel", "from": "state:17200", "to": "state:17210", "fromState": 17200, "toState": 17210,
         "command": "a", "contact": "contact", "earliestTick": None, "timeoutFrames": 45, "input": [["a"], []], "notes": []},
    ],
    "warnings": [],
}
with open(os.path.join(fx, "m4_goku_reconstructed_plan.json"), "w", newline="\n") as f:
    json.dump(plan, f, indent=2)
    f.write("\n")

def player(state, ctrl, move, anim, x, hit=0):
    return {"state": state, "prevState": None, "ctrl": ctrl, "stateType": "S", "moveType": move, "anim": anim, "animElem": None, "life": 3000,
            "power": 0, "x": x, "y": 0, "velX": 0, "velY": 0, "facing": 1 if x < 0 else -1, "moveHit": hit, "moveContact": hit, "hitPause": None}

events = [
    {"type": "probe_loaded", "frame": 0, "probeVersion": "0.2-m3-audit"},
    {"type": "meta", "frame": 0, "schema": "ikemenlab.xray.trace/0", "probeVersion": "0.2-m3-audit", "engineVersion": "reconstructed-fixture", "character": "Goku",
     "platform": "windows", "capabilities": {"p1.state": True}, "hooks": ["hook:loop"], "planFingerprint": None, "engineSha256": None},
    {"type": "driver", "frame": 1, "event": "plan_start", "step": None, "detail": route},
]
last = 338
for frame in range(1, last + 1):
    if frame <= 86:
        p1, p2 = player(190, False, "I", 190, -5), player(191, False, "I", 190, 50)
    elif 291 <= frame <= 307:
        p1, p2 = player(200, False, "A", 200, -5), player(0, True, "I", 0, 50)
    else:
        p1, p2 = player(0, True, "I", 0, -5), player(0, True, "I", 0, 50)
    events.append({"type": "frame", "frame": frame, "engineTick": frame + 2, "round": 1, "p1": p1, "p2": p2, "distance": p2["x"] - p1["x"],
                   "distanceSource": "derived:p2.x-p1.x", "p1TargetCount": None, "p1TargetId": None, "combo": None})
    if frame == 87:
        events.append({"type": "state_change", "frame": 87, "player": 1, "from": 190, "to": 0})
    if frame == 270:
        events.append({"type": "driver", "frame": 270, "event": "neutral_ready", "step": None, "detail": None})
    if frame == 288:
        events.append({"type": "driver", "frame": 288, "event": "step_wait", "step": 1, "detail": edge1})
    if frame == 289:
        events.append({"type": "driver", "frame": 289, "event": "step_ready", "step": 1, "detail": edge1})
    if frame == 290:
        events.append({"type": "input", "frame": 290, "player": 1, "keys": ["a"], "step": 1, "phase": "input"})
    if frame == 291:
        events.append({"type": "input", "frame": 291, "player": 1, "keys": [], "step": 1, "phase": "input"})
        events.append({"type": "state_change", "frame": 291, "player": 1, "from": 0, "to": 200})
    if frame == 308:
        events.append({"type": "state_change", "frame": 308, "player": 1, "from": 200, "to": 0})
    if frame == last:
        events.append({"type": "driver", "frame": last, "event": "step_timeout", "step": 1, "detail": "expected state 17200, saw 0"})
        events.append({"type": "end", "frame": last, "reason": "stepTimeout"})

with open(os.path.join(fx, "m4_goku_reconstructed_trace.jsonl"), "w", newline="\n") as f:
    for e in events:
        f.write(json.dumps(e, separators=(",", ":")) + "\n")
