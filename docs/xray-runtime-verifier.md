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
held set for each player. The bundled adapter works when the sandbox engine exposes `__xraySetVirtualInput`; no `--adapter`
argument is needed on that patched build. An engine without that binding produces `Inconclusive (InputInjectionUnavailable)`.
A custom adapter is optional and must support P1 and P2; the driver holds the dummy's input empty as well as controlling P1.
Every injection call must return true. Any later failure stops the run. Both overrides are released on finish.

## CLI

```
ikemenlab xray rank-subjects --root R [--limit N]
ikemenlab xray verify-plan   <character> [combo options] [--route N] [--lua]
ikemenlab xray verify-trace  <character> [combo options] [--route N] --trace t.jsonl
ikemenlab xray runtime-verify <character> --root R --dummy <folder> --stage <stages/x.def> [--route N] [--adapter a.lua] [--engine sandbox.exe] [--engine-dlls DLL_DIR] [--timeout S] [--keep]
```

Route selection reuses the `combos` options; `--route N` is the 1-based position in that result. The same options reproduce the
same plan; its SHA-256 fingerprint is embedded in the plan and trace metadata and compared by `verify-trace`.
Missing or mismatched fingerprints are Inconclusive. A fingerprint binds the declared plan; it is not authentication of arbitrary
user-edited traces or a hash of the character package. Archived traces lacking identity remain readable for inspection.

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

## M3 adversarial hardening

`Verified` now requires a nonempty, consecutively numbered plan and a matching fingerprint, one matching plan start,
contiguous unique frames without reader issues, complete state/life/round/victim-control telemetry, unchanged round and no
life reset/heal/death. Input records must use the input phase, agree with the planned held-key prefix and include the final
command keys before the target response. Later steps are locked to the exact source occurrence reached by the previous step;
a wrong first exit, late return to the source, or response outside the attempt deadline cannot repair that attempt.
Required contact is supported on that source occurrence, before its exit, after a readable flag reset or corroborating fresh
victim damage. Target-state contact and an unreset inherited contact flag do not count. This conservative rule may refuse
instant contacts where the available telemetry cannot establish freshness.

Continuity begins with the first victim hit at/after the first tested move enters, never with a later unrelated hit outside the
route. Through the final transition plus the existing three-frame maximum tail, P2 must have no sample outside hit state and
no sample with control restored, including during hitpause. A complete driver finish and end record are also required.
Custom victim states are not expanded into a new continuity model: M3 retains moveType H, or the 5000–5999 fallback only
when moveType is absent throughout. An intermittent missing moveType is Inconclusive.

`Prefer` is not a trust-any-string rule. Only a current, corroborated wait timeout for an unattempted step can explain
"never attempted": matching step_wait/edge, more than 180 wait ticks, no readiness/attempt, and no supported ready
precondition. An attempted wrong state remains WrongState even if a timeout claims otherwise. Setup timeouts are DriverTimeout.

The raw X positions remain raw fields. `distanceSource = "derived:p2.x-p1.x"` identifies the signed common-axis subtraction;
it is not an independently observed P2DistX trigger or body-edge distance. `"engine-trigger"` marks a directly read distance.
The driver compares its absolute magnitude; F/B injection uses the selected player's actual facing. A common camera translation
cancels in subtraction. Correctness still depends on the engine exposing both positions in the same coordinate units.
Missing positions leave null distance and cause a documented approach skip; approach retains its existing bounded timeout.

An explicit missing engine override is an error, never a fallback. Runtime reports record the actual sandbox executable path,
SHA-256 and source path, plus the trace hash and plan fingerprint. A stale trace copied from the install is removed before launch.
These identify evidence scope; state numbers alone do not prove which competing controller fired or the final move's eventual damage.

New Inconclusive reasons include TraceIntegrity, PlanMismatch and RoundChanged. PlayerDefeated is a conservative M3 refusal.
A route report never mutates sibling edges, controllers, abilities or other routes. Changes require new Windows runtime acceptance;
old SBA runs do not validate these tightened proof conditions. See [the audit](xray-m3-adversarial-audit.md).
