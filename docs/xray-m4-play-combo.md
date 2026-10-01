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
| Closing X-Ray | `XRayWindow.Closed → XRayViewModel.Close` starts `PlaybackSession.ShutdownAsync` and **returns immediately**. The session is marked closed first (so queued notifications are dropped when they run), the run is cancelled (engine tree killed, sandbox deleted, no record unless the commit already won), and a task completes when the run has unwound. The result (`ShutdownResult`: clean / problem / leftover sandboxes) is not discarded: a timeout (20 s), an error or an undeletable sandbox is shown to the user. `App.OnExit` makes a **bounded best-effort** wait (≤ 15 s) for tracked shutdowns and tells the user if any had not finished (`ExitReport`); each shutdown may itself take up to 20 s, so this is **not** a guarantee that no engine or sandbox survives the process exit. |
| Why no deadlock | Worker → UI notifications are **posted** (`Dispatcher.BeginInvoke`), never `Invoke`d, so a worker never waits on the UI thread; and the UI thread never waits on a worker (it only starts cleanup). The old design (UI `Shutdown()` blocking while the worker did a synchronous `Dispatcher.Invoke`) could stall until its timeout. |
| Cancel / commit | One atomic decision per run (`PlaybackCancellation`): a lock guards the states Pending → Cancelled \| Committed. `Cancel()` and `TryCommit(publish)` are both transitions out of Pending taken under that lock, and the publish step (renaming `meta.json.tmp` → `meta.json`, the only thing that makes a record visible) runs **inside** the commit lock. So exactly one wins: if the cancel is accepted first, `TryCommit` returns false and the run deletes everything and throws; if the commit wins, `Cancel()` returns false ("too late") and the record survives. Everything before the commit (engine exit, judging, `report.json`, the temp meta) is uncommitted and removed on cancel. A raw `CancellationToken` passed to `Play` is routed through the same gate. |
| Distance | `P1 x` / `P2 x` are raw engine facts. `Distance` is shown with its recorded source (`derived: p2.x − p1.x`, `engine trigger`, or `source not recorded`) and the trace window says so; it is never presented as an engine observation. |
| Runtime DLLs | Playback setup has an *Engine runtime DLLs* folder (Browse…). Blank = DLLs beside the engine are used automatically (convention). |
| Linger | With `lingerFrames` set (playback sets 90) the probe **holds the match running** with no input for that many ticks, *then* asks the engine to leave (`esc`). Both ends are recorded as `linger_start` / `linger_end` driver events with the probe tick and the engine tick (when readable). That is execution evidence only (ticks the match advanced). **During the hold the match keeps advancing with no injected input — it does not freeze the final pose** — and the trace says nothing about how long a person saw the screen (`os.clock()` is CPU time and is not recorded). Visible duration must be measured with a wall clock on Windows; whether a frozen final frame is desirable is decided after that observation. Without `lingerFrames` the old immediate-exit behaviour stands. |

### Cleanup failure is never silent (preparation included)

Sandbox deletion goes through one reporting path (`RuntimeSandbox.RemoveReporting`) used both when preparation fails or is cancelled (inside `RuntimeSandbox.Create`) and after a run (`VerifyRunner`). A failed deletion raises `CleanupFailed(path, reason)` → `PlaybackSession` records it → `ShutdownResult.Clean` is false with the leftover path/reason → the user-facing warning shows it. Tests inject the failure through a deleter seam (no filesystem timing or permissions).

## Test harness (WPF)

All WPF test classes share one STA host thread, one `Application` and a non-parallel xUnit collection (`WpfHost`, `WpfUiCollection`) — this replaces
two classes racing to create the Application. Window tests now construct → `Show()` → pump Loaded/Render/Idle → measure → assert → close in a finally;
a detached window never realises its content tree. Close/notification tests assert on notification counters (`NotificationsApplied` / `NotificationsDropped`), not on state getters such as `Headline`, which may legitimately change when a cancelled run ends. Window width is clamped by the window's `MinWidth` (1040).

## Honesty rules kept

- `Verified` only from `RouteVerifier` on a real trace; a run that could not inject input is `Inconclusive`.
- A candidate route stays a candidate in the lens; the banner is a verdict about *this run*.
- Nothing is written to the real install or characters; records live under `%LOCALAPPDATA%\IKEMEN Lab\xray-playback\`.

## Not in M4

Timing-window search, conditional routes, infinites, recording video, choosing P2 behaviour, playing a route that starts from a
state (neutral-start routes only), and the non-default `inputRemap` case.

## Notification delivery vs close — the contract

**No subscriber begins after the session is closed.** That covers a later queued notification *and* the remaining subscribers of the notification being delivered when
a subscriber closes the session (delivery invokes subscribers one at a time and re-checks `closed` before each, instead of a single multicast `Invoke`). The check and the
delivery share one lock with the close (order: delivery → gate): a subscriber already running on **another** thread when a close is requested finishes first (the close waits
for it); a subscriber that itself closes the session (the lock is reentrant on its own thread) completes normally. In the application delivery and close both run on the UI thread;
the lock makes the contract hold for a close from any thread (a QA verb, a test). Tests: in-flight delivery blocks a cross-thread close; subscriber A closes → subscriber B does not run;
a contention test asserts no listener ever observes `IsClosed`. These do **not** explain the earlier "0 → 3 after SessionIsClosed" counter observation, which was never reproduced.

## Attempt identity (QA / evidence)

Every Play press has its own `AttemptId` — including one refused before any engine was launched — with an explicit `AttemptState`:
`None → Started → RuntimeStarted → VerdictProduced`, or `PreflightRefused` / `Cancelled` / `Error`. The stored result remembers which attempt produced it
(`ResultAttemptId`, `ResultRunId`, `ResultRouteKey`); `ResultIsCurrent` is true only when that is the latest attempt. `PlaybackSession.VerdictForLatestAttempt()` is what "wait for a verdict"
means: `Verdict` only when the **latest** attempt produced the stored result; `RefusedPreflight` (message `no runtime verdict: attempt N refused during preflight: …`) when the latest
attempt was refused; `Pending` while it runs; `EndedWithoutVerdict` for cancel/error; `NoAttempt` before any press. An earlier run's committed result never satisfies a wait for a newer attempt.

**Preflight refusal ≠ runtime Inconclusive.** Preflight refusal: the engine/setup is rejected before launch (e.g. the engine does not appear to contain the X-Ray input hook) — attempt `PreflightRefused`, no
verdict, no run. Runtime Inconclusive: the engine launched and the verifier returned `Inconclusive / InputInjectionUnavailable` — attempt `VerdictProduced`, a record exists. They are never converted into each other.

## QA verbs (`QaScriptRunner`)

`playback-play | -cancel | -replay | -status | -wait | -inspect | -trace` drive the real `ComboPlaybackPanel → PlaybackSession → M3/M4 pipeline`; `playback-play` is asynchronous.
`playback-status` prints identity first (`selectedRouteKey`, `attemptId`, `attemptState`, `attemptIssue`, `attemptRouteKey`, `runRouteKey`, `resultAttemptId`, `resultRunId`, `resultRouteKey`, `resultIsCurrent`);
verdict/reason/record/engine-hash/dummy/stage fields are printed **only when the result is current**, so a stale verdict can never sit beside a newer setup.
`playback-wait verdict` is attempt-bound as above and throws on a preflight-refused attempt.
Route selection: `xray-combo-route-index N` (1-based, as `--route N`), `xray-combo-route-key <exact key>` (ordinal, exact); the historical `xray-combo-route <substring>` now **errors when the substring matches more than one route** (listing them) instead of taking the first.
Every selection logs `selected route #N of M by …; key=…; title=…` and re-checks that the lens really selected it.
