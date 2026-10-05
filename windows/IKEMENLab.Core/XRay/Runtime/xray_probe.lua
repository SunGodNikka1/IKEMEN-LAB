-- IKEMEN Lab X-Ray runtime probe (compatibility spike, schema ikemenlab.xray.trace/0).
--
-- Reports raw engine facts and explicitly tagged derived distance as append-only JSON lines. It never decides what a fact *means* (no "combo", no "anti-air");
-- IKEMEN Lab interprets the trace afterwards. Only ever loaded into a disposable sandbox copy of an IKEMEN install.
--
-- STATUS: written against what IKEMEN GO's Lua API is believed to expose, NOT yet verified on a real engine build. Every engine
-- call is wrapped in pcall and the result of each attempt is recorded in the "capabilities" map of the meta event, so the first
-- run documents what this build really supports. A field the build does not expose is written as null, never guessed.
--
-- Known quirks this file is written around:
--   * a `-p1/-p2` command-line run can exit before the normal external/mods loader, so lib/main.lua may also dofile() this file
--   * the `hook` table may not exist yet when this file first runs (registration is deferred and retried)
--   * rewriting a file leaves stale trailing bytes, so output is append-only, one JSON object per line, flushed per line

if rawget(_G, "__ikemenlab_xray") then return end
_G.__ikemenlab_xray = true

local PROBE_VERSION = "0.5-phase6-director"
local cfg = { trace = "xray_trace.jsonl", maxFrames = 900, hooks = { "loop" }, character = "" }
do
	local ok, c = pcall(dofile, "external/mods/xray_config.lua")
	if ok and type(c) == "table" then
		for k, v in pairs(c) do cfg[k] = v end
	end
end

-- ---------------------------------------------------------------- JSON (ordered objects, explicit null)

local NULL = {}

local function O(...)
	local o = { __object = true }
	local n = select("#", ...)
	for i = 1, n, 2 do o[#o + 1] = { (select(i, ...)), (select(i + 1, ...)) } end
	return o
end

local function esc(s)
	return (tostring(s):gsub('[%c"\\]', function(c)
		local m = { ['"'] = '\\"', ["\\"] = "\\\\", ["\n"] = "\\n", ["\r"] = "\\r", ["\t"] = "\\t" }
		return m[c] or string.format("\\u%04x", string.byte(c))
	end))
end

local function enc(v)
	local t = type(v)
	if v == nil or v == NULL then return "null" end
	if t == "boolean" then return v and "true" or "false" end
	if t == "number" then
		if v ~= v or v == math.huge or v == -math.huge then return "null" end
		if v == math.floor(v) and math.abs(v) < 9007199254740992 then return string.format("%d", v) end
		return string.format("%.10g", v)
	end
	if t == "string" then return '"' .. esc(v) .. '"' end
	if t == "table" then
		if v.__object then
			local parts = {}
			for _, kv in ipairs(v) do parts[#parts + 1] = '"' .. esc(kv[1]) .. '":' .. enc(kv[2]) end
			return "{" .. table.concat(parts, ",") .. "}"
		end
		local parts = {}
		for _, x in ipairs(v) do parts[#parts + 1] = enc(x) end
		return "[" .. table.concat(parts, ",") .. "]"
	end
	return "null"
end

local out = nil
local function emit(obj)
	if not out then out = io.open(cfg.trace, "a") end
	if not out then return false end
	out:write(enc(obj), "\n")
	out:flush()
	return true
end

-- ---------------------------------------------------------------- input driver (milestone 3, only when cfg.plan is set)

local driver = nil
local driverError = nil
local planFingerprint = nil
local function loadDriver(emitFn, OFn)
	if not cfg.plan then return end
	local ok, plan = pcall(dofile, cfg.plan)
	if not ok or type(plan) ~= "table" then driverError = "plan: " .. tostring(plan); return end
	planFingerprint = plan.fingerprint
	local okD, mod = pcall(dofile, cfg.driver or "external/mods/xray_driver.lua")
	if not okD or type(mod) ~= "table" then driverError = "driver: " .. tostring(mod); return end
	pcall(dofile, cfg.adapter or "external/mods/xray_inject.lua")
	local inject = rawget(_G, "__ikemenlab_xray_inject")
	driver = mod.new(plan, {
		inject = type(inject) == "function" and inject or nil,
		emit = function(kind, frame, ...) emitFn(OFn("type", kind, "frame", frame, ...)) end,
		-- State Preview only: the engine's own changeState, redirected to the player with player(n). True only when the engine reports that
		-- an existing state was entered. Never used by a route plan.
		force = function(playerNo, stateNo)
			local sel, change = rawget(_G, "player"), rawget(_G, "changeState")
			if type(sel) ~= "function" or type(change) ~= "function" then return false end
			local okSel, selected = pcall(sel, playerNo)
			if not okSel or not selected then return false end
			local ok, entered = pcall(change, stateNo)
			pcall(sel, 1)
			return ok and entered == true
		end,
		-- Teach AI fixture only: give the player's input slot back and hand the player to the engine's AI at `level` (setAILevel, redirected with
		-- player(n)). From then on the character's own AI — including any generated Director behavior — decides; nothing is injected for it.
		setAI = function(playerNo, level)
			local sel, set = rawget(_G, "player"), rawget(_G, "setAILevel")
			if type(sel) ~= "function" or type(set) ~= "function" then return false end
			local release = rawget(_G, "__ikemenlab_xray_release")
			if type(release) == "function" then pcall(release, playerNo) end
			local okSel, selected = pcall(sel, playerNo)
			if not okSel or not selected then return false end
			local ok = pcall(set, level)
			pcall(sel, 1)
			return ok
		end,
	})
end

-- ---------------------------------------------------------------- engine access (all guarded)

local function G(name)
	local f = rawget(_G, name)
	if type(f) == "function" then return f end
	return nil
end

local function call(name, ...)
	local f = G(name)
	if not f then return nil end
	local ok, v = pcall(f, ...)
	if ok then return v end
	return nil
end

local function num(v)
	if type(v) == "number" then return v end
	if type(v) == "boolean" then return v and 1 or 0 end
	return nil
end

local function str(v)
	if v == nil then return nil end
	return tostring(v)
end

local function bool(v)
	if type(v) == "boolean" then return v end
	if type(v) == "number" then return v ~= 0 end
	return nil
end

-- Each field lists alternative ways to read it; the first that yields a value is remembered and reported in capabilities.
-- Spellings are tried in order. The camelCase names first are the ones IKEMEN actually binds to Lua
-- (script.go registers e.g. "stateNo", "stateType", "velX" — not the MUGEN System-script "stateno").
-- The MUGEN spellings stay as fallbacks so the probe still works against a build that exposes those.
-- All of these read the context that withPlayer() set with player(n), so they are per-player.
local FIELDS = {
	{ "state", num, { function() return call("stateNo") end, function() return call("stateno") end } },
	{ "prevState", num, { function() return call("prevStateNo") end, function() return call("prevstateno") end } },
	{ "ctrl", bool, { function() return call("ctrl") end } },
	{ "stateType", str, { function() return call("stateType") end, function() return call("statetype") end } },
	{ "moveType", str, { function() return call("moveType") end, function() return call("movetype") end } },
	{ "anim", num, { function() return call("anim") end } },
	{ "animElem", num, { function() return call("animElemNo") end, function() return call("animelemno", 0) end, function() return call("animelem") end } },
	{ "life", num, { function() return call("life") end } },
	{ "power", num, { function() return call("power") end } },
	{ "x", num, { function() return call("posX") end, function() return call("posx") end, function() return call("pos", "x") end } },
	{ "y", num, { function() return call("posY") end, function() return call("posy") end, function() return call("pos", "y") end } },
	{ "velX", num, { function() return call("velX") end, function() return call("velx") end, function() return call("vel", "x") end } },
	{ "velY", num, { function() return call("velY") end, function() return call("vely") end, function() return call("vel", "y") end } },
	{ "facing", num, { function() return call("facing") end } },
	{ "moveHit", num, { function() return call("moveHit") end, function() return call("movehit") end } },
	{ "moveContact", num, { function() return call("moveContact") end, function() return call("movecontact") end } },
	{ "hitPause", num, { function() return call("hitpausetime") end } },
	-- Phase 5 (behavior recognition): the engine's own answers, so "falling", "cornered" and "AI-controlled" are read, not guessed.
	{ "aiLevel", num, { function() return call("aiLevel") end, function() return call("ailevel") end } },
	{ "hitFall", bool, { function() return call("hitFall") end, function() return call("hitfall") end } },
	{ "backEdgeBodyDist", num, { function() return call("backEdgeBodyDist") end, function() return call("backedgebodydist") end } },
	{ "frontEdgeBodyDist", num, { function() return call("frontEdgeBodyDist") end, function() return call("frontedgebodydist") end } },
	{ "numProj", num, { function() return call("numProj") end, function() return call("numproj") end } },
	-- Phase 6: the width of the player's own localcoord (320, 640, 1280 …). Every position, velocity and edge distance above is in these units.
	{ "localCoord", num, { function() return call("localCoordX") end } },
}

local MATCH_FIELDS = {
	{ "round", num, { function() return call("roundNo") end, function() return call("roundno") end } },
	{ "engineTick", num, { function() return call("gameTime") end, function() return call("tickcount") end, function() return call("gametick") end, function() return call("gametime") end } },
	-- The engine's own P2Dist X read from P1: facing-relative, in P1's localcoord units, and correct across characters of different localcoords
	-- (IKEMEN converts through world units). "p2distx" was the old guess at the binding's name; IKEMEN registers it as "p2DistX".
	{ "distance", num, { function() return call("p2DistX") end, function() return call("p2distx") end, function() return call("p2dist", "x") end } },
	-- The camera's x in world units: Lua's posX is pos − camera (the camera is NOT scaled by the player's localcoord).
	{ "cameraX", num, { function() return call("cameraPosX") end } },
	{ "p1TargetCount", num, { function() return call("numtarget") end } },
	{ "p1TargetId", num, { function() return call("targetid") end } },
}

-- The AI Director's intention register (generated code writes these; see DirectorCompiler.Maps). "xrd_beh" is recorded as "beh", and so on.
local DIRECTOR_MAPS = { "xrd_beh", "xrd_why", "xrd_next", "xrd_dist", "xrd_tick", "xrd_down", "xrd_roll" }

local capabilities = {}
local chosen = {}

local function read(prefix, field, ctxFn)
	local key = prefix .. field[1]
	local converter, alternatives = field[2], field[3]
	local start = chosen[key] or 1
	for i = start, #alternatives do
		local v = converter(alternatives[i]())
		if v ~= nil then
			chosen[key] = i
			capabilities[key] = true
			return v
		end
	end
	if capabilities[key] == nil then capabilities[key] = false end
	return nil
end

local function withPlayer(n, fn)
	local p = G("player")
	if not p then return nil, false end
	local ok, r = pcall(p, n)
	if not ok or not r then return nil, false end
	local v = fn()
	pcall(p, 1)
	return v, true
end

local function snapshot(n)
	local s, ok = withPlayer(n, function()
		local o = { __object = true }
		for _, f in ipairs(FIELDS) do
			local v = read("p" .. n .. ".", f)
			o[#o + 1] = { f[1], v == nil and NULL or v }
		end
		return o
	end)
	return s, ok
end

-- ---------------------------------------------------------------- sampling

local tick = 0
local activeFrames = 0
local metaWritten = false
local finished = false
local exitCountdown = nil
local hooksRegistered = {}
local previous = { [1] = {}, [2] = {} }

local function value(o, key)
	for _, kv in ipairs(o) do if kv[1] == key then return kv[2] end end
	return nil
end

local function engineVersion()
	for _, name in ipairs({ "GAMEVERSION", "gameversion", "VERSION", "version" }) do
		local v = rawget(_G, name)
		if type(v) == "string" then return v end
		if type(v) == "function" then
			local ok, r = pcall(v)
			if ok and type(r) == "string" then return r end
		end
	end
	local cfgTable = rawget(_G, "config")
	if type(cfgTable) == "table" and type(cfgTable.Version) == "string" then return cfgTable.Version end
	return nil
end

local function writeMeta()
	if metaWritten then return end
	metaWritten = true
	local caps = { __object = true }
	local keys = {}
	for k in pairs(capabilities) do keys[#keys + 1] = k end
	table.sort(keys)
	for _, k in ipairs(keys) do caps[#caps + 1] = { k, capabilities[k] } end
	local hooks = {}
	for name in pairs(hooksRegistered) do hooks[#hooks + 1] = name end
	table.sort(hooks)
	emit(O("type", "meta", "frame", 0, "schema", "ikemenlab.xray.trace/0", "probeVersion", PROBE_VERSION,
		"engineVersion", engineVersion() or NULL, "luaVersion", _VERSION or NULL, "character", cfg.character,
		"platform", (package and package.config and package.config:sub(1, 1) == "\\") and "windows" or "other",
		"capabilities", caps, "hooks", hooks, "planFingerprint", planFingerprint or NULL,
        -- Host-supplied launch provenance, not engine trigger fields.
        "engineSha256", cfg.engineSha256 or NULL, "engineExecutable", cfg.engineExecutable or NULL, "engineSource", cfg.engineSource or NULL))
end

-- Linger (watched playback). With cfg.lingerFrames unset the old behaviour stands: ask the engine to leave the match at once.
-- With it set, the match is HELD running for that many ticks after the plan ends (no input is fed: the held keys were released),
-- and only then is esc requested, so the final position stays on screen. Both ends are recorded as driver events with the engine tick
-- (when readable) and the probe tick count. These are EXECUTION evidence only (how many ticks the match advanced). They say nothing about
-- how long a person saw the screen: os.clock() is CPU time, not elapsed time, and is deliberately not recorded. Measure that on the
-- machine with a wall clock. During the hold the match keeps advancing with no input; it is not frozen on the final pose.
local lingerLeft = nil
local lingerTotal = 0

local function lingerStamp()
	local t = call("tickcount") or call("gametick") or call("gametime")
	return string.format("probeTick=%d engineTick=%s", tick, t ~= nil and tostring(t) or "nil")
end

local function leaveMatch()
	local esc_fn = G("esc")
	if esc_fn then pcall(esc_fn, true) end
end

local function finish(reason)
	if finished then return end
	local release = rawget(_G, "__ikemenlab_xray_release")
	if type(release) == "function" then pcall(release, 1); pcall(release, 2) end
	finished = true
	emit(O("type", "end", "frame", tick, "reason", reason))
	local hold = tonumber(cfg.lingerFrames)
	if hold and hold > 0 then
		lingerLeft, lingerTotal = hold, hold
		emit(O("type", "driver", "frame", tick, "event", "linger_start", "detail", "holding the match " .. hold .. " ticks; " .. lingerStamp()))
	else
		leaveMatch()
		exitCountdown = 60
	end
end

local function sample()
	tick = tick + 1
	if finished then
		if lingerLeft then
			lingerLeft = lingerLeft - 1
			if lingerLeft <= 0 then
				lingerLeft = nil
				emit(O("type", "driver", "frame", tick, "event", "linger_end", "detail", "held " .. lingerTotal .. " ticks; " .. lingerStamp()))
				leaveMatch()
				exitCountdown = 30
			end
			return
		end
		if exitCountdown then
			exitCountdown = exitCountdown - 1
			if exitCountdown <= 0 and os and os.exit then pcall(os.exit, 0) end
		end
		return
	end

	local p1, ok1 = snapshot(1)
	local p2, ok2 = snapshot(2)
	if not (ok1 and ok2) then return end -- not in a match yet (menus, loading)
	if value(p1, "state") == nil and value(p1, "life") == nil then return end

	activeFrames = activeFrames + 1
	local frame = O("type", "frame", "frame", tick)
	local extra = {}
	for _, f in ipairs(MATCH_FIELDS) do
		local v = withPlayer(1, function() return read("match.", f) end)
		extra[f[1]] = v
	end
	frame[#frame + 1] = { "engineTick", extra.engineTick == nil and NULL or extra.engineTick }
	frame[#frame + 1] = { "round", extra.round == nil and NULL or extra.round }
	frame[#frame + 1] = { "p1", p1 }
	frame[#frame + 1] = { "p2", p2 }
	-- ONE distance interpretation (0.5): the horizontal centre-to-centre distance from P1 to P2 along the world axis (positive when P2 is to the right),
	-- in the engine's world units — the 320-wide scale every distance in X-Ray is written in (approach, chase, behavior ranges, Teach AI), whatever
	-- either character's localcoord. Sources, best first:
	--   engine:p2DistX     the engine's P2Dist X (P1's units, facing-relative) × P1 facing × 320 / P1 localcoord
	--   derived:world      both positions back in world units: (posX + camera) × 320 / localcoord, per player
	--   derived:p2.x-p1.x  raw positions (the pre-0.5 reading; wrong when the localcoords differ) — only when nothing else is readable
	-- The driver uses only its magnitude (approach and chase thresholds).
	local ax, bx = value(p1, "x"), value(p2, "x")
	local lc1, lc2, f1 = value(p1, "localCoord"), value(p2, "localCoord"), value(p1, "facing")
	local distance, distanceSource = nil, nil
	if type(extra.distance) == "number" and type(lc1) == "number" and lc1 > 0 and type(f1) == "number" and f1 ~= 0 then
		distance, distanceSource = extra.distance * f1 * 320 / lc1, "engine:p2DistX"
	elseif type(ax) == "number" and type(bx) == "number" and type(extra.cameraX) == "number" and type(lc1) == "number" and lc1 > 0
		and type(lc2) == "number" and lc2 > 0 then
		distance, distanceSource = (bx + extra.cameraX) * 320 / lc2 - (ax + extra.cameraX) * 320 / lc1, "derived:world"
	elseif type(ax) == "number" and type(bx) == "number" then
		distance, distanceSource = bx - ax, "derived:p2.x-p1.x"
	end
	extra.distance = distance
	frame[#frame + 1] = { "distance", extra.distance == nil and NULL or extra.distance }
	frame[#frame + 1] = { "distanceSource", distanceSource or NULL }
	frame[#frame + 1] = { "p1TargetCount", extra.p1TargetCount == nil and NULL or extra.p1TargetCount }
	frame[#frame + 1] = { "p1TargetId", extra.p1TargetId == nil and NULL or extra.p1TargetId }
	frame[#frame + 1] = { "combo", extra.combo == nil and NULL or extra.combo }
	-- Phase 6 (AI Director): the generated behavior's intention register — the maps its generated code writes with MapSet — read for P1 every frame,
	-- only when the run tests a Director build. These are what the generated code PUBLISHED; IKEMEN Lab checks them against what executed.
	if cfg.director then
		local reg = withPlayer(1, function()
			local o = { __object = true }
			for _, name in ipairs(DIRECTOR_MAPS) do
				local v = num(call("map", name))
				o[#o + 1] = { name:sub(5), v == nil and NULL or v }
			end
			return o
		end)
		frame[#frame + 1] = { "dir", reg or NULL }
	end

	writeMeta()
	emit(frame)

	if driver then
		local obs = { distance = extra.distance, p1 = {}, p2 = {} }
		for _, f in ipairs(FIELDS) do
			local a, b = value(p1, f[1]), value(p2, f[1])
			if a ~= NULL then obs.p1[f[1]] = a end
			if b ~= NULL then obs.p2[f[1]] = b end
		end
		local okD, reason = pcall(driver.tick, tick, obs)
		if not okD then
			emit(O("type", "driver", "frame", tick, "event", "driver_error", "detail", tostring(reason)))
			driver = nil
			finish("driverError")
		elseif reason then
			finish(reason)
			return
		end
	end

	for n, snap in pairs({ [1] = p1, [2] = p2 }) do
		local state, life = value(snap, "state"), value(snap, "life")
		local prev = previous[n]
		if prev.state ~= nil and state ~= nil and prev.state ~= state then
			emit(O("type", "state_change", "frame", tick, "player", n, "from", prev.state, "to", state))
		end
		if prev.life ~= nil and life ~= nil and prev.life ~= life then
			emit(O("type", "life_change", "frame", tick, "player", n, "from", prev.life, "to", life))
		end
		prev.state, prev.life = state, life
	end

	if activeFrames >= cfg.maxFrames then finish("maxFrames") end
end

-- ---------------------------------------------------------------- registration (deferred, retried, recorded)

local function tryRegister()
	local h = rawget(_G, "hook")
	if type(h) == "table" and type(h.add) == "function" then
		for _, name in ipairs(cfg.hooks) do
			if not hooksRegistered["hook:" .. name] then
				local ok = pcall(h.add, name, "ikemenlab_xray", sample)
				if ok then hooksRegistered["hook:" .. name] = true end
			end
		end
	end

	-- Fallback: wrap the global loop() the engine calls each frame, if it exists and no hook took.
	local lp = rawget(_G, "loop")
	if type(lp) == "function" and not hooksRegistered["wrap:loop"] and not next(hooksRegistered) then
		rawset(_G, "loop", function(...)
			pcall(sample)
			return lp(...)
		end)
		hooksRegistered["wrap:loop"] = true
	end
end

local function watchGlobal(name, callback)
	local mt = getmetatable(_G)
	if mt and mt.__ikemenlab_watch then
		mt.__ikemenlab_watch[name] = callback
		return
	end
	local watch = { [name] = callback }
	local new = {}
	if mt then for k, v in pairs(mt) do new[k] = v end end
	local previousNewIndex = new.__newindex
	new.__ikemenlab_watch = watch
	new.__newindex = function(t, k, v)
		if type(previousNewIndex) == "function" then previousNewIndex(t, k, v)
		elseif type(previousNewIndex) == "table" then rawset(previousNewIndex, k, v)
		else rawset(t, k, v) end
		local cb = watch[k]
		if cb then pcall(cb) end
	end
	pcall(setmetatable, _G, new)
end

loadDriver(emit, O)
if cfg.plan and not driver then
	emit(O("type", "driver", "frame", 0, "event", "driver_load_failed", "detail", driverError or "unknown"))
end

emit(O("type", "probe_loaded", "frame", 0, "probeVersion", PROBE_VERSION, "trace", cfg.trace,
	"hookPresent", type(rawget(_G, "hook")) == "table", "loopPresent", type(rawget(_G, "loop")) == "function"))

tryRegister()
if not rawget(_G, "hook") then watchGlobal("hook", tryRegister) end
if not rawget(_G, "loop") then watchGlobal("loop", tryRegister) end
