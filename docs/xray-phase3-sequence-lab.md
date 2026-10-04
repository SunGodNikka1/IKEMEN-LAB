# X-Ray Phase 3 — Sequence Lab (manual follow-up experimentation)

```
Play Ability ──▶ situation (opponent posture, distance, who acts first, meter) ──▶ [Try Follow-Up]
                                                                                       │
Sequence Lab:  Kung Fu Palm  ↓  Chase until within 35  ↓  Normal A      (edit · reorder · remove · save as variant)
                                                                                       │
              Run ×1 / ×10 / ×50 ──▶ ONE plan for the existing driver ──▶ same sandbox / probe / commit gate ──▶ trace
                                                                                       │
              per step: Done / Failed (plain reason) / Not reached      per run: True Combo · Connected Sequence · Did Not Connect · Could Not Test
```

There is no second playback architecture.
- A sequence becomes one `InputPlan`:
  - abilities are ordinary plan steps (a command, then an expected state);
  - walk, wait, chase and jump are `StepAction` steps between them.
- The driver gained exactly those phases: hold keys, release, walk forward until within a distance, and jump.
- Each trial replays the whole sequence from neutral in a fresh sandbox. There is no save/restore branching; that is deferred until determinism is proven.

## What the user sees

**Ability Lab.**
- After **Play Ability**, the result card begins with the **situation**, taken when you can act again.
- Example: *Situation when you could act again (frame 329): the opponent is launched (in the air, in hitstun), 137 away; you can act 52 frame(s) before they can; you have 18 meter.*
- **Try Follow-Up →** opens the Sequence Lab with a new sequence that starts with that ability.

**Sequence Lab tab.**
- Step cards linked by ↓, labelled with your names. Each card shows how it will be played, for example:
  - *Cancel Jab into it by pressing "y" once it hits.*
  - *Once you can act, walk forward until within 35 (give up after 300 frames).*
- Each card has ▲ ▼ ✕ to reorder and remove, and fields to edit frames, distance, the stop-if-the-opponent-recovers option, or which ability (picked by name).
- A step that can't be played is marked with the reason. Examples: *only entered from other moves*, *its command only works on the ground*, *no forward-dash command X-Ray can read*.
- **Add a step:**
  - **+ Ability** (picked by name);
  - **+ Walk →** and **+ Walk ←** (N frames);
  - **+ Wait** (N frames);
  - **+ Chase** (within D, optionally *stop if they recover*);
  - **+ Dash** and **+ Jump**.
- **Saved sequences (variants):**
  - New, Save, Save as new, Delete.
  - Every edit to the steps gives the next version. Running an unsaved or edited draft saves it first, so results always belong to an exact version.
- **Run controls:**
  - **Run ×1** and **Run ×10**.
  - **Run ×50** stays disabled until you tick *I understand ×50 takes a while*. The cost line says what N runs cost (N launches, minutes measured from the last trial, about 0.3 MB per trial).
  - **Cancel**, **View Trace** (last trial), **Open experiment folder**, **Details** (technical: plan steps, commands, edges, frames, rules, engine, scope).
- **Result banner and per-step marks:**
  - Banner colours: *true combo* green; *connected sequence* green outline only, never drawn like a combo; *did not connect* red; *could not test* or mixed trials amber.
  - Step marks: ✓ done/connected, ✗ failed or whiffed (with the plain reason), · not reached.
- **Compare two variants:** the latest experiment of each variant's current version, side by side. A warning appears when the setups differ (character files, engine, opponent, stage, spacing).

## Verdicts

| Verdict | Meaning |
|---|---|
| **True Combo** | Every step happened and every attack connected. From the first contact to the last, the opponent was in a hit state (moveType H, or 5000–5999), without control, on **every** sample. |
| **Connected Sequence** | Every step happened and every attack connected, but the opponent was out of hitstun for N frames in between, and could act for M of them. A chase after a knockdown that connects is this, not a combo. A one-attack sequence that connects is reported here too ("no follow-up attack to judge as a combo"). |
| **Did Not Connect** | A step didn't happen, or an attack didn't touch the opponent. The failing step and a plain reason are given. |
| **Could Not Test** | The run can't show either way. |

Reasons for **Did Not Connect**:

| Reason | What happened |
|---|---|
| The move never started | A route-verifier reason: `WrongState`, `TransitionNotObserved`, `PreconditionNeverMet`… |
| `OutOfReach` | The chase never got within D in 300 frames; the closest distance is given. |
| `OpponentRecovered` | The opponent could act before you got close, with *stop if they recover* set. |
| `NeverAirborne` | The jump never left the ground. |
| `NeverActionable` | Control never came back to start the step. |
| `NoContact` | The attack started but whiffed; the distance at its start is given. |

Reasons for **Could Not Test**: no input injection, missing telemetry, a foreign or broken recording, the run stopping before a move was attempted, or a driver claim the samples contradict (`DriverDisagrees`).

**Evidence semantics.**
- Each move step's start is judged by the **unchanged route verifier**. It is given the plan's move steps and the trace with the movement steps' bookkeeping removed. The recording is first checked against the full plan's fingerprint.
- Only its `runtime.transition-observed` is cited per move step. No `RuntimeVerified` claim is made about combos, movement or contact, and no route or edge confidence changes.
- Walk, wait, chase and jump are judged from the driver's step events checked against the samples: the distance at chase end, airborne at jump end.
- Contact is read in each attack's window (its start to the next attack's start): opponent life lost, a new hit state, or P1's own moveHit rising from zero.
- Unknown prerequisites stay unknown. There is no general prerequisite solver.

## Data isolation

- Sequence trials go to `%LOCALAPPDATA%\IKEMEN Lab\xray-experiments\<experiment>\`:
  - `experiment.json` (scope, trials, statistics);
  - `trials\<record>\` (plan, trace, `sequence.json`, `meta.json` with `mode: sequence`).
- They **never** read, write or prune `xray-playback\`, the Play Combo / Play Ability history with its keep-25 rule; that rule is unchanged.
- An experiment keeps all of its trials (its own store, per-experiment record limit). The experiment store keeps its newest 30 experiments.
- Saved sequences live in `%LOCALAPPDATA%\IKEMEN Lab\xray\sequences\<hash of the character folder>.json`, never in character files.
- **Scope.** Every experiment records:
  - the character files' content hash and the engine sha256 (taken from trial 1; a different engine in a later trial is noted);
  - the dummy, the stage and the approach distance;
  - the sequence id and version, and the plan fingerprint.

  Compare warns when scopes differ, and results are shown only for the exact version that ran.

## Core pieces (`IKEMENLab.Core.XRay.Sequences`)

| Piece | Role |
|---|---|
| `Sequence`, `SequenceAction`, `SequenceSpec`, `SequenceLabels` | The model; the compact form (`1000 > chase:35! > wait:8 > walk:12 > back:4 > dash > jump > 200`); labels through the Phase 1 name resolver. |
| `SequencePlanner` | One plan per sequence. A follow-up is a real candidate-graph cancel out of the previous move when one exists, otherwise the ability's own command once P1 can act (air starts only after a jump). Dash is a two-forward-tap command, including into an engine common state (kfm `FF` → 100). Anything else is refused with the step and the reason. |
| `SequenceVerifier` | The verdicts above; `sequence.json` schema `ikemenlab.xray.sequence/1`; plain `Summary` and a technical `Details` text. |
| `SequenceExperimentRunner`, `ExperimentStore`, `ExperimentSummary`, `ExperimentText` | ×N trials, isolation, statistics (success, true-combo and connection rates, a failure histogram by step, damage, end distance and opponent posture, timing), cost text, Compare. |
| `SequenceStore` | Saved variants with versions. |
| `MoveObservation.Situation` (Playback) | The situation after Play Ability. |
| `ComboPlaybackService.PlaySequence` / `WithStore` / `RecordLimit` | A trial through the same sandbox, driver, probe and commit gate; the experiment's own store. |
| `PlaybackSession.RunSequenceAsync` | The same one-run-at-a-time session. Scope `sequence:<id>@v<n>`. Trial progress appears in the phase text ("Trial 3 of 10 · …"). Cancel stops the current trial (no record for it) and the rest; finished trials stay and the experiment says it was stopped. With none finished, nothing is published. |

**Driver** (`xray_driver.lua`):
- Action steps (`action = "hold" | "release" | "chase" | "jump"`, `waitCtrl`, `stopOnRecover`) add `act_wait` / `act` phases and record `step_ready` / `step_done` / `step_timeout` / `chase_stopped`.
- Route, ability and preview plans contain no action fields, so their Lua, JSON and fingerprints are unchanged.
- The mock engine (`scripts/xray_driver_mock.py`, scenarios `seq_true`, `seq_wait`, `seq_gap`, `seq_chase`, `seq_far`, `seq_recover`, `seq_jump`) was checked: the old and new driver produce identical traces for every combo and Phase 2 scenario.

## QA verbs and CLI

- **QA:** `seq-load <steps>`, `seq-save [name]`, `seq-run 1|10|50` (the Sequence Lab's own Run buttons), `seq-status`; `playback-wait settled` / `playback-cancel` apply.
- **CLI:**
  - `ikemenlab xray sequence-plan <char> "<steps>"` shows how each step would be played, or the refusal.
  - `ikemenlab xray runtime-sequence <char> "<steps>" --root R --dummy D --stage S [--engine E] [--trials N≤50] [--store DIR]` runs it in disposable matches. Trials go to the experiment store. Exit codes: 0 all succeeded, 4 some did not connect, 5 untestable.

## Tests

- `XRaySequenceLabTests` (23, Core):
  - spec round trip;
  - planner (real cancel vs own command, wait keeps the cancel, chase parameters, refusals);
  - plan JSON/Lua unchanged for route plans; the route verifier refuses sequence plans;
  - mock-engine traces of the real driver:
    - ability → ability true combo;
    - ability → wait → ability;
    - ability → walk → ability connected but not a combo;
    - ability → chase → ability;
    - a follow-up from too far (no contact, distance given);
    - the opponent recovering during the chase;
    - a jump;
  - hand-built traces: chase out of reach, a driver claim the samples contradict, a foreign recording, a wrong move, a run that stops before the first move;
  - renamed abilities throughout;
  - versioned saves;
  - experiment isolation from a full playback history (30 trials past keep-25);
  - cancellation keeping finished trials and cleaning up;
  - statistics and Compare;
  - the session scope;
  - the situation after Play Ability (real kfm trace).
- `SequenceLabUiTests` (4, WPF):
  - Try Follow-Up, then edit, reorder and remove, the ×50 confirmation, the cost line;
  - Run ×1 showing a verdict per step only for the exact saved version, never in Combos, with the playback store untouched;
  - cancel and cleanup;
  - renamed abilities in the cards and the picker.

## Deferred to Phase 4 and later

- Save/restore branching for fast trials (needs determinism proven).
- Running prefixes once and branching follow-ups.
- Opponent behaviour profiles (the dummy still receives no input).
- Meter grants.
- A prerequisite solver.
- Crouching starts.
- Dash back and run variants.
- Timing-window search.
- MCP, Teach AI, AI Director, Behavior Graph, Assistant / Autopilot (not started).
