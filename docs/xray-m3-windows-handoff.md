# Milestone 3 — Windows / runtime handoff (Space Bunny Alpha)

Main Agent side is complete and unit-tested without an engine. What only a Windows machine with the real engine can do:

1. **Write the input adapter** (`xray_inject.lua`). Read the engine's Lua input path in `src/script.go` (and how `player(n)` /
   command buffers / `inputs` are fed) of the build in use. Contract: `_G.__ikemenlab_xray_inject(player, keys)`, `keys` = the complete
   held set of logical keys `U D F B a b c x y z s` (F/B relative to facing), called every tick from the probe's `loop` hook after
   sampling. Return `true` only if the keys were applied. Do not guess API names; record which engine function you used and why.
   Check when in the tick order the injection takes effect (same tick vs next) and report it.
2. **Check determinism options** (seed / fixed RNG, single round, no intro) so two runs give the same trace. Report what the engine offers.
3. **Pick the subject**: `ikemenlab xray rank-subjects --root <install>`; choose a high scorer with a 3–4 move neutral-start route
   (cancel + special ideally super). Confirm in the CMD/CNS that the route is plausible.
4. **Run end to end**: `ikemenlab xray runtime-verify <subject> --root <install> --dummy <dummy folder> --stage <stages/x.def> --adapter <adapter.lua> --route N --keep`.
   Confirm the sandbox never touches the install, inspect the trace, and report: status, reason, failed step, and frames.
5. **Cross-check the verdict** by hand: does the trace show each transition and P2 in hitstun throughout? Try a route you expect to fail
   (e.g. a cancel into a move that needs more meter) and confirm it is `Failed` with the right reason, not `Verified`.
6. Report anything the trace fields cannot express (e.g. moveHit latency, P2 hit-state detection on your build).
