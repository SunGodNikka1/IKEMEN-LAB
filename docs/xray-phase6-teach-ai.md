# X-Ray Phase 6 — Teach AI v1 (Knockdown Chase)

```
use an ability (Play Ability) ─▶ prove a sequence (Sequence Lab) ─▶ "Teach AI…" ─▶ plain wizard ─▶ Draft
Draft ─▶ Test (working copy, sandboxes) ─▶ Testing ─▶ passing test of THIS build ─▶ Ready for approval
Ready ─▶ Why? (exact, intention register) ─▶ Approve (this exact build + diff) ─▶ Deploy (separate; SafeMutation + manifest) ─▶ Live ─▶ Roll back / Retire
```

Phase 6 closes the loop for **one** behavior family: *Knockdown Chase* — the enemy is knocked down (falling, lying, or both) → if within the chase
range, walk toward them → when close enough, use the chosen follow-up → if the opponent recovers first, the fallback; give up after N frames. Every
other behavior template stays read-only recognition (Phase 5). Not here: general AI generation, Autopilot, Strategy, behavior synthesis, arbitrary
trigger editing, controller attribution for existing AI, ML, a prerequisite solver.

## Prerequisite fixed first: one distance everywhere

Play Ability, Sequence Lab (Phase 2–4) and the chase driver used the probe's `p2.x − p1.x`. Against an opponent with another localcoord (kfm720 =
1280) those positions are in mixed units, so "chase until within 35" could never be reached. Root cause: the probe asked the engine for bindings that
do not exist (`p2distx`); IKEMEN GO's Lua bindings are camelCase. Probe **0.5** reads `p2DistX` (P1's units, facing-relative) and `localCoordX`, and
converts once: `distance = p2DistX × facing × 320 / localcoord(P1)` — world axis, 320-wide units, centre to centre (`distanceSource:
"engine:p2DistX"`). Fallback when a build lacks it: both positions re-expressed with `cameraPosX` (`"derived:world"`); the legacy raw difference
remains only for very old builds (`"derived:p2.x-p1.x"`). `BehaviorCoordinates` (Phase 5) keeps a world distance as it is (never re-scaled) and
places positions from it, so Play Ability, Sequence Lab, Watch & Ask and Teach AI read the same number. Not a coordinate-system refactor: one field
and its consumers.

Real regression (2026-10-05, installed engine F68DC970, KFM vs **kfm720**): Sequence Lab `1000 > chase:35 > 200` — the chase began 137 px away and
the driver stopped it at frame 408 with "reached 34"; at that moment the raw `p2.x − p1.x` was **850.6** (the old reading would have chased until
the 300-frame give-up). Pinned as `Fixtures/phase6_kfm_vs_kfm720_chase_real_trace.jsonl.gz`.

## Architecture (`IKEMENLab.Core/XRay/Director`, reused by the app and MCP)

| Piece | File | What it does |
|---|---|---|
| Semantic model | `DirectorModels.cs` | `KnockdownChaseSpec` (the plain answers), `TaughtBehavior` (revision, status, source, test plan, approval, deployment), model hash, plain text (`DirectorText`). |
| Teach | `TeachAi.cs` | Drafts from a Sequence Lab experiment, a recognised chase (Watch & Ask) or an Ability Lab move; follow-up **runtime proof** (`Prove`); follow-up choices. |
| Facts | `DirectorFacts.cs` | The loaded `[Statedef -1]` (exactly one, in load order), a free state block (9790, 8790, …), the next `st<n>` key, localcoord / units per px, walk / stand animations. |
| Ownership | `DirectorOwnership.cs` | Every existing state change that competes for the knockdown or could take over the running chase; verdict Owned / Owned-after / Refused. |
| Compiler | `DirectorCompiler.cs` | Model → deterministic ASCII CNS: the State −1 block, the generated states file, the DEF line, suppression lines. |
| Working copy | `DirectorWorkspace.cs` | `base/` (complete copy of the installed folder) + `working/` (base + generated insertions), build hash, divergence / drift checks, diff. |
| Tests | `DirectorTesting.cs` | Fixture plans (proven opening → hand-over to the AI), the suite, the run evaluator over the intention register, regressions, the report. |
| Why | `DirectorWhy.cs` | Exact cause for the generated behavior's decisions, confirmed against what executed; nothing claimed elsewhere. |
| Service | `DirectorService.cs` | Store (`%LOCALAPPDATA%\IKEMEN Lab\ai-director\<character>\`), propose / revise / analyze / generate / record test / approve / deploy / roll back / retire. |

Runtime path: `ComboPlaybackService.PlayDirector` → the same sandbox / probe / driver / cleanup as every playback, with the subject's folder copied
from the **working copy** (or a disposable staging build for MCP), `director = true` in the probe config (the intention register is read), and a
plan whose last action is the new driver step **`handover`**: release P1's virtual input and put P1 on the engine's AI (level 8), optionally the
opponent too. `PlaybackSession.RunDirectorAsync` serializes it with every other run (one engine at a time).

## The wizard (plain questions → model)

| Question | Answers | Pre-filled from |
|---|---|---|
| When | Enemy falling / lying / both | the posture when the proven chase began |
| Chase limit | px (1–600) | the proven chase's start distance + 20, rounded |
| Attack distance | px | the chase step's distance (e.g. "Chase until within 35") |
| Timing | while they are down / as they get up (+ lead frames) | a Wait step between chase and follow-up ⇒ as they get up |
| Give up | frames | the proven chase + wait × 1.5 + 30 |
| How often | Always / Often (70%) / Sometimes (35%) | Always — rolled once per knockdown |
| Follow-up | the move by its Phase-1 name, ✓ when proven | the step after the chase |
| Fallback | Stop and reassess / Block / Retreat | Stop and reassess |
| Meter rule | only shown when the follow-up costs power | the follow-up's cost |
| Ownership | Before the existing AI / After it / Suppress specific rules on knockdowns | Before |

Technical details (triggers, generated code, diff) are behind **Advanced**. Entry points: **Teach AI…** on a successful Sequence Lab experiment with a
chase, on a recognised Knockdown Chase in Watch & Ask, and on an Ability Lab move (that one starts with default distances and says so).

**Follow-up proof.** A follow-up is usable only when Play Ability or a Sequence Lab trial **performed** it (route verifier
`runtime.transition-observed`) on these character files and this engine; air moves are refused (v1 starts ground follow-ups). Unproven → the draft
lists the problem and cannot be generated, tested or saved from the wizard.

## Ownership — one owner per decision

The generated decision is one ChangeState in the character's single loaded `[Statedef -1]`. For every existing State −1/−2/−3 ChangeState X-Ray
classifies the relation (same opportunity — reads a knockdown itself; command — the engine's AI may press it; unknown — conditions X-Ray cannot read;
disjoint; human-only; a continuation that needs contact or an active HitDef) and asks one question: **can it fire while the generated chase runs?**
The chase state runs with `ctrl = 0`, `type = S`, `movetype = I`, no HitDef. A branch cannot fire there when it requires control, needs contact or
`HitDefAttr`, is limited to other source states / types / move types, or matches one of two bounded idioms:

- a flag variable reset unconditionally earlier in State −1 and set only by rules that cannot fire in the chase (KFM's `var(1)` combo condition);
- `(ctrl || stateno = 21 || stateno = [500,501])` — control, or a few literal states none of which is the chase state (found on FV during
  acceptance: it removed 22 false risks there).

Anything else is assumed able to fire. Verdicts: **Owned** (placed before the existing AI and nothing can take over), **Owned after the existing
AI** (existing rules keep priority on the knockdown), **Refused** (a rule could take over the chase and is not suppressed — or it is outside State −1,
or `[Statedef -1]` is declared in more than one loaded file). *Suppress* adds one line, `triggerall = Map(xrd_kc_down) = 0`, right after the header
of each chosen State −1 rule, so it stands down on knockdowns. Nothing is ever silently stacked; a refusal names each rule with file and line.

## The generated code

Deterministic (same model → same bytes; hash recorded), ASCII, every rule named `IKEMEN Lab Director - …`, in four parts:

- **Perception** — the plain answers as triggers on engine facts: `EnemyNear, StateType = L`, falling = `StateType = A && MoveType = H && HitFall`,
  getting up = `StateNo = 5120 && AnimTime >= −lead` (IKEMEN 1.0's common1), `abs(P2Dist X)` in the character's own units (a 640-localcoord fighter
  gets `<= 70` for 35 px).
- **Decision ownership** — one start rule in State −1 (`AILevel > 0`, `RoundState = 2`, `Ctrl`, on the ground, a new knockdown this behavior has not
  handled, in range, the once-per-knockdown roll), placed per the ownership choice.
- **Execution** — the chase state (9790 on KFM): turn, walk at `const(velocity.walk.fwd.x)`, stand in range; one ChangeState per exit — attack
  distance reached → the proven follow-up; opponent recovered → the fallback; `Time >= give-up` → stand with control; optional retreat state.
- **Intention register** — before every ChangeState, MapSets with **exactly that ChangeState's trigger** write `xrd_beh`, `xrd_why` (1 start,
  2 attack, 3 recovered, 4 give up, 5 no meter), `xrd_next`, `xrd_dist`, `xrd_tick` (GameTime), `xrd_down`; `xrd_roll` holds the roll. The probe
  reads these maps every frame. A register written on a tick is therefore the decision taken on that tick — and the trace says whether the state
  it names was entered.

KFM's State −1 block (inserted before KFM's first State −1 rule; the register MapSets of the start rule are elided here):

```
; ==== IKEMEN Lab AI Director - generated - BEGIN knockdown-chase kc-0ab10a441e rev 1 - do not edit: regenerate it from the behavior in IKEMEN Lab (X-Ray > AI Director) ====
; When enemy is knocked down (falling or lying) within 160 px, kfm walks toward them until 35 px, then uses x (LP) as they get up. Always. If the opponent recovers first or 180 frames pass, stop and reassess.
; Placement: Before the existing AI (this behavior owns the knockdown when it applies). Model 902DC5D297D5, ikemenlab-director/1.
; Distances: P2Dist X is in this character's units (1 per px; localcoord 320).

[State -1, IKEMEN Lab Director - knockdown seen: roll how often]
type = MapSet
triggerall = AILevel > 0
trigger1 = Map(xrd_kc_down) = 0 && (EnemyNear, StateType = L || (EnemyNear, StateType = A && EnemyNear, MoveType = H && EnemyNear, HitFall))
map(xrd_roll) = Random
… knockdown seen / knockdown over (MapSets) … register: start the chase (7 MapSets with the same triggers) …
[State -1, IKEMEN Lab Director - Knockdown Chase: start]
type = ChangeState
triggerall = AILevel > 0 && RoundState = 2
triggerall = Ctrl && StateType != A
triggerall = Map(xrd_kc_down) = 1 && Map(xrd_kc_done) = 0
triggerall = EnemyNear, Alive
triggerall = (EnemyNear, StateType = L || (EnemyNear, StateType = A && EnemyNear, MoveType = H && EnemyNear, HitFall))
triggerall = abs(P2Dist X) <= 160
trigger1 = Map(xrd_roll) < 1000
value = 9790
; ==== IKEMEN Lab AI Director - generated - END knockdown-chase kc-0ab10a441e rev 1 ====
```

and `ikemenlab_director.cns` (loaded by one DEF line, `st1 = ikemenlab_director.cns ; IKEMEN Lab AI Director (generated) - remove this line and the
file to retire it`):

```
[Statedef 9790]
type = S
movetype = I
physics = S
anim = 20
ctrl = 0
velset = 0,0
[State 9790, IKEMEN Lab Director - face the opponent]          Turn       P2Dist X < 0
[State 9790, IKEMEN Lab Director - walk toward them]           VelSet     abs(P2Dist X) > 35 → x = const(velocity.walk.fwd.x)
[State 9790, IKEMEN Lab Director - in range: wait for them to get up]   VelSet 0 / stand anim
[State 9790, IKEMEN Lab Director - show the intention (Ctrl+D debug view)]   DisplayToClipboard
… register: attack distance reached (7 MapSets) …
[State 9790, IKEMEN Lab Director - attack distance reached: x (LP)]
type = ChangeState
trigger1 = abs(P2Dist X) <= 35 && ((EnemyNear, StateNo = 5120 && EnemyNear, AnimTime >= 0) || ((EnemyNear, StateType != L && EnemyNear, MoveType != H) && EnemyNear, Time <= 15))
value = 200
ctrl = 0
… register + ChangeState: opponent recovered → 0 (ctrl 1) when (they are up and not hit) && (Time > 15 || out of range) …
… register + ChangeState: give up → 0 (ctrl 1) when Time >= 180 …
```

No existing controller, value, HitDef, velocity, timing or animation is changed (the mechanics firewall): the generated code only adds a state and
decisions that use the character's own moves, walk speed and animations.

## Working copy, lifecycle, approval, deployment

`%LOCALAPPDATA%\IKEMEN Lab\ai-director\<folder>\` holds `behaviors/<id>.json`, `base/<folder>` (a complete copy of the installed folder, binaries
included), `working/<folder>` (base + the generated insertions — rebuilt from the model every time, never edited in place), `workspace.json` (base
hash, build hash, model and code hash), `tests/`, `deployments/`, `staging/` (MCP). Character files are read and written **byte for byte** (BOM,
CRLF and non-ASCII bytes kept); a build is the base with only insertions. A working copy changed outside IKEMEN Lab is never overwritten.

| Status | Means | The installed character |
|---|---|---|
| Draft | saved answers | untouched |
| Testing | generated into the working copy and tested (not yet a pass of this build) | untouched |
| Ready for approval | a passing test of **this revision on the build now in the working copy** | untouched |
| Live | deployed | the approved build |
| Retired | rolled back / removed from the working copy | restored |

**Approve** (app only, with a confirmation that shows the sentence, ownership, files and the test) binds the approval to the build hash, diff hash,
model hash and the passing test; it deploys nothing. Any change voids it: a new revision, a regenerated build, a working copy edited outside the app,
or code generated by another version of IKEMEN Lab than the one in the working copy. **Deploy** is a separate button: it re-checks all of that plus
that the installed folder is still the base, then writes each changed file through `SafeMutationService` (verified backup, `expectedCurrentHash`,
per-file manifest), verifies the installed hashes, rolls everything back on any failure, and records a deployment manifest. **Roll back** restores
every byte (the generated file is removed again). Deployment never runs from MCP or a QA script.

## Testing a behavior

The suite runs from the working copy (MCP: a staging build), one IKEMEN run after another:

| Setup | What happens |
|---|---|
| Proven setup (idle opponent) × trials | the proven opening by the driver (e.g. Palm), then `handover`: P1 on AI level 8 — the generated behavior acts; the opponent never acts |
| Opponent active after the knockdown × trials | the same, but the opponent goes on AI level 4 at the hand-over: it gets up and acts, so recoveries and fallbacks happen |
| Second opponent (optional) × trials | the same against another installed character (e.g. kfm720) |
| Natural match (30 s) | no script: P1 on AI 8, the dummy on AI 1 — only knockdowns the AI makes itself |
| Regressions | the proven Sequence Lab sequence on the build; the follow-up through Play Ability on the build |

The evaluator reads each run from the intention register and the trace: knockdowns, chases started, follow-ups (executed), connected (Phase 5's
outcome rule), fallbacks, give-ups, **conflicts** (a published decision that did not execute, or a chase taken over by another state without a
decision of its own), interruptions (hit out of the chase), **chases still running when the recording ended** (neither success nor failure), damage.
Pass = every setup ran, the proven setup produced a follow-up, no conflicts, every regression held. Every report says: *these counts describe these
setups only … a high rate here is not evidence that the behavior is good against other opponents.*

## Exact Why

For a moment of a test run, `DirectorWhy` finds the generated behavior's latest decision and states its cause **only when the register and the
trace agree** (the register's next state was entered within 3 frames):

- in the chase — the live view (enemy posture ✓, distance, chase limit ✓, attack distance, give-up, action, next) and *"Knockdown Chase is running
  because its start rule fired at frame 331: the enemy was in the air (falling), 141 px away (chase limit 160 px), the fighter could act on the
  ground."*
- after a decision — *"x (LP) chosen because the generated Knockdown Chase reached its attack-distance condition: 35 px ≤ 35 px, as the enemy was
  getting up …"*, with *"Intention register at frame 430 (reason "attack distance reached", next State 200); the fighter entered State 200 —
  confirmed."*
- published but not executed — the mismatch is reported, no cause is claimed.
- anything else (the driver's opening, the existing AI) — *"This moment is not a decision of the generated behavior … no cause is claimed."* Phase
  5's bounded Why for existing AI is unchanged (`cause.known = false`).

## App — X-Ray → AI Director

Left: taught behaviors with status badges, ownership verdict, follow-up proof. Centre: the card — **WHEN / DO / THEN / GIVE UP IF / STATUS** and the
sentence, with **[Test] [Edit] [Why?] [View Evidence] [Approve] [Deploy] [Roll back] [Retire]**; the wizard (plain questions, ✓/✗ follow-ups,
suppress list when chosen); test runs and their moments (decisions in bold); the Why card with its badge (*Exact (intention register, confirmed)* /
*Not a confirmed cause*); evidence (proof, ownership rows, report, regressions, working copy, approval, deployment); **Advanced** (generated rules,
CNS, diff). Screenshots: `docs/images/xray-phase6-kfm-chase-live-view.png`, `docs/images/xray-phase6-kfm-knockdown-chase-why.png`.

## MCP (server 0.6.0, 23 tools)

| Tool | Tier | |
|---|---|---|
| `propose_behavior` | Experiment (writes only a Draft into IKEMEN Lab's data) | from an experiment, a watched episode (`"<run>@<frame>"`) or a follow-up; overrides for every wizard answer; returns the card, proof, ownership, generated rules |
| `inspect_behavior_draft` | Observe (read-only) | the list, or one behavior with `include_code` (State −1 block, states file, DEF line, diff) |
| `test_behavior` | Experiment (job) | the suite on a disposable **staging** build — never the working copy or the install; a pass there never makes a behavior Ready for approval |

There is no approve, deploy, rollback or edit tool. The server instructions say approval and deployment are the user's, in the app.

## QA verbs (`--qa-script`)

`director-teach <experiment id|latest>`, `director-set key=value|…` (when, limit, attack, timing, lead, giveup, often, fallback, placement, second,
trials, follow), `director-save`, `director-test`, `director-status`, `director-select-run N`, `director-why <frame|start|attack>`,
`director-evidence on|off`, `director-approve`. **No deploy verb**: a script never writes to the install. `xray-shot` now lets the dispatcher's
lower-priority work (the CanExecute requery) run first, so buttons render with their real enabled state.

## Tests

- `IKEMENLab.Tests/XRayDirectorTests.cs` (17): experiment → wizard model and proof (and other engine ⇒ unproven); unproven follow-up refused and
  nothing written; ownership (competitors listed, after-placement, an ungated rule **refused**, suppression restores ownership, the flag idiom and
  its missing reset, two `[Statedef -1]` refused, the `ctrl || stateno` idiom incl. a range covering 9790 and a variable state); deterministic
  ASCII code whose every ChangeState has register MapSets with identical triggers, variants (falling only, meter, block, retreat, while down);
  640-localcoord unit scaling; working copy (install untouched, base = install, insert-only diff, removing the insertions gives the installed CMD
  back byte for byte with BOM/CRLF/non-ASCII, deterministic regeneration, divergence refused, staging); fixture plan with hand-over (Lua, JSON
  round-trip); evaluator (connect, recovery fallback, unexecuted ⇒ conflict, taken over ⇒ conflict, cut off at the end, not tested); exact Why vs
  unconfirmed vs not generated, existing AI still cause-unknown; probe distance source and no re-scaling (1280 vs 320, hi-res fighter); approval
  boundary (not ready, revision voids, working-copy edit voids, older generated code refused); deploy through SafeMutation with manifest, install
  drift refused, roll back byte-exact, retire; the suite through the session with a fake engine (setups, regressions, the build in every sandbox,
  report on disk); cancel leaves no report or sandbox. Real fixtures: the kfm720 chase (above) and a full KFM Director run (3 chases: 2 connected
  as kfm got up, 1 cut off; exact Why at 334 and 433).
- `IKEMENLab.Mcp.Tests/McpDirectorTests.cs` (3): propose/inspect write only a draft (install and working copy untouched); an unproven follow-up
  cannot be tested; `test_behavior` runs every setup on staging, leaves nothing behind, never approves. `McpServerTests`: 23 tools, no
  edit/deploy/approve/rollback tool.
- `IKEMENLab.App.Tests/DirectorUiTests.cs` (4): the wizard pre-filled from an experiment and saving only a draft; testing every setup from the
  working copy and exact Why in the window; approval as its own step (No/Yes), deploy through the injected SafeMutation, roll back byte-exact;
  teaching from a recognised chase in Watch & Ask.

Totals at commit: Core 828, App 49, MCP 23 — all passing; Release build clean.

## Windows acceptance (2026-10-05)

Release app driven by `--qa-script` (all app data in `%LOCALAPPDATA%\IKEMEN Lab\qa-isolated`), installed engine
`D:\Games 3\Ikemen_GO-v1.0.0\Ikemen_GO.exe` (sha256 `F68DC970…D0A1`, "jg-simul8-v1 - ffa-build"), stage ASI_FightIsland, probe 0.5.

**KFM — the first generated behavior.** Taught from the Phase 3 experiment `20261004-085352-0a29ef` (*Palm, chase, wait, jab*: QCF x → Chase until
within 35 → Wait 20f → x). In plain English:

> When the enemy is knocked down (falling or lying) within 160 px, Kung Fu Man walks toward them until 35 px, then uses x (LP) as they get up.
> Always. If the opponent recovers first or 180 frames pass, stop and reassess.

Follow-up proof: x (LP) performed in 2 Sequence Lab trial steps on these files and this engine. Ownership: **Owned** — evaluated before the 31
existing rules that could act on the knockdown (KFM's command rules the engine's AI may press), nothing can interrupt the chase.

Three passes on the real engine (each: 2 trials idle, 2 trials with the opponent active, 2 trials against **kfm720**, a 30 s natural match, then
the two regressions — 9 IKEMEN runs, about 3½ minutes):

| Pass | Build | Chases | Follow-ups | Connected | Fallbacks | Give-ups | Conflicts | Cut off at the end | Regressions |
|---|---|---|---|---|---|---|---|---|---|
| 1 | `91DED20CD709` | 15 | 12 | 10 | 0 | 0 | 0 | (3 — not yet reported, defect 2) | 2/2 held |
| 2 (after defects 2–6) | `321840782282` | 14 | 10 | 10 | 1 | 0 | 0 | 3 | 2/2 held |
| **final** (all fixes) | `4715FC8F705C` | **12** | **11** | **9** | 0 | 0 | **0** | 1 | **2/2 held** |

Final pass per run (test `20261005-072741-4b22e9`, *Passed*):

| Setup | Knockdowns | Chases | Follow-ups | Connected | Notes |
|---|---|---|---|---|---|
| Proven setup (idle) #1 | 2 | 2 | 2 | 2 | |
| Proven setup (idle) #2 | 2 | 2 | 1 | 1 | the second chase was still running when the recording ended (frame 760) |
| Opponent active #1 | 1 | 1 | 1 | 0 | the jab whiffed: kfm, now on the AI, got up and moved |
| Opponent active #2 | 1 | 1 | 1 | 1 | |
| Second opponent kfm720 #1 | 2 | 2 | 2 | 2 | |
| Second opponent kfm720 #2 | 3 | 2 | 2 | 2 | |
| Natural match (30 s) | 3 | 2 | 2 | 1 | knockdowns KFM's own AI produced |

Every chase is accounted for (12 = 11 follow-ups + 1 cut off). The regressions on the build: the proven sequence → *ConnectedSequence*; x (LP)
through Play Ability → *Performed*. Damage per run is the opponent's life lost in the 90 frames after each follow-up, so it includes what KFM's
own AI did next (e.g. 220 in the first idle trial is not two jabs). **Scope:** these numbers describe KFM (these files) on this engine against an
idle or level-4 kfm and an idle kfm720 on one stage — the behavior whiffed against an opponent that acts, and nothing here says it is good
elsewhere.

Exact Why in the final pass (run 1): at frame 334 — *Exact (intention register, confirmed)* — "Knockdown Chase is running because its start rule
fired at frame 331: the enemy was in the air (falling), 141 px away (chase limit 160 px), the fighter could act on the ground." Live view: *Enemy
lying: yes ✓ · Distance: 135 px · Chase limit: 160 px (started at 141) ✓ · Attack distance: 35 px · Give up: after 180 frames · Action: Walking
toward them (the generated chase) · Next: x (LP) as they get up*. At frame 433: "x (LP) chosen because the generated Knockdown Chase reached its
attack-distance condition: 35 px ≤ 35 px, as the enemy was getting up (0 frame(s) before they could act, or just after)." — "Intention register at
frame 430 (reason "attack distance reached", next State 200); the fighter entered State 200 — confirmed." A moment of the driver's opening returns
the no-claim answer.

Lifecycle observed for real: Draft → (Test) Testing → Ready for approval → approved `91DED20CD709`; after the generator fixes the reopened card
**refused to deploy** that approved build ("The change to deploy is no longer the approved diff" — it would have dropped KFM's BOM); Test
regenerated the working copy, which **voided the approval** (Ready for approval, not approved); the final build was approved (`4715FC8F705C`,
diff `AF1940E57587`). The working copy is the base plus insertions only: 115 lines in `kfm.cmd` after line 377 (before KFM's first State −1 rule),
one line in `kfm.def` after line 19, and the new `ikemenlab_director.cns`; KFM's BOM (`EF BB BF`) is kept.

**Coordinates.** The kfm720 runs used the same generated `abs(P2Dist X) <= 35` (KFM's units) and reached the follow-up the same way as against kfm.

**Funny Valentine — read-only, not forced.** Ownership analysis (no copy, nothing written) with Ground Punch (230) as the follow-up: **Refused**.
Three rules can take over a running chase: *AI Guard* (State −1, `Funny_Valentine.cmd:1538`; its gate also accepts `Anim = 5120 && AnimTime >= −1`,
which the bounded idiom does not evaluate — suppressible), and two outside State −1 that cannot be suppressed there: `IA.cns:154` (State −2,
ChangeState 3990, only against an enemy named "Johnny Joestar") and *ZENKAI* (`IA.cns:479`, State −3, ChangeState 2100 at life ≤ 50 with helper
2120 present). The follow-up also has no runtime proof on this engine yet. Before the `ctrl || stateno` idiom the same check listed 25 rules; FV's
`(ctrl || stateno = 21 || stateno = 501 || stateno = 101)` gates are now read correctly.

**MCP** (Release `ikemenlab-mcp.exe --data-dir <isolated copy of the QA settings + experiments>`): server **0.6.0**, 23 tools, `propose_behavior` / `test_behavior` not read-only, `inspect_behavior_draft` read-only, no edit / deploy / approve /
rollback tool. `propose_behavior kfm from_experiment=20261004-085352-0a29ef second_opponent=kfm720 trials=1` → Draft, the same sentence, proof and
ownership as the app, six generated rules; `inspect_behavior_draft` → the list and the code (no working copy: MCP never makes one);
`test_behavior` → a job of 6 IKEMEN runs on a staging build (`B771FCFAFB30`), completed in ~2 min: *Passed · 8 chases, 6 follow-ups, 3 connected,
1 fallback, 0 give-ups, 0 conflicts, 1 cut off · 4 runs, 2/2 regressions held* (the two follow-ups against the active opponent both whiffed), status
still **Testing** with "Passed on a disposable staging build. Approval binds to the working copy: the user runs Test in the app …"; server exit 0,
staging removed.

**Nothing else was touched.** The real stores (`settings.json`, `xray`, `xray-playback`, `xray-experiments`, `xray-mcp-runs`, `xray-watch`,
runtime) were byte-identical before and after (182/182 files); the installed `kfm`, `kfm720` and `Funny_Valentine` folders were byte-identical (41/41);
no sandbox or engine process remained. **No deployment was performed** to the real installation: deployment and roll back are proven against a
fixture install in the Core and App tests.

### Defects found and fixed during acceptance

1. **Byte-order mark dropped.** KFM's CMD starts with a UTF-8 BOM; `File.ReadAllText(path, Latin1)` still detects BOMs and drops them, so the
   working copy lost three bytes (and non-ASCII UTF-8 text would have been re-encoded). Character files are now read and written as raw bytes; the
   test fixture CMD has a BOM and the byte-for-byte check fails without the fix.
2. **Chases cut off by the end of a recording were silent.** 15 chases, 12 follow-ups, 0 fallbacks, 0 give-ups: the 3 missing were each the last
   decision of a run that ended mid-chase. They are now counted and reported ("still running when the recording ended") — neither success nor failure.
3. **FV's `ctrl || stateno = …` gates read as "no control requirement"** (22 false take-over risks) — the bounded idiom above.
4. **Approving a build the user is not shown.** After a generator change the working copy still held the older (tested, approved) code while the card
   showed the new code; approval now requires the working copy's code to be the code generated now.
5. MCP said `passed: true` with status *Testing* and no explanation — it now says the pass was on a staging build and approval needs the app's Test.
6. **The deploy re-check works on real data**: the build approved before fix 1 was refused at deploy time once the diff was read byte for byte
   (it would have removed the BOM); nothing was deployed.
7. Cosmetics: the moments list was unreadable (default dark text), Why repeated "✓ ✓" and marked an unreached attack distance ✗, `AnimTime >= -0`,
   the follow-up's name in Why was "State 200" instead of the chosen name, the deploy blocker named the diff before the install drift that caused it,
   QA screenshots showed stale button states.

## Known limitations

- **One family, one slot.** Only Knockdown Chase; one taught behavior per character's working copy at a time (the register has one slot).
- **Common states.** Getting up is IKEMEN 1.0's common1 5120; custom get-up / lying states are not recognised. Falling uses the engine's `HitFall`.
- **Ownership is bounded, not a solver.** Two idioms are proven; any other gate is assumed able to fire (conservative, may refuse safe characters —
  FV). Rules in State −2/−3 cannot be suppressed. The engine's own AI (command presses) is listed, not controlled.
- **Distances** are centre to centre in 320-wide units; body widths are not subtracted.
- **Test scope.** Counts describe the tested setups (character build, engine, opponents, stage) only; runs are short (the fixture watches ~430 frames
  after the opening), so chases can be cut off; no statistical confidence.
- **Why** is exact for the generated behavior only; for existing AI it stays bounded (no controller attribution).
- The follow-up proof is per character files + engine; an engine change makes a proven follow-up unproven again.

## Deferred (Phase 7+)

More behavior families and templates; several taught behaviors per character; custom get-up/lying states; a broader ownership analysis (or
controller attribution); MCP Edit-AI permission on the working copy (explicit, never deploy); statistical confidence and longer suites; Autopilot,
Strategy, behavior synthesis, prerequisite solving, ML.
