# Independent M3 runtime correctness audit

**Acceptance assessment: NOT READY for the Main Agent's final acceptance.** Concrete verifier and Lua defects were found and corrected on the local `codex/m3-adversarial-audit` branch. The Lua regressions execute successfully. The C# changes are uncompiled/unexecuted in this session, the pinned Go hook source is unavailable, and the tightened proof needs a new Windows acceptance run.

Reviewed LAB baseline: `b7eeb2ade92870ae480a41a5987548e93e2cfc12`. Requested Go-hook baseline: `f51ac49`, **not inspected**. This cloud workspace began without the repositories. LAB was retrieved and checked out at the supplied commit; no accessible repository contained the supplied Go commit. No engine source or executable was modified.

SBA's reported evidence is external evidence, not reproduced here: bangirasu route 0, 494 frames, four transitions, contacts at 417/439/465, continuity 417–476, Verified; route 1 Verified; unpatched engine Inconclusive/InputInjectionUnavailable; Windows suite 582 passed. I did not obtain those raw Windows traces or execute IKEMEN. Their successful verdicts do not establish the verifier's soundness.

## Files and path inspected

| Area | Files |
|---|---|
| Candidate creation and scope | `windows/IKEMENLab.Core/XRay/Combo/CandidateGraph.cs`, `ComboModels.cs`, `ComboSearch.cs`; `Model/EvidenceRules.cs` |
| Candidate → plan | `windows/IKEMENLab.Core/XRay/Verify/InputPlan.cs` |
| Plan → sandbox and process | `Runtime/RuntimeSandbox.cs`, `Verify/VerifyRun.cs`; `windows/IKEMENLab.Cli/VerifyCommands.cs`, `RuntimeCommands.cs` |
| Input and samples | `Runtime/xray_driver.lua`, `xray_inject.lua`, `xray_probe.lua` |
| JSONL → interpretation | `Runtime/TraceModels.cs`, `TraceReader.cs`, `RuntimeLink.cs`; `Verify/RouteVerifier.cs` |
| Existing regressions and mocks | `windows/IKEMENLab.Tests/XRayVerifyTests.cs`, `XRayRuntimeTests.cs`, `XRayProbeTests.cs`; driver mock JSONL fixtures; `windows/scripts/xray_driver_mock.py`, `xray_probe_mock.py` |
| Documentation | `docs/xray-runtime-verifier.md`, `xray-runtime-spike.md`, `xray-runtime-spike-result.md`, `xray-m3-windows-handoff.md`, `xray-milestone-3-blocker.md`, `xray-evidence-rules.md`, `xray.md` |
| New regressions | `windows/IKEMENLab.Tests/XRayAdversarialTests.cs`, `windows/scripts/xray_m3_adversarial.lua` |

The executable path ends in the sandbox's `Ikemen_GO.exe` through `ProcessStartInfo`, with no shell. The Go `InputReader.LocalInput` implementation is the missing link in the requested complete audit. The adapter's comments describe a 14-boolean override, but comments are not independent proof of Go storage isolation, bounds checking, inactive behavior or disable behavior.

## Necessary and sufficient conditions at the reviewed baseline

For a normal nonempty plan, the baseline produces RuntimeVerified exactly when `RouteVerifier.Verify` returns `VerifyStatus.Verified`. `VerificationReport.RouteConfidence` performs that mapping. In the baseline implementation this means:

1. No parsed `inject_unavailable` driver event; some match frames; at least one readable P1 state and at least one readable P2 state or moveType somewhere in the log.
2. Every step with a nonempty input plan has at least one nonempty P1 input event attributed to that step. Keys and phase need not match the plan, and injection success after the first adapter call is not checked by the driver.
3. For each step, scanning forward from the cursor eventually finds adjacent **remaining samples** whose P1 state changes into the target, with the previous state equal to the source when specified. The transition frame may equal the input frame. Wrong exits are remembered but scanning continues; no attempt deadline binds this search. The cursor is the found transition's index.
4. When contact is required, a positive moveHit/moveContact is found by scanning backward across that source-state span **including the target's first sample**. Neither flag reset nor a fresh hit is required.
5. Continuity finds a victim hit state at/after the selected start and no sampled non-hit state through final transition plus `min(TailFrames, 3)`. moveType H/Hit counts; otherwise the 5000–5999 fallback counts. Ctrl, round, life, missing frames, reader issues and endpoint coverage do not participate. The search for the start can find a hit **after the interval ends**, leaving an empty interval which passes.
6. No step has been marked failed. Driver completion, end records, plan identity, executable identity and controller attribution are not proof conditions.

These are code-derived conditions; the new C# adversarial traces have not been run here against the baseline assembly.

## Defects and false-positive paths

| Finding | Consequence | Local correction |
|---|---|---|
| Source → wrong → source → intended target can satisfy the original step later | Unrelated recovery/re-entry repairs a failed attempt | Lock later steps to the preceding transition's exact source occurrence; reject its first wrong exit; bound responses to the attempt |
| Any nonempty attributed input qualifies, including wrong phase/keys; same-frame response qualifies | Wrong or unapplied input can receive credit | Require input phase, planned held-key prefix, final command keys, and a later sample; respect source minimum tick |
| Source flags can be inherited/persistent; first target sample is counted | Stale contact or a target move's hit can satisfy source contact | Require source-only evidence after a readable reset or fresh victim damage; target-only contact cannot count |
| Reader drops malformed/out-of-order lines but verifier ignores issues; duplicate sample IDs accepted | Lost recovery frames or impossible ordering can pass | Keep tolerant reading for inspection; verification rejects reader issues, duplicate samples, skipped samples and nonmonotonic events |
| Continuity can begin after the final window, and endpoint samples are not required | Empty/truncated interval can report continuous | Bound first hit before final transition; require the full existing tail and completed driver/end records |
| Ctrl is ignored when moveType remains H | A real control-restoration frame can pass | Zero tolerated gaps; Ctrl=true is a drop, including during hitpause |
| Round/death/life reset is ignored | Separate attempts/matches can be joined | Require round/life/control/state coverage within the played attempt; refuse round changes, life reset/healing and death |
| `Prefer` trusts the last timeout's step and blindly says never attempted | Stale/incorrect timeout hides WrongState/NoContact or contradicts recorded input | Use a corroborated current wait timeout only for an unattempted step; require matching edge, wait duration, no readiness and no supported ready precondition |
| Driver checks injection success only once | A failed later call is logged as applied input | Check every call, including release-to-empty and P2 isolation; stop on failure |
| Shipped release function is never called | Overrides remain enabled after finish; abnormal stop can leave prior held bits until process exit | Probe finish invokes release for both slots |
| P2 is described as a no-input dummy but only P1 is overridden | Real P2 hardware can enter the test | Feed the empty held set to P2 each tick and check success |
| Missing explicit engine override silently falls back to production | The report can concern the wrong executable | Reject missing override/DLL directories; overwrite the actual launch filename; record actual executable path/hash/source in trace and report |
| Append-only trace can be copied from the install root | Old run evidence can enter a new sandbox | Remove the copied trace before starting the probe |
| No exact-plan binding | A trace can be judged against a different reconstructed plan | Embed/compare SHA-256 of exact plan JSON; legacy/mismatched traces are Inconclusive |
| Distance comment/docs imply only raw trigger facts; no-adapter docs are stale | Evidence provenance and unavailable-input semantics are misleading | Tag derived distance; update bundled-adapter contract and mark historical blocker evidence explicitly |

First-step motion inputs can legitimately move through neutral walk/crouch states. The first actual attack/control-loss exit after the start input is decisive; it cannot be repaired by a later attack. Consecutive same-number re-entry is Inconclusive because state numbers do not expose that occurrence boundary. Nonadjacent repeated-state routes are checked with distinct forward transitions and fresh source contact.

## Updated RuntimeVerified contract

These conditions are necessary and collectively sufficient in the changed code, assuming the trace truthfully represents the declared run:

- Nonempty consecutive step indexes, no unobservable same-state re-entry, matching exact-plan fingerprint and one matching plan start.
- No reader issues or backwards events. From the first route-input sample through the recorded finish, unique contiguous sample IDs and complete P1/P2 states, life, round and victim Ctrl. Intro preparation is outside the played interval, so ordinary intro control/life initialisation does not invalidate a subsequent attempt.
- Stable round, no life increase/reset/heal and no death during that interval; victim initially free. If engine ticks are available, they must have complete coverage and cannot go backwards or jump over simulation ticks. If absent, the report explicitly records the one-simulation-tick-per-loop assumption for Windows acceptance.
- Each input step has input-phase records after plan start, agrees with active driver bookkeeping when present, matches the planned held prefix, reaches the final command keys before the target response, and satisfies the observed source minimum tick.
- Each later transition is the first exit from the exact source occurrence reached by the preceding step. A wrong exit, pre-input transition, deadline expiry, earlier driver stop or contradictory step_done prevents success.
- Every required contact is supported on that source occurrence before exit, with a reset or fresh victim damage corroborating freshness. Guarded contact additionally excludes positive moveHit.
- The first victim hit is within the tested move sequence and no later than the final transition. Every sample through final transition plus the existing maximum-three-frame tail is in hit state with Ctrl=false. Intermittent missing moveType is Inconclusive; the numeric fallback is used only when moveType is absent throughout the run.
- No current-run injection/driver error, and both plan_complete and end/planComplete cover the proof interval.

This certifies observed route/run behavior. It does not establish the exact controller that caused an otherwise identical state transition, require the final state's eventual damage, authenticate a user-edited trace, or hash the character package. The static graph/underlying controllers/sibling edges/abilities are never promoted. A current package with the same reconstructed plan could still be different from the package used for an archived trace; keep the tested sandbox/evidence when reviewing acceptance.

## Prefer, distance and hook assessments

**Prefer:** the baseline change fixed SBA's specific misdiagnosis but was overbroad. The changed logic corroborates current step/edge, wait duration, no input/readiness and observed preconditions. An attempted wrong-state exit remains WrongState despite a later timeout string. A genuine source whiff with no supported wait stop remains NoContact. Setup timeouts need matching terminal records; arbitrary strings are not trusted. Driver bookkeeping remains derived execution bookkeeping, separate from raw samples.

**Distance:** `p2.x - p1.x` is derived signed common-axis telemetry. `distanceSource = derived:p2.x-p1.x` now records that provenance; a directly read trigger uses engine-trigger. Individual positions stay raw. The driver uses abs(distance), so its threshold works on either side; F/B uses the selected player's actual facing. A shared camera translation cancels. Inspection of the accessible current upstream `ikemen-engine/Ikemen-GO/src/script.go` showed posX reading character position minus common camera position and camelCase roundNo/gameTime bindings. That is supplementary source evidence, **not verification of f51ac49**; mixed-coordinate/unit behavior on the pinned engine still needs inspection. Missing positions remain null and cause an explicit approach skip. No approach optimizer or sweep was introduced. High-speed overshoot or facing away may exhaust the existing 400-tick bound; a supported setup stop reports DriverTimeout.

**Input adapter:** the Lua tests establish complete held sets, simultaneous bits, per-player facing selection, invalid-slot rejection at the adapter, per-tick failure detection and calls to disable both overrides on finish. The shipped adapter needs no custom --adapter when __xraySetVirtualInput exists. An unpatched engine returns unavailable. Custom adapters must implement equivalent slot isolation and release behavior.

**Go hook:** inert-by-default behavior, direct binding bounds, storage isolation, inactive normal-input equivalence, actual command-buffer clearing after disable, Go tick order and actual Windows binary/source provenance remain **UNVERIFIED**. No source was supplied/retrievable for the requested commit; Lua stubs cannot prove these. In particular, disabling storage may restore LocalInput immediately while command-buffer history remains until engine processing: only source/runtime checks can settle that distinction.

## Regression coverage and validation

C# adversarial coverage was added for genuine success, wrong exit then correct later, source ending before input, target-only contact, persistent stale flags, duplicate/skipped/reordered frames, one-frame control/neutral/custom-state recovery, hitpause, round/death/reset, incomplete state/contact/moveType/tail, late unrelated hit, attempted wrong state plus stale timeout, uncorroborated stop, supported wait stop, stale earlier stop, active-step mismatch, deadline expiry, wrong keys, plan mismatch/legacy traces, torn-reader issues, repeated-state failure and supported fresh repetition, intro preparation and hidden engine-tick gaps. Sandbox tests cover missing explicit engine, different executable filename, identity and stale-trace removal. Existing success/failure fixtures are retained; their absent fingerprints are supplied **only by an explicit synthetic test harness**, never retrofitted by production TraceReader. They remain historical mock evidence.

Executed locally:

- `texlua windows/scripts/xray_m3_adversarial.lua`: **14 passed, 0 failed**. Actual shipped Lua modules with API stubs; not IKEMEN or Go execution.
- Running the late-adapter-failure case against Lua files read directly from the baseline git commit: **assertion failure**, as expected. The baseline continues after the failed adapter call.
- `git diff --check`: passed.
- `dotnet test windows/IKEMENLab.Tests/IKEMENLab.Tests.csproj --configuration Release`: **not run; dotnet command not found (exit 127)**. No .NET SDK exists in the container. C# compilation and all new C# assertions remain unverified, including compatibility with the original 582-test suite.
- Windows app build and real Windows/IKEMEN acceptance: **not run**.

A local Windows CI workflow was prepared, but the branch was **not published**: automatic approval review rejected the attempted push because auditing/local fixes did not authorize remote repository mutation. No indirect publishing workaround was attempted. The workflow has not run.

## Acceptance blockers and next checks

Provide the actual f51ac49 source/diff and its input-reader callers. Compile/run the changed Windows tests and app build. Rerun both bangirasu routes with the new bundled probe/driver/adapter and retain fingerprints, trace and actual executable hash. Independently check fresh source contact, round/tick coverage and the uninterrupted victim interval. Recheck unpatched-engine InputInjectionUnavailable and a real whiff/stop failure. Verify one loop sample per simulation tick, normal inactive input, disabling overrides, command-buffer history, P1/P2 isolation and invalid direct-binding players on the pinned engine.

No M4, timing sweep, optimization, infinite detection, Funny Valentine dynamic-target model or UI feature was started. Custom victim/TargetState expansion was not introduced. The existing strict continuity rule remains strict; unsupported instant contact, same-state re-entry, heals/resets and incomplete telemetry are conservative limitations, not excuses to label uncertain traces Verified.
