# M3 re-acceptance after the Codex adversarial audit

Base reviewed: `b7eeb2a`. Codex branch `feb1003a` (adds `eda699a`, `feb1003a`).

## Codex finding verdicts

Accepted as written: wrong-state recovery later satisfying a step; inherited/stale contact;
contact first appearing in the target state; duplicate/skipped/reordered trace evidence;
incomplete continuity intervals; P2 regaining ctrl while still moveType=H; `Prefer`
contradicting evidence of an actual attempt; late adapter failure; input override release;
P2 hardware-input leakage; silent production-engine fallback.

Accepted and extended: the sandbox now records the actual executable, its SHA-256 and its
source into the trace, and refuses to substitute the install engine when an override is
requested but missing. A missing requested engine throws instead of silently falling back.

Revised (one regression found by running Codex's C#): the tail-completeness check ran before
drop detection, so a trace that both dropped the combo and ended early reported Inconclusive
instead of Failed/ComboDropped. Missing evidence must not mask observed evidence; the tail is
now a gate on Verified only, and an observed drop outranks it.

Rejected: none.

## Executed evidence (this machine)

- Windows suite: 615 passed, 0 failed (582 baseline + 33 adversarial).
- Go hook audit `f51ac49`: 6 tests, all pass, in `src/xray_input_test.go`. Covers inert-by-default,
  slot scoping, simultaneous slots, release with no stuck bits, out-of-range rejection, and
  replace-not-accumulate. Requires `CC=gcc`, `CGO_ENABLED=1`, `GOEXPERIMENT=arenas`, mingw64 on PATH.
- Pre-existing latent defect found and recorded, not fixed: `LocalInput` guards its device lookups with
  `in < len(...)` only, so a negative slot panics on `sys.keyConfig[-1]`. Unreachable from play; the
  X-Ray guard itself is correct (`in >= 0 && in < len`).

## Fresh real-engine results (tightened verifier)

| Outcome | Case | Detail |
| --- | --- | --- |
| Verified | bangirasu route 0, patched sandbox engine | 494 frames, 4 transitions, contact 417/439/465, continuity 417-476, no drop |
| Failed | bangirasu route 2, patched sandbox engine | Failed / PreconditionNeverMet step 4; driver emitted `step_wait` but never `step_ready` |
| Inconclusive | bangirasu route 0, production engine | InputInjectionUnavailable, engineSha256 `F7277138...` |

Verified provenance: planFingerprint `F326066F...`, engineSha256 `EC1F2F7B...`, engineSource the
patched sandbox binary. Production `Ikemen_GO.exe` byte-identical at `F7277138...`.

M4 not started.
