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
4. **Close/cancel/replay:** close X-Ray during preparation and during live playback → engine process gone, sandbox folder gone, no record in `%LOCALAPPDATA%\IKEMEN Lab\xray-playback`, no crash or late UI. Cancel button the same. Replay starts a new run.
5. **Runtime DLLs:** with no setting and DLLs beside the engine (auto convention), and with the folder set in Playback setup (Browse…): the sandbox engine starts without STATUS_DLL_NOT_FOUND.
6. **Focus / linger:** is the engine window visible and the final pose on screen for ≈ 1.5 s after the combo? In the saved trace find `linger_start` / `linger_end`: report their frame difference (should be 90), engineTick and clock values, and your stopwatch/visual observation. If the pose is lost when `esc` is requested, say exactly when.
7. **Saved evidence:** `xray-playback\<id>\` has plan/trace/report/meta; View Trace shows `P1 x`, `P2 x` and Distance labelled with its source.
8. Production `Ikemen_GO.exe` hash unchanged; no orphan engine processes after any of the above.
