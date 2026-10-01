# X-Ray milestone 4 — Play / Verify Combo from the Combo Lens

```
Candidate route ──▶ [▶ Play Combo] ──▶ disposable sandbox ──▶ scripted input ──▶ visible IKEMEN playback
                                                                                     │
        Combo Lab ◀── Verified / Failed / Inconclusive ◀── RouteVerifier ◀── trace ◀─┘
        [Replay] [Inspect Failure] [View Trace]
```

M4 is a front end to M3. **No second playback architecture:** the Play button calls `ComboPlaybackService`, which runs the same
`VerifyRunner` (InputPlanner → RuntimeSandbox → driver/adapter → engine → trace → RouteVerifier) that `ikemenlab xray runtime-verify`
uses. The engine window is simply left on screen, and the run is kept.

## What the user sees (Combos lens)

- **▶ Play Combo** (needs a selected route). Phases: *Preparing the sandbox → Playing the combo in IKEMEN — watch the engine window → Checking what happened*. **Cancel** kills the engine and removes the sandbox.
- A result banner: `✓ Verified · 4 of 4 transitions observed · opponent stayed in hitstun, frames 417–476`, `✗ Failed at step 4: the move it needed to start from never happened`, or `? Could not be tested: this engine build cannot receive scripted input`.
- The route's step rows get marks: ✓ observed, ✗ failed here, · not reached. The route's static confidence is not changed.
- **Replay** plays the same route again (the engine is not guaranteed deterministic; a replay is a new run and a new record).
- **Inspect Failure** explains the failing step in plain terms with hints, selects the step in every lens, and cuts the trace to the frames around the failure.
- **View Trace** opens a table of every recorded frame (both fighters' raw facts, plus the driver's input/step notes), scrolled to the decisive frame; **Open record folder** shows `plan.json`, `trace.jsonl`, `report.json`, `meta.json`.
- **Playback setup**: X-Ray engine path (required), dummy character and stage (automatic by default: `kfm` or the first other character; the first existing `[ExtraStages]` entry). Remembered in settings. Play refuses with the reason (and opens setup) if the engine is missing, or does not appear to contain the `__xraySetVirtualInput` hook (a string search — a heuristic; the run itself is the real test).

## Core pieces (`IKEMENLab.Core.XRay.Playback`, all unit-tested without an engine)

| Piece | Role |
|---|---|
| `PlaybackPreflight` | resolves dummy/stage/engine from settings, reports what is missing; never launches anything |
| `ComboPlaybackService` | one visible run → saved `PlaybackRecord` (newest 25 kept); cancel/refusal leaves no half-written record |
| `PlaybackSession` | the Play button's state machine: one run at a time, phases, Cancel, Replay, headline |
| `PlaybackInspector` | `Timeline` (trace rows) and `Inspect` (headline, why, hints, focus frame, window) |
| `VerifyRunner` (M3) | extended with progress, cancellation and a `Collect` hook; unchanged verdict logic |
| `ProcessEngineRunner` | now cancellable: polls, kills the process tree, throws `OperationCanceledException` |

The sandbox engine lingers `lingerFrames = 90` ticks (≈ 1.5 s) on the final position before exiting so the result can be seen.

## Honesty rules kept

- `Verified` only from `RouteVerifier` on a real trace; a run that could not inject input is `Inconclusive`.
- A candidate route stays a candidate in the lens; the banner is a verdict about *this run*.
- Nothing is written to the real install or characters; records live under `%LOCALAPPDATA%\IKEMEN Lab\xray-playback\`.

## Not in M4

Timing-window search, conditional routes, infinites, recording video, choosing P2 behaviour, playing a route that starts from a
state (neutral-start routes only), and the non-default `inputRemap` case.
