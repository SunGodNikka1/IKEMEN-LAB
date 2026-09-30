# Character X-Ray — Milestone 2: ACCEPTED

Reviewed by the main agent against `1457e55` and `b6fdf27` (static combo candidate graph, deterministic search, CLI,
Combos lens) and `03f3cf1` (independent Windows acceptance against the real install and Funny Valentine).

## Verdict: ACCEPTED

Milestone 2 did what it promised, end to end on real data:

```
source code → semantic graph → candidate graph → deterministic route search → honest uncertainty
```

Evidence from the real install (`D:\Games 3\Ikemen_GO-v1.0.0`, Funny Valentine): 8,261 objects, 13,179 relationships,
284 states, 50 abilities, 539 candidate edges (Start 235 / Chain 181 / Cancel 94 / Recovery 27 / OnHit 2 / Link 0),
489 Inferred vs 50 StaticProven, 28 dynamic targets reported and not guessed, CLI output byte-identical on repeat runs,
dfs/beam/best-first deterministic and agreeing, meter and unmodelled policies behaving, the real WPF Combos lens working
with route-step selection propagating through the shared X-Ray selection, and 18/18 combo tests passing.

**No incorrect confidence promotion was found.** The Milestone 1 fix for ambiguous controller ownership survives correctly
into the candidate graph: edges from a controller whose block header disagrees with its Statedef stay `Inferred` and list
"owning state" as an unmodelled condition rather than being promoted to a proven fact.

Funny Valentine is an unusual subject, but that is a property of the character, not a reason to reject the milestone.

## Hardening

Accepted, then hardened in the same cycle. The coverage findings that prompted it — `timemod = a,b` failing to parse, AIR
`Interpolate *` noise, and the absence of real Link-edge coverage — are recorded in `xray-milestone-2-hardening.md`.