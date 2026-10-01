# M4 diagnostics and spacing hardening

Base: `7b5dec36a1c6afd00b1d2277747f089929c53753`. M4 remains **unaccepted**. Two real user-found playbacks are now permanent regression fixtures.

| Fixture | Source | Files |
|---|---|---|
| Funny Valentine (`200 → 210 → 215 → 220`) | **Real** plan and trace from a Windows run (its recorded plan fingerprint matches the plan file byte-for-byte after newline normalisation) | `IKEMENLab.Tests/Fixtures/m4_fv_plan.json`, `m4_fv_trace.jsonl` |
| Goku (Step 1 expects State 17200, `state:-1/ctrl:114`) | **Reconstructed** from the frame numbers the user reported (the raw trace was not available): input `a` at 290, State 200 at 291, neutral at 308, driver timeout at 338 | `m4_goku_plan.json`, `m4_goku_trace.jsonl`, generator `scripts/make_m4_goku_fixture.py` |

Replace the Goku files with the real trace when it is available; the tests assert only the reported facts.

## Structured failure evidence (`StepEvidence`, in each failed step of `ikemenlab.xray.verify/1`)

Every field is measured from the recorded frames or quoted from the driver; nothing says which controller ran (`executedController` is always `unknown`). `null` = not applicable / not measurable, never `false`.

`failure` (the unmet prerequisite, in this precedence): `DifferentMoveEntered` (an attempted input led to another move) · `SourceStateNeverObserved` · `RequiredContactNotObserved` · `TimingNotSatisfied` · `InputNotAttempted` · `ExpectedStateNeverObserved`.
Plus: `expectedState`, `sourceState`, `sourceStateObserved` (+ first/last frame), `requiredContact`, `requiredContactObserved` (fresh contact inside the source occurrence; `null` when contact telemetry is missing), `earliestTick`/`timingSatisfied`,
`inputAttempted`/`firstInputFrame`/`attemptedKeys`, `firstMismatchState`/`firstMismatchFrame`, `expectedStateEverObserved`/`expectedStateFirstFrame`, `driverClaim`/`driverClaimFrame` (the driver's own words, labelled as a claim),
`configuredApproachDistance`, `derivedSeparationAtInput`/`Min`/`Max` and `derivedSeparationSources`.
Reason codes are unchanged (`WrongState`, `PreconditionNeverMet`, …); the evidence only sharpens the explanation. The first decisive mismatch is found once; a later return to neutral or the driver timeout never replaces it.

* **Goku** → reason `WrongState`; `expectedState 17200`, `firstMismatchState 200`, `firstMismatchFrame 291`, `expectedStateEverObserved false`, `inputAttempted true`. Text: *Step 1 failed: different move. Expected State 17200. Observed State 200 at frame 291 after the reported a input. State 17200 was never observed.* Focus frame 291. No claim that another controller "won"; hints offer a static explanation only as a possibility.
* **Funny Valentine** → reason `PreconditionNeverMet` (unchanged); `sourceStateObserved true` (frames 274–287), `requiredContactObserved false`, `inputAttempted false`. Text: *Step 2 was not attempted: the opening attack did not connect. State 200 occurred (frames 274–287), but the required contact (contact) was never observed.* (The old "the move it needed to start from never happened" was wrong.) Focus frame 287.

## Approach distance

Playback setup has **Approach distance** (blank = 60; whole number 1–1000; invalid input is a preflight issue, never silently replaced). It is persisted in settings like the other playback fields, validated by `PlaybackPreflight`, carried as `PlaybackSetup.ApproachDistance` into the real `PlanOptions`
(so it is in the plan JSON and therefore in the plan fingerprint), saved in the run record (`meta.json`) and shown in the diagnostic. It is documented everywhere as a **threshold**, not the attack's range. The actually derived separation around the input / source move is reported separately
(with its source, `derived:p2.x-p1.x`). Funny Valentine: configured 60, derived 60→59 during State 200, no contact. No automatic spacing retry, sweep or optimisation exists.

Fingerprints are now newline-normalised (CRLF) before hashing: Windows (CRLF) values already on record stay valid and the same plan hashes identically on any platform.

## Copy Full Diagnostic — `ikemenlab.xray.diagnostic/1`

One button; one Core model (`PlaybackDiagnostic`) with deterministic JSON (declaration order, LF) and a readable text rendering; the clipboard gets the text followed by the JSON. The QA verb `playback-diagnostic` logs the same text.
Contents: schema/kind (`run` | `refused` | `ended`), attempt id/state/issue, run and result identity, character + the index's per-file content hashes, exact route key and steps, the failed step's expected edge/controller/branch/confidence, modelled requirements, unmodelled expressions **as written**,
`triggerall` and numbered trigger groups with file:line, a bounded (≤ 10) list of statically related controllers sharing a command with file order (never "priority"), explicit coverage limitations, planned vs driver-reported input, structured failure evidence, telemetry at the anchor frame (state, ctrl, statetype, movetype, power, facing, position, derived distance + source),
configured vs derived spacing, a ±12-frame decisive window, missing telemetry (never-readable fields and unsupported capabilities), trace-integrity issues, engine executable/SHA/version/source, dummy, stage, plan fingerprint, and `executedController = unknown`.
Examples generated from the two fixtures (no character source was available, so their static section is empty): `xray-m4-diagnostic-example-valentine.txt`, `xray-m4-diagnostic-example-goku.txt`.

**Scoping.** The static evidence is captured **once, when the attempt starts** (`StaticSnapshot.Capture` from the graph the attempt was given) and stored with the attempt; a diagnostic never re-reads character files, so editing or re-indexing the character cannot change it (tested). The diagnostic always belongs to the **latest attempt**:
a refused press yields a `refused` diagnostic with no runtime section and none of an earlier run's identity; a cancelled/failed attempt yields `ended`; Replay and every new Play are new attempts; the button is enabled only while the selected route is the attempt's route. A result produced by an earlier attempt is never attached.
The statement "the semantic model does not require X" is never rendered as "the engine does not require X": the limitations say a missing requirement means the index did not model one.

## Validation honesty
Executed here: the Core suite (Linux). Compile-checked only: WPF app and WPF tests (scratch project against the desktop reference assemblies). Mock/fixture: the Goku trace (reconstructed), the mock-engine driver traces. Not executed: any WPF test, any real Windows playback.
