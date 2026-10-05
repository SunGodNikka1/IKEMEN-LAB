using System.Globalization;
using System.Text.Json;
using IKEMENLab.Core.XRay.Behavior;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Sequences;
using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.Core.XRay.Director;

/// <summary>Whether a follow-up has been established as usable at runtime on the current character files and engine, and by what.</summary>
public sealed record FollowUpProof(bool Proven, string Text, string? SourceKind, string? SourceId, double? ConnectedRate = null);

/// <summary>A pre-filled Teach AI wizard: the spec, where it came from, the controlled test it will run, and what was read from the evidence.</summary>
public sealed record TeachAiDraft(KnockdownChaseSpec Spec, TaughtSource Source, TaughtTestPlan Tests, IReadOnlyList<string> Notes);

/// <summary>One follow-up the wizard may offer: a ground move with runtime proof on the current files and engine.</summary>
public sealed record FollowUpChoice(string AbilityId, int EntryState, string Name, double PowerCost, FollowUpProof Proof);

/// <summary>
/// Teach AI v1 (Knockdown Chase only): pre-fills the wizard from real evidence — a successful Sequence Lab experiment, a recognised Knockdown Chase in
/// a watched run, or an ability — and proves the follow-up. "Execution only through a path Play Ability / Sequence Lab established": a follow-up is
/// offered only when a Sequence Lab trial performed it (runtime.transition-observed) or Play Ability reported it Performed, on the CURRENT character
/// files and engine. Anything else is refused with the reason.
/// </summary>
public static class TeachAi
{
    public const int DefaultChaseLimit = 160, DefaultAttackDistance = 35, DefaultGiveUpDown = 90, DefaultGiveUpWakeUp = 150, DefaultLead = 4;

    /// <summary>Runtime evidence that <paramref name="abilityId"/> is usable: Sequence Lab trials (performed, and how often it connected) or Play Ability runs.</summary>
    public static FollowUpProof Prove(SemanticIndex index, CandidateGraph graph, string abilityId, string? engineSha, ExperimentStore experiments,
        IEnumerable<string> playbackRoots)
    {
        var ability = index.Get(abilityId);
        if (ability is null || ability.Kind != ObjectKind.Ability) return new FollowUpProof(false, $"{abilityId} is not an ability of this character.", null, null);
        var entry = ability.Prop("entryState");
        var move = entry is null ? null : graph.Move(entry);
        var name = index.NameOf(abilityId);
        if (move is null) return new FollowUpProof(false, $"{name} has no entry state X-Ray can play.", null, null);
        if (string.Equals(move.StateType, "A", StringComparison.OrdinalIgnoreCase))
            return new FollowUpProof(false, $"{name} is an air move; the v1 Knockdown Chase only starts ground follow-ups.", null, null);
        if (engineSha is null) return new FollowUpProof(false, "The playback engine is not known, so runtime proof cannot be matched to it.", null, null);

        var hash = ExperimentScope.HashOf(index);
        var performed = 0;
        var connected = 0;
        string? sourceId = null;
        foreach (var e in experiments.List(hash).Where(e => string.Equals(e.Scope.EngineSha256, engineSha, StringComparison.OrdinalIgnoreCase)))
            foreach (var t in e.Trials)
            {
                var steps = TrialSteps(Path.Combine(e.Directory, "trials", t.RecordId));
                foreach (var st in steps.Where(s => s.ToState == move.Number && s.Done))
                {
                    performed++;
                    if (st.Connected == true) connected++;
                    sourceId ??= e.Id;
                }
            }

        if (performed > 0)
            return new FollowUpProof(true, $"{name} was performed in {performed} Sequence Lab trial step(s) on these files and this engine ({connected} connected).",
                "experiment", sourceId, (double)connected / performed);

        foreach (var root in playbackRoots.Where(Directory.Exists))
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var meta = Path.Combine(dir, "meta.json");
                if (!File.Exists(meta)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(meta));
                    var r = doc.RootElement;
                    string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    if (S("mode") != "ability" || S("target") != abilityId || S("status") != "Performed") continue;
                    if (!string.Equals(S("engineSha256"), engineSha, StringComparison.OrdinalIgnoreCase)) continue;
                    if (S("characterHash") is { } ch && ch != hash) continue;
                    return new FollowUpProof(true, $"Play Ability performed {name} on this engine (run {Path.GetFileName(dir)}).", "ability", Path.GetFileName(dir));
                }
                catch (Exception ex) when (ex is IOException or JsonException) { /* unreadable record: not proof */ }
            }

        return new FollowUpProof(false,
            $"{name} has no runtime proof on these character files and this engine. Run Play Ability for it, or a Sequence Lab test that uses it, first.", null, null);
    }

    /// <summary>Every ground ability with a playable entry state, each with its proof (proven ones first).</summary>
    public static IReadOnlyList<FollowUpChoice> Choices(SemanticIndex index, CandidateGraph graph, string? engineSha, ExperimentStore experiments, IEnumerable<string> playbackRoots)
    {
        var roots = playbackRoots.ToList();
        return index.Of(ObjectKind.Ability)
            .Select(a => (a, Move: a.Prop("entryState") is { } e ? graph.Move(e) : null))
            .Where(x => x.Move is { IsNeutral: false, HitDefCount: > 0 } && !string.Equals(x.Move.StateType, "A", StringComparison.OrdinalIgnoreCase))
            .Select(x => new FollowUpChoice(x.a.Id, x.Move!.Number, index.NameOf(x.a.Id), x.Move.PowerCost, Prove(index, graph, x.a.Id, engineSha, experiments, roots)))
            .OrderByDescending(c => c.Proof.Proven).ThenBy(c => c.Name, StringComparer.CurrentCulture).ToList();
    }

    // ------------------------------------------------------------------ pre-fill

    /// <summary>From a Sequence Lab experiment: opening → chase → (wait) → follow-up. The chase distance, wait and timing come from its best trial.</summary>
    public static TeachAiDraft FromExperiment(SemanticIndex index, CandidateGraph graph, ExperimentSummary experiment)
    {
        var notes = new List<string>();
        var trial = experiment.Trials.FirstOrDefault(t => t.Verdict is nameof(SequenceVerdict.ConnectedSequence) or nameof(SequenceVerdict.TrueCombo))
                    ?? experiment.Trials.FirstOrDefault() ?? throw new InvalidOperationException("The experiment has no trial.");
        var dir = Path.Combine(experiment.Directory, "trials", trial.RecordId);
        var steps = TrialSteps(dir);
        var chase = steps.FirstOrDefault(s => s.Action == StepAction.Chase) ?? throw new InvalidOperationException("This sequence has no chase step, so it is not a Knockdown Chase.");
        var follow = steps.FirstOrDefault(s => s.Index > chase.Index && s.Attack) ?? throw new InvalidOperationException("No attack follows the chase in this sequence.");
        var opening = steps.Where(s => s.Index < chase.Index && s.Attack).Select(s => AbilityOf(index, s.ToState)).OfType<string>().ToList();
        if (opening.Count == 0) throw new InvalidOperationException("The sequence's opening move could not be matched to an ability.");
        var followAbility = AbilityOf(index, follow.ToState) ?? throw new InvalidOperationException($"State {follow.ToState} is not the entry of an ability.");
        var waited = steps.Any(s => s.Index > chase.Index && s.Index < follow.Index && s.Action == StepAction.Release);
        var log = File.Exists(Path.Combine(dir, "trace.jsonl")) ? TraceReader.ReadFile(Path.Combine(dir, "trace.jsonl")) : null;
        var frames = log?.Frames.OrderBy(f => f.Frame).ToList() ?? [];
        var postureAtChase = chase.StartFrame is { } cs ? frames.LastOrDefault(f => f.Frame <= cs)?.P2 : null;
        var when = postureAtChase is { } p && BehaviorConditions.Lying(p) ? KnockdownWhen.Lying : KnockdownWhen.Both;
        var limit = Round10(Math.Max((chase.DistanceAtStart ?? DefaultChaseLimit - 20) + 20, (chase.Distance ?? DefaultAttackDistance) + 20));
        var lead = DefaultLead;
        if (waited && follow.StartFrame is { } fs && frames.FirstOrDefault(f => f.Frame >= fs && f.P2.Ctrl == true) is { } up)
            lead = (int)Math.Clamp(up.Frame - fs, 0, KnockdownChaseSpec.MaxLead);
        var chaseFrames = chase.StartFrame is { } a && chase.EndFrame is { } b ? (int)(b - a) : 60;
        var waitFrames = steps.Where(s => s.Index > chase.Index && s.Index < follow.Index && s.Action == StepAction.Release).Sum(s => s.Frames ?? 0);
        var giveUp = Math.Clamp(Round10((chaseFrames + waitFrames) * 3 / 2 + 30), KnockdownChaseSpec.MinGiveUp, KnockdownChaseSpec.MaxGiveUp);
        var move = graph.Move("state:" + follow.ToState.ToString(CultureInfo.InvariantCulture));
        notes.Add($"From experiment {experiment.Id} ({experiment.Scope.SequenceName}), trial {trial.Number}: {experiment.Steps}.");
        if (chase.DistanceAtStart is { } ds) notes.Add($"The chase started {ds:0} px away; the chase limit allows {limit} px.");
        if (waited) notes.Add($"The proven sequence waited before the follow-up: it is used as they get up ({lead} frame(s) before they could act in that trial).");
        if (postureAtChase is not null) notes.Add($"When the chase began the opponent was {Situation.Posture(postureAtChase)}.");
        var spec = new KnockdownChaseSpec(when, limit, chase.Distance ?? DefaultAttackDistance, waited ? FollowUpTiming.AsTheyGetUp : FollowUpTiming.WhileDown, lead,
            giveUp, ChaseFrequency.Always, followAbility, follow.ToState, index.NameOf(followAbility), ChaseFallback.StopAndReassess, (int)Math.Round(move?.PowerCost ?? 0),
            OwnershipPlacement.BeforeExisting, []);
        return new TeachAiDraft(spec, new TaughtSource("experiment", experiment.Id, $"Sequence Lab: {experiment.Scope.SequenceName} — {experiment.Steps}"),
            new TaughtTestPlan(opening, SpecOf(steps), null), notes);
    }

    /// <summary>From a recognised Knockdown Chase in a watched run: the approach distance and the attack that followed (and connected or not).</summary>
    public static TeachAiDraft FromEpisode(SemanticIndex index, CandidateGraph graph, BehaviorRun run, BehaviorEpisode episode, IReadOnlyList<string> opening)
    {
        if (episode.Template != BehaviorTemplates.KnockdownChase) throw new InvalidOperationException("Only a Knockdown Chase episode can be taught in v1.");
        var attack = episode.Steps.First(s => s.Code == "attack");
        var distance = episode.Steps.First(s => s.Code == "distance");
        var follow = AbilityOf(index, attack.State ?? -1) ?? throw new InvalidOperationException($"State {attack.State} is not the entry of an ability; choose the follow-up by hand.");
        var lying = episode.Steps.Any(s => s.Code == "enemy-lying") && episode.Steps.First(s => s.Code == "approach").Frame >= episode.Steps.First(s => s.Code == "enemy-lying").Frame;
        var startDistance = distance.From ?? DefaultChaseLimit;
        var attackDistance = (int)Math.Ceiling((attack.From ?? distance.To ?? DefaultAttackDistance) + 2);
        var move = graph.Move("state:" + attack.State!.Value.ToString(CultureInfo.InvariantCulture));
        var spec = new KnockdownChaseSpec(lying ? KnockdownWhen.Lying : KnockdownWhen.Both, Round10(Math.Max(startDistance + 20, attackDistance + 20)), attackDistance,
            FollowUpTiming.WhileDown, DefaultLead, DefaultGiveUpDown, ChaseFrequency.Always, follow, attack.State.Value, index.NameOf(follow), ChaseFallback.StopAndReassess,
            (int)Math.Round(move?.PowerCost ?? 0), OwnershipPlacement.BeforeExisting, []);
        var chain = BehaviorText.Chain(episode, index);
        return new TeachAiDraft(spec, new TaughtSource("episode", $"{run.Id}@{episode.StartFrame}", $"Watch & Ask: {chain} ({run.SetupText})"),
            new TaughtTestPlan(opening, null, null),
            [$"From the watched run {run.Id}, frames {episode.StartFrame}–{episode.EndFrame}: {chain}.",
             $"The fighter attacked at {attack.From:0} px, {(episode.Outcome == "connected" ? "and it connected" : "outcome " + episode.Outcome)}."]);
    }

    /// <summary>From an ability (Ability Lab): the ability is the follow-up; distances are the defaults.</summary>
    public static TeachAiDraft FromAbility(SemanticIndex index, CandidateGraph graph, string abilityId, IReadOnlyList<string> opening)
    {
        var entry = index.Get(abilityId)?.Prop("entryState") ?? throw new InvalidOperationException($"{abilityId} has no entry state.");
        var move = graph.Move(entry) ?? throw new InvalidOperationException($"{entry} is not a move.");
        var spec = new KnockdownChaseSpec(KnockdownWhen.Both, DefaultChaseLimit, DefaultAttackDistance, FollowUpTiming.WhileDown, DefaultLead, DefaultGiveUpDown,
            ChaseFrequency.Always, abilityId, move.Number, index.NameOf(abilityId), ChaseFallback.StopAndReassess, (int)Math.Round(move.PowerCost),
            OwnershipPlacement.BeforeExisting, []);
        return new TeachAiDraft(spec, new TaughtSource("ability", abilityId, $"Ability Lab: {index.NameOf(abilityId)}"), new TaughtTestPlan(opening, null, null),
            ["Distances are defaults: nothing measured them yet. Prove the sequence in the Sequence Lab to pre-fill them from a real run."]);
    }

    /// <summary>A knockdown opening for the controlled test when the source has none: a proven ability whose HitDef knocks down (fall), from neutral.</summary>
    public static IReadOnlyList<string> DefaultOpening(SemanticIndex index, CandidateGraph graph, ExperimentStore experiments, string? engineSha)
    {
        var hash = ExperimentScope.HashOf(index);
        foreach (var e in experiments.List(hash).Where(e => string.Equals(e.Scope.EngineSha256, engineSha, StringComparison.OrdinalIgnoreCase)))
            foreach (var t in e.Trials.Where(t => t.Verdict is nameof(SequenceVerdict.ConnectedSequence) or nameof(SequenceVerdict.TrueCombo)))
            {
                var steps = TrialSteps(Path.Combine(e.Directory, "trials", t.RecordId));
                var chase = steps.FirstOrDefault(s => s.Action == StepAction.Chase);
                if (chase is null) continue;
                var opening = steps.Where(s => s.Index < chase.Index && s.Attack).Select(s => AbilityOf(index, s.ToState)).OfType<string>().ToList();
                if (opening.Count > 0) return opening;
            }

        return [];
    }

    // ------------------------------------------------------------------ trial records

    internal sealed record TrialStep(int Index, int ToState, bool Attack, string? Action, int? Distance, int? Frames, bool Done, bool? Connected, long? StartFrame,
        long? EndFrame, double? DistanceAtStart);

    /// <summary>A Sequence Lab trial's steps: plan.json (what was planned) joined with sequence.json (what the verifier saw).</summary>
    internal static IReadOnlyList<TrialStep> TrialSteps(string trialDir)
    {
        var planPath = Path.Combine(trialDir, "plan.json");
        var reportPath = Path.Combine(trialDir, "sequence.json");
        if (!File.Exists(planPath) || !File.Exists(reportPath)) return [];
        try
        {
            var plan = InputPlanner.FromJson(File.ReadAllText(planPath));
            using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
            var outcomes = new Dictionary<int, JsonElement>();
            foreach (var s in report.RootElement.GetProperty("steps").EnumerateArray())
                if (s.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number) outcomes[i.GetInt32()] = s.Clone();
            return plan.Steps.Select(p =>
            {
                outcomes.TryGetValue(p.Index, out var o);
                bool Has(string n) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null;
                var done = Has("outcome") && o.GetProperty("outcome").GetString() == "Done" &&
                           (p.IsAction || (Has("runtimeRules") && o.GetProperty("runtimeRules").EnumerateArray().Any(r => r.GetString() == "runtime.transition-observed")));
                return new TrialStep(p.Index, p.ToState, p.Attack, p.Action?.Kind, p.Action?.Distance, p.Action?.Frames, done,
                    Has("connected") ? o.GetProperty("connected").GetBoolean() : null,
                    Has("startFrame") ? o.GetProperty("startFrame").GetInt64() : null, Has("endFrame") ? o.GetProperty("endFrame").GetInt64() : null,
                    Has("distanceAtStart") ? o.GetProperty("distanceAtStart").GetDouble() : null);
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException) { return []; }
    }

    /// <summary>The compact spec of a trial's sequence (for the regression re-run).</summary>
    private static string? SpecOf(IReadOnlyList<TrialStep> steps)
    {
        var parts = new List<string>();
        foreach (var s in steps)
        {
            if (s.Attack) parts.Add(s.ToState.ToString(CultureInfo.InvariantCulture));
            else if (s.Action == StepAction.Chase) parts.Add($"chase:{s.Distance}");
            else if (s.Action == StepAction.Release) parts.Add($"wait:{s.Frames}");
            else return null;   // walk / jump / dash: re-planned from the saved sequence instead
        }

        return string.Join(" > ", parts);
    }

    /// <summary>The ability whose entry state is <paramref name="state"/>.</summary>
    public static string? AbilityOf(SemanticIndex index, int state)
    {
        var id = "state:" + state.ToString(CultureInfo.InvariantCulture);
        return index.Of(ObjectKind.Ability).FirstOrDefault(a => a.Prop("entryState") == id)?.Id;
    }

    private static int Round10(double v) => (int)(Math.Ceiling(v / 10) * 10);
}
