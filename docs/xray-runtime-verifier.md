# X-Ray milestone 3 — Runtime Combo Verifier

A static route (milestone 2) says "these inputs could connect these states". The verifier plays it in a **disposable** IKEMEN match and
answers, from engine facts only: did every transition occur, and did P2 stay in one continuous combo?

```
candidate route ──▶ InputPlanner ──▶ plan (JSON + Lua literals)
                                        │
            RuntimeSandbox (copy of the install, one subject, one dummy, one stage, probe + driver + adapter)
                                        │ engine launched with no AI flags
                      xray_probe.lua  ──┤ per-frame raw facts (JSONL, append-only)
                      xray_driver.lua ──┤ waits for the source state / contact, feeds keys, reports what it did
                      xray_inject.lua ──┘ the ONE engine-specific piece: apply a held key set to P1
                                        │
                      trace.jsonl ──▶ RouteVerifier ──▶ Verified | Failed(step, reason) | Inconclusive(reason)
```

Nothing is written to the real install or to the real character. The sandbox refuses to live inside the source and is deleted
afterwards (`--keep` keeps it, `runtime-clean` removes it; it only deletes folders carrying its marker file).

## What the verdict means

| Status | Meaning | Exit code |
|---|---|---|
| `Verified` | every step's transition was observed in order, each required contact (`movehit`/`movecontact`) was observed on the source move, and P2 never left a hit state between the first hit and the last step | 0 |
| `Failed` | the route was played and something did not happen; `reason` and `failedStep` say what | 4 |
| `Inconclusive` | the route could not be judged (no input injection, no telemetry, no match frames) | 5 |

Failure reasons: `PreconditionNeverMet` (the source state was never reached), `NoContact`, `TransitionNotObserved`, `WrongState`
(left the source for another state), `ComboDropped` (P2 left its hit state, with the frame and gap), `DriverTimeout`.
Inconclusive reasons: `InputInjectionUnavailable`, `TelemetryMissing`, `NoMatchFrames`.

`RuntimeVerified` exists in one place: a fully `Verified` report (`routeConfidence`) and the `runtime.*` rules cited per step.
The candidate graph, the edges and the search results keep their static confidence and never say `RuntimeVerified`.
A hit state is `moveType = H` (or StateNo 5000–5999 if the engine does not expose moveType). Zero recovery frames are tolerated.

## Input plan

`InputPlanner.Plan(graph, route)` refuses what it cannot script, with the reason: a route that does not start from neutral, a
Link/Recovery step through neutral (timing is not modelled), a step with no readable command. For each step it records:
source and target state numbers, the required contact, the earliest tick, and the per-tick held keys expanded from the CMD
(`~D, DF, F, x` → `D D · · D+F D+F · F F x x ·`; logical keys `U D F B a b c x y z s`, F/B relative to facing). Unmodelled
conditions on an edge become plan warnings: the script may not satisfy them, and the verdict will say so.

The driver is **reactive**, not a timeline: it waits for the source state, the contact and the earliest tick, then presses, then
expects the target state within a timeout. A missed window therefore becomes a named failure, not a silent drift.

## The adapter contract (what is engine-specific)

`external/mods/xray_inject.lua` must define `_G.__ikemenlab_xray_inject(player, keys) -> true`, called every tick with the complete
held set for P1. The shipped template injects nothing, so a run without an adapter ends `Inconclusive (InputInjectionUnavailable)`.
The adapter is written against the engine source of the build in use; it is deliberately **not** guessed here.

## CLI

```
ikemenlab xray rank-subjects --root R [--limit N]
ikemenlab xray verify-plan   <character> [combo options] [--route N] [--lua]
ikemenlab xray verify-trace  <character> [combo options] [--route N] --trace t.jsonl
ikemenlab xray runtime-verify <character> --root R --dummy <folder> --stage <stages/x.def> [--route N] [--adapter a.lua] [--timeout S] [--keep]
```

Route selection reuses the `combos` options; `--route N` is the 1-based position in that result. The same options reproduce the
same plan, so `verify-trace` judges a recorded trace against exactly the plan that produced it.

## Choosing the first subject

Funny Valentine is a poor first proof (AI-driven dynamic redirects: `enemynear(var(59))`, `numhelper`, `!ailevel`). `rank-subjects`
scores every installed character by clean edges (StaticProven, no unmodelled condition), clean hit-confirm cancels, scriptable
neutral-start routes of clean edges, literal command starts, dynamic targets and AI-gated controllers, and prints the best route
for each with its reasons and concerns. Take a high scorer with a 3–4 move route that includes a cancel and, ideally, a super.

## Testing without an engine

`windows/scripts/xray_driver_mock.py <plan.lua> <out.jsonl> --scenario verified|dropped|nocontact|wrongstate|noinject` runs the real
`xray_probe.lua` + `xray_driver.lua` against a mock engine (`pip install lupa`); the committed traces in
`IKEMENLab.Tests/Fixtures/xray_driver_mock_*.jsonl` are its output and `XRayVerifyTests` judges them. This proves the driver and the
verifier agree; it proves nothing about a real engine — that is the Windows acceptance run.

## Limits

- One route, one P2 dummy that receives no input; blocking, pushback, juggle limits and random hit effects are not modelled.
- Neutral-start routes only; links and timing-window search are milestone 4.
- Time-based and unmodelled gate conditions may make a plausible route `Failed`; the report names the step, not the cause inside the gate.
