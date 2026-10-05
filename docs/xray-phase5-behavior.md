# X-Ray Phase 5 — Watch & Ask + Behavior Recognition

```
static AI rules (AILevel controllers) ──▶ Possible           "the code contains a rule consistent with it"
watched match (both sides on the AI) ──▶ trace ──▶ episodes ──▶ Observed            "a complete episode was recorded"
repeated complete episodes, ≥2 runs, ≥2 setups ───────────────▶ Confirmed Pattern   (explicit rule, configurable)
character files or engine changed ────────────────────────────▶ runtime evidence is stale (back to what the code says)
```

The goal is to read an existing AI as combat behavior — Knockdown Chase, Anti-Air, Punish, Pressure, Retreat … — instead of state numbers. Nothing
here edits the character or its AI, and nothing claims which AI controller fired.

## Architecture (all in `IKEMENLab.Core/XRay/Behavior`, reused by the app and MCP)

| Piece | File | What it does |
|---|---|---|
| Condition vocabulary | `BehaviorConditions.cs` | 21 plain conditions, each with its runtime evidence (`Holds`) and the literal trigger shapes read as it (`FromTrigger`). Anything else stays Unknown — not a trigger solver. |
| Template library | `BehaviorTemplates.cs` | 10 fixed templates: name, category, conditions, actions, outcomes, limitations, completion rule. |
| Coordinates | `BehaviorCoordinates.cs` | Puts both fighters on one scale (320-wide units, the fighter's frame) before anything spatial is read — see *Coordinates*. |
| Episode detector | `BehaviorDetector.cs` | Pure function trace → episodes (exact frame ranges, steps as data, conditions, the fighter's states, outcome) + incomplete counts + missing telemetry + notes + who controlled P1. Split per round. `Version` 7; stored runs of an older version are re-detected from their trace when listed. |
| Static matcher | `StaticBehavior.cs` | AI-entry ChangeState controllers whose recognised conditions and target kind fit a template → `Possible`. |
| Evidence / cards | `BehaviorEvidence.cs` | Levels, the Confirmed Pattern rule, staleness, `BehaviorCatalog.Build` → cards. |
| Store + watch runner | `BehaviorStore.cs` | Watched runs (`%LOCALAPPDATA%\IKEMEN Lab\xray-watch\<run>\`: meta.json, trace.jsonl, run-notes.txt, behavior.json; newest 40 per origin). `BehaviorWatch.Run` records a match through `ComboPlaybackService.Watch`. |
| Text, timeline, Why | `BehaviorText.cs` | Names-first rendering (Phase-1 names applied at display time), the semantic timeline, and the bounded "Why?". |

The runtime path is the existing one: `ComboPlaybackService.Watch` → `VerifyRunner.Observe` (the same disposable sandbox, probe, cleanup and
commit/cancel gate as every playback, with no plan: both sides on the engine's AI, P1 at AI level 8 by default) → `PlaybackSession.WatchAsync`
(one engine at a time, the machine-wide `RuntimeBroker` lease). A watched match is a recording, never a verdict (`AttemptState.ObservationProduced`).

The research repository's situation → action → outcome census (`tools/fv_situation_action_census.py`) is the conceptual source: its situation
predicates (opponent lying / airborne / attacking, the 60 / 160 range bands), the unit "situation → chosen action → outcome → follow-up", and the
OBSERVED vs VERIFIED discipline. The production implementation is the C# detector above; nothing runs the Python tool.

**Probe 0.4** adds five engine fields per player (null when a build lacks them): `aiLevel`, `hitFall`, `backEdgeBodyDist`, `frontEdgeBodyDist`,
`numProj`. They make "Enemy Falling", "Cornered", "Enemy Projectile", "was P1 really on the AI?" and the coordinate scale engine facts instead of
guesses. Both the installed engine and the configured `xray_sandbox_engine.exe` expose all five.

## Condition vocabulary

| Condition | Runtime (trace) | Static (literal trigger shapes) |
|---|---|---|
| Enemy Falling | P2 hit + airborne + engine fall flag (without the flag: airborne hitstun while descending) | `P2StateType = A` with `P2MoveType = H`, `EnemyNear,HitFall`, `P2StateNo 5050–5099` |
| Enemy Lying | P2 StateType L | `P2StateType = L`, `EnemyNear,StateType = L`, `P2StateNo 5100–5119` |
| Enemy Getting Up | P2 in 5120, or just left L without control | `P2StateNo = 5120` |
| Enemy Attacking / Airborne / in Hitstun / Blocking | P2 MoveType A / StateType A not hit / hit / 120–159 | `P2MoveType = A`, `InGuardDist`; `P2StateType = A`; `P2MoveType = H`; `P2StateNo 120–159` |
| Enemy Projectile | P2 NumProj > 0 (Projectile controller only) | `EnemyNear,NumProj > 0` |
| Close / Medium / Long Range | distance < 60 / 60–160 / > 160 (320-wide units) | `P2BodyDist X` / `P2Dist X` `<`/`>` a literal in that band |
| Distance Decreasing / Increasing | distance changed ≥ 6 over 6 samples | — |
| Can Act / Has Meter | P1 Ctrl / Power ≥ 1000 | `Ctrl`; `Power >= N` with N ≥ 1000 |
| Low Life | P1 Life ≤ 30% of its first sample in the round | `Life < N` with a literal N ≤ 300, or `Life < LifeMax × k` with k ≤ 0.3 (`Life < 900` stays Unknown) |
| Cornered / Enemy Cornered | P1 / P2 BackEdgeBodyDist ≤ 20 | `BackEdgeBodyDist < N` (N ≤ 60), `EnemyNear,…`, `FrontEdgeBodyDist < N` |
| Move Connected / Blocked / Whiffed | read per attack from what newly happens during it (see *Templates*) | `MoveHit`, `MoveContact`; `MoveGuarded`; — |

A negated comparison, a variable, a computed value or a character-specific state is never read as a condition. A condition found inside a larger
expression (e.g. `random < 100 + 300 * (enemynear,statetype = L)`) is kept but marked *weighed*, not *required*.

## Evidence rules

| Level | Requires | Never |
|---|---|---|
| **Possible** | A static AI rule (a ChangeState controller that reads AILevel) whose literal conditions and target fit the template. Knockdown Chase needs both an *approach* rule (enemy down → a movement state) and a *follow-up* rule (enemy down or getting up → an attack). | Runtime proof. It stays Possible however many rules match. |
| **Observed** | ≥ 1 complete episode in a watched run on the **current** character files hash **and** engine sha256, with P1 on the AI. | Counted from scripted runs (Play Ability / Sequence Lab), from a run where the engine reported AI level 0, or from stale runs. |
| **Confirmed Pattern** | `ConfirmedRule.Default` = **≥ 3 complete episodes in ≥ 2 separate watched runs against ≥ 2 different setups** (setup = opponent, stage, opponent AI level), all current. Configurable internally (`ConfirmedRule(MinEpisodes, MinRuns, MinSetups)`, passed to `BehaviorCatalog.Build`). | Statistics: it is a plain threshold. |

**Stale.** A run does not count when the character files hash differs from now, the engine sha256 differs, the current engine is unknown, or the run did
not record its engine. Cards show how many episodes went stale and why; the card falls back to what the static code says. A run where the engine reported
P1 off the AI is shown (badge *Not AI evidence*) but never counted. Incomplete situations (e.g. a knockdown and an approach with no attack) are counted
separately and never as episodes.

## Templates

| Template | Complete episode | Notes |
|---|---|---|
| Knockdown Chase | opponent falls/lies → the fighter's **own** movement toward them (≥ 15 units, not an attack's motion) → an attack starting while they are down or ≤ 15 frames after they can act | contact optional (connected / blocked / whiffed) |
| Ground Follow-Up | the opponent loses life while lying (an OTG hit) | one per time on the ground; contact required |
| Wake-Up Pressure | opponent getting up, fighter within 60 and no approach in the 30 frames before → attack around their wake-up | getting up = 5120 or leaving L without control; ≤ 90 frames to control |
| Anti-Air | opponent airborne on their own ≥ 3 frames, within 160 → the fighter attacks from the ground while they are still airborne | |
| Punish | the opponent's attack misses or is blocked → the fighter's attack, started in its second half or ≤ 6 frames after, hits while they have no control | attempts that miss are counted as incomplete |
| Pressure Continuation | an attack is blocked → another attack within 20 frames | |
| Defensive Response (Block) | opponent attacks → the fighter enters a guard state (120–159) **within that attack** (≤ 2 samples after it, never once their next attack started) and is not hit | custom guard states are not recognised |
| Retreat / Reposition | the fighter's own movement away ≥ 30 units over ≥ 8 frames while free | knockback, guard pushback, the opponent walking away excluded |
| Meter / Super Attempt | with ≥ 1000 power, a Super ability's entry state starts spending ≥ 500, or any move spends ≥ 990 | Super = X-Ray's inferred category |
| Projectile Response | the opponent's projectile count rises → the fighter jumps, guards, moves or attacks within 45 frames | Projectile-controller projectiles only (NumProj); helper projectiles are invisible |

**Outcomes** are read only from things that newly happen during the attack (the defender loses life while not guarding, newly enters a hit state, or the
attacker's MoveHit rises from zero; blockstun entered or MoveContact rising without MoveHit = blocked) — so an attack against an already lying or
stunned opponent is not read as a hit just because they are in a hit state. Attacks are segmented by state: a move made of several attack states is
several attacks (the timeline does not call the first part a miss when it runs straight into the next).

**Movement** (approach / retreat) is the fighter's own velocity toward / away from the opponent, capped by how much the distance between them actually
changed — both camera-free. Being pushed or knocked back has no velocity of its own; walking into a body or a wall changes no distance. (Recorded x
positions are screen-relative, so they also move when the camera scrolls; they are used only when velocity or distance is missing.)

## Coordinates

Each player reports position, velocity and edge distances **in its own localcoord**, and the engine measures x from the camera — for a 320 character
`x = world − camera`, for a 1280 character `x = 4 × world − camera` (measured, not assumed: in the real FV vs kfm720 run the 1280 character's implied
screen drifted by 0.75 × the camera). Subtracting raw positions therefore gave FV vs kfm720 "distances" of ~1000.

`BehaviorCoordinates.Normalize`:
1. The unit ratio between the two players is the engine's own: the median ratio of their screen-edge distance sums (the same screen, measured by each) —
   exactly 0.2500 for FV vs kfm720, 1.0 for same-scale pairs.
2. If it is not 1 (± 5%), the opponent's position is re-expressed in the fighter's frame from its own edge distances (`fighter's screen centre +
   ratio × opponent's screen offset`, exact up to half a body-width asymmetry); its velocity and edge distances are scaled; a probe-derived distance is
   recomputed (the engine's own P2DistX, when a build exposes it, is already in the fighter's units).
3. Everything is read in 320-wide units using the fighter's DEF `localcoord` (stored with the run; none = 320).

Same-scale matches come out unchanged. A trace without edge distances (probe < 0.4) is used as recorded, with a note. The timeline and Why read the
trace through the same pass (`BehaviorDetector.Samples`). Each correction is listed in the run's notes.

## Why?

`BehaviorWhy.Explain(index, graph, run, log, frame)` returns the observed context (posture, distance, the fighter's state, control, power), the
conditions that held, what the fighter did next (≤ 1.5 s: a movement toward or away that is under way at that moment or starts within 1 s, then the
attack after it; else an attack; else the next state, e.g. a guard), the matching patterns, and the static AI rules that are *consistent* with it
(target = a state the fighter entered next, and no required readable condition was false just before it was entered). It always carries
`CauseKnown = false` and:

> Which AI rule actually fired is not recorded — X-Ray has no controller attribution yet. This is what was observed and which static rules are
> consistent with it; it is not proof of why the AI chose it.

It never says a rule fired or that the AI chose something "because" of it. Rules read in plain words: `when Can Act → Ground Punch (230)`;
conditions only found inside a larger expression are listed once as `weighs …`.

## Watch & Ask (app)

X-Ray window → **Watch & Ask** tab (read-only):
- **Behaviors** (left): every template with its evidence badge (Confirmed: filled green; Observed: green outline; Possible: amber; Not seen: plain),
  counts, and the selected card: summary, level meaning, When / Does / Outcomes, setups seen against, stale evidence, the moves involved (buttons that
  select the state/ability), and — behind expanders — limitations, the consistent static rules, and the technical evidence.
- **Watch a match** (right): seconds and opponent AI level; the match runs in an IKEMEN window and closes by itself (the timeline is read when it ends).
- The easy view: the current likely behavior with its badge and **Situation / Action / Outcome**; the run's episodes; the timeline around the episode
  (or All events); **Why did it do that?** for a selected moment; Details for raw states, frames, hashes, notes and missing telemetry.

## MCP (Observe tier; server 0.5.0, 20 tools)

| Tool | Returns |
|---|---|
| `list_behaviors` | every behavior with its level, episodes, runs, setups, static rules, stale count; watched runs current / stale / not on the AI |
| `inspect_behavior` | one card: conditions, actions, outcomes, runtime episodes (situation → action → outcome, chain, run, setup), static rules (plain text + source), linked states/abilities, limitations, evidence |
| `why_did_ai_do_this` | the bounded Why for one frame of a watched run, `cause.known: false`, nearby timeline, whether the run is current or stale |
| `watch_match` | job: records a match (writes only a recording, `readOnlyHint: false`) and returns its episodes, control, notes and unreadable fields |

No Edit AI / Deploy tool exists. `get_runtime_trace` also reads watched runs. The server instructions tell agents the three levels and that a cause
must never be claimed.

## QA verbs (`--qa-script`)

`watch-run [seconds] [opponentAi]`, `watch-status`, `watch-select-run N`, `watch-episode <behavior|index>[@startFrame]`, `watch-card <behavior>`,
`watch-why <frame|ep>`, `xray-rename <id> <name>` (the window's own Rename, into the isolated name store); `playback-wait settled` waits for a watch.

## Tests

- `IKEMENLab.Tests/XRayBehaviorTests.cs` (17): each template from synthetic traces and what must not be recognised (incomplete chase, opponent sliding,
  lunge, round split, pushed-not-retreat, P1 off the AI); the bounded trigger shapes (`Life < 900`, variables, negations stay Unknown); static Possible
  and nothing more; the Confirmed threshold and its configuration; staleness (files, engine, unknown engine, unrecorded engine); names in episodes and
  the timeline; Why's context and its refusal to name a cause; one coordinate scale whatever the localcoord and the camera; real traces: the Phase 3 KFM
  chase (driver, not AI evidence), FV vs KFM (3 chases), FV vs kfm720 (one scale), KFM's 1050 → 1051 guard (one block); the store, retention per
  origin; the watch through the playback session; cancel leaves nothing.
- `IKEMENLab.Mcp.Tests/McpBehaviorTests.cs` (3): watch → Observed cards → bounded Why → stale after an edit; cancel; never touching the real stores.
- `IKEMENLab.App.Tests/WatchAskUiTests.cs` (4): the window's easy view and badge, renamed moves, an off-the-AI run never counted, cancel.

## Windows acceptance (2026-10-04)

Release app driven by `--qa-script` (all app data in `%LOCALAPPDATA%\IKEMEN Lab\qa-isolated`), the installed engine `D:\Games 3\Ikemen_GO-v1.0.0\Ikemen_GO.exe`
(sha256 `F68DC970…D0A1`, reports "jg-simul8-v1 - ffa-build"), stage ASI_FightIsland, subject on AI level 8, probe 0.4. Every run: AI level 8 read
from the engine, no field unreadable, sandbox deleted afterwards.

| Run | Setup | Samples | Episodes (detector v7) |
|---|---|---|---|
| `20261005-005411-3b4b62` | KFM vs kfm, opponent AI 1, 45 s | 2700 | Block ×3, Retreat ×4, Anti-Air ×1 |
| `20261005-005730-ae8434` | Funny Valentine vs kfm, opponent AI 1, 90 s | 5400 | 30 — Ground Follow-Up ×8, Knockdown Chase ×5, Anti-Air ×4, Retreat ×4, Meter/Super … |
| `20261005-005908-f4c5e6` | Funny Valentine vs **kfm720** (localcoord 1280), opponent AI 1, 90 s | 5400 | 25 — Ground Follow-Up ×5, Knockdown Chase ×3, Pressure ×3, Block ×3 … |

**KFM** (no custom AI — the engine's own AI drives it): Defensive Response, Retreat and Anti-Air **Observed** (one run, one setup). Example:
`Enemy jumps (85 away) → State 210 at 46 → Whiffed`; Why at 2133: "Observed context: enemy airborne, medium range, distance decreasing; distance 71.
Kung Fu Man attacked with State 210 at frame 2159, 46 away — whiffed. This matches the Anti-Air pattern." No static rule (KFM has no AI rules).

**Funny Valentine — knockdown → movement → follow-up is recognised as one episode.** Knockdown Chase is a **Confirmed Pattern**: 8 complete episodes,
2 runs, 2 setups (4 connected, 4 whiffed; 4 more knockdowns were followed by movement but no attack — incomplete, not counted). With the user's names
state 21 → "Walk Forward", 230 → "Ground Punch" (isolated name store):

```
vs kfm     2155–2278  Enemy knocked down (falling) → Moves toward them: State 100 → Distance 165 → 68 → State 400 at 64 → Whiffed
           2288–2398  Enemy knocked down (falling) → Enemy lying on the ground → Moves toward them: Walk Forward (21) → Distance 64 → 36
                      → Ground Punch (230) at 36 → Connected (while still lying)
           3772–3898  … Walk Forward (21) → Distance 79 → 49 → Ground Punch (230) at 49 → Connected
           4785–5023  … Walk Forward (21) → Distance 60 → 42 → Ground Punch (230) at 42 → Connected
vs kfm720  2013–2225  Enemy knocked down (falling) → Moves toward them: State 356 → Distance 124 → 44 → State 600 at 36 → Connected
```

Against kfm, FV walks in with its AI walk (21) and uses the down-hitting 230 on the lying opponent; against kfm720 it chases through the air (356
"holdup ↑" → 600). Why at frame 2368 (just before the walk):

> Observed context: enemy lying, enemy in hitstun, medium range, enemy cornered; distance 64. Funny Valentine began Walk Forward (21) toward the
> opponent at frame 2370 (64 away → 36), then attacked with Ground Punch (230) at frame 2394, 36 away — connected. This matches the Knockdown Chase and
> Ground Follow-Up patterns. Static AI contains a rule consistent with this behavior, but the executing controller is not known.

Consistent static rules: `State -1, controller state:-1/ctrl:14: when Can Act → Ground Punch (230)` and ctrl 28 (weighs Can Act, Enemy Getting Up,
Enemy Lying, … inside a larger expression). FV's other cards: Anti-Air, Block, Ground Follow-Up, Meter/Super, Retreat Confirmed; Pressure, Punish,
Wake-Up Pressure Observed (Wake-Up: 2 episodes in 2 setups — below the 3-episode threshold); Projectile Response Not seen (neither opponent throws a
Projectile-controller projectile).

Screenshots: `docs/images/xray-phase5-fv-knockdown-chase-why.png` (FV card Confirmed Pattern, episodes, timeline around 2288–2398 with the user's
names, the Why answer and its no-attribution line), `docs/images/xray-phase5-kfm-anti-air.png` (KFM: Observed cards, the Anti-Air easy view, Why).

**MCP on the configured default engine (closes Phase 4's default-path gap).** The Release `ikemenlab-mcp.exe` with **no arguments** — default data
folder, the app's real settings, engine `D:\Games 3\Mugen AI Research\Ikemen-GO-ffa\xray_sandbox_engine.exe` (sha256 `EC1F2F7B…2791`, DLLs from
`C:\msys64\mingw64\bin`) — driven over stdio: protocol 2025-06-18, 20 tools (behavior tools read-only except `watch_match`; no edit/deploy tool);
`inspect_character kfm` → playback ready on that engine; `watch_match kfm 20 s` → completed in 28.6 s, run `20261005-011200-c51fbd`, 1200 samples,
"AI-controlled (AI level 8, read from the engine)", no unreadable field, Anti-Air ×1, Retreat ×2, Block ×1; `list_behaviors` → three Observed;
`inspect_behavior Anti-Air` → engine `EC1F2F7B…`; `why_did_ai_do_this` → `cause.known: false` (at frame 728: "Kung Fu Man was moving State 50
away from the opponent at frame 723 (123 away → 229), then attacked with State 230 at frame 772, 229 away — whiffed. This matches the Retreat /
Reposition pattern."); server exit 0, no engine process or sandbox left. A later read-only pass with the final build (server 0.5.0) left the real
stores byte-identical.
The real stores before/after: the only change is that one watched run (origin `mcp`) in `xray-watch`; settings.json, playback history, experiments,
names and MCP runs byte-identical. The three app QA runs left the real stores byte-identical (178/178 files).

### Defects found and fixed during acceptance

1. **One guard counted as blocking two attacks.** KFM's opponent move 1050 → 1051 is two attack states with no gap; the guard at 538 (during 1051) was
   also credited to 1050, which had ended. A guard (or hit) now answers only the attack it falls in. Pinned with the real trace (frames 500–600).
2. **Mixed localcoords measured in mixed units.** FV (320) vs kfm720 (1280) read distances of ~1000, so ranges and approach directions were wrong.
   `BehaviorCoordinates` (above). Pinned with the real trace (frames 1990–2240) and a synthetic scrolling-camera / 4× / 640 case.
3. **Screen-relative positions.** The engine measures x from the camera, so a scrolling camera hid or shortened the fighter's own movement. Movement is
   now its own velocity capped by the change in distance. Same real chases; more real KFM retreats found (camera-masked before).
4. **Why skipped the movement under way.** Asked inside a retreat (or mid-approach) it described only the next attack; it now reports the movement
   in progress, then the attack.
5. Timeline called the first state of a multi-state move "missed" just before saying the move was blocked; `Life < N` was Low Life for any N; a run
   that never recorded its engine was "the engine changed"; static rules repeated "(as part of a larger expression…)" per condition; MCP counted
   off-the-AI runs as stale; long behavior names ran into their badges.

## Known limitations

- **No controller attribution.** Why describes context and consistent static rules only; it cannot say which controller fired.
- **The timeline is read after the match**, not live while the IKEMEN window plays.
- **Template coverage.** Projectile Response has no real episode yet (helper projectiles are invisible to NumProj); custom guard and get-up states
  are not recognised; "Super" relies on X-Ray's inferred category or the power spent; multi-state moves are segmented by state.
- **Static Possible is literal-only.** AI that decides through variables or computed values (FV's weighting terms) is shown as *weighs*, never as a
  required condition; no rule found is not evidence of absence.
- **Coordinates** assume symmetric bodies when re-expressing a mixed-scale opponent (error ≤ half the front/back width difference) and need probe 0.4
  edge distances; DLL changes next to the engine are not part of the engine identity (exe sha256 only).
- **Driver approach distance (Phase 2–4, not changed here).** The driver's approach threshold uses the probe's raw `p2.x − p1.x`, so against an
  opponent with another localcoord (e.g. kfm720) Play Ability / Sequence Lab approach distances are in mixed units. Recorded, not reopened. **Fixed in Phase 6**
  (probe 0.5 reads the engine's `p2DistX` in world units — see `xray-phase6-teach-ai.md`).
- QA isolation keeps names in `qa-isolated\names`, while MCP with `--data-dir qa-isolated` reads `xray\names` under it (an existing Phase 3/4 layout
  difference); in normal use both share `%LOCALAPPDATA%\IKEMEN Lab\xray\names`.

## Deferred

Teach AI, generated AI code, Edit/Deploy MCP permissions, Assistant/autopilot, Strategy, automatic combo search, save/restore branching, prerequisite
solving, controller attribution; live (streaming) timelines; statistical confidence.
