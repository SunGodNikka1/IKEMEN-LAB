# X-Ray runtime compatibility spike

**Status: run against a real IKEMEN GO build on Windows (2026-09-30).** 3 of the 4 checks pass: the disposable sandbox launches,
the trace is valid JSONL, and the source install is proven byte-identical afterwards. The remaining check —
associating a runtime StateNo with a static State object — **fails because this engine build does not expose the probe's
state accessor**. See "Windows result" at the bottom. Until that is resolved nothing about runtime state association is a claim.

## Goal (and only this)

Launch a disposable IKEMEN → run a tiny controlled match → produce valid JSONL telemetry → parse it back → associate at least one
runtime StateNo with its static semantic State object. Then stop. The full runtime verifier (milestone 3) is out of scope.

## What exists

| Piece | Where |
|---|---|
| Lua probe (append-only JSONL, deferred registration, every API `pcall`-guarded) | `IKEMENLab.Core/XRay/Runtime/xray_probe.lua` (embedded in Core) |
| Disposable sandbox builder (one subject, one dummy, one stage, probe installed) | `RuntimeSandbox` |
| Tolerant trace reader | `TraceReader` |
| Runtime → static association | `RuntimeLink.Associate` |
| CLI | `ikemenlab xray runtime-prepare / runtime-report / runtime-clean` |
| Windows runner | `windows/scripts/Run-XRaySpike.ps1` |
| Logic check of the probe against a mock engine (needs `pip install lupa`) | `windows/scripts/xray_probe_mock.py` |

## Safety

- The sandbox is created under `%LOCALAPPDATA%\IKEMEN Lab\runtime-sandboxes\<id>` and is **refused** if it would be inside the source install.
- Only the subject, the dummy and one stage are copied; the source install and characters are read, never written (a test compares SHA-256 of every source file before and after).
- `runtime-clean` deletes only folders carrying the `.ikemenlab-runtime-sandbox` marker.
- The probe is loaded only from the sandbox copy (`external/mods/`, and a `dofile` line prepended to the **sandbox's** `main.lua`).

## Engine quirks the design accounts for (from the earlier Jev Goku experiment)

| Quirk | Handling |
|---|---|
| `-p1/-p2` command-line runs can exit before the normal `external/mods` loader | the sandbox also `dofile`s the probe from the top of its `main.lua` (`ProbeInjection.ModsAndMainLua`) |
| the `hook` table may not exist when a System script first runs | registration is deferred: a metatable watcher on `_G` registers when `hook`/`loop` are defined; also tries again immediately |
| the match loop may not exist yet | frames are emitted only once `player(1)` and `player(2)` resolve; a wrapped `loop()` is the fallback |
| rewriting a file leaves stale trailing bytes | output is append-only, one object per line, flushed per line; `TraceReader` ignores a torn last line and stale bytes |

## Trace format `ikemenlab.xray.trace/0`

One JSON object per line. `frame` is the probe's own strictly increasing counter and orders every event; `engineTick` is the engine's tick if the build exposes one.
The probe reports **raw engine facts only**; deciding that something was a hit, a combo or an anti-air is C#'s job, later.

```
{"type":"probe_loaded","frame":0,"probeVersion":"0.1-spike","hookPresent":false,"loopPresent":false}
{"type":"meta","frame":0,"schema":"ikemenlab.xray.trace/0","engineVersion":null,"capabilities":{"p1.state":true,"p1.prevState":false,...},"hooks":["hook:loop"]}
{"type":"frame","frame":126,"engineTick":null,"round":1,"p1":{"state":200,"ctrl":false,"anim":200,"life":1000,...},"p2":{...},"distance":83,"p1TargetCount":0,"combo":null}
{"type":"state_change","frame":127,"player":1,"from":200,"to":210}
{"type":"life_change","frame":132,"player":2,"from":1000,"to":920}
{"type":"end","frame":900,"reason":"maxFrames"}
```

Deviation from the sketch in the request: the probe emits `life_change` (a raw fact) rather than `hit` (an interpretation). `TraceReader` still parses `hit` events so a later probe may add them.

Fields captured per player: state, prevState, ctrl, stateType, moveType, anim, animElem, life, power, x, y, velX, velY, facing, moveHit, moveContact, hitPause.
Match-level: round, engineTick, distance, target count/id, combo count. Any field the engine build does not expose is `null`, and `meta.capabilities` records `true`/`false` for each — **nothing is faked**.

## Static ↔ runtime association

```
STATIC MODEL                    RUNTIME TRACE
State 200      ←──────────────  p1.state = 200 at frames 126…131
Anim 200       ←──────────────  p1.anim  = 200
```

`RuntimeLink.Associate` ties the subject's runtime states and animations to `state:<n>` / `anim:<n>` objects and reports numbers the index does not define. These links are kept **outside** the index's relationships and never change a confidence: `RuntimeVerified` stays unused until milestone 3.

## What the first Windows run must answer

1. Does the engine expose the trigger functions the probe tries (`stateno`, `life`, `posx`/`pos("x")`, …)? → `meta.capabilities`.
2. Which registration path worked (`hook:loop`, `wrap:loop`)? → `meta.hooks`.
3. Do `-p1 <folder> -p2 <folder> -s <stage> -p1.ai 1 -p2.ai 1 -nosound` start a match in this build? If not, pass different flags with `-ExtraArgs` and record the working command line in the result file.
4. Does `esc(true)` / `os.exit` end the run, or does the runner's timeout have to?

## Windows result (2026-09-30, first real run)

Engine: `v1.0.0-jg-policy-5 - ffa-build`, Lua 5.1, `Ikemen_GO.exe`. Subject `chars/Funny_Valentine` vs dummy `kfm` on `stages/kfm.def`.

The launcher arguments in the request work as written — no `-ExtraArgs` were needed:

```
Ikemen_GO.exe -p1 Funny_Valentine -p2 kfm -s stages/kfm.def -p1.ai 1 -p2.ai 1 -nosound
```

The engine exits cleanly (code 0) and the probe registers on `hook:loop`, so the deferred registration works. The match really
starts: frames carry `life=3000`, `anim=190`, `x=-70/+70`, `facing=±1`.

**The blocker.** The probe reads player state through bare Lua globals (`xray_probe.lua`: `{ "state", num, { function() return call("stateno") end } }`).
On this build `stateno`, `prevstateno`, `statetype`, `movetype`, `animelemno`, `velx` and `vely` are **not** exposed as globals, so
`p1.state` is reported `false` in `meta.capabilities` and written as `null` in every frame. The globals that *do* exist
(`anim`, `ctrl`, `facing`, `life`, `power`, `posx`, `posy`) are all simple value getters.

With no StateNo in the trace, `RuntimeLink.Associate` has nothing to tie to `state:<n>`, the "Subject states seen at runtime"
table is empty, and the third check cannot pass. The probe is honest about this — it records `false` rather than guessing, which
is the intended behaviour.

To unblock, the probe needs a player-scoped accessor for the fields it currently reads globally (IKEMEN exposes these through the
`player(id)` table), and `meta.capabilities` must keep reporting per-field `true`/`false` so a build that exposes neither is still
described accurately. That is a change to the probe's field readers, which is milestone 3 work and is deliberately not started here.

`RuntimeVerified` therefore stays unused, and no runtime evidence has been allowed to raise any static confidence.
