-- IKEMEN Lab X-Ray input adapter. Sandbox copy only.
--
-- Engine: Ikemen GO. The build has no Lua API for writing live per-player input, so the X-Ray
-- sandbox engine carries one minimal override, consulted at the top of InputReader.LocalInput:
--
--     __xraySetVirtualInput(playerNo, active, [14 booleans])
--
-- The fourteen booleans are the engine's own input layout (src/input.go):
--     1 U   2 D   3 L   4 R   5 a   6 b   7 c   8 x   9 y   10 z   11 s   12-14 unused here
--
-- The driver calls this every tick with the COMPLETE held set, so there is no edge-triggered
-- "press" to get stuck: whatever is not in `keys` is false on that tick, and an empty set is
-- all-false. The override stays active for the whole run so real hardware can never leak in and
-- make a run non-deterministic.
--
-- F/B are logical (relative to the character's facing) and are resolved here against the real
-- facing, so the plan does not have to know which way the character happens to be turned.

local hasHook = type(_G.__xraySetVirtualInput) == "function"

-- logical key -> bit index (1-based, matching the Lua vararg order above)
local BIT = { U = 1, D = 2, L = 3, R = 4, a = 5, b = 6, c = 7, x = 8, y = 9, z = 10, s = 11 }
local BUTTONS = { a = true, b = true, c = true, x = true, y = true, z = true, s = true }

local function facing(playerNo)
	if type(_G.player) ~= "function" then return nil end
	local ok, selected = pcall(_G.player, playerNo)
	if not ok or not selected then return nil end
	if type(_G.facing) == "function" then
		local ok, f = pcall(_G.facing)
		if ok and type(f) == "number" and (f == -1 or f == 1) then return f end
	end
	return nil
end

--- Apply `keys` (array of logical names) to `player` for this tick.
--- Returns true when the engine accepted them.
function _G.__ikemenlab_xray_inject(player, keys)
	if not hasHook then return false end
	if player ~= 1 and player ~= 2 then return false end

	local bits = { false, false, false, false, false, false, false, false, false, false, false, false, false, false }
	if type(keys) == "table" then
		local fwd, back = BIT.R, BIT.L
		local needsFacing = false
		for _, k in ipairs(keys) do if k == "F" or k == "B" then needsFacing = true end end
		if needsFacing then
			local f = facing(player)
			if type(_G.player) == "function" then pcall(_G.player, 1) end
			if f == nil then return false end
			if f < 0 then fwd, back = BIT.L, BIT.R end
		end

		for i = 1, #keys do
			local k = keys[i]
			if type(k) ~= "string" then return false end
			if type(k) == "string" then
				if k == "F" then
					bits[fwd] = true
				elseif k == "B" then
					bits[back] = true
				else
					local bit = BIT[k]
					-- Refuse anything we do not understand rather than dropping it silently: a plan
					-- that asked for a key this adapter cannot express must not look like it succeeded.
					if bit == nil and not BUTTONS[k] then return false end
					if bit then bits[bit] = true end
				end
			end
		end
	end

	-- Active stays true even with no keys held, so the engine reads "nothing pressed" instead of
	-- falling back to whatever the operator happens to be typing.
	_G.__xraySetVirtualInput(player, true,
		bits[1], bits[2], bits[3], bits[4], bits[5], bits[6], bits[7],
		bits[8], bits[9], bits[10], bits[11], bits[12], bits[13], bits[14])
	return true
end

--- Hand the slot back to real hardware input. Called after a plan finishes.
function _G.__ikemenlab_xray_release(player)
	if not hasHook or (player ~= 1 and player ~= 2) then return false end
	_G.__xraySetVirtualInput(player, false)
	return true
end

return hasHook

