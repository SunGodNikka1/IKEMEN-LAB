-- Run from the repository root with any Lua 5.3+ interpreter (or texlua).
-- Exercises the actual shipped Lua modules against explicit API stubs; it does not validate the Go hook.
local root = arg[1] or "windows/IKEMENLab.Core/XRay/Runtime/"
local filter = os.getenv("M3_TEST_FILTER")
local passed = 0
local function test(name, fn) if filter and not name:find(filter,1,true) then return end; fn(); passed = passed + 1; print("PASS " .. name) end
local function adapter()
  _G.__ikemenlab_xray_inject = nil
  _G.__ikemenlab_xray_release = nil
  dofile(root .. "xray_inject.lua")
end
local slots, current = {}, 1
_G.player = function(n) current = n; return n == 1 or n == 2 end
_G.facing = function() return current == 1 and -1 or 1 end
_G.__xraySetVirtualInput = function(n, active, ...)
  slots[n] = { active = active, bits = {...} }
end
adapter()
test("simultaneous directions and buttons use the correct facing", function()
  assert(__ikemenlab_xray_inject(1, {"D", "F", "x", "y"}))
  local b = slots[1].bits; assert(b[2] and b[3] and not b[4] and b[8] and b[9])
end)
test("P2 uses its own facing and leaves P1 held set untouched", function()
  assert(__ikemenlab_xray_inject(2, {"F", "a"}))
  assert(slots[2].bits[4] and not slots[2].bits[3] and slots[2].bits[5])
  assert(slots[1].bits[8] and current == 1)
end)
test("empty held set clears every bit", function()
  assert(__ikemenlab_xray_inject(1, {})); assert(slots[1].active)
  for _, bit in ipairs(slots[1].bits) do assert(bit == false) end
end)
test("invalid slots and unknown keys cannot alter another held set", function()
  local p1, p2 = slots[1], slots[2]
  for _, n in ipairs({0, 3, -1, 1.5}) do assert(not __ikemenlab_xray_inject(n, {"x"})) end
  assert(not __ikemenlab_xray_inject(1, {"unknown"}))
  assert(not __ikemenlab_xray_inject(1, {42}))
  assert(slots[1] == p1 and slots[2] == p2)
end)
test("missing facing does not silently inject the wrong direction", function()
  local old = facing; facing = nil; assert(not __ikemenlab_xray_inject(1, {"F"})); facing = old
end)
test("release disables the selected override", function()
  assert(__ikemenlab_xray_release(2)); assert(not slots[2].active)
  assert(not __ikemenlab_xray_release(3))
end)
test("unpatched engine is unavailable without needing a custom adapter", function()
  local old = __xraySetVirtualInput; __xraySetVirtualInput = nil; adapter()
  assert(not __ikemenlab_xray_inject(1, {})); __xraySetVirtualInput = old; adapter()
end)
local driver = dofile(root .. "xray_driver.lua")
local plan = {route = "r", neutralFrames = 1, approachDistance = 60, tailFrames = 3,
  steps = {{edge="e", toState=200, input={{"x"}, {"x"}, {}}, timeout=2}}}
local function run(inject, observations)
  local events, d = {}, nil
  d = driver.new(plan, {inject=inject, emit=function(...) events[#events+1]={...} end})
  local reason
  for i, obs in ipairs(observations) do reason=d.tick(i,obs); if reason then break end end
  return reason, events
end
local idle = {p1={state=0,ctrl=true},p2={},distance=50}
local obs = {idle,idle,idle,idle,idle,idle,idle,idle,idle,idle}
test("adapter failure after initial success is reported, never fed", function()
  local calls=0
  local reason, events=run(function(n, keys)
    if n==2 then return true end
    calls=calls+1; return calls~=5
  end, obs)
  assert(reason=="noInject")
  local unavailable=false
  for _, e in ipairs(events) do
    if e[1]=="driver" and e[4]=="inject_unavailable" then unavailable=true end
    assert(not(e[1]=="input" and e[6][1]=="x"))
  end
  assert(unavailable)
end)
test("P2 hardware isolation failure stops the run", function()
  local reason=run(function(n) return n==1 end,obs); assert(reason=="noInject")
end)
test("missing positions skip approach but do not invent a distance", function()
  local observations={idle,idle,{p1={state=0,ctrl=true},p2={}}}
  local reason,events=run(function() return true end,observations)
  local skipped=false
  for _,e in ipairs(events) do if e[4]=="approach_skipped" then skipped=true end end
  assert(reason==nil and skipped)
end)
test("approach uses absolute distance on either side", function()
  for _,delta in ipairs({50,-50}) do
    local _,events=run(function() return true end,{idle,idle,{p1={state=0,ctrl=true},distance=delta}})
    local waiting=false
    for _,e in ipairs(events) do if e[4]=="step_wait" then waiting=true end end
    assert(waiting)
  end
end)
-- The real probe, with io/dofile mocked so no engine and no filesystem are needed.
local realDofile, realIO, realOS = dofile, io, os
local probeText
local file=assert(realIO.open(root.."xray_probe.lua")); probeText=file:read("*a");file:close()
local function probe(ax,bx)
  local lines, callback, released={},nil,{}
  _G.__ikemenlab_xray=nil; _G.hook={add=function(_,_,fn) callback=fn end}
  _G.player=function(n) current=n;return true end
  _G.stateNo=function() return 0 end
  _G.ctrl=function() return true end
  _G.life=function() return 1000 end
  _G.posX=function() return current==1 and ax or bx end
  _G.roundNo=function() return 1 end
  _G.p2distx=nil; _G.p2dist=nil
  _G.__ikemenlab_xray_release=function(n) released[n]=true;return true end
  _G.io={open=function() return {write=function(_,s)lines[#lines+1]=s end,flush=function()end} end}
  _G.os={exit=function()end}
  _G.dofile=function(path)
    if path:match("xray_config") then return {maxFrames=1,plan="plan",character="s"} end
    if path=="plan" then return {fingerprint="test-plan",route="r"} end
    if path:match("xray_driver") then return {new=function()return {tick=function()return "planComplete" end} end} end
    return true
  end
  assert(load(probeText))(); callback()
  _G.dofile,_G.io,_G.os=realDofile,realIO,realOS
  return table.concat(lines,"\n"),released
end
test("probe retains raw positions and tags signed derived delta",function()
  local lines=probe(25,-40)
  assert(lines:find('"distance":%-65') and lines:find('"distanceSource":"derived:p2.x%-p1.x"'))
  assert(lines:find('"x":25') and lines:find('"x":%-40'))
  assert(lines:find('"planFingerprint":"test%-plan"'))
  assert(lines:find('"round":1'))
end)
test("unreadable positions remain null with no derived provenance",function()
  local lines=probe(nil,nil)
  assert(lines:find('"distance":null') and lines:find('"distanceSource":null'))
end)
test("finish releases both overrides",function()
  local _,released=probe(0,50);assert(released[1] and released[2])
end)
print(string.format("%d passed, 0 failed (Lua stubs; Go engine hook not executed)",passed))
