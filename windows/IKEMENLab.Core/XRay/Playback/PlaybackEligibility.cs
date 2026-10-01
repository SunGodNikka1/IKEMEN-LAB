using System.Globalization;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Expressions;
using IKEMENLab.Core.XRay.Model;

namespace IKEMENLab.Core.XRay.Playback;

/// <summary>
/// Whether the player-playback driver (key injection, AILevel 0) can enter a route's selected branches. A third axis, kept
/// apart from the static <see cref="Confidence"/> of the route and from the runtime verdict.
/// </summary>
public enum PlayerEligibility
{
    /// <summary>Every step has a supported player entry and no fixed-context condition rules it out. Not a promise it will fire.</summary>
    PlayerScriptable,
    /// <summary>Incompatibility is proven: a fixed-context requirement of the selected branch contradicts the playback context, or no player entry exists.</summary>
    NotPlayerScriptable,
    /// <summary>Something in the selected branch is not modelled or not readable, so nothing is proven either way. Never a rejection.</summary>
    EligibilityUnknown
}

/// <summary>The fixed context player playback runs in. Only facts constant for the whole run belong here; StateType, Ctrl, power and variables change between steps and are not.</summary>
public sealed record PlaybackContext(int AiLevel = 0)
{
    public static PlaybackContext Player { get; } = new();
}

public static class EligibilityCodes
{
    public const string AiLevelIncompatible = "ai-level-incompatible";
    public const string NoPlayerEntry = "no-player-entry";
    public const string UnmodelledCondition = "unmodelled-condition";
    public const string AiLevelUnmodelled = "ai-level-unmodelled";
    public const string CommandUnreadable = "command-unreadable";
    public const string BranchNotReadable = "branch-not-readable";
    public const string RouteShape = "route-shape";
}

/// <summary>One thing found in a selected branch. <see cref="Proven"/> findings make the step NotPlayerScriptable; the rest only make it unknown.</summary>
public sealed record EligibilityFinding(string Code, bool Proven, string Detail, string Short);

public sealed record StepEligibility(int Step, string EdgeId, PlayerEligibility Eligibility, IReadOnlyList<EligibilityFinding> Findings)
{
    public EligibilityFinding? FirstProven => Findings.FirstOrDefault(f => f.Proven);
}

public sealed record RouteEligibility(PlayerEligibility Eligibility, IReadOnlyList<StepEligibility> Steps, PlaybackContext Context)
{
    public bool IsPlayerScriptable => Eligibility == PlayerEligibility.PlayerScriptable;
    public bool IsNotPlayerScriptable => Eligibility == PlayerEligibility.NotPlayerScriptable;

    /// <summary>
    /// The pre-Play reason for a NotPlayerScriptable route, e.g. "Cannot script this candidate: Step 1 requires AILevel &gt; 0 in the selected
    /// branch; player playback uses AILevel = 0. Step 3 also selects an AI-only branch." Null for any other class.
    /// </summary>
    public string? Reason
    {
        get
        {
            if (Eligibility != PlayerEligibility.NotPlayerScriptable) return null;
            var parts = new List<string>();
            foreach (var s in Steps)
            {
                if (s.FirstProven is not { } p) continue;
                parts.Add(parts.Count == 0 ? $"Step {s.Step} {p.Detail}." : $"Step {s.Step} also {p.Short}.");
            }

            return "Cannot script this candidate: " + string.Join(" ", parts);
        }
    }

    /// <summary>Why a route is unknown (first few reasons), or null.</summary>
    public string? UnknownNote
    {
        get
        {
            if (Eligibility != PlayerEligibility.EligibilityUnknown) return null;
            var parts = Steps.Where(s => s.Eligibility == PlayerEligibility.EligibilityUnknown)
                .Select(s => $"Step {s.Step} {s.Findings.First(f => !f.Proven).Detail}").Take(3).ToList();
            return parts.Count == 0 ? "Eligibility unknown." : "Eligibility unknown — " + string.Join("; ", parts) + ".";
        }
    }
}

/// <summary>
/// Classifies a candidate route against the player playback context. This is not a solver: it reads the selected branch of each step
/// (its <c>triggerall</c> plus the branch the edge came from) and checks fixed-context facts only — AILevel comparisons and whether the
/// branch names an injectable command. NotPlayerScriptable needs a proven finding; any condition the index does not model, or any
/// shape it cannot read, yields EligibilityUnknown.
/// </summary>
public static class PlaybackEligibility
{
    public static RouteEligibility Classify(CandidateGraph graph, ComboRoute route, PlaybackContext? context = null)
    {
        context ??= PlaybackContext.Player;
        var steps = new List<StepEligibility>();
        foreach (var (step, i) in route.Steps.Select((s, i) => (s, i + 1)))
        {
            var findings = ClassifyEdge(graph, step.Edge, context, i == 1 ? route.StartState : null);
            var eligibility = findings.Any(f => f.Proven) ? PlayerEligibility.NotPlayerScriptable
                : findings.Count > 0 ? PlayerEligibility.EligibilityUnknown
                : PlayerEligibility.PlayerScriptable;
            steps.Add(new StepEligibility(i, step.Edge.Id, eligibility, findings));
        }

        var overall = steps.Count == 0 ? PlayerEligibility.EligibilityUnknown
            : steps.Any(s => s.Eligibility == PlayerEligibility.NotPlayerScriptable) ? PlayerEligibility.NotPlayerScriptable
            : steps.Any(s => s.Eligibility == PlayerEligibility.EligibilityUnknown) ? PlayerEligibility.EligibilityUnknown
            : PlayerEligibility.PlayerScriptable;
        return new RouteEligibility(overall, steps, context);
    }

    /// <param name="routeStart">The route's start state when this is its first step; otherwise null.</param>
    public static IReadOnlyList<EligibilityFinding> ClassifyEdge(CandidateGraph graph, CandidateEdge edge, PlaybackContext context, string? routeStart = null)
    {
        var findings = new List<EligibilityFinding>();
        var branch = SelectedBranch(graph.Index, edge, out var triggerAll);
        if (branch is null)
        {
            findings.Add(new EligibilityFinding(EligibilityCodes.BranchNotReadable, false,
                "has a selected branch that could not be read back from the index", "has a branch that could not be read"));
            return findings;
        }

        var aiOpaque = false;
        var commandOpaque = false;
        foreach (var c in triggerAll.Concat(branch.Lines).SelectMany(l => ExprAnalyzer.Conjuncts(l.Expression)))
        {
            if (AiLevelTest(c) is { } test)
            {
                if (!test.Holds(context.AiLevel))
                    findings.Add(new EligibilityFinding(EligibilityCodes.AiLevelIncompatible, true,
                        $"requires {test.Text} in the selected branch; player playback uses AILevel = {context.AiLevel.ToString(CultureInfo.InvariantCulture)}",
                        "selects an AI-only branch"));
                continue;
            }

            var f = ExprAnalyzer.Analyze(c);
            if (f.ReadsAiLevel) aiOpaque = true;
            if (f.Commands.Count > 0 && c is not Binary { Op: "=" or "!=", Left: Ident { Name: "command" }, Right: StringLit }) commandOpaque = true;
        }

        if (findings.Count > 0) return findings;   // proven: further caveats would only add noise

        // A step that needs a player input must name an injectable command.
        if (edge.Kind is EdgeKind.Start or EdgeKind.Cancel && edge.Commands.Count == 0)
        {
            findings.Add(commandOpaque
                ? new EligibilityFinding(EligibilityCodes.CommandUnreadable, false, "has a command test that is not a literal command name", "has a command test that is not modelled")
                : new EligibilityFinding(EligibilityCodes.NoPlayerEntry, true,
                    "selects a branch with no injectable command, so there is no supported player entry", "selects a branch with no injectable command"));
            return findings;
        }

        foreach (var name in edge.Commands)
        {
            var obj = graph.Index.Get("cmd:" + name);
            if (obj is null || !obj.Props.TryGetValue("raw", out var raw) || string.IsNullOrWhiteSpace(raw))
                findings.Add(new EligibilityFinding(EligibilityCodes.CommandUnreadable, false, $"uses command \"{name}\", which is not readable from the CMD", $"uses command \"{name}\" that is not readable"));
        }

        if (aiOpaque)
            findings.Add(new EligibilityFinding(EligibilityCodes.AiLevelUnmodelled, false, "uses AILevel in a condition shape that is not modelled", "uses AILevel in a shape that is not modelled"));
        else if (edge.Unmodelled.Count > 0)
            findings.Add(new EligibilityFinding(EligibilityCodes.UnmodelledCondition, false,
                $"has {edge.Unmodelled.Count} condition(s) that are not modelled (first: {edge.Unmodelled[0]})", "has conditions that are not modelled"));

        if (edge.Kind is EdgeKind.Link or EdgeKind.Recovery)
            findings.Add(new EligibilityFinding(EligibilityCodes.RouteShape, false, $"goes through neutral ({edge.Kind}); link timing is not modelled", "goes through neutral"));
        if (routeStart is not null && routeStart != CandidateGraph.NeutralId)
            findings.Add(new EligibilityFinding(EligibilityCodes.RouteShape, false, $"belongs to a route that starts in {routeStart}, not neutral", "starts outside neutral"));
        return findings;
    }

    private static GateBranch? SelectedBranch(SemanticIndex index, CandidateEdge edge, out IReadOnlyList<TriggerLine> triggerAll)
    {
        triggerAll = [];
        var rel = index.Relationships.FirstOrDefault(r => r.Id == edge.RelationshipId);
        var gate = rel?.Gate ?? index.Get(edge.ControllerId)?.Gate;
        var branch = gate?.Branches.FirstOrDefault(b => b.Number == edge.BranchNumber);
        if (gate is null || branch is null) return null;
        triggerAll = gate.TriggerAll;
        return branch;
    }

    private sealed record AiTest(string Text, Func<int, bool> Holds);

    /// <summary>Recognises exactly <c>AILevel</c>, <c>!AILevel</c> and <c>AILevel op N</c> / <c>N op AILevel</c>; anything else is not an AILevel test.</summary>
    private static AiTest? AiLevelTest(Expr c)
    {
        switch (c)
        {
            case Ident { Name: "ailevel" }:
                return new AiTest("AILevel != 0", v => v != 0);
            case Unary { Op: "!", Operand: Ident { Name: "ailevel" } }:
                return new AiTest("AILevel = 0", v => v == 0);
            case Binary { Left: Ident { Name: "ailevel" }, Right: NumberLit n } b when Compare(b.Op) is { } cmp:
                return new AiTest($"AILevel {b.Op} {n.Text}", v => cmp(v, n.Value));
            case Binary { Left: NumberLit n, Right: Ident { Name: "ailevel" } } b when Compare(Flip(b.Op)) is { } cmp:
                return new AiTest($"AILevel {Flip(b.Op)} {n.Text}", v => cmp(v, n.Value));
            default:
                return null;
        }
    }

    private static string Flip(string op) => op switch { "<" => ">", ">" => "<", "<=" => ">=", ">=" => "<=", _ => op };

    private static Func<int, double, bool>? Compare(string op) => op switch
    {
        "=" => (a, b) => a == b,
        "!=" => (a, b) => a != b,
        ">" => (a, b) => a > b,
        "<" => (a, b) => a < b,
        ">=" => (a, b) => a >= b,
        "<=" => (a, b) => a <= b,
        _ => null
    };
}
