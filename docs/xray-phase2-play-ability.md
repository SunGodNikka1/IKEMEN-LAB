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
| `MoveObservation` | What followed a start: connected (yes / blocked / no / unknown), own hit or guard, opponent hit / guarded / launched / knocked down, damage, P1 states, missing telemetry. Control: *frames until control* counts only control returning after the move took it away; a state that never took it away is reported as *kept control throughout* (`keptControl`). |
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
- `xray-select ability:N`, then `ability-play`, `ability-preview` or `ability-replay` (fire-and-forget, each invoking the Ability Lab's own button command), then `ability-status` and `ability-diagnostic`.
- The shared session's `playback-wait` and `playback-cancel` apply.
- `playback-wait verdict` throws on a preview (it never has a verdict); `playback-wait settled` accepts a verdict, a preview, or cancel/error.

**CLI.**
- `ikemenlab xray ability-plan <char> <ability:N|N> [--approach N]` prints the chosen path, alternatives, warnings and plan, or why it can't be played (exit 3). With `--preview` (or a `state:N` target) it prints the force plan, with `"proof": false`.
- `ikemenlab xray runtime-ability <char> <ability> --root R --dummy D --stage S [--engine E] [--preview] [--keep]` plays it.
  - Play Ability exits 0 (Performed), 4 (NotPerformed) or 5 (Inconclusive).
  - A preview exits 0 when it ran, 4 when the engine refused the state, and 5 when it could not run.

## Tests

- `XRayAbilityPlaybackTests` (25, Core):
  - the real kfm runs from the Windows acceptance pass (see below), re-read from their traces: provenance (the plan fingerprint the engine recorded), Performed, the forced super, and the forced follow-up state that kept control;
  - a forced state that never takes control away is reported as keeping it;
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

## Windows acceptance (2026-10-04)

**Setup.**
- Engine: the installed IKEMEN build `D:\Games 3\Ikemen_GO-v1.0.0\Ikemen_GO.exe` (version `jg-simul8-v1 - ffa-build`, sha256 `F68DC970…23D0A1`). It carries the X-Ray input hook and is copied into a disposable sandbox; the install is never written, and its hash was unchanged afterwards.
- Subject kfm, dummy kfm, stage `stages/ASI_FightIsland/ASI_FightIsland.def`, approach distance 40.
- Runs 1–6 were made with `ikemenlab xray runtime-ability`; A–E through the real app with a QA script.

| # | Run | Result | Engine evidence (from the trace) |
|---|---|---|---|
| 1 | Play Ability `ability:1000` (Kung Fu Palm, ↓↘→ x) | **Performed** | Inputs D (272), DF (275), F (278), x (280); P1 entered 1000 at 281. Own hit at 289; P2 went 5000 → airborne 5030/5035/5050 → lying 5100. 90 damage (3000 → 2910); control back at 329 (48 f). Only `runtime.transition-observed`. |
| 2 | Play Ability `ability:3000` (Triple Kung Fu Palm, needs power ≥ 1000) | **Not performed / WrongState** | The full double quarter-circle + x was fed (272–288). P1 power stayed 0–118 and P1 entered 1000 (Kung Fu Palm, whose command is the input's tail) at 289; 3000 was never observed. Exit 4. |
| 3, 5 | Preview `state:3000` | **Forced**, `proof: false` (identical twice) | Forced at 271 with power 0, sampled in 3000 (anim 3000) from 272 to 434. Three hits: 3000 → 2928 (316) → 2856 (346) → 2781 (376); the third launched P2, who lay down at 420. Control back at 435 (163 f). |
| 4, 6 | Preview `state:1055` (Kung Fu Knee follow-up; Play Ability refuses it as "only entered from other moves") | **Forced**, `proof: false` | P1 ran 1055 (statetype A, anim 1055) → 1056 → 0. No contact. Its Statedef sets no ctrl, so P1 kept the control it had in neutral (see the defect below). |
| A | App: Play Ability `ability:1000` | **Performed**, attempt 1 | Same figures as run 1. Record `…074416-fdc4ac` has plan, trace, report, ability, diagnostic and meta (`mode: ability`). |
| B | App: Combos lens, the identical one-step route `cand:neutral>state:1000@state:-1/ctrl:8#0` selected | **No contamination** | Combo panel `hasResult=False`, `canReplay=False`, `canInspect=False`, `playback-diagnostic` (none), no step marks. |
| C | App: Replay | **Performed**, attempt 2 | A new record `…074431-ef8509`. |
| D | App: Cancel 2.5 s into a live run (attempt 3) | **Cancelled** | No record; sandbox removed; no engine process left. |
| E | App: Preview `state:3000` | **Forced**, attempt 4, `PreviewProduced` | Banner kind Preview; record `…074450-37afc1` has `preview.json` and no `report.json`; `meta.json` and `diagnostic.json` have `"proof": false`. The diagnostic is headed "State Preview diagnostic — NOT PROOF" and says "not a verdict". |

After every run: zero sandboxes in `runtime-sandboxes`, no engine processes. The record store's keep-25 rule pruned the three oldest records.

**Defect found and fixed: recovery reading for a state that never takes control away.**
- Run 4 reported "back in control after 1f" for state 1055. P1 never lost control: forcing skipped the move that normally takes it away, and 1055's Statedef sets no ctrl.
- `MoveObservation` now counts control only when it returns after being lost, and reports `keptControl: true` ("kept control throughout") otherwise. A preview adds a note explaining why.
- Run 6, on the fixed build, shows `keptControl: true`, `framesUntilControl: null` and the note. Runs 1–3 are unchanged.

**What this establishes about `changeState` from the probe's loop hook.** It enters the state with its own animation: anim 3000 and 1055 were observed. Statedef `ctrl` applies when the state sets it (3000: ctrl 0). When the state doesn't set it, P1 keeps its current control (1055).

**Not established.**
- No picture of the engine window could be captured. The install's config runs the engine in exclusive fullscreen (`Fullscreen = 1`, copied into the sandbox): GDI copy and `PrintWindow` returned black frames, and Desktop Duplication lost access when the engine switched to fullscreen (`DXGI_ERROR_ACCESS_LOST`). The config was not changed for a screenshot. The visible execution above is the engine's own per-frame telemetry: state, animation, position, hit flags and the opponent's reaction.
- ForcedNotSeen (a state that changes on its first tick) has been seen only in the mock engine.
- The M4 acceptance items (verdicts, close timing, wall-clock linger) remain open as before.

## Not in Phase 2

- Sequence Lab: follow-ups, movement steps between moves, run ×N, compare.
- Try Follow-Up, Teach AI, AI Director, Behavior Graph, MCP.
- Choosing among an ability's alternative command paths in the UI (the CLI lists them).
- Granting meter for power-gated commands.
- Previewing arbitrary states from other lenses (the Atlas previews the selected ability's entry state; the CLI previews any `state:N`).
- Scripting crouching or airborne starts.
