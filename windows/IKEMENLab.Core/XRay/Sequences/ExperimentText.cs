using System.Globalization;

namespace IKEMENLab.Core.XRay.Sequences;

/// <summary>Plain-language summaries of sequence runs and experiments, and the side-by-side comparison of two variants.</summary>
public static class ExperimentText
{
    private static string Pct(double? r) => r is { } v ? (v * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "n/a";
    private static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>One run: its own verdict sentence. Several: counts and rates.</summary>
    public static string Headline(ExperimentSummary s, SequenceReport? last)
    {
        if (s.Requested == 1 && s.Completed == 1 && last is not null) return last.Summary;
        var head = s.Stopped ? $"Stopped after {s.Completed} of {s.Requested} trials" : $"{s.Completed} trials";
        return $"{head}: {s.Successes} connected ({Pct(s.SuccessRate)})" + (s.TrueCombos > 0 ? $", {s.TrueCombos} true combo(s)" : ", no true combo") +
               (s.Untestable > 0 ? $", {s.Untestable} could not be tested" : string.Empty);
    }

    public static IReadOnlyList<string> Lines(ExperimentSummary s)
    {
        var lines = new List<string>
        {
            $"Succeeded (every step happened and every attack connected): {s.Successes} of {s.Completed} ({Pct(s.SuccessRate)})",
            $"True combos: {s.TrueCombos} of {s.Completed} ({Pct(s.TrueComboRate)})",
            $"Attacks that connected: {s.AttacksConnected} of {s.AttacksTried} ({Pct(s.ConnectionRate)})"
        };
        foreach (var (reason, count) in s.FailureReasons) lines.Add($"Did not work — {Plain(reason)}: {count}×");
        if (s.DamageRange is { } d) lines.Add($"Damage: {N(d.Min)}–{N(d.Max)} (average {N(d.Average)})");
        if (s.EndDistanceRange is { } e) lines.Add($"Distance at the end: {N(e.Min)}–{N(e.Max)} (average {N(e.Average)})");
        if (s.EndOpponent.Count > 0) lines.Add("Opponent at the end: " + string.Join(", ", s.EndOpponent.Select(o => $"{o.Posture} ({o.Count}×)")));
        var gaps = s.Trials.Where(t => t.OutOfHitstunFrames is > 0).Select(t => t.OutOfHitstunFrames!.Value).ToList();
        if (gaps.Count > 0) lines.Add($"When connected but not a combo, the opponent was out of hitstun for {gaps.Min()}–{gaps.Max()} frames");
        if (s.AverageSeconds is { } sec) lines.Add($"Each trial took about {sec:0} s");
        if (s.Stopped) lines.Add($"Stopped by you after {s.Completed} of {s.Requested} trials; the finished trials are kept.");
        lines.AddRange(s.Notes);
        return lines;
    }

    /// <summary>"Step 3 (Normal A): OutOfReach" → "Step 3 (Normal A): never got close enough".</summary>
    public static string Plain(string reason)
    {
        var colon = reason.LastIndexOf(": ", StringComparison.Ordinal);
        var head = colon >= 0 ? reason[..(colon + 2)] : string.Empty;
        var code = colon >= 0 ? reason[(colon + 2)..] : reason;
        return head + code switch
        {
            SequenceReason.OpponentRecovered => "the opponent recovered first",
            SequenceReason.OutOfReach => "never got close enough",
            SequenceReason.NeverAirborne => "the jump did not leave the ground",
            SequenceReason.NeverActionable => "never got control back",
            SequenceReason.DriverDisagrees => "the recording could not be trusted",
            SequenceReason.NoContact => "the attack did not touch the opponent",
            _ => Playback.PlaybackInspector.Describe(code)
        };
    }

    public static string ScopeText(ExperimentScope s) =>
        $"{s.SequenceName} v{s.SequenceVersion} · character files {s.CharacterHash} · engine {(s.EngineSha256 is { Length: >= 8 } e ? e[..8] : "not recorded")} · " +
        $"dummy {s.Dummy ?? "?"} · stage {s.Stage ?? "?"} · approach {s.ApproachDistance}";

    /// <summary>Rows for comparing two variants' experiments, plus whether they are comparable (same character files, engine, opponent, stage, spacing).</summary>
    public static (IReadOnlyList<(string Metric, string A, string B)> Rows, string? Warning) Compare(ExperimentSummary a, ExperimentSummary b)
    {
        string Range((double Min, double Average, double Max)? r) => r is { } v ? $"{N(v.Average)} ({N(v.Min)}–{N(v.Max)})" : "n/a";
        var rows = new List<(string, string, string)>
        {
            ("Sequence", $"{a.Scope.SequenceName} v{a.Scope.SequenceVersion}", $"{b.Scope.SequenceName} v{b.Scope.SequenceVersion}"),
            ("Steps", a.Steps, b.Steps),
            ("Trials", a.Completed.ToString(CultureInfo.InvariantCulture), b.Completed.ToString(CultureInfo.InvariantCulture)),
            ("Succeeded", $"{a.Successes} ({Pct(a.SuccessRate)})", $"{b.Successes} ({Pct(b.SuccessRate)})"),
            ("True combos", $"{a.TrueCombos} ({Pct(a.TrueComboRate)})", $"{b.TrueCombos} ({Pct(b.TrueComboRate)})"),
            ("Attacks connected", $"{a.AttacksConnected}/{a.AttacksTried} ({Pct(a.ConnectionRate)})", $"{b.AttacksConnected}/{b.AttacksTried} ({Pct(b.ConnectionRate)})"),
            ("Most common failure", a.FailureReasons.FirstOrDefault() is { Reason: { } ra } ? Plain(ra) : "none", b.FailureReasons.FirstOrDefault() is { Reason: { } rb } ? Plain(rb) : "none"),
            ("Damage", Range(a.DamageRange), Range(b.DamageRange)),
            ("End distance", Range(a.EndDistanceRange), Range(b.EndDistanceRange)),
            ("Opponent at the end", a.EndOpponent.FirstOrDefault().Posture ?? "n/a", b.EndOpponent.FirstOrDefault().Posture ?? "n/a")
        };
        var warning = a.Scope.SameSetup(b.Scope) ? null
            : "These two experiments did not run under the same setup (character files, engine, opponent, stage or approach distance differ), so the numbers are not directly comparable.";
        return (rows, warning);
    }
}
