using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.Core.XRay.Sequences;

/// <summary>The character's forward dash: the command to press and the state it starts (possibly an engine common state such as 100 RunFwd).</summary>
public sealed record DashPath(IReadOnlyList<string> Commands, string StateId, int State, string ControllerId, CandidateEdge? Edge);

/// <summary>How one step will be run, in plain words, and what it rests on.</summary>
public sealed record SequenceStepPlan(int Index, SequenceAction Action, string Label, string How, CandidateEdge? Edge, string? StateId);

public sealed record SequencePlanResult(InputPlan? Plan, string? Refused, int? RefusedStep, IReadOnlyList<SequenceStepPlan> Steps, IReadOnlyList<string> Warnings);

/// <summary>
/// Turns a Sequence into ONE input plan for the existing driver: abilities become ordinary plan steps (command → expected state), and walk, wait,
/// chase and jump become <see cref="StepAction"/> steps between them. It never guesses: a follow-up is pressed as a real cancel when the candidate graph
/// has one from the previous move, otherwise through the ability's own command once P1 can act; anything else is refused with the reason.
/// </summary>
public static class SequencePlanner
{
    public const int ChaseGiveUpFrames = 300, JumpWatchFrames = 45;

    public static SequencePlanResult Plan(CandidateGraph graph, Sequence sequence, int approachDistance)
    {
        var index = graph.Index;
        var steps = new List<PlanStep>();
        var described = new List<SequenceStepPlan>();
        var warnings = new List<string>();
        var options = new PlanOptions { ApproachDistance = approachDistance };
        SequencePlanResult Refuse(int i, string why) => new(null, why, i, described, warnings);

        if (sequence.Actions.Count == 0) return Refuse(0, "The sequence has no steps.");
        if (sequence.Actions[0].Kind != SequenceActionKind.Ability) return Refuse(1, "A sequence starts with an ability (Play Ability), from neutral.");

        string? previousMove = null;      // the state of the last move step, while nothing but waits followed it
        var airborne = false;              // right after a Jump
        int? lastAnimTicks = null;
        var budget = options.NeutralFrames + 400;
        for (var i = 1; i <= sequence.Actions.Count; i++)
        {
            var action = sequence.Actions[i - 1];
            var label = SequenceLabels.Label(action, index);
            if (action.Problem() is { } problem) return Refuse(i, $"Step {i} ({label}): {problem}");
            switch (action.Kind)
            {
                case SequenceActionKind.Ability:
                {
                    var ability = index.Get(action.AbilityId!);
                    if (ability is null || ability.Kind != ObjectKind.Ability) return Refuse(i, $"Step {i}: {action.AbilityId} is not an ability.");
                    var entry = ability.Prop("entryState");
                    var move = entry is null ? null : graph.Move(entry);
                    if (move is null || move.IsNeutral) return Refuse(i, $"Step {i} ({label}): its entry state is not a move X-Ray can play.");

                    CandidateEdge? edge;
                    string how;
                    int? fromState = null;
                    if (i == 1)
                    {
                        var path = AbilityPlayback.Resolve(graph, action.AbilityId!, options);
                        if (path.Path is null) return Refuse(i, $"Step 1 ({label}): {path.Refused}");
                        edge = path.Path.Edge;
                        warnings.AddRange(path.Path.Warnings.Select(w => $"Step 1 ({label}): {w}"));
                        how = $"From neutral, press “{path.Path.Command}”.";
                    }
                    else if (previousMove is not null && CancelInto(graph, previousMove, entry!) is { } cancel)
                    {
                        edge = cancel;
                        fromState = graph.Move(previousMove)?.Number;
                        how = $"Cancel {StateName(index, previousMove)} into it by pressing “{string.Join("+", cancel.Commands)}”" +
                              (cancel.Contact switch { ContactRequirement.Hit => " once it hits", ContactRequirement.Contact => " once it touches", ContactRequirement.Guarded => " once it is blocked", _ => string.Empty }) + ".";
                    }
                    else
                    {
                        edge = StartFrom(graph, entry!, airborne, out var why);
                        if (edge is null) return Refuse(i, $"Step {i} ({label}) cannot be started here: {why}");
                        how = $"Once you can act, press “{string.Join("+", edge.Commands)}”" + (airborne ? " in the air." : ".");
                        if (edge.Facets.Power.Count > 0) warnings.Add($"Step {i} ({label}): its gate tests power; the test match grants no meter.");
                    }

                    var notes = new List<string>();
                    var input = InputPlanner.BuildInput(index, edge.Commands, options, notes);
                    if (input is null) return Refuse(i, $"Step {i} ({label}): the command {string.Join("+", edge.Commands)} cannot be read literally from the CMD.");
                    if (edge.Unmodelled.Count > 0) warnings.Add($"Step {i} ({label}): its gate has {edge.Unmodelled.Count} condition(s) X-Ray does not model.");
                    var contact = fromState is null || edge.Contact == ContactRequirement.None ? null : edge.Contact.ToString().ToLowerInvariant();
                    steps.Add(new PlanStep(i, edge.Id, edge.Kind.ToString(), fromState is null ? CandidateGraph.NeutralId : previousMove!, entry!, fromState, move.Number,
                        string.Join("+", edge.Commands), input, contact, fromState is null ? null : edge.EarliestTick, options.StepTimeout, notes) { Attack = true });
                    described.Add(new SequenceStepPlan(i, action, label, how, edge, entry));
                    previousMove = entry;
                    airborne = false;
                    lastAnimTicks = move.AnimTicks;
                    budget += 180 + input.Count + options.StepTimeout + (move.AnimTicks ?? 120);
                    break;
                }
                case SequenceActionKind.Dash:
                {
                    var dash = Dash(graph);
                    if (dash is null) return Refuse(i, $"Step {i}: this character has no forward-dash command X-Ray can read (a command of two forward taps that starts a dash state from neutral).");
                    var notes = new List<string>();
                    var input = InputPlanner.BuildInput(index, dash.Commands, options, notes)!;
                    steps.Add(new PlanStep(i, dash.Edge?.Id ?? $"dash:{dash.ControllerId}", "Start", CandidateGraph.NeutralId, dash.StateId, null, dash.State,
                        string.Join("+", dash.Commands), input, null, null, options.StepTimeout, notes));
                    described.Add(new SequenceStepPlan(i, action, label, $"Once you can act, dash (“{string.Join("+", dash.Commands)}”, into State {dash.State}).", dash.Edge, dash.StateId));
                    previousMove = dash.Edge is null ? null : dash.StateId;
                    airborne = false;
                    budget += 180 + input.Count + options.StepTimeout;
                    break;
                }
                case SequenceActionKind.WalkForward or SequenceActionKind.WalkBackward:
                {
                    var forward = action.Kind == SequenceActionKind.WalkForward;
                    steps.Add(ActionStep(i, action, new StepAction(StepAction.Hold, action.Frames, Keys: [forward ? "F" : "B"]), label));
                    described.Add(new SequenceStepPlan(i, action, label, $"Once you can act, walk {(forward ? "forward" : "back")} for {action.Frames} frames.", null, null));
                    previousMove = null;
                    budget += 180 + action.Frames;
                    break;
                }
                case SequenceActionKind.Wait:
                    steps.Add(ActionStep(i, action, new StepAction(StepAction.Release, action.Frames, WaitForControl: false), label));
                    described.Add(new SequenceStepPlan(i, action, label, $"Wait {action.Frames} frames (counted from the end of the previous step).", null, null));
                    budget += action.Frames;
                    break;
                case SequenceActionKind.Chase:
                    steps.Add(ActionStep(i, action, new StepAction(StepAction.Chase, ChaseGiveUpFrames, action.Distance, action.StopIfOpponentRecovers), label));
                    described.Add(new SequenceStepPlan(i, action, label,
                        $"Once you can act, walk forward until within {action.Distance} (give up after {ChaseGiveUpFrames} frames)" +
                        (action.StopIfOpponentRecovers ? "; stop as soon as the opponent can act again." : "."), null, null));
                    previousMove = null;
                    budget += 180 + ChaseGiveUpFrames;
                    break;
                default:
                    steps.Add(ActionStep(i, action, new StepAction(StepAction.Jump, JumpWatchFrames), label));
                    described.Add(new SequenceStepPlan(i, action, label, "Once you can act, jump (press up).", null, null));
                    previousMove = null;
                    airborne = true;
                    budget += 180 + JumpWatchFrames;
                    break;
            }
        }

        var tail = AbilityPlayback.TailFramesFor(lastAnimTicks);
        var max = Math.Clamp(budget + tail + 200, 1800, 7200);
        var plan = new InputPlan(index.CharacterId, sequence.ScopeKey, steps, approachDistance, options.NeutralFrames, tail, max, warnings.ToList());
        return new SequencePlanResult(plan, null, null, described, warnings);
    }

    private static PlanStep ActionStep(int i, SequenceAction action, StepAction step, string label) =>
        new(i, $"action:{i}:{action.Kind}", "Action", string.Empty, string.Empty, null, -1, null, [], null, null, step.Frames ?? 0, [label]) { Action = step };

    private static string StateName(SemanticIndex index, string stateId) => index.Get(stateId) is not null ? index.NameOf(stateId) : stateId;

    /// <summary>A pressable cancel out of <paramref name="from"/> into <paramref name="to"/> (a Cancel edge, or a self-continuing Chain that still names a command).</summary>
    private static CandidateEdge? CancelInto(CandidateGraph graph, string from, string to) =>
        graph.From(from).Where(e => e.To == to && e.Commands.Count > 0 && e.Kind is EdgeKind.Cancel or EdgeKind.Chain && InputPlanner.BuildInput(graph.Index, e.Commands, new PlanOptions(), []) is not null)
            .OrderBy(e => e.Contact == ContactRequirement.None ? 1 : 0)              // a hit-confirm cancel is the normal follow-up
            .ThenBy(e => Rank(e.Confidence)).ThenBy(e => e.Unmodelled.Count).ThenBy(e => e.Id, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>The ability's own command from neutral (P1 must be able to act). In the air only air starts count; on the ground only standing ones.</summary>
    private static CandidateEdge? StartFrom(CandidateGraph graph, string to, bool airborne, out string why)
    {
        var starts = graph.From(CandidateGraph.NeutralId).Where(e => e.To == to && e.Kind == EdgeKind.Start && e.Commands.Count > 0).ToList();
        var fitting = starts.Where(e => Stance(e) == (airborne ? "air" : "ground") || Stance(e) == "any")
            .Where(e => InputPlanner.BuildInput(graph.Index, e.Commands, new PlanOptions(), []) is not null).ToList();
        why = starts.Count == 0
            ? "it has no command from neutral (it is entered by the AI or only from other moves)."
            : airborne ? "its command only works on the ground." : "its command only works while crouching or in the air.";
        return fitting.OrderBy(e => e.Facets.Power.Any(p => p.Value > 0) ? 1 : 0).ThenBy(e => Rank(e.Confidence)).ThenBy(e => e.Unmodelled.Count)
            .ThenBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>"air" (statetype A required), "ground" (standing, or S required), "crouch" (C only) or "any" (no statetype test).</summary>
    private static string Stance(CandidateEdge e)
    {
        var positive = e.Facets.StateTypes.Where(t => !t.EndsWith('!')).Select(t => t.ToLowerInvariant()).ToList();
        if (positive.Count == 0) return "any";
        if (positive.Contains("a") && !positive.Contains("s")) return "air";
        return positive.Contains("s") ? "ground" : "crouch";
    }

    /// <summary>
    /// The forward dash: a Start edge from neutral whose command is two forward taps (or is named FF / dash / run); failing that, the literal
    /// ChangeState in the always-running states (-1/-2/-3) gated by such a command, whose target may be an engine common state the files do not define
    /// (kfm's FF → 100 RunFwd). The transition into that state number is what the route verifier then checks.
    /// </summary>
    public static DashPath? Dash(CandidateGraph graph)
    {
        if (DashEdge(graph) is { } edge) return new DashPath(edge.Commands, edge.To, graph.Move(edge.To)!.Number, edge.ControllerId, edge);
        var index = graph.Index;
        foreach (var hub in new[] { "state:-1", "state:-2", "state:-3" })
        {
            foreach (var c in index.ControllersOf(hub))
            {
                var branch = c.Gate?.Branches.FirstOrDefault(b => b.Facets.Commands.Count == 1 && IsDashCommand(index, b.Facets.Commands[0]));
                if (branch is null) continue;
                var target = index.Outgoing(c.Id, RelationKind.ChangesState).Select(r => r.To).FirstOrDefault(t => t.StartsWith("state:", StringComparison.Ordinal));
                if (target is null || !int.TryParse(target["state:".Length..], out var number) || number <= 0) continue;
                if (InputPlanner.BuildInput(index, branch.Facets.Commands, new PlanOptions(), []) is null) continue;
                return new DashPath(branch.Facets.Commands, target, number, c.Id, null);
            }
        }

        return null;
    }

    private static bool IsDashCommand(SemanticIndex index, string command)
    {
        if (command.Equals("FF", StringComparison.OrdinalIgnoreCase) || command.Equals("dash", StringComparison.OrdinalIgnoreCase) || command.Equals("run", StringComparison.OrdinalIgnoreCase))
            return true;
        var raw = index.Get("cmd:" + command)?.Prop("raw");
        if (raw is null) return false;
        var taps = CommandIndexer.Tokenize(raw);
        return taps.Count == 2 && taps.All(t => t.Keys.Count == 1 && t.Keys[0].Trim() == "F");
    }

    /// <summary>A Start edge from neutral whose command is two forward taps (or is named FF / dash / run) into a non-neutral state.</summary>
    public static CandidateEdge? DashEdge(CandidateGraph graph)
    {
        bool IsDash(string command)
        {
            if (command.Equals("FF", StringComparison.OrdinalIgnoreCase) || command.Equals("dash", StringComparison.OrdinalIgnoreCase) || command.Equals("run", StringComparison.OrdinalIgnoreCase))
                return true;
            var raw = graph.Index.Get("cmd:" + command)?.Prop("raw");
            if (raw is null) return false;
            var taps = CommandIndexer.Tokenize(raw);
            return taps.Count == 2 && taps.All(t => t.Keys.Count == 1 && t.Keys[0].Trim() == "F");
        }

        return graph.From(CandidateGraph.NeutralId)
            .Where(e => e.Kind == EdgeKind.Start && e.Commands.Count == 1 && IsDash(e.Commands[0]) && graph.Move(e.To) is { IsNeutral: false })
            .Where(e => InputPlanner.BuildInput(graph.Index, e.Commands, new PlanOptions(), []) is not null)
            .OrderBy(e => Rank(e.Confidence)).ThenBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    private static int Rank(Confidence c) => c switch { Confidence.StaticProven => 0, Confidence.Inferred => 1, _ => 2 };
}
