# X-Ray evidence rules

Every relationship and label in the Character Semantic Index cites one of these rule ids, and the rule dictates its confidence — a heuristic cannot be recorded as StaticProven by accident (`IndexBuilder.Relate` takes the confidence from the rule).

`RuntimeVerified` is **reserved** for milestone 3 (facts observed in a running match). Nothing in milestone 1 emits it, and a test asserts that.

This file is checked against the registry in `EvidenceRules.cs` by a test; regenerate it with `ikemenlab xray rules`.

## StaticProven

The character's own files say it literally, with literal operands (`ChangeState value = 200`, `command = "x"`, `var(20)`, `power >= 1000`). This means **the code contains the edge**, not that it is reachable or will fire: nothing is evaluated in milestone 1.

| Rule | Meaning |
|---|---|
| `ability.static-closure` | State reached from the ability's entry through StaticProven ChangeState edges only. |
| `anim.clsn` | Collision box written in the AIR file. |
| `anim.literal-ref` | Statedef anim= or ChangeAnim with a literal action number. |
| `anim.sprite-ref` | An AIR frame names sprite group,index. |
| `bind.target` | TargetBind controller. |
| `command.literal-ref` | A trigger compares command against a quoted command name that is defined in the CMD. |
| `entry.ai-trigger` | A trigger reads AILevel. |
| `entry.engine-state` | The engine itself runs states -1, -2 and -3 every tick. |
| `gate.command` | A trigger compares command against a quoted name. |
| `helper.literal-spawn` | Helper controller with a literal stateno/id. |
| `power.literal-cost` | PowerAdd/Statedef poweradd with a literal number. |
| `power.literal-gate` | A trigger compares Power with a literal number. |
| `projectile.spawn` | Projectile controller. |
| `state.literal-attacker` | HitDef with a literal p1stateno. |
| `state.literal-change` | ChangeState/SelfState with a literal (or constant-arithmetic) target state number. |
| `state.literal-victim` | HitDef/ReversalDef/TargetState with a literal p2stateno/value. |
| `structure.contains` | Block nesting in the source file. |
| `target.affect` | A Target* controller acts on the current target(s). |
| `timeline.animelem-literal` | AnimElem = n with a literal n in a state whose only animation source is a literal Statedef anim=. |
| `var.literal-index` | var/fvar/sysvar access with a literal index. |
| `var.literal-reset` | A variable is set to the literal value 0. |

## Inferred

A heuristic, a non-literal step, or a human comment. Always drawn differently from StaticProven (half disc, dashed line, the word "inferred") and never presented as a fact. Ability categories, variable names, helper roles and "throw" are all here.

| Rule | Meaning |
|---|---|
| `ability.hub-exit` | State shared by many abilities treated as a hub, not a member. |
| `ability.inferred-closure` | State reached from the ability's entry through at least one non-literal edge. |
| `cat.counter` | ReversalDef or HitOverride with a state. |
| `cat.defense` | NotHitBy/HitBy/HitOverride controllers. |
| `cat.mobility` | Movement controllers and no HitDef. |
| `cat.mode` | Writes a variable that many other states read. |
| `cat.normal` | Single-button command leading to an attacking state. |
| `cat.projectile` | Projectile controller or a helper that owns a HitDef. |
| `cat.special` | Motion command (two or more direction steps) leading to an attacking state. |
| `cat.summon` | Helper without a HitDef. |
| `cat.super` | Power gate or cost of 1000 or more. |
| `cat.throw` | HitDef p2stateno together with TargetBind in the ability's states. |
| `cat.victim-state` | HitDef p2stateno without TargetBind (strike that moves the victim into a custom state). |
| `dynamic.candidate` | Candidate target/value derived from other literal facts. |
| `helper.follower` | Helper's states use Bind/BindToRoot/BindToParent. |
| `helper.has-hitdef` | Helper's states contain a HitDef. |
| `helper.state-owner` | Which entity (root or a helper) runs a shared state is not known statically. |
| `label.author-comment` | Name taken from a comment above the block. |
| `label.controller-name` | Name taken from the [State n, name] text. |
| `state.duplicate-definition` | Statedef defined more than once; first definition assumed to win. |
| `state.engine-common` | Target is a standard common-state number; common1.cns was not loaded, so the definition is assumed. |
| `timeline.animelem-mixed` | AnimElem gate mapped to a frame although the state may change animation. |
| `timeline.time-gate` | Time gate mapped to a frame by ticks; state time and animation time can differ. |
| `var.ai-read` | Read by a controller that also reads AILevel. |
| `var.boolean-flag` | Only ever written with 0 or 1. |
| `var.counter` | Incremented by a literal step. |
| `var.cross-entity` | Accessed through Root/Parent/Helper/Target redirects. |
| `var.name-from-writer` | Name taken from the state or controller that writes it. |

## Unknown

Could not be determined: an expression that does not parse, a dynamic `ChangeState value = var(5)+1200`, or a reference to a state, animation, sprite or command that is not defined.

| Rule | Meaning |
|---|---|
| `anim.dynamic` | Animation number is an expression. |
| `anim.missing` | Animation number is not defined in the AIR. |
| `anim.sprite-missing` | Sprite named by an AIR frame is not in the SFF. |
| `command.undefined` | Command name is not defined in the CMD. |
| `expr.unparsed` | The expression could not be parsed. |
| `helper.dynamic-spawn` | Helper stateno/id is not a literal. |
| `state.ambiguous-owner` | The controller block names a state other than the `[Statedef]` it sits in, so which state owns it — and therefore owns everything the block does — is a reading of the layout, not a literal. |
| `state.dynamic-target` | ChangeState target is an expression that is not constant. |
| `state.missing` | Target state is not defined in any indexed file. |
| `var.dynamic-index` | var/fvar index is not a literal. |

## Deterministic vs inferred at a glance

| Relationship / fact | Confidence |
|---|---|
| ChangeState / SelfState to a literal (or constant-arithmetic) state | StaticProven |
| ChangeState to `var(n)+…` or any non-constant | Unknown |
| Edge to a common state (0, 20, 5000…) when `common1.cns` is loaded | StaticProven |
| … when it is not loaded | Inferred (`state.engine-common`) |
| HitDef `p2stateno` / `p1stateno`, TargetState (literal) | StaticProven |
| TargetBind, Target* controllers | StaticProven |
| Helper / Projectile spawn with literal id and stateno | StaticProven |
| `command = "x"` gate to a defined command | StaticProven; undefined command: Unknown |
| var/fvar/sysvar access with a literal index (incl. `root,` `parent,` `helper(n),` redirects) | StaticProven; non-literal index: Unknown |
| Literal `Power` gates and PowerAdd / Statedef poweradd costs | StaticProven |
| AIR frames, sprite references, Clsn boxes (incl. `Clsn Default` inheritance) | StaticProven; sprite missing from the SFF: Unknown |
| AnimElem = n mapped to a frame when the state's only animation is a literal `anim=` | StaticProven; otherwise Inferred |
| `Time` gate mapped to a frame | Inferred |
| Ability membership through StaticProven edges only | StaticProven; through any inferred edge: Inferred |
| Ability category (Normal, Special, Super, Throw, Projectile, Counter, Defensive, Summon, Mobility, Mode) | **Always Inferred** |
| Variable names / traits, helper roles, author-comment names | **Always Inferred** |
