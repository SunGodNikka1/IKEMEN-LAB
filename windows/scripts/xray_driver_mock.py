#!/usr/bin/env python3
"""
Runs the milestone-3 input driver (xray_driver.lua, hosted by xray_probe.lua) against a MOCK engine (pip install lupa) and writes
the JSONL trace. The mock character is built from the plan itself, so this checks the DRIVER and the trace it produces (waiting,
contact gating, key sequencing, release, timeouts, the no-injector path) — not any real engine or character.

usage: xray_driver_mock.py <plan.lua> <out.jsonl> --scenario verified|dropped|nocontact|wrongstate|noinject|stuck
                                                       |ability|preview|preview_refused|preview_leaves

Phase 3 scenarios (Sequence Lab plans; the opponent is pushed back, recovers after a set time, and is only hit when within 70):
seq_true (long hitstun: a cancel keeps it a true combo), seq_wait (same, with a wait before the cancel), seq_gap (short hitstun: the follow-up
connects after the opponent recovered), seq_chase (pushback, then a chase closes the gap), seq_far (pushed far, the follow-up whiffs),
seq_recover (the opponent recovers before the chase starts), seq_jump (a jump leaves the ground).

Phase 2 scenarios: "ability" plays a one-step Play Ability plan (P2 recovers, P1 regains control); "preview" answers the driver's
force with the mock's changeState; "preview_refused" makes changeState report no such state; "preview_leaves" leaves the forced
state on its first tick.
"""
import os, sys, tempfile, shutil, json
from lupa import LuaRuntime

here = os.path.dirname(os.path.abspath(__file__))
rt = os.path.join(here, "..", "IKEMENLab.Core", "XRay", "Runtime")
plan_path = os.path.abspath(sys.argv[1])
out_path = os.path.abspath(sys.argv[2])
scenario = sys.argv[sys.argv.index("--scenario") + 1] if "--scenario" in sys.argv else "verified"
linger = int(sys.argv[sys.argv.index("--linger") + 1]) if "--linger" in sys.argv else 0

work = tempfile.mkdtemp(prefix="xray-drv-")
mods = os.path.join(work, "external", "mods")
os.makedirs(mods)
for name in ("xray_probe.lua", "xray_driver.lua"):
    shutil.copy(os.path.join(rt, name), os.path.join(mods, name))
shutil.copy(plan_path, os.path.join(mods, "xray_plan.lua"))
with open(os.path.join(mods, "xray_config.lua"), "w") as f:
    f.write('return { trace = "%s", maxFrames = 1500, character = "MockChar", hooks = { "loop" }%s, '
            'plan = "external/mods/xray_plan.lua", driver = "external/mods/xray_driver.lua", adapter = "external/mods/xray_inject.lua" }\n'
            % (out_path.replace("\\", "/"), (", lingerFrames = %d" % linger) if linger else ""))
if scenario != "noinject":
    with open(os.path.join(mods, "xray_inject.lua"), "w") as f:
        f.write('_G.__ikemenlab_xray_inject = function(player, keys) _G.__mock_held = keys; return true end\n')
else:
    open(os.path.join(mods, "xray_inject.lua"), "w").write("return true\n")
if os.path.exists(out_path):
    os.remove(out_path)
os.chdir(work)

lua = LuaRuntime(unpack_returned_tuples=True)
lua.execute('SCENARIO = "%s"; EXITED = false; os.exit = function() EXITED = true end' % scenario)
lua.execute(r'''
local plan = dofile("external/mods/xray_plan.lua")
local held = {}
_G.__mock_held = {}
local function heldSet() local s = {} for _, k in ipairs(_G.__mock_held) do s[k] = true end return s end
local function sameKeys(a, b) -- a: array, b: array
  if #a ~= #b then return false end
  local s = {} for _, k in ipairs(a) do s[k] = true end
  for _, k in ipairs(b) do if not s[k] then return false end end
  return true
end

local tickCount, current = 0, 1
local esc_called = false
local P = {
  [1] = { state = 0, ticks = 0, hit = 0, x = -100 },
  [2] = { state = 0, life = 1000, x = 0, hit = false },
}
local stepAt = 0          -- how many plan steps the mock P1 has completed
local lastHeld = {}
local hitAt = nil

local function lastKeys(step)
  for i = #step.input, 1, -1 do if #step.input[i] > 0 then return step.input[i] end end
  return {}
end

local function setState(n) P[1].state = n; P[1].ticks = 0; P[1].hit = 0 end

-- Phase 3: per-scenario opponent behaviour (hitstun length after each hit, pushback per tick and for how many ticks)
local SEQ = {
  seq_true = { recover = 30, push = 0, pushTicks = 0 },
  seq_wait = { recover = 30, push = 0, pushTicks = 0 },
  seq_gap = { recover = 6, push = 0, pushTicks = 0 },
  seq_chase = { recover = 12, push = 3, pushTicks = 15 },
  seq_far = { recover = 12, push = 5, pushTicks = 20 },
  seq_recover = { recover = 10, push = 6, pushTicks = 25 },
  seq_jump = { recover = 12, push = 0, pushTicks = 0 },
}
local seq = SEQ[SCENARIO]
P[1].air = 0
P[2].pushLeft = 0
P[2].recoverAt = nil

-- the next plan step the mock character can perform: Sequence Lab action steps (walk, wait, chase, jump) are the driver's business, not moves
local function nextMove()
  for k = stepAt + 1, #plan.steps do
    if not plan.steps[k].action then return k, plan.steps[k] end
  end
  return nil, nil
end

function _G.__mock_advance()
  tickCount = tickCount + 1
  local p1, p2 = P[1], P[2]
  p1.ticks = p1.ticks + 1
  local keys = _G.__mock_held or {}
  local hs = heldSet()
  if hs["F"] and p1.state == 0 then p1.x = p1.x + 4 end
  if seq then
    if hs["B"] and p1.state == 0 then p1.x = p1.x - 3 end
    if hs["U"] and p1.state == 0 then p1.state = 40; p1.ticks = 0; p1.air = 30 end
    if p1.air > 0 then
      p1.air = p1.air - 1
      if p1.state == 40 and p1.ticks >= 3 then p1.state = 50 end
      if p1.air == 0 then setState(0) end
    end
    if p2.pushLeft > 0 then p2.x = p2.x + seq.push; p2.pushLeft = p2.pushLeft - 1 end
    if p2.hit and p2.recoverAt and tickCount >= p2.recoverAt then p2.state = 0; p2.hit = false end
  end

  local nextIndex, nextStep = nextMove()
  if nextStep then
    local fromOk = (nextStep.fromState == nil and p1.state == 0) or p1.state == nextStep.fromState
    if #nextStep.input > 0 then
      if fromOk and #keys > 0 and sameKeys(keys, lastKeys(nextStep)) and (nextStep.contact == nil or p1.hit > 0) then
        local target = nextStep.toState
        if SCENARIO == "wrongstate" and stepAt == 1 then target = 9999 end
        setState(target); stepAt = nextIndex
      end
    elseif fromOk and p1.ticks >= 4 and not nextStep.force then  -- a forced step is only ever entered by the driver's changeState
      setState(nextStep.toState); stepAt = nextIndex
    end
  end

  -- Phase 3: every hit lands only within 70, deals 50, refreshes hitstun and starts the scenario's pushback
  if seq and p1.state ~= 0 and p1.state ~= 40 and p1.state ~= 50 and p1.hit == 0 and p1.ticks == 3 and math.abs(p2.x - p1.x) < 70 then
    p1.hit = 1
    p2.hit = true; p2.state = 5000; p2.life = p2.life - 50; hitAt = tickCount
    p2.recoverAt = tickCount + seq.recover
    p2.pushLeft = seq.pushTicks
  end
  if seq and p1.state ~= 0 and p1.state ~= 40 and p1.state ~= 50 and p1.ticks >= 20 then setState(0) end

  -- the first move connects a few ticks in, unless the scenario says it whiffs
  if not seq and p1.state ~= 0 and p1.hit == 0 and p1.ticks == 3 and SCENARIO ~= "nocontact" and (p1.x - 0) > -70 then
    p1.hit = 1
    if not p2.hit then p2.hit = true; p2.state = 5000; p2.life = p2.life - 50; hitAt = tickCount end
  end
  -- keep P2 in hitstun until the scenario lets it recover
  if SCENARIO == "dropped" and p2.hit and stepAt >= 2 and p1.ticks >= 2 and p2.state ~= 0 then
    p2.state = 0; p2.hit = false
  end
  -- Phase 2: one move, then everybody recovers (P2 after 10 ticks of hitstun, P1 back to neutral with control after 20 ticks)
  if SCENARIO == "ability" or SCENARIO == "preview" or SCENARIO == "preview_leaves" then
    if p2.hit and hitAt and tickCount - hitAt >= 10 then p2.state = 0; p2.hit = false end
    if p1.state ~= 0 and p1.ticks >= 20 then setState(0) end
  end
  if SCENARIO == "preview_leaves" and p1.state == 1000 and p1.ticks >= 1 then setState(1001) end
end

-- State Preview: the engine's changeState, redirected by player(n). Refuses an unknown state like the engine does.
_G.changeState = function(n)
  if current ~= 1 then return false end
  if SCENARIO == "preview_refused" then return false end
  setState(n); stepAt = #plan.steps
  return true
end

_G.player = function(n) current = n; return P[n] ~= nil end
_G.stateNo = function() return P[current].state end
_G.ctrl = function() if current == 1 then return P[1].state == 0 and 1 or 0 end return P[2].state == 0 and 1 or 0 end
_G.stateType = function() if current == 1 and P[1].air > 0 then return "A" end return "S" end
_G.moveType = function()
  if current == 1 then return P[1].state == 0 and "I" or "A" end
  return P[2].state ~= 0 and "H" or "I"
end
_G.life = function() return current == 1 and 1000 or P[2].life end
_G.moveHit = function() if current == 1 then return P[1].hit end return 0 end
_G.moveContact = function() if current == 1 then return P[1].hit end return 0 end
_G.roundno = function() return 1 end
_G.p2distx = function() return math.abs(P[2].x - P[1].x) end
_G.esc = function(v) esc_called = v; ESC_TICK = tickCount end
_G.__mock_esc = function() return esc_called end
''')
lua.execute(open(os.path.join(mods, "xray_probe.lua"), encoding="utf-8").read())
lua.execute('''
local callbacks = {}
_G.hook = { add = function(name, id, fn) callbacks[#callbacks + 1] = { name = name, id = id, fn = fn } end }
_G.__run_loop = function() for _, c in ipairs(callbacks) do if c.name == "loop" then c.fn() end end end
''')
advance, run_loop = lua.globals().__mock_advance, lua.globals().__run_loop
for _ in range(1700):
    advance()
    run_loop()
    if lua.globals().EXITED:
        break

events = [json.loads(l) for l in open(out_path, encoding="utf-8").read().split("\n") if l]
ends = [e for e in events if e["type"] == "end"]
if linger:
    start = next(e for e in events if e.get("event") == "linger_start")
    end_ = next(e for e in events if e.get("event") == "linger_end")
    esc_tick = lua.globals().ESC_TICK
    problems = []
    if end_["frame"] - start["frame"] != linger: problems.append("linger lasted %d ticks, wanted %d" % (end_["frame"] - start["frame"], linger))
    if esc_tick is None or esc_tick < end_["frame"]: problems.append("esc was requested before the linger ended (tick %s)" % esc_tick)
    if [e for e in events if e["type"] == "frame" and e["frame"] > start["frame"]]: problems.append("frames were sampled during the linger")
    if problems:
        print(json.dumps({"problems": problems})); sys.exit(1)
print(json.dumps({"scenario": scenario, "events": len(events), "end": ends[0]["reason"] if ends else None,
                  "driver": [e["event"] for e in events if e["type"] == "driver"]}))
shutil.rmtree(work, ignore_errors=True)
