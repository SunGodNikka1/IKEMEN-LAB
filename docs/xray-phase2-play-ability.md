# X-Ray Phase 2 — Play Ability and Preview State

```
Ability Atlas ──▶ [▶ Play Ability] ──▶ one-step route (neutral → entry state, through the ability's own command)
                        │                    │
                        │       InputPlanner → RuntimeSandbox → driver/adapter → engine → trace → RouteVerifier (step check)
                        │                                                                   │
                        │                          Performed / Not performed / Could not be tested  +  what followed (measured)
                        │
                  [Preview State (not proof)] ──▶ force plan → same sandbox/driver/probe → engine's changeState → trace
                                                                                   │
                                                    Preview (not proof): Forced / Left at once / Refused / Could not run
```

Phase 2 is a front end to the M3/M4 playback stack, like Play Combo. **There is still no second playback architecture:** Play Ability is a
one-step `PlaybackRequest` played by `ComboPlaybackService.Play`, and a preview is `ComboPlaybackService.Preview`. Both use the same disposable
sandbox, driver, probe, record store, commit/cancel gate, linger, diagnostics and trace viewer. All three buttons share **one `PlaybackSession`
per X-Ray window**, so only one engine runs at a time across Play Combo, Play Ability and Preview State. Each result is scoped to its job:

| Job | Scope key | Never equal to |
|---|---|---|
| Play Combo | the route key (`cand:…>…`) | |
| Play Ability | `ability-play:ability:N` | any route key, including the one-step route through the same command |
| Preview State | `preview:state:N` | a route key or a Play Ability scope |

A result is shown only against its own scope. Play Ability results never put marks on Combos-lens steps, and a preview is never shown as a verdict.

## What the user sees (Ability Atlas → Ability Lab bar)

- The selected ability, and **how it would be played**. Example: *Plays State 1000 from neutral by pressing "QCF_x" (controller state:-1/ctrl:4) · 1 other command path(s) not used.* If Play Ability can't play it, the bar says why and offers Preview State instead. The reasons:
  - it is entered only by the AI;
  - it is only entered from other moves (a cancel or follow-up);
  - its command can't be pressed literally;
  - it is accepted only while crouching or airborne.
- **Warnings** that come from the static graph: a power test in the gate (the test match grants no meter), or unmodelled conditions.
- **▶ Play Ability**, **Preview State (not proof)**, Cancel, Replay, Inspect Failure, View Trace and Copy Full Diagnostic.
- **Playback setup** opens the Combos lens's setup. The engine, dummy, stage and approach distance are shared with Play Combo.
- **The result banner and card.** Examples:
  - `✓ Performed · Revolver Shot (State 1000) started via "QCF_x" · connected · knocked down · 70 damage · back in control after 38f`
  - `✗ Not performed: the character went into a different move`
  - `? Could not be tested: this engine build cannot receive scripted input`
  - `◇ Preview (not proof) · State 1000 forced · connected · …`
  
  The preview banner is neutral with an amber outline, never green. The card lists:
  - whether it connected, and how;
  - the opponent's reaction (hit, launched, knocked down);
  - damage, from the opponent's life before and the lowest life afterwards;
  - when control came back;
  - the states P1 passed through;
  - for Play Ability, an evidence line; for a preview, the not-proof statement.

## Evidence semantics (what each result may claim)

**Play Ability.**
- Its status comes only from `RouteVerifier`'s **step-1 verdict** on the one-step plan.
  - **Performed** means the planned command inputs, from neutral, were followed by P1 entering the ability's entry state, in this run.
  - It cites exactly `runtime.transition-observed`, copied from the verifier's own step verdict.
- The verifier's combo continuity check is **not** used, because a single move isn't a combo. On a one-step plan the verifier usually returns `Failed / ComboDropped` ("never hit before the final transition"). That status is kept in `ability.json` (`routeVerifierStatus`/`routeVerifierReason`) and the diagnostic for transparency, but it is not the ability's result.
- `runtime.opponent-continuous` is never cited, even when the verifier calls the one-step run Verified.
- No route or edge confidence changes, and an ability report has no confidence field.
- A setup integrity failure withdraws Performed: if the opponent was hit before the move started, the result is Inconclusive.
- *What followed* (contact, reaction, damage, recovery) is a **measurement** of this one run against this dummy at this spacing (`MoveObservation`), not a claim about the character.
  - "Did not connect" is only said when the driver finished its watch (`plan_complete` and `end planComplete`); otherwise it's "unknown".
  - Own hits count only when `moveHit` rises from zero after the start, never a carried flag.
  - Projectile and helper hits don't set P1's `moveHit`; they show through the opponent's reaction and life.

**Preview State.**
- Forces the entry state with the engine's own Lua `changeState(stateNo)`, redirected with `player(1)`. It is a stock IKEMEN GO binding, present in both the stock and the X-Ray engine.
- It **is never proof.** It skips the command, every trigger and any prerequisite. It:
  - cites no evidence rule;
  - has no confidence;
  - is never a verdict (`AttemptState.PreviewProduced`, `VerdictWaitKind.Preview`);
  - writes no `report.json`;
  - writes `"proof": false` in `preview.json`, `meta.json` and the diagnostic.
- `RouteVerifier` refuses to judge a force plan (`Inconclusive / PlanMismatch`).
- Only the character's own defined, non-negative, non-common states can be forced.
- Preview statuses:

| Status | Meaning |
|---|---|
| Forced | The state was sampled. |
| ForcedNotSeen | The engine accepted the state but P1 left it on its first tick; the first states seen are noted. |
| Refused | The engine's `changeState` returned false. |
| Inconclusive | The run never forced the state, or the recording can't be trusted. |

## Core pieces (`IKEMENLab.Core.XRay.Playback`)

| Piece | Role |
|---|---|
| `AbilityPlayback.Resolve` | Picks the command path: Start edges from neutral into the entry state that name a command and the planner can script. Order: no power test, then strongest confidence, then fewest unmodelled conditions, then edge id. It never guesses a lead-in through other moves. |
| `AbilityPlayback.PlanOptionsFor` | The route plan with a longer watch after the step: entry animation length + 90 ticks, clamped to 120–480. |
| `AbilityVerifier` | Reads the ability from the route verifier's report plus the samples after the move started; `ability.json` schema `ikemenlab.xray.ability/1`. |
| `StatePreview` | `CanPreview`, `Plan` (a single `PlanStep.ForceKind` step with no input), `Read` (integrity checks, then observations); `preview.json` schema `ikemenlab.xray.preview/1`. |
| `MoveObservation` | What followed a start: connected (yes / blocked / no / unknown), own hit or guard, opponent hit / guarded / launched / knocked down, damage, frames until control, P1 states, missing telemetry. |
| `VerifyRunner.Execute` | The engine-run half of `VerifyRunner.Run`, factored out so a preview can reuse it. `Run` is unchanged in behaviour: same notes in the same order. |
| `PlaybackSession` | `PlayAsync` (combo or ability) and `PreviewAsync` share one attempt core. `RunRouteKey` is the latest job's scope; `PreviewResult` holds a preview, `Outcome` a route or ability result; `ReplayAsync` replays the latest job, whatever it was. |
| `StaticSnapshot` | Ability attempts also freeze the ability's own name; `CaptureState` freezes a preview's files and names. |
| `PlaybackDiagnostic` | Adds `mode`, `ability` and `preview` (omitted for combos, so a combo diagnostic is byte-identical to before). Text header: *Play Ability diagnostic* / *State Preview diagnostic — NOT PROOF*. |

**Driver and probe.**
- A plan step with `force = true`, emitted only for `Force` steps, so every route plan's Lua and fingerprint are unchanged, is forced through `io.force` once P1 is free and close enough.
- The driver records `force_applied` or `force_failed` (which ends the run with `forceFailed`), then only watches the tail.
- The probe supplies `io.force` using `player(n)` and `changeState`; its version is `0.3-phase2-force`.
- Checked with the mock engine (`scripts/xray_driver_mock.py`, scenarios `ability`, `preview`, `preview_refused` and `preview_leaves`): the HEAD and new driver/probe produce identical traces for every combo scenario.

## Records

Records go under `%LOCALAPPDATA%\IKEMEN Lab\xray-playback\` as before:

| Run | Files written | `meta.json` |
|---|---|---|
| Play Ability | `plan.json`, `trace.jsonl`, `report.json` (the route verifier's report on the one-step plan), `ability.json`, `diagnostic.json` | `mode: ability`, `target: ability:N`, `status`: Performed / NotPerformed / Inconclusive |
| Preview | `plan.json`, `trace.jsonl`, `preview.json`, `diagnostic.json` (no `report.json`) | `mode: preview`, `target: state:N`, `proof: false` |

A combo's `meta.json` is unchanged and has no `mode`.

## QA verbs and CLI

**QA verbs (`QaScriptRunner`).**
- `xray-select ability:N`, then `ability-play` or `ability-preview` (fire-and-forget), then `ability-status` and `ability-diagnostic`.
- The shared session's `playback-wait` and `playback-cancel` apply.
- `playback-wait verdict` throws on a preview (it never has a verdict); `playback-wait settled` accepts a verdict, a preview, or cancel/error.

**CLI.**
- `ikemenlab xray ability-plan <char> <ability:N|N> [--approach N]` prints the chosen path, alternatives, warnings and plan, or why it can't be played (exit 3). With `--preview` (or a `state:N` target) it prints the force plan, with `"proof": false`.
- `ikemenlab xray runtime-ability <char> <ability> --root R --dummy D --stage S [--engine E] [--preview] [--keep]` plays it.
  - Play Ability exits 0 (Performed), 4 (NotPerformed) or 5 (Inconclusive).
  - A preview exits 0 when it ran, 4 when the engine refused the state, and 5 when it could not run.

## Tests

- `XRayAbilityPlaybackTests` (23, Core):
  - path choice and refusals (only from other moves, AI-only, power warning);
  - plan shape;
  - route-plan Lua unchanged;
  - Performed, whiff, not performed (different move, no move) and Inconclusive (plan mismatch, no injection);
  - only `runtime.transition-observed` is cited, even when the verifier calls the one-step run Verified;
  - preview: allowed states, force plan, verifier refusal, and Forced / ForcedNotSeen / Refused / Inconclusive;
  - the service writes the right files (mock-engine fixtures produced by the real driver and probe);
  - a refused preview never creates a sandbox or a record;
  - session scoping, replay and diagnostics;
  - a refused ability or preview press keeps its kind and never borrows a verdict;
  - combo diagnostics and meta gain no Phase 2 fields;
  - frozen names;
  - the CLI.
- `AbilityLabUiTests` (6, WPF):
  - the bar's layout at 1040 and 1440 px;
  - the bindings;
  - a Play Ability result shown only for its ability and never in the Combos lens, even for the identical one-step route;
  - a preview labelled not proof (◇, not green);
  - a press without an engine says it did not start, runs nothing, and its diagnostic can be copied;
  - one engine at a time across Play Combo and the Ability Lab.

## Not yet established (needs a real Windows run)

- Play Ability and Preview State have only run against the mock engine and scripted runners. Still to observe on the X-Ray engine build:
  - one Performed and one Not-performed Play Ability, on a simple character (for example kfm or bangirasu);
  - a preview of a cancel-only state (expected to show Forced);
  - a preview of a state that changes on its first tick (expected to show ForcedNotSeen).
- That calling `changeState` from the probe's `loop` hook behaves like the source suggests (the state is entered with its own animation and ctrl) is **inferred from the engine source**, not observed.
- The M4 acceptance items (verdicts, close timing, wall-clock linger) remain open as before.

## Not in Phase 2

- Sequence Lab: follow-ups, movement steps between moves, run ×N, compare.
- Try Follow-Up, Teach AI, AI Director, Behavior Graph, MCP.
- Choosing among an ability's alternative command paths in the UI (the CLI lists them).
- Granting meter for power-gated commands.
- Previewing arbitrary states from other lenses (the Atlas previews the selected ability's entry state; the CLI previews any `state:N`).
- Scripting crouching or airborne starts.
