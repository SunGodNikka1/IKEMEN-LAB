-- IKEMEN Lab X-Ray input driver (milestone 3). Plays an input plan against P1 in a disposable sandbox match.
--
-- The driver is a reactive state machine. It reads raw facts the probe already samples and decides only WHEN to press; it never
-- judges whether a combo worked (IKEMEN Lab does that from the trace). It reports what it did as "input" and "driver" events.
--
-- Input injection is the one engine-specific piece and lives in the adapter, external/mods/xray_inject.lua, which must define
--     _G.__ikemenlab_xray_inject(player, keys) -> true when the keys were applied
-- keys is the COMPLETE set of logical keys P1 holds this tick: U D F B a b c x y z s  (F/B relative to facing). It is called every
-- tick while the plan runs. No adapter, or one that returns false/errors on the first call => "inject_unavailable" and the run
-- ends; the verifier then reports Inconclusive, never Verified.

local M = {}

local function same(a, b)
	if #a ~= #b then return false end
	for i = 1, #a do if a[i] ~= b[i] then return false end end
	return true
end

-- io: { emit = function(type, frame, kv...), inject = function(player, keys) -> ok }
-- Returns an object with :tick(frame, obs) -> nil | finishReason. obs = { p1 = {...}, p2 = {...}, distance = n|nil }
function M.new(plan, io)
	local d = {}
	local phase = "start"
	local stepIdx = 0
	local step = nil
	local counter = 0          -- ticks in the current phase
	local stateTicks = 0       -- ticks P1 has been in its current state
	local lastState = nil
	local held = {}
	local inputPos = 0
	local neutralRun = 0
	local injectChecked = false

	local function setKeys(frame, keys, tag)
		local ok, res = pcall(io.inject, 1, keys)
		if not injectChecked then
			injectChecked = true
			if not ok or not res then
				io.emit("driver", frame, "event", "inject_unavailable", "detail",
					ok and "the adapter returned false" or ("the adapter raised: " .. tostring(res)))
				return false
			end
		end
		if not same(keys, held) then
			held = keys
			io.emit("input", frame, "player", 1, "keys", keys, "step", stepIdx > 0 and stepIdx or nil, "phase", tag)
		end
		return true
	end

	local function note(frame, event, detail)
		io.emit("driver", frame, "event", event, "step", stepIdx > 0 and stepIdx or nil, "detail", detail)
	end

	local function contactOk(p1)
		if not step or not step.contact then return true end
		local hit, con = tonumber(p1.moveHit) or 0, tonumber(p1.moveContact) or 0
		if step.contact == "hit" then return hit > 0 end
		if step.contact == "guarded" then return con > 0 end
		return hit > 0 or con > 0
	end

	local function beginStep(frame)
		stepIdx = stepIdx + 1
		step = plan.steps[stepIdx]
		counter = 0
		inputPos = 0
		if not step then phase = "tail"; note(frame, "tail_start"); return end
		phase = "wait"
		note(frame, "step_wait", step.edge)
	end

	function d.tick(frame, obs)
		local p1 = obs.p1 or {}
		local state = p1.state
		if state ~= lastState then lastState = state; stateTicks = 0 else stateTicks = stateTicks + 1 end
		counter = counter + 1

		if phase == "start" then
			if type(io.inject) ~= "function" then
				io.emit("driver", frame, "event", "inject_unavailable", "detail", "no injector is installed")
				return "noInject"
			end
			note(frame, "plan_start", plan.route)
			phase = "neutral"
			counter = 0
			if not setKeys(frame, {}, "neutral") then return "noInject" end
			return nil
		end

		if phase == "neutral" then
			if not setKeys(frame, {}, "neutral") then return "noInject" end
			local ready = p1.ctrl == true or (p1.ctrl == nil and state == 0)
			neutralRun = ready and neutralRun + 1 or 0
			if neutralRun >= (plan.neutralFrames or 20) then phase = "approach"; counter = 0; note(frame, "neutral_ready") end
			if counter > 600 then note(frame, "timeout", "P1 never became free"); return "neutralTimeout" end
			return nil
		end

		if phase == "approach" then
			local dist = obs.distance
			if dist == nil or math.abs(dist) <= (plan.approachDistance or 60) then
				if dist == nil then note(frame, "approach_skipped", "distance is not readable") end
				if not setKeys(frame, {}, "approach") then return "noInject" end
				beginStep(frame)
				return nil
			end
			if not setKeys(frame, { "F" }, "approach") then return "noInject" end
			if counter > 400 then note(frame, "timeout", "never reached the approach distance"); return "approachTimeout" end
			return nil
		end

		if phase == "wait" then
			local inSource = step.fromState == nil and (p1.ctrl == true or (p1.ctrl == nil and state == 0)) or state == step.fromState
			local ready = inSource and contactOk(p1) and stateTicks >= (step.earliestTick or 0)
			if state == step.toState and #step.input == 0 then ready = true end
			if not setKeys(frame, {}, "wait") then return "noInject" end
			if ready then
				note(frame, "step_ready", step.edge)
				counter = 0
				if #step.input == 0 then phase = "watch" else phase = "input"; inputPos = 0 end
				return nil
			end
			if counter > 180 then note(frame, "timeout", "precondition never met for " .. step.edge); return "waitTimeout" end
			return nil
		end

		if phase == "input" then
			if state == step.toState and stateTicks > 0 then
				-- reached early, while the tail of the input is still being fed: stop pressing
				setKeys(frame, {}, "input_end")
				note(frame, "step_done", tostring(state))
				beginStep(frame)
				return nil
			end
			inputPos = inputPos + 1
			local keys = step.input[inputPos]
			if keys == nil then
				phase = "watch"; counter = 0
				setKeys(frame, {}, "input_end")
				return nil
			end
			if not setKeys(frame, keys, "input") then return "noInject" end
			return nil
		end

		if phase == "watch" then
			if not setKeys(frame, {}, "watch") then return "noInject" end
			if state == step.toState then
				note(frame, "step_done", tostring(state))
				beginStep(frame)
				return nil
			end
			if counter > (step.timeout or 45) then
				note(frame, "step_timeout", "expected state " .. tostring(step.toState) .. ", saw " .. tostring(state))
				return "stepTimeout"
			end
			return nil
		end

		if phase == "tail" then
			setKeys(frame, {}, "tail")
			if counter >= (plan.tailFrames or 20) then note(frame, "plan_complete"); return "planComplete" end
			return nil
		end
		return nil
	end

	return d
end

return M
