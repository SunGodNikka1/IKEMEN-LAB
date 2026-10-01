# Character X-Ray Milestone 3 — ACCEPTED

Main Agent architectural review of unified state `xray-m3-reaudit` @ `c8143cd65074afe15bb340cdcda4adde2929a6db`
(provenance confirmed with `git ls-remote origin refs/heads/xray-m3-reaudit`; strict descendant of `2fc9105`).

## Why it is accepted

| Question | Evidence |
|---|---|
| Is `RuntimeVerified` only produced from real trace evidence? | Only `RouteVerifier` emits it, through the three `runtime.*` rules; candidate graph, edges and search results never do (tests). |
| Are the three outcomes distinct? | Real-engine runs: Verified (bangirasu route 0, 4 transitions, fresh contact, continuous hitstun), Failed (route 2, `PreconditionNeverMet` step 4), Inconclusive (production engine, `InputInjectionUnavailable`). |
| Could the verifier be fooled by stale or unrelated evidence? | Codex adversarial audit: every finding accepted or extended (wrong-state recovery, inherited contact, duplicate/skipped frames, truncated tails, ctrl regained during H, input override release, P2 hardware leakage, silent engine fallback). 33 adversarial tests. One regression found by running them (tail check masking an observed drop) and fixed. |
| Is the production install safe? | Sandbox copy only; the sandbox engine override is explicit, hashed and recorded (`engineSha256`, `engineSource`); a missing override throws instead of falling back; the production `Ikemen_GO.exe` stayed byte-identical. |
| Is the engine-side hook contained? | Six Go tests (inert by default, P1/P2 isolation, simultaneous slots, release, bounds, replace-not-accumulate). |
| Is the UI entry reachable? | `WrapPanel` fix for the clipped X-Ray button, five WPF layout tests at 200/359/900 px, published smoke test Character → X-Ray → Combos, repeated open/close clean. |
| Suite | 620 passed, 0 failed (Windows), Release clean, win-x64 publish clean. |

## Recorded caveats (accepted, not blocking)

1. **Playback needs the X-Ray sandbox engine build.** The production engine cannot receive scripted input; M3 reports that as `Inconclusive`. M4 therefore treats the sandbox engine as an explicit, user-visible setup item.
2. The input hook indexes `sys.inputRemap[controller]`; the proof used the default identity mapping. Non-default remapping is a hardening case.
3. `LocalInput` negative-slot panic is a pre-existing engine defect, unreachable from play; the X-Ray guard is correct.
4. The scripted-host `panelW=0` limitation stays documented; the real WPF layout tests cover the dimensions that caused the clipping.
5. `Verified` means: this route, on this engine build, this subject/dummy/stage, this run. It is not a claim about every spacing, round state or opponent.
