# Milestone 4 — narrow Windows acceptance pass (Space Bunny Alpha)

Main Agent repaired the confirmed defects (see `xray-m4-play-combo.md`, "Repairs after the first Windows audit") and the WPF test harness. This pass is
**validation only** — please do not redesign or broadly repair M4; report what fails.

Note: SBA's uncommitted three-file harness fix was never pushed, so its intent was re-created: `WpfUiCollection.cs` (non-parallel collection),
`WpfHost.cs` (one STA thread + one `Application`, created under a lock), both WPF test classes in the collection.

## Not executed by the Main Agent (no Windows / WPF / engine here)
- `IKEMENLab.App.Tests` (13 tests incl. the new route-scope and close tests) — compiled against the desktop reference assemblies only.
- The real engine, real focus/linger behaviour, the Go hook.
Core logic (planner, session scoping, cancel boundary, shutdown, linger trace, provenance, DLL convention) has 40+ passing unit tests on Linux
(suite: 634 passed, only the same 8 Windows-only baseline failures).

## Checklist
1. Full solution build, `dotnet test` (including `IKEMENLab.App.Tests`), Release build, win-x64 publish. Report any red WPF test with its message; if the cause is the test, say so.
2. Real `bangirasu` playback: route 0 → **Verified**; route 2 → **Failed** (`PreconditionNeverMet`, step 4); production engine → **Inconclusive**.
3. **Route-switch scoping:** play A, select B → A's banner/Inspect/Replay/View Trace/step marks vanish; reselect A → they return.
4. **Close/cancel/replay:** closing returns immediately (no UI stall — time it); cleanup completes in the background; if a dialog reports a timeout or a leftover sandbox (also after a close *during preparation*), record it. Exit-time cleanup is bounded best-effort (15 s wait, warning if unfinished): after quitting the app right after a close, check for an orphan engine process. close X-Ray during preparation and during live playback → engine process gone, sandbox folder gone, no record in `%LOCALAPPDATA%\IKEMEN Lab\xray-playback`, no crash or late UI. Cancel button the same. Replay starts a new run.
5. **Runtime DLLs:** with no setting and DLLs beside the engine (auto convention), and with the folder set in Playback setup (Browse…): the sandbox engine starts without STATUS_DLL_NOT_FOUND.
6. **Focus / linger:** is the engine window visible and the final pose on screen for ≈ 1.5 s after the combo? In the saved trace find `linger_start` / `linger_end`: report their tick difference (should be 90) and probeTick/engineTick values as *execution* evidence, and separately your **wall-clock** measurement of how long the engine window stayed up after the combo (the trace has no wall-clock; `os.clock()` was removed because it is CPU time). The match keeps running with no input during the hold (it is not frozen): describe what the final pose does during it, and whether anything is lost when `esc` is requested.
7. **Saved evidence:** `xray-playback\<id>\` has plan/trace/report/meta; View Trace shows `P1 x`, `P2 x` and Distance labelled with its source.
8. Production `Ikemen_GO.exe` hash unchanged; no orphan engine processes after any of the above.


## Scripted acceptance notes (QA verbs)
- Select routes with `xray-combo-route-index N` or `xray-combo-route-key <exact key>` and keep the logged `selected route #N … key=…` line as proof of which candidate ran.
- Preflight vs runtime: an engine refused before launch shows `attemptState=PreflightRefused` and `playback-wait verdict` fails with `no runtime verdict: attempt N refused during preflight`; an engine that launched and could not inject shows `attemptState=VerdictProduced`, `verdict=Inconclusive`, `reason=InputInjectionUnavailable`, a `runId` and an engine hash. Report these as different results.
- Check `status.resultIsCurrent=True` before trusting any verdict/hash fields.

## Diagnostics / spacing pass (see `xray-m4-diagnostics.md`)
Please also check on Windows: (1) Approach distance field in Playback setup (blank = 60, invalid refused, remembered across restarts, visible in the plan JSON/fingerprint of a run); (2) **Copy Full Diagnostic** — paste after a Failed run, a preflight-refused press, and with another route selected (disabled); `playback-diagnostic` logs the same text;
(3) re-run Funny Valentine with a smaller Approach distance (manual retry only) and report the derived separation and whether contact occurs; (4) re-run Goku and replace `m4_goku_*` fixtures with the real trace if it differs from the reconstruction; (5) the new WPF tests `ApproachDistanceIsInPlaybackSetupValidatedAndRemembered` and `CopyFullDiagnosticFollowsTheAttemptAndTheSelectedRoute` (compile-checked only here).

Corrective pass: also run `playback-setup approachDistance=abc` then `=45`, `playback-diagnostic` (after a run, after a refused press, with another route selected), and the new WPF tests `TheApproachDistanceValidationNotifiesAndShowsThroughValidInvalidValid` and `TheRealQaRunnerExposesDiagnosticSetupAndExactRouteVerbs`. If you have the real Goku trace, add it as `m4_goku_real_trace.jsonl` (and its plan, if it exists) — the real-trace test activates by itself.
