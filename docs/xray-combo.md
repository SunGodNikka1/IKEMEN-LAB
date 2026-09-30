# Character X-Ray — Milestone 2: Static Combo Candidate Graph

Built on the accepted Character Semantic Index ([xray.md](xray.md)). It asks *"which transitions could be combo links, and which routes do they form?"* of the
graph that already exists — no re-parsing, no simulation, no runtime. Every edge and route is a **candidate**; none is ever called valid or verified.

```
SemanticIndex ─▶ CandidateGraph (edges + MoveInfo) ─▶ ComboSearch (DFS · beam · best-first) ─▶ routes
   gate facets        evidence · confidence · unmodelled            constraints                    every step is an edge
```

## Readiness review: is the semantic graph enough for trustworthy candidates?

Before building, the index was checked against what candidate generation needs.

| Needed | In the M1 index? | Action |
|---|---|---|
| State-to-state transitions with their gates, evidence, confidence | yes (`ChangesState`, `SetsAttackerState`, `Gate`) | reused |
| **Source-state constraint of a `-1` cancel** (`stateno = 200`, `[200,210]`, `>= 1000`) | **no** — it fell into `otherConditions` | **closed:** `GateFacets.SourceStates / ExcludedStates / PrevStates` |
| Which conditions are *not* modelled (not just how many) | no — only a count | **closed:** `GateFacets.Unmodelled` (S-expressions) |
| Command, contact (`movehit/movecontact/moveguarded`), power, time, AnimElem, ctrl, state/move type | yes (facets) | reused |
| HitDef data (damage, pausetime, hitflag, p1stateno) | yes, as raw props | parsed literal-only by `MoveInfo` |
| State type / movetype / ctrl / anim and animation length | yes (Statedef props, AIR ticks) | reused |
| Meter cost / gate | yes (`ResourceCost`, `GatedByPower`) | reused |
| Ownership uncertainty (`f9d3ffa`) | yes (`p.declaredState`, `state.ambiguous-owner`) | inherited: edge is Inferred and lists "owning state" as unmodelled |
| Hit-stun / block-stun, pushback, juggle points, damage scaling, meter gain, cancel-window state | **no** | **not modelled**; every readiness report and every route says so |

Verdict: with the two additive facet changes the graph is enough for **trustworthy candidate generation** — *trustworthy* meaning each candidate is a real
edge of the files with its confidence and its gaps stated — and **not** enough to claim any route works. That is the job of Milestone 3.

## Edges

An edge is one *(source, target, controller, gate branch)*. Source states of a `-1/-2/-3` controller are expanded exactly from literal `stateno` tests
(`combo.source-literal`, StaticProven); a global gate that names no source is expanded to every attacking state that satisfies its statetype/movetype/contact
conditions (`combo.source-unconstrained`, **Inferred**). One heuristic node, `neutral` (common stand/crouch/walk states or an idle `ctrl = 1` Statedef), is the start of routes.

| Kind | Meaning |
|---|---|
| `Start` | neutral → move: a command starts a move |
| `Cancel` | move → move driven by an input or by contact; with `Contact = Hit/Contact` it needs `movehit`/`movecontact`, with `None` it also fires on whiff |
| `OnHit` | HitDef `p1stateno`: the attacker enters this state when the hit lands |
| `Chain` | no input and no contact: the move continuing by itself on a timer or animation frame |
| `Link` | needs `ctrl`, i.e. only after recovery; timing not modelled |
| `Recovery` | move → neutral |

Each edge carries: gate `facets`, `commands`, `contact`, `earliestTick` (from Time/AnimElem gates), `evidence` (rule ids + the index relationship it came from),
`unmodelled` (unrecognised trigger conditions plus structural caveats such as "owning state"), `notes`, and a `confidence` = the weakest of the relationship,
the source expansion and any neutral-node step. Edges whose target is not a constant (`value = var(5)+200`) are **not** in the graph; they are listed
(`dynamicTargets`) so the gap is visible.

## Search

`ComboSearch.Find(graph, options)` — deterministic (edges visited in id order, ties broken by id; the same index and options give the same bytes).

| Option | Default | Effect |
|---|---|---|
| `From` | neutral | start in this state (as if it just connected) |
| `MaxMoves` | 4 | most `Start/Cancel/Link` steps |
| `StartMeter` | 0 | a gate `power >= n` needs meter ≥ n; a move needs to afford its literal cost. Meter gained from hits is **not** modelled |
| `MaxFrames` | none | prunes on a lower-bound frame estimate |
| `HitConfirmOnly` | on | cancels must need `movehit`/`movecontact` |
| `AllowChains` / `AllowLinks` | on / off | continuation; links and recoveries through neutral |
| `MaxRepeats` | 1 | times a state may appear |
| `MaxUnmodelledPerEdge` | none | drop edges with too many unmodelled conditions |
| `Strategy` | DFS | `Dfs` exhaustive within `MaxExpansions`; `Beam` (width) and `BestFirst` (damage-greedy) are approximate |

A route reports `DamageKnown` (literal HitDef damage; `DamageComplete = false` when a move has several HitDefs or a non-literal damage, so it is a lower bound),
meter spent, `MinFrames` (a lower bound from Time/AnimElem gates, flagged when incomplete), the weakest edge `Confidence`, `UnmodelledCount`, and `Notes`.
`status` is always `candidate`. Routes are ranked by known damage, then fewer unmodelled conditions, then confidence, then length, then id.

## Interfaces

- CLI: `ikemenlab xray readiness <char>` · `candidates <char> [--from state|neutral]` · `combos <char> [--from] [--max-moves] [--meter] [--max-frames] [--top] [--strategy dfs|beam|best] [--beam] [--repeats] [--max-unmodelled] [--all-cancels] [--no-chains] [--allow-links]`.
- JSON: `ikemenlab.xray.combo/1` ([xray-combo-schema-v1.json](xray-combo-schema-v1.json), checked by a test).
- X-Ray window: **Combos** lens — candidate edges out of the selected state (hover for rules, notes, unmodelled conditions), a route finder with these options, and route steps that select their states in every other lens.
- Evidence rules: `combo.*` in [xray-evidence-rules.md](xray-evidence-rules.md).

## Limitations

- Nothing is simulated. Hit-stun/block-stun, pushback and spacing, juggle points (`airjuggle`), damage scaling, meter gain, and whether a cancel window is open on a tick are not modelled; a candidate route may be impossible in a real match.
- Statedefs are read as authored; helper-driven and projectile-driven combos are not followed (a `Helper` spawn is not a cancel).
- Source expansion for gates that name no state can be large; it is capped at 400 sources per gate and is Inferred.
- `neutral` is a heuristic; routes starting from it are at best Inferred.
- Duplicate `Statedef` and ownership caveats from Milestone 1 apply.
- ZSS/`[Include]` are still not indexed, so a character built on them yields a partial graph (the readiness report says how partial).

## Milestone 3 hand-off (Runtime Combo Verifier)

The route is the unit to verify. A candidate carries the edge ids, commands and expected states, so the verifier can: script the sandboxed dummy through the input sequence
(`runtime-prepare` already builds the disposable match), record `ikemenlab.xray.trace/0`, and compare the observed `stateNo` sequence, `moveHit`/`moveContact` and life
loss with the route's edges. Only an edge (and then a route) whose expected transition is observed is promoted to `RuntimeVerified`, with the trace frames as evidence;
a route that drops names the first edge that failed and the frame gap. Not started here.
