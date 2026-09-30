#!/usr/bin/env python3
"""
Runs IKEMENLab.Core/XRay/Runtime/xray_probe.lua against a MOCK engine (pip install lupa) and writes the JSONL it emits.

This checks the probe's own logic: deferred hook registration, pcall-guarded API probing, null for missing APIs,
append-only output, monotonic frames, state_change / life_change facts, end + exit. It does NOT prove anything about the real
IKEMEN GO Lua API; that is what scripts/Run-XRaySpike.ps1 is for.

usage: xray_probe_mock.py <out.jsonl> [--frames N]
"""
import os, sys, json, tempfile, shutil
from lupa import LuaRuntime

here = os.path.dirname(os.path.abspath(__file__))
probe = os.path.join(here, "..", "IKEMENLab.Core", "XRay", "Runtime", "xray_probe.lua")

out_path = os.path.abspath(sys.argv[1])
frames = int(sys.argv[sys.argv.index("--frames") + 1]) if "--frames" in sys.argv else 40

work = tempfile.mkdtemp(prefix="xray-mock-")
os.makedirs(os.path.join(work, "external", "mods"))
with open(os.path.join(work, "external", "mods", "xray_config.lua"), "w") as f:
    f.write('return { trace = "%s", maxFrames = %d, character = "MockChar", hooks = { "loop" } }\n' % (out_path.replace("\\", "/"), frames))
if os.path.exists(out_path):
    os.remove(out_path)
os.chdir(work)

lua = LuaRuntime(unpack_returned_tuples=True)
setup = r'''
-- Mock engine state. P1 walks 0 -> 200 -> 210 -> 0; P2 gets hit (life drops) on a fixed tick. Some APIs are deliberately absent.
local tickCount = 0
local current = 1
local esc_called = false
_G.__mock_esc_called = function() return esc_called end

local P = {
  [1] = { state = 0, life = 1000, power = 0, x = -60, y = 0, anim = 0 },
  [2] = { state = 0, life = 1000, power = 0, x = 60, y = 0, anim = 0 },
}
function _G.__mock_advance()
  tickCount = tickCount + 1
  local p1, p2 = P[1], P[2]
  if tickCount == 5 then p1.state, p1.anim = 200, 200 end
  if tickCount == 12 then p1.state, p1.anim = 210, 210 end
  if tickCount == 14 then p2.life = 920; p2.state = 5000 end
  if tickCount == 20 then p1.state, p1.anim = 0, 0; p2.state = 0 end
  p1.x = p1.x + 1
end
function _G.__mock_in_match() return true end

_G.player = function(n) current = n; return P[n] ~= nil end
_G.stateno = function() return P[current].state end
_G.ctrl = function() return P[current].state == 0 and 1 or 0 end
_G.statetype = function() return "S" end
_G.movetype = function() return P[current].state == 200 and "A" or "I" end
_G.anim = function() return P[current].anim end
_G.life = function() return P[current].life end
_G.power = function() return P[current].power end
_G.posx = function() return P[current].x end
_G.pos = function(axis) if axis == "y" then return P[current].y end error("unsupported axis") end
_G.facing = function() return current == 1 and 1 or -1 end
_G.movehit = function() return P[current].state == 200 and 1 or 0 end
_G.roundno = function() return 1 end
_G.numtarget = function() return P[2].state == 5000 and 1 or 0 end
_G.p2dist = function(axis) return 120 end
-- deliberately missing: prevstateno, velx/vel, movecontact, hitpausetime, animelemno, combocount, tickcount...
_G.esc = function(v) esc_called = v end
'''
lua.execute(setup)

# 1) The probe loads BEFORE the hook table exists (System-script evaluation order).
with open(probe, "r", encoding="utf-8") as f:
    lua.execute(f.read())

# 2) The engine later defines the hook table; the probe must register itself then.
lua.execute('''
local callbacks = {}
_G.hook = { add = function(name, id, fn) callbacks[#callbacks + 1] = { name = name, id = id, fn = fn } end }
_G.__run_loop = function() for _, c in ipairs(callbacks) do if c.name == "loop" then c.fn() end end end
''')

run_loop = lua.globals().__run_loop
advance = lua.globals().__mock_advance
for _ in range(frames + 100):
    advance()
    run_loop()

esc_called = lua.globals().__mock_esc_called()
lines = [l for l in open(out_path, encoding="utf-8").read().split("\n") if l]
events = [json.loads(l) for l in lines]
types = {}
for e in events:
    types[e["type"]] = types.get(e["type"], 0) + 1
meta = next(e for e in events if e["type"] == "meta")

problems = []
last = -1
for e in events:
    if e["frame"] < last: problems.append("frame went backwards at %r" % e)
    last = e["frame"]
if types.get("probe_loaded") != 1: problems.append("expected one probe_loaded event")
if types.get("frame", 0) != frames: problems.append("expected %d frame events, saw %s" % (frames, types.get("frame")))
if types.get("end") != 1: problems.append("expected one end event")
if not esc_called: problems.append("probe did not ask the engine to leave the match")
if not [e for e in events if e["type"] == "state_change" and e["player"] == 1 and e["to"] == 200]:
    problems.append("missing state_change to 200")
if not [e for e in events if e["type"] == "life_change" and e["player"] == 2 and e["from"] == 1000 and e["to"] == 920]:
    problems.append("missing life_change")
if meta["capabilities"].get("p1.prevState") is not False: problems.append("prevState should be reported unsupported")
if meta["capabilities"].get("p1.state") is not True: problems.append("state should be reported supported")
first_frame = next(e for e in events if e["type"] == "frame")
if first_frame["p1"]["prevState"] is not None: problems.append("unsupported field must be null, not guessed")
if "hook:loop" not in meta["hooks"]: problems.append("hook was not registered after being defined late")

print(json.dumps({"events": len(events), "types": types, "supported": sorted(k for k, v in meta["capabilities"].items() if v),
                  "unsupported": sorted(k for k, v in meta["capabilities"].items() if not v), "hooks": meta["hooks"],
                  "problems": problems}, indent=2))
shutil.rmtree(work, ignore_errors=True)
sys.exit(1 if problems else 0)
