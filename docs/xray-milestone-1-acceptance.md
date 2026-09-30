# Character X-Ray — Milestone 1: ACCEPTED

Reviewed by the main agent against commits `f9d3ffa` (Windows acceptance against the real install and Funny Valentine) and
`1737544` (runtime gate 4). Status: **ACCEPTED**, with the caveats below recorded rather than hidden.

## What was reviewed and why it satisfies the architecture

| Requirement | Evidence | Verdict |
|---|---|---|
| One UI-agnostic Character Semantic Index in `IKEMENLab.Core`, many lenses over it | six WPF lenses, the CLI and the JSON schema all consume `SemanticIndex`; the QA harness drove the real window and showed one selection highlighting rows in the other lenses | met |
| Every object has provenance; every relationship has evidence and a confidence | index contract tests, plus the real-install sweep over every character asserting evidence on every relationship | met |
| Heuristics never look like facts | labels cannot be `StaticProven` (tested); `f9d3ffa` found a real violation on Funny Valentine (87% of controllers sit under a `[Statedef]` whose number differs from the block header) and downgraded the consequences to Inferred | met, and the acceptance run improved it |
| `RuntimeVerified` unused | asserted by tests; runtime links never change a static confidence | met |
| Disposable runtime probe → JSONL → `TraceReader` → `RuntimeLink` resolves an existing `state:<n>` | `1737544`: P1 `stateNo 190 → state:190` and common `0 → state:0` from a real match; P1=190 and P2=191 in one frame proves the reads are per-player | met |
| Read-only against the user's install | source fingerprinted (17 files SHA-256) before and after; sandboxes removed | met |
| No engine modification | none was made or needed | met |

## What the runtime failure actually was

The earlier "the build exposes no player-scoped state" conclusion was wrong, and the fault was in the probe, not the engine: IKEMEN registers its
Lua getters under camelCase names (`stateNo`, `stateType`, `moveType`, `velX`, `animElemNo`, `moveHit`, `moveContact`), while the probe called
the MUGEN spellings (`stateno`, …). Lua is case sensitive, so those returned nil, and the capability map was a fingerprint of that naming bug.
The probe now tries the engine binding first and keeps the MUGEN spelling as a fallback. Lesson kept: **a capability map records what the probe could
reach, not what the engine has** — never write "the build does not expose X" from it alone.

## Caveats carried forward (none blocks acceptance)

1. **Source ↔ binary correspondence is unproven.** The local Go source postdates the installed executable and reports `development`. The Go source is
   mechanism evidence only; the compatibility claim rests on runtime evidence from the exact installed binary.
2. **Statedef ownership of `[State N, …]` blocks is an open semantic question.** `f9d3ffa` treats a block whose header number differs from its enclosing
   `[Statedef]` as an *inferred* owner, on the stated basis that the engine runs it under the header number. That claim has not been checked against engine
   source or a runtime trace. The downgrade is the conservative direction and stays, but the true rule should be settled (a runtime trace of a shared
   `[State 0, …]` block inside a per-move Statedef would decide it). Milestone 2 inherits the downgrade: those edges are Inferred and list "owning state" as unmodelled.
3. **Still null on this build:** `animElem`, `hitPause`, match-level fields. They stay null; nothing is guessed.
4. **The runtime evidence is one real association**, which is what the gate asked for. It is not a claim that every field is validated on every character.

## Reviewer note on the milestone boundary

Milestone 1 stops here. Runtime combo verification was deliberately not started before checking that the static graph carries enough for trustworthy
candidate generation; that review is `docs/xray-combo.md` ("Readiness review"), and it found and closed one real gap in the index.
