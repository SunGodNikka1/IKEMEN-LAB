# Character X-Ray — Milestone 2 hardening

Follows the acceptance in `xray-milestone-2-acceptance.md`. Three of the four findings raised by that review are closed
here; the fourth is recorded as a known limitation because it is a property of the character, not of the tool.

## 1. `timemod = a,b` failed to parse — fixed

`timemod` is a two-argument trigger: the engine has a dedicated compiler case for it (`compiler.go`, `case "timemod"`,
which calls `parseOldAnimElemStyle` to read two integers). The expression parser treated the comma as a separator and
rejected it, so the whole condition became an `expr.unparsed` diagnostic and an opaque blob in the edge's unmodelled list.

Funny Valentine uses it 49 times, almost always as `timemod = N,0`.

The parser now produces a dedicated `TimeModCompare` node, `ExprFacts` records both literal arguments as a `TimeModFact`,
`GateAnalyzer` recognises the shape, and the value/time pair is published in the edge JSON as `facets.timeMods`.

The trigger's *syntax* is now fully understood. What the index still does not resolve is the state's effective TimeMod, and
it does not pretend to: the condition is modelled and named, not guessed.

Measured on Funny Valentine (real install):

| | before | after |
|---|---|---|
| parse notes | 135 | **87** |
| relationships | 13,179 | 13,183 |
| `expr.unparsed` diagnostics | 20+ timemod | **0 timemod** |
| candidate edges | 539 | 539 |
| edges with unmodelled conditions | 537 | 537 |

**The candidate graph did not move, and that is the honest headline.** The hypothesis that `timemod` was suppressing
legitimate candidates is *not* borne out for this character: its `timemod` triggers sit on controllers that do not create
candidate edges. The parse bug was real and worth fixing, but it was not what was holding the routes back.

## 2. AIR `Interpolate *` warnings — fixed

`Interpolate Blend`, `Interpolate Offset`, `Interpolate Scale` and `Interpolate Angle` (along with `LoopEnd` and
`Continue`) are IKEMEN AIR block directives, not sprite frames. The AIR indexer fell through to its frame parser and
warned `air.bad-frame` on every one of them — dozens of warnings per character burying the genuine parse problems.
They are now recognised and skipped, as `LoopStart` already was.

## 3. Real Link-edge coverage — obtained

`EdgeKind.Link` (move → move that needs control back) had fixture coverage but zero real-character coverage: Funny
Valentine, Goku, kfm and a ten-character sample all produced `Link = 0`. The classifier is reachable — it needs
`CtrlRequired` — and the install has 2,634 bare-`ctrl` trigger lines, so this is a property of the characters sampled
rather than a dead code path.

Scanning the installed library found `(All-Stars) USJ_Nomu` with **45 Link edges**, all with `facets.ctrlRequired = true`
and all correctly `Inferred`. FV was not forced to produce one.

## 4. What is still open

- **`--max-unmodelled 0` still yields zero routes on Funny Valentine.** The dominant causes are `enemynear(var(59))`
  redirect chains (1,570 of 4,666 unmodelled entries) — the *index* of the enemy is a variable, so which opponent is meant
  is genuinely dynamic — plus `numhelper(...)` (669) and AI-only `!ailevel` gates (331). Modelling these is a much larger
  decision than a parser fix and is deliberately not attempted here.
- **`SelectedEdge` staleness** (the flag removed in `03f3cf1` was presumably intended for this) was not reproduced as a
  user-facing bug and is left alone rather than fixed speculatively.