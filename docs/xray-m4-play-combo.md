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

## Repairs after the first Windows audit (still not accepted)

| Area | Behaviour now |
|---|---|
| Result scope | A result belongs to the route it was played for (`PlaybackSession.RunRouteKey`). Against any other selected route the banner, failure panel, Inspect Failure, Replay, View Trace and step marks disappear; selecting the played route brings them back. A live run stays visible whatever is selected. |
| Closing X-Ray | `XRayWindow.Closed → XRayViewModel.Close` starts `PlaybackSession.ShutdownAsync` and **returns immediately**. The session is marked closed first (so queued notifications are dropped when they run), the run is cancelled (engine tree killed, sandbox deleted, no record unless the commit already won), and a task completes when the run has unwound. The result (`ShutdownResult`: clean / problem / leftover sandboxes) is not discarded: a timeout (20 s), an error or an undeletable sandbox is shown to the user. `App.OnExit` waits (≤ 15 s) for tracked shutdowns so exiting right after a close cannot orphan an engine. |
| Why no deadlock | Worker → UI notifications are **posted** (`Dispatcher.BeginInvoke`), never `Invoke`d, so a worker never waits on the UI thread; and the UI thread never waits on a worker (it only starts cleanup). The old design (UI `Shutdown()` blocking while the worker did a synchronous `Dispatcher.Invoke`) could stall until its timeout. |
| Cancel / commit | One atomic decision per run (`PlaybackCancellation`): a lock guards the states Pending → Cancelled \| Committed. `Cancel()` and `TryCommit(publish)` are both transitions out of Pending taken under that lock, and the publish step (renaming `meta.json.tmp` → `meta.json`, the only thing that makes a record visible) runs **inside** the commit lock. So exactly one wins: if the cancel is accepted first, `TryCommit` returns false and the run deletes everything and throws; if the commit wins, `Cancel()` returns false ("too late") and the record survives. Everything before the commit (engine exit, judging, `report.json`, the temp meta) is uncommitted and removed on cancel. A raw `CancellationToken` passed to `Play` is routed through the same gate. |
| Distance | `P1 x` / `P2 x` are raw engine facts. `Distance` is shown with its recorded source (`derived: p2.x − p1.x`, `engine trigger`, or `source not recorded`) and the trace window says so; it is never presented as an engine observation. |
| Runtime DLLs | Playback setup has an *Engine runtime DLLs* folder (Browse…). Blank = DLLs beside the engine are used automatically (convention). |
| Linger | With `lingerFrames` set (playback sets 90) the probe **holds the match running** with no input for that many ticks, *then* asks the engine to leave (`esc`). Both ends are recorded as `linger_start` / `linger_end` driver events with the probe tick and the engine tick (when readable). That is execution evidence only (ticks the match advanced). **During the hold the match keeps advancing with no injected input — it does not freeze the final pose** — and the trace says nothing about how long a person saw the screen (`os.clock()` is CPU time and is not recorded). Visible duration must be measured with a wall clock on Windows; whether a frozen final frame is desirable is decided after that observation. Without `lingerFrames` the old immediate-exit behaviour stands. |

## Test harness (WPF)

All WPF test classes share one STA host thread, one `Application` and a non-parallel xUnit collection (`WpfHost`, `WpfUiCollection`) — this replaces
two classes racing to create the Application. Window tests now construct → `Show()` → pump Loaded/Render/Idle → measure → assert → close in a finally;
a detached window never realises its content tree. Window width is clamped by the window's `MinWidth` (1040).

## Honesty rules kept

- `Verified` only from `RouteVerifier` on a real trace; a run that could not inject input is `Inconclusive`.
- A candidate route stays a candidate in the lens; the banner is a verdict about *this run*.
- Nothing is written to the real install or characters; records live under `%LOCALAPPDATA%\IKEMEN Lab\xray-playback\`.

## Not in M4

Timing-window search, conditional routes, infinites, recording video, choosing P2 behaviour, playing a route that starts from a
state (neutral-start routes only), and the non-default `inputRemap` case.
