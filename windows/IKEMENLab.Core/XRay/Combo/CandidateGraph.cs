using System.Globalization;
using IKEMENLab.Core.XRay.Expressions;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Query;
using IKEMENLab.Core.XRay.Source;

namespace IKEMENLab.Core.XRay.Combo;

/// <summary>
/// The Character Semantic Index read as a graph of combo candidates: states are nodes (plus one <c>neutral</c> node), and every
/// ChangeState / on-hit attacker state is an edge that keeps its gate facets, evidence, confidence and unmodelled conditions.
/// Nothing is simulated: an edge means "the files allow this transition under these conditions".
/// </summary>
public sealed class CandidateGraph
{
    public const string NeutralId = "neutral";
    private const int MaxSourcesPerGlobalBranch = 400;
    private static readonly HashSet<int> CommonNeutralStates = [0, 11, 12, 20];

    private readonly Dictionary<string, MoveInfo> _moves;
    private readonly Dictionary<string, List<CandidateEdge>> _out = new(StringComparer.Ordinal);

    private CandidateGraph(SemanticIndex index, Dictionary<string, MoveInfo> moves, List<CandidateEdge> edges, List<string> dynamic)
    {
        Index = index;
        _moves = moves;
        Edges = edges;
        DynamicTargets = dynamic;
        foreach (var e in edges) (_out.TryGetValue(e.From, out var l) ? l : _out[e.From] = []).Add(e);
    }

    public SemanticIndex Index { get; }
    public IReadOnlyList<CandidateEdge> Edges { get; }
    /// <summary>ChangeState controllers whose target is not a constant: they can never be candidates and are listed, not guessed.</summary>
    public IReadOnlyList<string> DynamicTargets { get; }
    public IReadOnlyCollection<MoveInfo> Moves => _moves.Values;

    public MoveInfo? Move(string stateId) => _moves.TryGetValue(stateId, out var m) ? m : null;
    public IReadOnlyList<CandidateEdge> From(string id) => _out.TryGetValue(id, out var l) ? l : [];

    public static string? ResolveState(SemanticIndex index, string spec)
    {
        spec = spec.Trim();
        if (spec.Equals(NeutralId, StringComparison.OrdinalIgnoreCase)) return NeutralId;
        var id = spec.StartsWith("state:", StringComparison.Ordinal) ? spec : "state:" + spec;
        return index.Get(id) is { IsStub: false } ? id : null;
    }

    // ------------------------------------------------------------------ build

    public static CandidateGraph Build(SemanticIndex index)
    {
        var moves = BuildMoves(index);
        var edges = new List<CandidateEdge>();
        var dynamic = new List<string>();

        foreach (var rel in index.Relationships)
        {
            if (rel.Kind is not (RelationKind.ChangesState or RelationKind.SetsAttackerState)) continue;
            var controllerId = rel.From.EndsWith("/hitdef", StringComparison.Ordinal) ? rel.From[..^7] : rel.From;
            var controller = index.Get(controllerId);
            var owner = index.OwnerState(controllerId);
            if (controller is null || owner is null) continue;

            var target = index.Get(rel.To);
            if (target is null) continue;
            if (rel.To.StartsWith("dynamic:", StringComparison.Ordinal))
            {
                dynamic.Add(controllerId);
                continue;
            }

            if (target.IsStub && target.Prop("engineCommon") != "true") continue;   // undefined state: nothing to enter
            var gate = rel.Gate ?? controller.Gate;
            if (gate is null || gate.Branches.Count == 0) continue;                   // no trigger1: never fires

            var toId = ToNode(moves, rel.To, index);
            if (toId is null) continue;

            for (var b = 0; b < gate.Branches.Count; b++)
                AddBranchEdges(index, moves, edges, rel, controller, owner, toId, gate.Branches[b], b);
        }

        edges.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
        return new CandidateGraph(index, moves, edges, dynamic.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

    private static string? ToNode(Dictionary<string, MoveInfo> moves, string stateId, SemanticIndex index)
    {
        if (moves.TryGetValue(stateId, out var m)) return m.IsNeutral ? NeutralId : m.StateId;
        // Engine-common state that was not loaded: a common-neutral number is neutral, anything else is opaque.
        if (index.Get(stateId) is { } o && o.Prop("engineCommon") == "true" && int.TryParse(o.Id["state:".Length..], out var n))
            return CommonNeutralStates.Contains(n) ? NeutralId : null;
        return null;
    }

    private static void AddBranchEdges(SemanticIndex index, Dictionary<string, MoveInfo> moves, List<CandidateEdge> edges,
        Relationship rel, SemanticObject controller, SemanticObject owner, string toId, GateBranch branch, int branchIndex)
    {
        var f = branch.Facets;
        var ownerNumber = int.TryParse(owner.Prop("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var on) ? on : 0;
        var isGlobal = ownerNumber is -1 or -2 or -3;
        var isOnHit = rel.Kind == RelationKind.SetsAttackerState;

        var contact = isOnHit ? ContactRequirement.Hit : ContactOf(f);
        var sources = new List<(string Id, string Rule, bool Inferred)>();

        if (isGlobal)
        {
            if (f.HasSourceStateConstraint)
            {
                foreach (var m in moves.Values.Where(m => m.Number >= 0 && StateRange.Any(f.SourceStateRanges, m.Number) &&
                                                          !StateRange.Any(f.ExcludedStateRanges, m.Number) && (!m.IsCommon || m.IsNeutral)))
                {
                    var id = m.IsNeutral ? NeutralId : m.StateId;
                    if (!sources.Any(s => s.Id == id)) sources.Add((id, "combo.source-literal", false));
                }
            }
            else
            {
                if (f.CtrlRequired || contact == ContactRequirement.None) sources.Add((NeutralId, "combo.neutral-node", true));
                if (!f.CtrlRequired && contact != ContactRequirement.None)
                {
                    foreach (var m in moves.Values.Where(m => !m.IsNeutral && !m.IsCommon && m.Number >= 0 && m.HitDefCount > 0 && MatchesTypes(m, f)).Take(MaxSourcesPerGlobalBranch))
                        sources.Add((m.StateId, "combo.source-unconstrained", true));
                }
            }
        }
        else
        {
            if (!moves.TryGetValue(owner.Id, out var om)) return;
            if (f.HasSourceStateConstraint && !StateRange.Any(f.SourceStateRanges, om.Number)) return;   // contradicts its own state: cannot fire
            if (StateRange.Any(f.ExcludedStateRanges, om.Number)) return;
            if (om.IsCommon && !om.IsNeutral) return;                                                     // hit / get-up / air-recovery states
            if (om.MoveType == "H") return;
            sources.Add((om.IsNeutral ? NeutralId : om.StateId, om.IsNeutral ? "combo.neutral-node" : "structure.contains", om.IsNeutral));
        }

        var ownerInferred = controller.Prop("p.declaredState") is not null;
        foreach (var (fromId, sourceRule, sourceInferred) in sources)
        {
            if (fromId == NeutralId && toId == NeutralId) continue;

            var kind = ClassifyKind(isOnHit, fromId, toId, contact, f);
            if (kind == EdgeKind.Recovery && fromId == NeutralId) continue;

            var notes = new List<string>();
            var evidence = new List<EdgeEvidence>
            {
                new(rel.Evidence[0].RuleId, rel.Id, rel.Evidence[0].Source),
                new(ClassificationRule(kind), rel.Id, rel.Evidence[0].Source)
            };
            if (sourceRule != "structure.contains") evidence.Add(new EdgeEvidence(sourceRule, rel.Id, rel.Evidence[0].Source));
            if (fromId == NeutralId && sourceRule != "combo.neutral-node") evidence.Add(new EdgeEvidence("combo.neutral-node", null, null));
            if (toId == NeutralId) evidence.Add(new EdgeEvidence("combo.neutral-node", null, null));

            var confidence = rel.Confidence;
            if (sourceInferred || toId == NeutralId || fromId == NeutralId && kind != EdgeKind.Start) confidence = StateGraph.Weakest(confidence, Confidence.Inferred);
            if (kind == EdgeKind.Start && fromId == NeutralId) confidence = StateGraph.Weakest(confidence, Confidence.Inferred);

            var unmodelled = new List<string>(f.UnmodelledConditions);
            if (ownerInferred)
            {
                notes.Add($"the block declares State {controller.Prop("p.declaredState")} but sits in {owner.Name}; its owning state is a reading of the layout");
                unmodelled.Add("owning state (block header disagrees with its Statedef)");
            }

            if (f.CtrlRequired && fromId != NeutralId) notes.Add("needs ctrl: only after the source move returns control");
            if (f.Power.Any(p => p.Op is "<" or "<=" or "=" or "!=")) unmodelled.Add("power upper bound / exact power test");
            if (f.Commands.Count == 0 && f.NegatedCommands.Count == 0 && kind is EdgeKind.Start or EdgeKind.Cancel)
                notes.Add("no command in this gate: the edge fires without an input");
            if (rel.Kind == RelationKind.ChangesState && controller.Prop("persistent") is { } persistent && persistent != "1") notes.Add($"persistent = {persistent}");

            var hasHit = fromId != NeutralId && moves.TryGetValue(fromId, out var srcMove) && srcMove.HitDefCount > 0;
            if (contact != ContactRequirement.None && fromId != NeutralId && !hasHit && !isOnHit)
                notes.Add("the source state has no HitDef, so this contact condition cannot become true from it");
            if (kind == EdgeKind.Cancel && contact == ContactRequirement.None)
                notes.Add("no contact condition: this cancels on whiff too, so it is not a hit-confirm");

            edges.Add(new CandidateEdge
            {
                Id = $"cand:{fromId}>{toId}@{controller.Id}#{branchIndex}",
                From = fromId,
                To = toId,
                Kind = kind,
                ControllerId = controller.Id,
                RelationshipId = rel.Id,
                BranchNumber = branch.Number,
                Contact = contact,
                Confidence = confidence,
                Facets = f,
                Evidence = evidence,
                Unmodelled = unmodelled,
                Notes = notes,
                IsGlobal = isGlobal,
                SourceHasHitDef = hasHit,
                EarliestTick = fromId == NeutralId ? null : EarliestTick(index, fromId, f)
            });
        }
    }

    private static EdgeKind ClassifyKind(bool isOnHit, string from, string to, ContactRequirement contact, GateFacets f)
    {
        if (isOnHit) return EdgeKind.OnHit;
        if (to == NeutralId) return EdgeKind.Recovery;
        if (from == NeutralId) return EdgeKind.Start;
        if (f.CtrlRequired) return EdgeKind.Link;
        // An input, or a contact condition, makes it a cancel (with no contact it is a whiff cancel). No input and no contact is
        // the move continuing by itself on a timer or an animation frame.
        return contact != ContactRequirement.None || f.Commands.Count > 0 || f.NegatedCommands.Count > 0 ? EdgeKind.Cancel : EdgeKind.Chain;
    }

    private static string ClassificationRule(EdgeKind kind) => kind switch
    {
        EdgeKind.Start => "combo.candidate-start",
        EdgeKind.Cancel => "combo.candidate-cancel",
        EdgeKind.OnHit => "combo.candidate-onhit",
        EdgeKind.Chain => "combo.candidate-chain",
        EdgeKind.Link => "combo.candidate-link",
        _ => "combo.recovery"
    };

    private static ContactRequirement ContactOf(GateFacets f)
    {
        if (f.Contact.Contains("movehit")) return ContactRequirement.Hit;
        if (f.Contact.Contains("movecontact")) return ContactRequirement.Contact;
        if (f.Contact.Contains("moveguarded")) return ContactRequirement.Guarded;
        return ContactRequirement.None;
    }

    private static bool MatchesTypes(MoveInfo m, GateFacets f) => Matches(m.StateType, f.StateTypes) && Matches(m.MoveType, f.MoveTypes);

    /// <summary>Facet values are "s" (must be) or "a!" (must not be); a state without the property only satisfies negations.</summary>
    private static bool Matches(string? actual, IReadOnlyList<string> wanted)
    {
        var positive = wanted.Where(w => !w.EndsWith('!')).ToList();
        var negative = wanted.Where(w => w.EndsWith('!')).Select(w => w[..^1]).ToList();
        var a = actual?.ToLowerInvariant();
        if (positive.Count > 0 && (a is null || !positive.Contains(a))) return false;
        return a is null || !negative.Contains(a);
    }

    /// <summary>Earliest tick of the source state at which the gate's Time / AnimElem conditions can hold.</summary>
    private static int? EarliestTick(SemanticIndex index, string stateId, GateFacets f)
    {
        int? best = null;
        foreach (var t in f.Time)
        {
            int? v = t.Op switch { "=" or ">=" => (int)t.Value, ">" => (int)t.Value + 1, _ => null };
            if (v is not null) best = Math.Max(best ?? 0, v.Value);
        }

        var animId = index.Outgoing(stateId, RelationKind.UsesAnim).FirstOrDefault(r => r.Confidence == Confidence.StaticProven)?.To;
        if (animId is not null)
        {
            foreach (var a in f.AnimElem.Where(a => a.Op == "="))
            {
                var frame = index.Get($"{animId}/frame:{(int)a.Value - 1}");
                if (frame is not null && int.TryParse(frame.Prop("startTick"), out var tick)) best = Math.Max(best ?? 0, tick);
            }
        }

        return best;
    }

    // ------------------------------------------------------------------ moves

    private static Dictionary<string, MoveInfo> BuildMoves(SemanticIndex index)
    {
        var moves = new Dictionary<string, MoveInfo>(StringComparer.Ordinal);
        foreach (var s in index.Of(ObjectKind.State))
        {
            if (s.IsStub || s.Prop("shadowed") == "true" || !int.TryParse(s.Prop("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) continue;
            var common = s.Prop("common") == "true";
            var type = s.Prop("p.type")?.Trim().ToLowerInvariant();
            var moveType = s.Prop("p.movetype")?.Trim().ToUpperInvariant();
            var controllers = index.ControllersOf(s.Id);

            var hitDefs = controllers.Select(c => index.Get(c.Id + "/hitdef")).OfType<SemanticObject>()
                .Where(h => h.Prop("controller") is "hitdef").ToList();
            double? damage = 0;
            var exact = hitDefs.Count <= 1;
            foreach (var h in hitDefs)
            {
                if (First(h.Prop("p.damage")) is { } d) damage += d;
                else exact = false;
            }

            if (hitDefs.Count > 0 && hitDefs.All(h => First(h.Prop("p.damage")) is null)) damage = null;
            if (hitDefs.Count == 0) damage = null;

            var firstHit = hitDefs.Select(h => h.ParentId).OfType<string>()
                .SelectMany(cid => index.Outgoing(cid, RelationKind.ActiveAtFrame))
                .Select(r => index.Get(r.To)).OfType<SemanticObject>()
                .Select(fr => int.TryParse(fr.Prop("startTick"), out var t) ? (int?)t : null)
                .Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty(-1).Min();

            var animRel = index.Outgoing(s.Id, RelationKind.UsesAnim).FirstOrDefault(r => r.Confidence == Confidence.StaticProven);
            var anim = animRel is null ? null : index.Get(animRel.To);
            int? ticks = anim is not null && anim.Prop("endsInfinite") != "true" && int.TryParse(anim.Prop("totalTicks"), out var tt) ? tt : null;

            var cost = 0.0;
            foreach (var owner in controllers.Select(c => c.Id).Append(s.Id))
                foreach (var r in index.Outgoing(owner, RelationKind.ResourceCost))
                    if (double.TryParse(r.Prop("amount"), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) && amount < 0) cost += -amount;

            var hitFirst = hitDefs.FirstOrDefault();
            var neutral = (common && CommonNeutralStates.Contains(number)) ||
                          (!common && moveType == "I" && s.Prop("p.ctrl")?.Trim() == "1" && hitDefs.Count == 0 && number >= 0);
            moves[s.Id] = new MoveInfo(
                s.Id, number, s.Name, type?.ToUpperInvariant(), moveType, common, neutral, hitDefs.Count, damage, exact && damage is not null,
                hitFirst is null ? null : (int?)First(hitFirst.Prop("p.pausetime")), hitFirst?.Prop("p.hitflag"), hitFirst?.Prop("p.guardflag"), hitFirst?.Prop("p.ground.type"),
                anim?.Id, ticks, firstHit < 0 ? null : firstHit, cost,
                index.Outgoing(s.Id, RelationKind.PartOf).Select(r => r.To).OrderBy(x => x, StringComparer.Ordinal).ToList());
        }

        return moves;
    }

    /// <summary>First comma-separated value of a HitDef parameter when it is a constant ("30, 5" → 30).</summary>
    public static double? First(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var first = raw.Split(',')[0];
        return StateLinker.ConstNumber(ExprParser.Parse(first));
    }

    // ------------------------------------------------------------------ readiness

    /// <summary>How much of the character the candidate graph can actually speak about; the honest answer to "is this enough?".</summary>
    public ReadinessReport Readiness()
    {
        var byKind = Edges.GroupBy(e => e.Kind.ToString()).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        var byConf = Edges.GroupBy(e => e.Confidence.ToString()).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        var moveStates = _moves.Values.Where(m => !m.IsCommon && !m.IsNeutral && m.Number >= 0).ToList();
        var withHit = moveStates.Count(m => m.HitDefCount > 0);
        var literal = moveStates.Count(m => m.Damage is not null);
        var ticks = moveStates.Count(m => m.AnimTicks is not null);
        var unmodelled = Edges.Count(e => e.Unmodelled.Count > 0);
        var globalEdges = Edges.Count(e => e.IsGlobal);
        var unconstrained = Edges.Count(e => e.Evidence.Any(x => x.RuleId == "combo.source-unconstrained"));

        var findings = new List<string>();
        if (Edges.Count == 0) findings.Add("No candidate edges: no ChangeState has a fireable gate.");
        if (Edges.Count > 0 && unmodelled * 2 > Edges.Count) findings.Add($"{unmodelled} of {Edges.Count} edges have conditions the index does not model; treat routes as leads, not results.");
        if (DynamicTargets.Count > 0) findings.Add($"{DynamicTargets.Count} ChangeState controller(s) have a non-constant target and are invisible to the graph.");
        if (moveStates.Count > 0 && literal * 2 < withHit) findings.Add("Fewer than half of the attacking moves have a literal HitDef damage; route damage will be a lower bound.");
        if (moveStates.Count > 0 && ticks * 2 < moveStates.Count) findings.Add("Animation length is unknown for most moves (dynamic or missing anim), so frame estimates are mostly unavailable.");
        if (unconstrained > 0) findings.Add($"{unconstrained} edge(s) come from a gate that names no source state and were expanded to every attacking state; they are inferred.");
        if (Index.Diagnostics.Any(d => d.Code == "state.ambiguous-owner")) findings.Add("This character parks controller blocks inside another Statedef; edges from them are inferred.");
        findings.Add("Not modelled anywhere: hit-stun and block-stun, pushback and spacing, juggle points, damage scaling, meter gain, and whether a cancel window is open on a given tick.");

        return new ReadinessReport(_moves.Count, moveStates.Count, _moves.Values.Count(m => m.IsNeutral), Edges.Count, byKind, byConf, unmodelled,
            DynamicTargets.Count, withHit, literal, ticks, globalEdges, unconstrained, findings);
    }
}
