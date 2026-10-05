using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IKEMENLab.Core.XRay.Behavior;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Sequences;
using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.Core.XRay.Director;

/// <summary>
/// One decision of the generated behavior as the intention register published it, and whether the trace shows it executed (P1 entered the published
/// next state within two samples). Published intent alone is never counted as behavior.
/// </summary>
public sealed record DirectorDecision(long Frame, long? Tick, int Why, int Next, double? DistancePx, int Down, bool Executed, int? StateAfter, string Outcome);

/// <summary>What one run shows about the taught behavior. Counts are of verified decisions only.</summary>
public sealed record DirectorRunMetrics(
    string RunId, string Setup, string Kind, bool Loaded, string? NotTestedWhy, int Knockdowns, int Activations, int FollowUps, int Connected,
    int Fallbacks, int GiveUps, int Conflicts, int Interrupted, double Damage, IReadOnlyList<DirectorDecision> Decisions, IReadOnlyList<string> Notes)
{
    public double? ConnectionRate => FollowUps == 0 ? null : (double)Connected / FollowUps;
    /// <summary>Chases still running when the recording ended: neither a success nor a failure.</summary>
    public int Unfinished { get; init; }
}

/// <summary>A setup the test suite runs: a controlled fixture (opening → AI) or a natural watched match.</summary>
public sealed record DirectorSetup(string Name, string? Dummy, string? Stage, int OpponentAi, bool Fixture, string Why);

public sealed record RegressionResult(string Name, bool Passed, string Text, string? RunId);

/// <summary>
/// A Teach AI test report, bound to the exact build it ran (build hash, generated code hash, model revision) and the engine. Passing says the behavior did
/// what it was taught in these setups — never that it is good in general.
/// </summary>
public sealed record DirectorTestReport(
    string Id, DateTime CreatedUtc, string BehaviorId, int Revision, string ModelHash, string CodeHash, string BuildHash, string? EngineSha256,
    IReadOnlyList<DirectorSetup> Setups, IReadOnlyList<DirectorRunMetrics> Runs, IReadOnlyList<RegressionResult> Regressions, bool Stopped, bool Passed,
    IReadOnlyList<string> Findings)
{
    public const string SchemaVersion = "ikemenlab.director.test/1";
    public string Directory { get; init; } = string.Empty;
    public int Activations => Runs.Sum(r => r.Activations);
    public int FollowUps => Runs.Sum(r => r.FollowUps);
    public int Connected => Runs.Sum(r => r.Connected);
    public int Fallbacks => Runs.Sum(r => r.Fallbacks);
    public int GiveUps => Runs.Sum(r => r.GiveUps);
    public int Conflicts => Runs.Sum(r => r.Conflicts);
    public int Unfinished => Runs.Sum(r => r.Unfinished);
    public int Knockdowns => Runs.Sum(r => r.Knockdowns);
    public double Damage => Runs.Sum(r => r.Damage);

    public const string ScopeText = "These counts describe these setups only (this character build, this engine, these opponents and stage). " +
                                    "A high rate here is not evidence that the behavior is good against other opponents.";
}

/// <summary>The session's published result for a Teach AI test.</summary>
public sealed record DirectorTestOutcome(DirectorTestReport Report, TaughtBehavior Behavior);

/// <summary>
/// Teach AI tests. The controlled fixture: the driver plays the opening the user proved (it knocks the opponent down), then hands P1 to the engine's AI —
/// from that moment the character's own AI, with the generated behavior in it, decides, against the setup's opponent (idle, or on the AI). A natural
/// watched match counts opportunities the AI creates itself. Regressions re-run what was proven before (the sequence, the follow-up) from the build.
/// </summary>
public static class DirectorTesting
{
    public const int NaturalWatchSeconds = 30, ActiveOpponentAi = 4, SubjectAi = 8;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The setups a behavior is tested against: the proven setup (idle opponent), an active opponent, an optional second opponent, a natural match.</summary>
    public static IReadOnlyList<DirectorSetup> Setups(TaughtBehavior b, PlaybackSetup setup) =>
    [
        new("Proven setup (idle opponent)", setup.Dummy, setup.Stage, 0, true,
            "The opening the user proved knocks the idle dummy down; then the AI (with the generated behavior) takes over. The opponent never acts."),
        new("Opponent active after the knockdown", setup.Dummy, setup.Stage, ActiveOpponentAi, true,
            $"The same opening, then BOTH sides are on the AI (opponent level {ActiveOpponentAi}): they get up and act, so recovery and fallbacks happen."),
        .. b.Tests.SecondOpponent is { } second && !string.Equals(second, setup.Dummy, StringComparison.OrdinalIgnoreCase)
            ? new[] { new DirectorSetup($"Second opponent: {second}", second, setup.Stage, 0, true, "The same opening against a different opponent (idle).") }
            : [],
        new($"Natural match ({NaturalWatchSeconds} s)", setup.Dummy, setup.Stage, 1, false,
            $"No script: the character on AI level {SubjectAi} against the dummy on AI level 1. Counts only the knockdowns the AI makes itself.")
    ];

    /// <summary>
    /// The fixture plan: the opening abilities (planned exactly like a Sequence Lab sequence), then hand P1 to the engine's AI once it can act, then watch
    /// <paramref name="watchFrames"/> frames. Refused (with the reason) when the opening cannot be played.
    /// </summary>
    public static (InputPlan? Plan, string? Refused) FixturePlan(CandidateGraph graph, TaughtBehavior b, int approachDistance, int opponentAi)
    {
        if (b.Tests.Opening.Count == 0)
            return (null, "No opening is known: prove a knockdown sequence in the Sequence Lab (an opening move, a chase and the follow-up) and teach from it.");
        var sequence = new Sequence("director-" + b.Id, "Teach AI fixture", b.Revision, b.Tests.Opening.Select(SequenceAction.Ability).ToList());
        var planned = SequencePlanner.Plan(graph, sequence, approachDistance);
        if (planned.Plan is null) return (null, "The opening cannot be played: " + planned.Refused);
        var p = planned.Plan;
        var n = p.Steps.Count;
        var handover = new PlanStep(n + 1, "action:handover", "Action", string.Empty, string.Empty, null, -1, null, [], null, null, 0, ["Hand P1 to its AI"])
        {
            Action = new StepAction(StepAction.Handover, WaitForControl: true) { AiLevel = SubjectAi, OpponentAiLevel = opponentAi > 0 ? opponentAi : null }
        };
        var watch = new PlanStep(n + 2, "action:watch", "Action", string.Empty, string.Empty, null, -1, null, [], null, null, b.Tests.WatchFrames, [$"Watch {b.Tests.WatchFrames} frames"])
        {
            Action = new StepAction(StepAction.Release, b.Tests.WatchFrames, WaitForControl: false)
        };
        var plan = p with
        {
            RouteKey = DirectorTestJob.ScopePrefix + b.Id + "@r" + b.Revision.ToString(CultureInfo.InvariantCulture),
            Steps = [.. p.Steps, handover, watch], TailFrames = 10, MaxFrames = p.MaxFrames + b.Tests.WatchFrames + 400
        };
        return (plan, null);
    }

    // ------------------------------------------------------------------ evaluation

    /// <summary>
    /// Reads one run: every decision the intention register published (a new reason or tick while it names this behavior), checked against what P1 did;
    /// the knockdowns the opponent suffered while P1 was on the AI; chases taken over by anything else. Distances are in px (the one distance scale).
    /// </summary>
    public static DirectorRunMetrics Evaluate(string runId, string setup, string kind, TraceLog log, TaughtBehavior b, CharacterFacts facts)
    {
        var frames = BehaviorCoordinates.Normalize(log, facts.LocalCoord).ToList();
        var notes = new List<string>();
        var driver = log.Events.OfType<DriverEvent>().ToList();
        string? notTested = null;
        if (frames.Count == 0) notTested = "Nothing was recorded (the engine did not start the match, or the character failed to load).";
        else if (driver.Any(d => d.Kind == "driver_load_failed")) notTested = "The input driver could not be loaded.";
        else if (kind == "fixture" && driver.FirstOrDefault(d => d.Kind is "handover_failed") is { } hf) notTested = "The hand-over to the AI failed: " + hf.Detail;
        else if (kind == "fixture" && driver.All(d => d.Kind != "handover"))
            notTested = "The opening never finished, so the AI never took over: " + (driver.LastOrDefault(d => d.Kind is "step_timeout" or "timeout" or "chase_stopped")?.Detail ?? "the run ended first.");
        if (frames.Count > 0 && frames.All(f => f.Director is null)) notes.Add("This trace has no intention register (it was not a Director run).");

        var handover = kind == "fixture" ? driver.FirstOrDefault(d => d.Kind == "handover")?.Frame ?? long.MaxValue : frames.FirstOrDefault()?.Frame ?? 0;
        var decisions = new List<DirectorDecision>();
        (double? Tick, double? Why) last = (null, null);
        for (var i = 0; i < frames.Count; i++)
        {
            var r = frames[i].Director;
            if (r is null || r.Behavior != TaughtBehavior.Slot || r.Why is not { } why || why <= 0) continue;
            if (r.Tick == last.Tick && r.Why == last.Why) continue;
            last = (r.Tick, r.Why);
            var next = (int)(r.Next ?? -1);
            var after = Enumerable.Range(i, Math.Min(3, frames.Count - i)).Select(j => frames[j].P1.State).ToList();
            var executed = after.Contains(next);
            var stateAfter = after.LastOrDefault(s => s is not null);
            var outcome = "n/a";
            if (executed && (int)why == DirectorCompiler.WhyAttack)
            {
                var start = Enumerable.Range(i, frames.Count - i).FirstOrDefault(j => frames[j].P1.State == next, i);
                var end = start;
                while (end + 1 < frames.Count && frames[end + 1].P1.State == next) end++;
                outcome = BehaviorDetector.OutcomeOf(frames, new BehaviorDetector.Attack(start, end, next), true).Outcome;
            }

            decisions.Add(new DirectorDecision(frames[i].Frame, r.Tick is { } t ? (long)t : null, (int)why, next, r.Distance is { } d ? d / facts.UnitsPerPx : null,
                (int)(r.Down ?? 0), executed, stateAfter, outcome));
        }

        // A chase that ended without a decision of its own: hit (an engine interruption) or taken over by another rule (a conflict).
        var interrupted = 0;
        var takenOver = 0;
        for (var i = 1; i < frames.Count; i++)
        {
            if (frames[i - 1].P1.State != facts.ChaseState || frames[i].P1.State == facts.ChaseState) continue;
            if (decisions.Any(d => d.Frame >= frames[i - 1].Frame - 1 && d.Frame <= frames[i].Frame && d.Why != DirectorCompiler.WhyStart)) continue;
            if (BehaviorConditions.Hit(frames[i].P1)) { interrupted++; notes.Add($"Frame {frames[i].Frame}: the chase was interrupted — the fighter was hit (State {frames[i].P1.State})."); }
            else { takenOver++; notes.Add($"Frame {frames[i].Frame}: the chase was taken over by State {frames[i].P1.State} without a decision of the generated behavior — another rule changed state."); }
        }

        var unexecuted = decisions.Where(d => !d.Executed).ToList();
        foreach (var d in unexecuted)
            notes.Add($"Frame {d.Frame}: the generated behavior published “{Reason(d.Why)} → State {d.Next}” but the fighter went to State {d.StateAfter?.ToString(CultureInfo.InvariantCulture) ?? "?"}.");

        // Knockdowns after the hand-over: each time the opponent goes down (falling with the fall flag, or lying).
        var knockdowns = 0;
        for (var i = 1; i < frames.Count; i++)
        {
            if (frames[i].Frame < handover) continue;
            var down = BehaviorConditions.Falling(frames[i].P2) || BehaviorConditions.Lying(frames[i].P2);
            var before = BehaviorConditions.Falling(frames[i - 1].P2) || BehaviorConditions.Lying(frames[i - 1].P2);
            if (down && (!before || frames[i - 1].Frame < handover)) knockdowns++;
        }

        var damage = 0.0;
        foreach (var d in decisions.Where(d => d.Executed && d.Why == DirectorCompiler.WhyAttack))
        {
            var at = frames.FindIndex(f => f.Frame >= d.Frame);
            var lifeBefore = frames[Math.Max(0, at - 1)].P2.Life;
            var lifeAfter = frames.Skip(at).Take(90).Select(f => f.P2.Life).OfType<double>().DefaultIfEmpty(lifeBefore ?? 0).Min();
            if (lifeBefore is { } lb) damage += Math.Max(0, lb - lifeAfter);
        }

        // The recording ended during a chase: it started, but nothing says how it would have ended.
        var unfinished = frames.Count > 0 && frames[^1].P1.State == facts.ChaseState ? 1 : 0;
        if (unfinished > 0)
            notes.Add($"The recording ended at frame {frames[^1].Frame} during a chase (started at frame " +
                      $"{decisions.LastOrDefault(d => d.Why == DirectorCompiler.WhyStart)?.Frame.ToString(CultureInfo.InvariantCulture) ?? "?"}): counted neither as a success nor as a failure.");
        int Count(int why) => decisions.Count(d => d.Executed && d.Why == why);
        return new DirectorRunMetrics(runId, setup, kind, notTested is null, notTested, knockdowns, Count(DirectorCompiler.WhyStart), Count(DirectorCompiler.WhyAttack),
            decisions.Count(d => d.Executed && d.Why == DirectorCompiler.WhyAttack && d.Outcome == "connected"),
            Count(DirectorCompiler.WhyRecovered) + Count(DirectorCompiler.WhyNoMeter), Count(DirectorCompiler.WhyGiveUp), unexecuted.Count + takenOver, interrupted,
            Math.Round(damage, 1), decisions, notes) { Unfinished = unfinished };
    }

    public static string Reason(int why) => why switch
    {
        DirectorCompiler.WhyStart => "start the chase",
        DirectorCompiler.WhyAttack => "attack distance reached",
        DirectorCompiler.WhyRecovered => "opponent recovered",
        DirectorCompiler.WhyGiveUp => "give up",
        DirectorCompiler.WhyNoMeter => "follow-up no longer valid",
        _ => "unknown"
    };

    // ------------------------------------------------------------------ the suite

    /// <summary>Everything a suite needs: the behavior, the build to test (its folder and hash), the generated code, the character's facts and playback.</summary>
    public sealed record SuiteRequest(
        TaughtBehavior Behavior, CandidateGraph Graph, CharacterFacts Facts, GeneratedCode Code, string BuildFolder, string BuildHash, string Root,
        string SubjectFolder, string SubjectDef, PlaybackSetup Setup, Func<string?, PlaybackSetup> SetupFor, string StoreRoot);

    /// <summary>
    /// Runs the suite: each fixture setup × the behavior's trial count, the natural match, then the regressions. A cancel stops it: finished runs stay in the
    /// report (marked stopped, never passed). Progress names each run ("trial:3/9").
    /// </summary>
    public static DirectorTestReport Run(ComboPlaybackService playback, SuiteRequest q, PlaybackCancellation job, Action<string>? progress = null)
    {
        var b = q.Behavior;
        var id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        var dir = Path.Combine(q.StoreRoot, id);
        Directory.CreateDirectory(dir);
        var service = playback.WithStore(Path.Combine(dir, "runs"), int.MaxValue);
        var setups = Setups(b, q.Setup);
        var runs = new List<DirectorRunMetrics>();
        var regressions = new List<RegressionResult>();
        var stopped = false;
        string? engine = null;
        var fixtures = setups.Where(s => s.Fixture).ToList();
        var total = fixtures.Count * b.Tests.Trials + setups.Count(s => !s.Fixture) + (b.Tests.SourceSequenceSpec is null ? 1 : 2);
        var n = 0;
        void Trial() => progress?.Invoke($"trial:{++n}/{total}");

        try
        {
            foreach (var s in setups)
            {
                var setup = q.SetupFor(s.Dummy);
                if (!setup.Ready) { runs.Add(NotRun(s, "The opponent is not installed or playback is not set up: " + string.Join(" ", setup.Issues))); continue; }
                if (s.Fixture)
                {
                    var (plan, refused) = FixturePlan(q.Graph, b, setup.ApproachDistance, s.OpponentAi);
                    if (plan is null) { runs.Add(NotRun(s, refused!)); continue; }
                    for (var t = 1; t <= b.Tests.Trials; t++)
                    {
                        job.Token.ThrowIfCancellationRequested();
                        Trial();
                        DirectorRunMetrics? metrics = null;
                        using var gate = new PlaybackCancellation(job.Token);
                        var (record, _) = service.PlayDirector(new DirectorRunRequest(q.Root, q.SubjectFolder, q.SubjectDef, plan, setup, $"Teach AI · {s.Name} · trial {t}", q.BuildFolder),
                            progress, gate, (runDir, log) =>
                            {
                                metrics = Evaluate(Path.GetFileName(runDir), s.Name, "fixture", log, b, q.Facts);
                                File.WriteAllText(Path.Combine(runDir, "director.json"), JsonSerializer.Serialize(metrics, Json));
                            });
                        engine ??= record.EngineSha256;
                        runs.Add(metrics! with { RunId = record.Id });
                    }
                }
                else
                {
                    job.Token.ThrowIfCancellationRequested();
                    Trial();
                    using var gate = new PlaybackCancellation(job.Token);
                    DirectorRunMetrics? metrics = null;
                    var watch = new WatchRequest(q.Root, q.Graph.Index.CharacterId, q.SubjectFolder, q.SubjectDef, setup, NaturalWatchSeconds, SubjectAi, s.OpponentAi,
                        $"Teach AI · {s.Name}") { SubjectSource = q.BuildFolder, Director = true };
                    var (record, _) = service.Watch(watch, progress, gate, (runDir, runId, log) =>
                    {
                        metrics = Evaluate(runId, s.Name, "watch", log, b, q.Facts);
                        File.WriteAllText(Path.Combine(runDir, "director.json"), JsonSerializer.Serialize(metrics, Json));
                    });
                    engine ??= record.EngineSha256;
                    runs.Add(metrics!);
                }
            }

            // Regressions: what was proven before must still hold on the build (the generated code is AI-only, so a driven run never enters it).
            if (b.Tests.SourceSequenceSpec is { } spec)
            {
                job.Token.ThrowIfCancellationRequested();
                Trial();
                regressions.Add(SequenceRegression(service, q, spec, job));
            }

            job.Token.ThrowIfCancellationRequested();
            Trial();
            regressions.Add(FollowUpRegression(service, q, job));
        }
        catch (OperationCanceledException)
        {
            stopped = true;
        }

        var report = Judge(id, b, q, engine, setups, runs, regressions, stopped) with { Directory = dir };
        Save(report);
        if (runs.Count == 0 && regressions.Count == 0 && stopped)
        {
            try { System.IO.Directory.Delete(dir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
            throw new OperationCanceledException(job.Token);
        }

        return report;
    }

    private static DirectorRunMetrics NotRun(DirectorSetup s, string why) =>
        new(string.Empty, s.Name, s.Fixture ? "fixture" : "watch", false, why, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], []);

    private static RegressionResult SequenceRegression(ComboPlaybackService service, SuiteRequest q, string spec, PlaybackCancellation job)
    {
        var sequence = new Sequence("director-regression-" + q.Behavior.Id, "Teach AI regression", 1, SequenceSpec.Parse(spec));
        var (run, planned) = SequenceExperimentRunner.Prepare(q.Graph, sequence, q.Root, q.SubjectFolder, q.SubjectDef, q.Setup, 1);
        if (run is null) return new RegressionResult("The proven sequence still plays", false, "It can no longer be planned: " + planned.Refused, null);
        using var gate = new PlaybackCancellation(job.Token);
        var outcome = service.PlaySequence(run.Request with { SubjectSource = q.BuildFolder }, null, gate);
        var ok = outcome.Report.Verdict is SequenceVerdict.ConnectedSequence or SequenceVerdict.TrueCombo;
        return new RegressionResult("The proven sequence still plays", ok,
            $"{SequenceLabels.Chain(sequence.Actions, q.Graph.Index)} on the build: {outcome.Report.Verdict}" + (ok ? string.Empty : $" ({outcome.Report.Reason})"), outcome.Record.Id);
    }

    private static RegressionResult FollowUpRegression(ComboPlaybackService service, SuiteRequest q, PlaybackCancellation job)
    {
        var s = q.Behavior.Spec;
        var path = AbilityPlayback.Resolve(q.Graph, s.FollowUpAbilityId, new PlanOptions { ApproachDistance = q.Setup.ApproachDistance });
        if (path.Path is null) return new RegressionResult($"{s.FollowUpName} still performs", false, "Play Ability cannot press it: " + path.Refused, null);
        using var gate = new PlaybackCancellation(job.Token);
        var request = new PlaybackRequest(q.Root, q.SubjectFolder, q.SubjectDef, q.Graph, path.Path.Route, q.Setup, s.FollowUpName)
        {
            Mode = PlaybackMode.Ability, AbilityId = s.FollowUpAbilityId, SubjectSource = q.BuildFolder
        };
        var outcome = service.Play(request, null, gate);
        var status = outcome.Ability?.Status.ToString() ?? outcome.Report.Status.ToString();
        return new RegressionResult($"{s.FollowUpName} still performs", outcome.Ability?.Status == AbilityStatus.Performed,
            $"Play Ability on the build: {status}", outcome.Record.Id);
    }

    /// <summary>
    /// Passing means: every setup loaded and ran; in the proven setup the behavior took a knockdown and started its follow-up; nothing it published went
    /// unexecuted and nothing took over a chase; every regression held. Each failure becomes a plain finding.
    /// </summary>
    private static DirectorTestReport Judge(string id, TaughtBehavior b, SuiteRequest q, string? engine, IReadOnlyList<DirectorSetup> setups,
        IReadOnlyList<DirectorRunMetrics> runs, IReadOnlyList<RegressionResult> regressions, bool stopped)
    {
        var findings = new List<string>();
        if (stopped) findings.Add("The test was stopped before it finished; it cannot pass.");
        foreach (var r in runs.Where(r => !r.Loaded)) findings.Add($"{r.Setup}: not tested — {r.NotTestedWhy}");
        var proven = runs.Where(r => r.Setup == setups[0].Name && r.Loaded).ToList();
        if (proven.Count > 0 && proven.Sum(r => r.Activations) == 0) findings.Add($"{setups[0].Name}: the behavior never started a chase after the proven knockdown.");
        if (proven.Count > 0 && proven.Sum(r => r.Activations) > 0 && proven.Sum(r => r.FollowUps) == 0)
            findings.Add($"{setups[0].Name}: the chase started but never reached the follow-up ({b.Spec.FollowUpName}).");
        var conflicts = runs.Sum(r => r.Conflicts);
        if (conflicts > 0) findings.Add($"{conflicts} conflict(s): something else changed state while the behavior owned the decision (see each run's notes).");
        foreach (var g in regressions.Where(g => !g.Passed)) findings.Add($"Regression failed — {g.Name}: {g.Text}");
        var natural = runs.Where(r => r.Kind == "watch" && r.Loaded).ToList();
        if (natural.Count > 0 && natural.Sum(r => r.Knockdowns) == 0)
            findings.Add("Note: the natural match produced no knockdown, so it says nothing about the behavior (not a failure).");
        var passed = !stopped && runs.Count > 0 && runs.All(r => r.Loaded || r.Kind == "watch") && proven.Sum(r => r.FollowUps) > 0 && conflicts == 0 &&
                     regressions.All(g => g.Passed);
        return new DirectorTestReport(id, DateTime.UtcNow, b.Id, b.Revision, b.ModelHash, q.Code.Hash, q.BuildHash, engine, setups, runs, regressions, stopped, passed,
            findings);
    }

    public static string Headline(DirectorTestReport r) =>
        (r.Passed ? "Passed" : r.Stopped ? "Stopped" : "Not passed") +
        $" · {r.Activations} chase(s) started, {r.FollowUps} follow-up(s), {r.Connected} connected, {r.Fallbacks} fallback(s), {r.GiveUps} give-up(s), " +
        $"{r.Conflicts} conflict(s)" + (r.Unfinished > 0 ? $", {r.Unfinished} cut off by the end of a run" : string.Empty) +
        $" · {r.Runs.Count} run(s), {r.Regressions.Count(g => g.Passed)}/{r.Regressions.Count} regression(s) held";

    /// <summary>Writes <paramref name="report"/> as report.json in its own directory (where <see cref="Load"/> and the behavior's history read it).</summary>
    public static void Save(DirectorTestReport report)
    {
        System.IO.Directory.CreateDirectory(report.Directory);
        File.WriteAllText(Path.Combine(report.Directory, "report.json"), JsonSerializer.Serialize(report, Json));
    }

    public static DirectorTestReport? Load(string reportDir)
    {
        var path = Path.Combine(reportDir, "report.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<DirectorTestReport>(File.ReadAllText(path), Json)! with { Directory = reportDir }; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }
}
