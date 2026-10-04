# Character X-Ray

One **Character Semantic Index**, many synchronized lenses. The index is built from a character's own files and lives entirely in
`IKEMENLab.Core` (`IKEMENLab.Core.XRay`, no UI types). The WPF window, the `ikemenlab` CLI and the JSON schema are all views of it.

```
DEF [Files] ─▶ CMD · CNS · ST · AIR · common1.cns · SFF
                        │
                SourceLexer (tolerant, line numbers, never throws)
                        │             ExprParser (trigger expressions, RawExpr on failure)
                        ▼
   Command / State / Controller / AIR / Variable / Helper indexers ─▶ relationships (each with evidence)
                        │
        AbilityGrouper · LabelInferrer (Inferred only)
                        ▼
                  SemanticIndex ──▶ Explain · TransitionsFrom · VarUsage · StateGraph · XRayJson
                        │
        ┌───────────────┼──────────────────────────┐
     WPF lenses     ikemenlab xray …        runtime traces (spike) attach by state number
```

Milestone 1 is static only: the index and its lenses. **Milestone 2 (static combo candidates) is described in [xray-combo.md](xray-combo.md)**. **Milestone 3 (runtime combo verification: a candidate route played in a disposable match and judged from the trace) is described in [xray-runtime-verifier.md](xray-runtime-verifier.md); accepted in [xray-milestone-3-acceptance.md](xray-milestone-3-acceptance.md).** **Milestone 4 (Play / Verify Combo from the Combos lens) is in [xray-m4-play-combo.md](xray-m4-play-combo.md).** **Phase 2 (Play Ability and Preview State from the Ability Atlas, on the same playback stack) is in [xray-phase2-play-ability.md](xray-phase2-play-ability.md).** **Phase 3 (Sequence Lab: manual follow-up experimentation) is in [xray-phase3-sequence-lab.md](xray-phase3-sequence-lab.md).**

## The model

Objects have a **stable id** built from content ordinals, never line numbers, so ids survive edits that only move lines
(a test shifts every line and compares the id lists).

| Id | Object |
|---|---|
| `char:<folder>` | the character |
| `file:<relative path>` | a source file |
| `cmd:<name>` (`#2` = alternate definition of the same name) | a `[Command]` |
| `state:<n>` (`state:<n>#2` = shadowed redefinition) | a `[Statedef]` (common states are flagged `common`) |
| `state:<n>/ctrl:<i>` | the i-th `[State]` controller in that Statedef |
| `state:<n>/ctrl:<i>/hitdef` | its HitDef / ReversalDef / Projectile hit parameters |
| `helper:<id>`, `proj:<id>` | helpers and projectiles (`…dynamic@<controller>` if the id is not literal) |
| `var:<var\|fvar\|sysvar\|sysfvar>:<n>` (`:*` = index not literal) | a variable |
| `anim:<n>`, `anim:<n>/frame:<i>`, `anim:<n>/frame:<i>/clsn<1\|2>:<j>` | AIR action, frame, collision box |
| `sprite:<group>,<index>` | an SFF sprite (opens the Sprite Inspector) |
| `ability:<entry state>` | a group of states entered by a command or by AI |
| `entity:target`, `resource:power` | engine entities the character acts on |

Every object carries `source` (file, first line, last line) where it comes from a file. Every relationship carries `evidence` (rule id, source, note).
The versioned JSON is described by [`xray-schema-v1.json`](xray-schema-v1.json) (schema id `ikemenlab.xray/1`).

Relationship kinds: `Contains`, `ChangesState`, `SetsVictimState`, `SetsAttackerState`, `Binds`, `AffectsTarget`, `SpawnsHelper`, `HelperRunsState`,
`SpawnsProjectile`, `ReferencesCommand`, `ReadsVar`, `WritesVar`, `ResetsVar`, `UsesAnim`, `AnimUsesSprite`, `HasClsn`, `DefinesHitDef`, `GatedByPower`,
`ResourceCost`, `EntryPoint`, `PartOf`, `ActiveAtFrame`.

Each controller also has a **gate**: `triggerall` lines AND at least one `triggerN` group, with per-branch **facets**
(commands, contact, power, time, AnimElem, ctrl, state/move type, AILevel, and `otherConditions` = how many conjuncts are not a recognised shape).
`otherConditions > 0` means the facets are incomplete; consumers must not treat them as the whole condition.

## Your names (Phase 1: semantic name overlay)

You can give any state, ability, command, animation, helper, projectile or variable a human name ("State 66345" → "Revolver Shot").
X-Ray's own name and the id stay as technical metadata; ids, relationships, evidence and confidence never change.

- **One resolver.** `SemanticIndex.NameOf(id)` / `SemanticIndex.Names` (`IKEMENLab.Core.XRay.Names.SemanticNames`) is the only place a
  label is decided. Lenses, the details panel, search, Explain, the Combos lens and route titles, trace rows, diagnostics and the CLI all use it.
- **Linked names.** Naming an ability also names its entry state, and the reverse, unless that object has its own name (`nameSource = linked`).
- **Storage.** `%LOCALAPPDATA%\IKEMEN Lab\xray\names\<hash of the character folder>.json`, written atomically. Character files are never written.
- **Fingerprint protection.** Each name stores a content fingerprint (`NameFingerprint`) of what the object *is*. For a state that is the Statedef
  header (type, movetype, physics, anim, ctrl, poweradd …) plus its HitDef attributes. Line moves, comments and AI conditions edited inside
  the state do not invalidate it; a new animation, state type or attack attribute does. A command's fingerprint is its steps, time and buffer.
  An animation's is its frame sprites. A helper's is its parameters and states. A variable's is the states that write it.
- **Review, never guess.** A name whose object changed is **Stale**, and one whose object vanished (e.g. renumbered) is **Orphaned**. Neither is shown
  until you decide. X-Ray → details panel → *Names to review*: **Keep on changed object**, **Move to selected**, or **Discard**.
  Unchanged objects with the same fingerprint are listed as suggestions only.
- **Diagnostics** freeze the names in effect when a Play attempt starts (`names` block, with X-Ray's name beside each). The verifier's own verdict
  text keeps state numbers: names are presentation, not evidence.
- **CLI.** Every `ikemenlab xray` command shows names (`"name"` = your name, `"defaultName"` = X-Ray's, `"nameSource"`; a stale saved name adds
  `"nameReview"`). `--no-names` turns them off. `ikemenlab xray names <character> [list|review|set <id> <name>|clear <id>|keep <id>|attach <old> <new>]`
  manages them (`200` means `state:200`). `--names-store DIR` uses another store (tools/tests).

## Play Ability and Preview State (Phase 2)

The Ability Atlas has an **Ability Lab** bar for the selected ability. Details are in [xray-phase2-play-ability.md](xray-phase2-play-ability.md).

- **▶ Play Ability** performs the ability from neutral through its own command: a one-step route on the M3/M4 pipeline. The result is
  **Performed / Not performed / Could not be tested**, from the route verifier's step check. Performed cites only `runtime.transition-observed`;
  the verifier's combo continuity check is not used. Contact, reaction, damage and recovery are shown as measurements of that run.
- AI-only and cancel-only abilities are refused with the reason, never given a guessed lead-in.
- **Preview State (not proof)** forces the entry state with the engine's `changeState`. It is never a verdict and cites no evidence;
  `"proof": false` appears everywhere it is recorded.
- One playback session per window: Play Combo, Play Ability and Preview State never run two engines at once, and each result is shown only
  against its own route, ability or previewed state.

## Sequence Lab (Phase 3)

After **Play Ability** the Ability Lab shows the resulting situation, and **Try Follow-Up** opens the **Sequence Lab** tab. There you chain abilities with
walk, wait, chase-until-within, dash and jump, run the sequence ×1 / ×10 / ×50 (each trial replays it from neutral through the same playback stack), and
read **True Combo / Connected Sequence / Did Not Connect / Could Not Test** with a plain reason per step. A chase after a knockdown that connects is a
*connected sequence*, not a combo. Experiments go to their own store and never touch the playback history. Details:
[xray-phase3-sequence-lab.md](xray-phase3-sequence-lab.md).

## Evidence confidence

`StaticProven` · `Inferred` · `Unknown` (and `RuntimeVerified`, reserved and never emitted). The rules and the deterministic-vs-inferred table are in
[`xray-evidence-rules.md`](xray-evidence-rules.md). In the UI one shared style is used everywhere: filled disc and solid line = proven,
half disc and dashed line = inferred, `?` and dotted line = unknown, and the glyph is repeated at the middle of every graph edge.

## The lenses (WPF: Characters → select → **X-Ray…**; the seventh, Combos, is Milestone 2)

All lenses read one selection. Selecting State 10800 anywhere highlights, in every other lens, its incoming commands and gates, controllers, helpers,
projectiles, variables, animation, HitDefs and outgoing states; the right panel lists them all with confidence and rule, and shows the source lines.

| Lens | Shows |
|---|---|
| Ability Atlas | abilities (entered by a command or AILevel) grouped by an **inferred** category; membership follows the state graph |
| Triggers | AND/OR tree of a controller's triggers, what each line means, facet summary, and what it does |
| State Graph | neighbourhood of a state (1–3 hops); ChangeState, victim, attacker and helper-spawn edges |
| Variables | every var/fvar/sysvar with readers, writers, clearers and scope; traits and names are inferred labels |
| Helpers | helper and projectile family: spawners, states run, HitDefs, variables written |
| Combos (M2) | candidate combo edges out of the selected state and a route finder; every row is a candidate with its confidence and unmodelled conditions |
| Animation & CLSN | frames by duration, Clsn1/Clsn2 with `Default` inheritance, controllers gated on a frame; a frame's sprite opens the Sprite Inspector |

## CLI for agents

```
ikemenlab xray index        <character>                  # whole index as JSON
ikemenlab xray explain      <character> state:10800       # incoming commands → gates → controllers → helpers → vars → anim → HitDefs → outgoing states
ikemenlab xray var          <character> "var(20)"         # every read / write / reset, with scope and source
ikemenlab xray helper       <character> 340
ikemenlab xray ai-entry     <character>                   # controllers that read AILevel
ikemenlab xray find         <character> throws|projectiles|counters|supers|specials|normals|mobility|summons|victim-states
ikemenlab xray transitions  <character> 200               # ChangeState edges leaving a state, with gates
ikemenlab xray search       <character> love
ikemenlab xray rules                                      # the evidence rules
ikemenlab xray readiness    <character>                   # (M2) how much the candidate graph can and cannot say
ikemenlab xray candidates   <character> [--from 200]      # (M2) candidate combo edges
ikemenlab xray combos       <character> --meter 1000      # (M2) deterministic candidate routes
ikemenlab xray rank-subjects --root R                     # (M3) which installed character suits a first runtime proof
ikemenlab xray verify-plan  <character> --route 1         # (M3) the scripted input plan for one candidate route
ikemenlab xray verify-trace <character> --route 1 --trace t.jsonl   # (M3) judge a recorded trace: exit 0 verified, 4 failed, 5 inconclusive
ikemenlab xray runtime-verify <character> --root R --dummy kfm --stage stages/x.def --adapter inject.lua   # (M3) play it in a sandbox
```

`<character>` is a folder or a `.def`; `--root` names the IKEMEN root if it is not the parent of `chars/`. Output is deterministic JSON (`--text` for `explain`).
Exit codes: 0 ok, 2 usage, 3 not found. `find` results come from **inferred** labels and say so in each result's `labels[].confidence`.

## Known limitations (milestone 1)

- Nothing is evaluated: `StaticProven` means the code contains the edge, not that it can fire. Triggers are structure, not truth values.
- ZSS scripts and `[Include]` are detected and reported, not indexed. Only CMD/CNS/ST/AIR (+ common states, SFF metadata) are read.
- Dynamic targets (`ChangeState value = var(5)+1200`, `anim = var(2)`, non-literal helper ids, `var(var(n))`) are `Unknown`; no candidate values are guessed.
- Duplicate `Statedef`s: the first is treated as in effect (flagged `state.duplicate`); the engine's true precedence is not verified.
- Which entity (root or a helper) runs a shared state is unknown, so variables are keyed by number and carry a **scope** per access rather than a per-entity identity.
- Ability grouping follows ChangeState/Helper/hit-victim edges from a command- or AI-gated entry, stops at common states, `-1/-2/-3` and states shared by many abilities, and is capped at 400 states; categories are keyword-free heuristics.
- The Trigger lens explains one controller at a time; the State Graph shows a neighbourhood (up to 90 nodes), not the whole character.
- Real characters have only been exercised through synthetic fixtures here; run the opt-in sweep (below) on a real install.

Opt-in real-install sweep: `IKEMENLAB_REAL_ROOT=D:\IKEMEN dotnet test --filter XRayRealInstall` (optionally `IKEMENLAB_XRAY_CHARACTER=Valentine`, `IKEMENLAB_XRAY_DUMP_DIR=…`).

## Roadmap and the milestone 2 architecture

1. **Semantic Index + X-Ray** (this milestone) 2. Static Combo Candidate Graph 3. Runtime Combo Verifier 4. Automatic timing-window search 5. Combo Lab, infinites, conditional routes.

Milestone 2 asks *"which transitions are combo candidates?"* of the graph that already exists, without re-parsing:

- **`CandidateGraph`**: a view over `SemanticIndex.TransitionsFrom` / `StateGraph`, one edge per (state → state, controller, gate branch). Each edge carries the branch's `GateFacets`: required commands, `movehit/movecontact/moveguarded`, power gate, time and AnimElem window, `ctrl`, state type, and `otherConditions` (incomplete facets are flagged, never dropped).
- **Move roles** come from the existing Inferred ability categories plus HitDef data (attr, pausetime, hitflag, p1stateno/p2stateno), each edge keeping its confidence.
- **Cancel windows** are estimated from AnimElem/Time gates against the AIR timeline that the index already maps to frames (`ActiveAtFrame`).
- **Search**: deterministic DFS, beam and best-first over the candidate graph, constrained by meter (`ResourceCost`/`GatedByPower`), route length, frame budget, hit-confirm, juggle/`airjuggle` and no repeated state; a genetic search is optional and only for continuous timing later.
- **Output**: routes as ordered edges, each tagged StaticProven / Inferred / Unknown, with the unmodelled conditions listed. `ikemenlab xray combos` (reserved today) returns them as JSON.
- **Hand-off to milestone 3**: the runtime trace (`ikemenlab.xray.trace/0`, see [xray-runtime-spike.md](xray-runtime-spike.md)) is joined to states and, later, to edges; only the exact plan-bound route/run report whose transitions and continuity pass is labelled `RuntimeVerified`; graph edges keep their static confidence. That value already exists in the schema and is never produced before then.
