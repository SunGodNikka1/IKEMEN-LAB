namespace IKEMENLab.Core.XRay.Model;

public sealed record EvidenceRule(string Id, Confidence Confidence, string Description);

/// <summary>
/// The single registry of why a fact has the confidence it has. Every relationship and label cites one of these ids,
/// so a reader (or an agent) can always see whether a fact is literal syntax or a heuristic.
/// </summary>
public static class EvidenceRules
{
    private static readonly List<EvidenceRule> All =
    [
        // ---- StaticProven: the file literally says it
        new("state.literal-change", Confidence.StaticProven, "ChangeState/SelfState with a literal (or constant-arithmetic) target state number."),
        new("state.literal-victim", Confidence.StaticProven, "HitDef/ReversalDef/TargetState with a literal p2stateno/value."),
        new("state.literal-attacker", Confidence.StaticProven, "HitDef with a literal p1stateno."),
        new("command.literal-ref", Confidence.StaticProven, "A trigger compares command against a quoted command name that is defined in the CMD."),
        new("var.literal-index", Confidence.StaticProven, "var/fvar/sysvar access with a literal index."),
        new("var.literal-reset", Confidence.StaticProven, "A variable is set to the literal value 0."),
        new("helper.literal-spawn", Confidence.StaticProven, "Helper controller with a literal stateno/id."),
        new("projectile.spawn", Confidence.StaticProven, "Projectile controller."),
        new("bind.target", Confidence.StaticProven, "TargetBind controller."),
        new("target.affect", Confidence.StaticProven, "A Target* controller acts on the current target(s)."),
        new("gate.command", Confidence.StaticProven, "A trigger compares command against a quoted name."),
        new("anim.literal-ref", Confidence.StaticProven, "Statedef anim= or ChangeAnim with a literal action number."),
        new("anim.sprite-ref", Confidence.StaticProven, "An AIR frame names sprite group,index."),
        new("anim.clsn", Confidence.StaticProven, "Collision box written in the AIR file."),
        new("power.literal-gate", Confidence.StaticProven, "A trigger compares Power with a literal number."),
        new("power.literal-cost", Confidence.StaticProven, "PowerAdd/Statedef poweradd with a literal number."),
        new("entry.engine-state", Confidence.StaticProven, "The engine itself runs states -1, -2 and -3 every tick."),
        new("entry.ai-trigger", Confidence.StaticProven, "A trigger reads AILevel."),
        new("timeline.animelem-literal", Confidence.StaticProven, "AnimElem = n with a literal n in a state whose only animation source is a literal Statedef anim=."),
        new("structure.contains", Confidence.StaticProven, "Block nesting in the source file."),
        new("ability.static-closure", Confidence.StaticProven, "State reached from the ability's entry through StaticProven ChangeState edges only."),

        // ---- Inferred: heuristics or non-literal steps
        new("state.engine-common", Confidence.Inferred, "Target is a standard common-state number; common1.cns was not loaded, so the definition is assumed."),
        new("state.duplicate-definition", Confidence.Inferred, "Statedef defined more than once; first definition assumed to win."),
        new("timeline.animelem-mixed", Confidence.Inferred, "AnimElem gate mapped to a frame although the state may change animation."),
        new("timeline.time-gate", Confidence.Inferred, "Time gate mapped to a frame by ticks; state time and animation time can differ."),
        new("ability.inferred-closure", Confidence.Inferred, "State reached from the ability's entry through at least one non-literal edge."),
        new("ability.hub-exit", Confidence.Inferred, "State shared by many abilities treated as a hub, not a member."),
        new("helper.state-owner", Confidence.Inferred, "Which entity (root or a helper) runs a shared state is not known statically."),
        new("cat.normal", Confidence.Inferred, "Single-button command leading to an attacking state."),
        new("cat.special", Confidence.Inferred, "Motion command (two or more direction steps) leading to an attacking state."),
        new("cat.super", Confidence.Inferred, "Power gate or cost of 1000 or more."),
        new("cat.throw", Confidence.Inferred, "HitDef p2stateno together with TargetBind in the ability's states."),
        new("cat.victim-state", Confidence.Inferred, "HitDef p2stateno without TargetBind (strike that moves the victim into a custom state)."),
        new("cat.projectile", Confidence.Inferred, "Projectile controller or a helper that owns a HitDef."),
        new("cat.summon", Confidence.Inferred, "Helper without a HitDef."),
        new("cat.mobility", Confidence.Inferred, "Movement controllers and no HitDef."),
        new("cat.defense", Confidence.Inferred, "NotHitBy/HitBy/HitOverride controllers."),
        new("cat.counter", Confidence.Inferred, "ReversalDef or HitOverride with a state."),
        new("cat.mode", Confidence.Inferred, "Writes a variable that many other states read."),
        new("label.author-comment", Confidence.Inferred, "Name taken from a comment above the block."),
        new("label.controller-name", Confidence.Inferred, "Name taken from the [State n, name] text."),
        new("var.boolean-flag", Confidence.Inferred, "Only ever written with 0 or 1."),
        new("var.counter", Confidence.Inferred, "Incremented by a literal step."),
        new("var.ai-read", Confidence.Inferred, "Read by a controller that also reads AILevel."),
        new("var.cross-entity", Confidence.Inferred, "Accessed through Root/Parent/Helper/Target redirects."),
        new("var.name-from-writer", Confidence.Inferred, "Name taken from the state or controller that writes it."),
        new("helper.has-hitdef", Confidence.Inferred, "Helper's states contain a HitDef."),
        new("helper.follower", Confidence.Inferred, "Helper's states use Bind/BindToRoot/BindToParent."),
        new("dynamic.candidate", Confidence.Inferred, "Candidate target/value derived from other literal facts."),

        // ---- Unknown
        new("state.dynamic-target", Confidence.Unknown, "ChangeState target is an expression that is not constant."),
        new("state.missing", Confidence.Unknown, "Target state is not defined in any indexed file."),
        new("command.undefined", Confidence.Unknown, "Command name is not defined in the CMD."),
        new("var.dynamic-index", Confidence.Unknown, "var/fvar index is not a literal."),
        new("anim.missing", Confidence.Unknown, "Animation number is not defined in the AIR."),
        new("anim.dynamic", Confidence.Unknown, "Animation number is an expression."),
        new("anim.sprite-missing", Confidence.Unknown, "Sprite named by an AIR frame is not in the SFF."),
        new("expr.unparsed", Confidence.Unknown, "The expression could not be parsed."),
        new("helper.dynamic-spawn", Confidence.Unknown, "Helper stateno/id is not a literal."),
        // ---- Milestone 2: combo candidates. These classify edges of the state graph; they never say a combo works.
        new("combo.source-literal", Confidence.StaticProven, "A -1/-2/-3 gate names its source states with literal stateno tests, so the edge starts from exactly those states."),
        new("combo.damage-literal", Confidence.StaticProven, "HitDef damage is a literal number."),
        new("combo.cost-literal", Confidence.StaticProven, "A literal negative PowerAdd/poweradd is this move's meter cost."),
        new("combo.source-unconstrained", Confidence.Inferred, "The gate does not name its source state; candidate sources are every attacking state that satisfies its statetype/movetype/contact conditions."),
        new("combo.neutral-node", Confidence.Inferred, "States treated as neutral (idle with control): common stand/crouch/walk states, or an idle Statedef with ctrl = 1."),
        new("combo.candidate-cancel", Confidence.Inferred, "A ChangeState out of a move that is driven by an input or by movehit/movecontact/moveguarded; with a contact condition it can only fire after the move connects."),
        new("combo.candidate-chain", Confidence.Inferred, "A ChangeState that needs no input and no contact: the move continuing by itself on a timer or animation frame."),
        new("combo.candidate-onhit", Confidence.Inferred, "HitDef p1stateno: the attacker moves to this state when the hit lands."),
        new("combo.candidate-start", Confidence.Inferred, "A command-gated edge that starts a move from a neutral state."),
        new("combo.candidate-link", Confidence.Inferred, "The move must recover to control (ctrl) before this input works; whether a link is possible depends on frame data that is not modelled."),
        new("combo.recovery", Confidence.Inferred, "The edge returns the character to a neutral state."),
        new("combo.damage-multi", Confidence.Inferred, "The state has several HitDefs or a non-literal damage, so its damage is a lower bound or unknown."),
        new("combo.frames-estimate", Confidence.Inferred, "Earliest cancel tick derived from Time/AnimElem gates against the AIR timeline."),
        new("combo.route", Confidence.Inferred, "A route is a candidate: every step is an edge of the static graph. Hit-stun, pushback, juggle, meter gain and timing are not modelled, so it is not a verified combo."),
        // ---- Milestone 3: facts observed in a real, disposable match. Only the runtime verifier may cite these, and only with trace frames.
        new("runtime.transition-observed", Confidence.RuntimeVerified, "In this tested route/run, the expected source-to-target state transition followed the planned inputs; this does not identify the controller that fired."),
        new("runtime.contact-observed", Confidence.RuntimeVerified, "In a real match, the contact the edge requires (moveHit/moveContact) was observed on the source move before the transition."),
        new("runtime.opponent-continuous", Confidence.RuntimeVerified, "In a real match, P2 stayed in a hit state from the first hit to the route's last step: no frame of recovery in between."),

        new("state.ambiguous-owner", Confidence.Inferred, "The controller block names a state other than the [Statedef] it sits in, so which state owns it (and therefore owns everything the block does) is a reading of the layout."),
    ];

    private static readonly Dictionary<string, EvidenceRule> ById = All.ToDictionary(r => r.Id, StringComparer.Ordinal);

    public static IReadOnlyList<EvidenceRule> Rules => All;

    public static EvidenceRule Get(string id) =>
        ById.TryGetValue(id, out var rule) ? rule : throw new KeyNotFoundException($"Unknown evidence rule '{id}'.");

    public static bool Exists(string id) => ById.ContainsKey(id);

    public static Confidence ConfidenceOf(string id) => Get(id).Confidence;
}
