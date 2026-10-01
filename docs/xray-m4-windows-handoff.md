# Milestone 4 — Windows acceptance (Space Bunny Alpha / Cursor)

Core is unit-tested here (`XRayPlaybackTests`: 15 tests, incl. real-process cancel on Linux). The WPF part compiles against the
desktop reference assemblies in a scratch check but **has not been executed**. Please:

1. `dotnet build` / `dotnet test` the Windows solution, including `IKEMENLab.App.Tests` — the new `ComboPlaybackUiTests`
   (layout at 700/1000/1400 px, Play bound and disabled without a route, setup field + settings persistence) are drafted, unexecuted. Fix tests or XAML as needed and say which.
2. Publish, launch, open **Characters → select bangirasu → X-Ray → Combos → Find routes**.
3. **Playback setup**: set the X-Ray sandbox engine (+ `engine-dlls` is not in the UI yet: if the engine needs runtime DLLs, add a field or a documented path convention — report what you need).
4. Select route 0 → **▶ Play Combo**: confirm the engine window is visible, the combo performs, the window lingers ~1.5 s, the banner shows **Verified** with frames, steps show ✓.
5. Select route 2 (known `PreconditionNeverMet`): **Failed** at step 4, steps ✓✓✓✗, **Inspect Failure** selects step 4 and shows hints, **View Trace** opens at the decisive frame.
6. Clear the engine path or point it at the production engine: Play refuses with the setup reason / ends **Inconclusive**.
7. **Cancel** mid-run: engine process gone within ~1 s, sandbox folder removed, no record created. **Replay** after a result starts a new run.
8. Check: production `Ikemen_GO.exe` hash unchanged; `%LOCALAPPDATA%\IKEMEN Lab\xray-playback\<id>\` holds plan/trace/report/meta; repeated open/close of the X-Ray window leaves no stale playback state or orphan engine process.
9. Real-engine questions to answer: does the engine window take focus and stay on top of nothing important? Does `lingerFrames` work on the sandbox build? Is a replay's trace identical in states/frames (determinism)?
