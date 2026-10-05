using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Sequences;
using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.Tests;

/// <summary>Phase 6 fixtures shared by the Core and MCP tests: walk / stand animations, a Sequence Lab experiment that proves a knockdown chase, and Director fixture traces.</summary>
public static class DirectorFixtures
{
    /// <summary>Stand (0), walk forward (20) and walk back (21) animations for a fixture AIR: the chase needs them.</summary>
    public const string Anims = """

        [Begin Action 0]
        0,0, 0,0, -1
        [Begin Action 20]
        0,0, 0,0, 5
        [Begin Action 21]
        0,0, 0,0, 5
        """;

    /// <summary>A Sequence Lab experiment on <paramref name="index"/>: 1000 (knockdown) → chase:35 → wait:20 → 200, every step performed, both attacks connected.</summary>
    public static ExperimentSummary Experiment(SemanticIndex index, ExperimentStore store, bool wait = true, string engine = "ENGINEHASH")
    {
        var dir = store.NewExperimentDirectory(out var id);
        var trial = Path.Combine(dir, "trials", "rec-1");
        Directory.CreateDirectory(trial);
        PlanStep Move(int i, int state) => new(i, $"edge:{i}", "Start", "neutral", "state:" + state, null, state, "x", [], null, null, 45, []) { Attack = true };
        PlanStep Act(int i, StepAction a) => new(i, $"action:{i}", "Action", string.Empty, string.Empty, null, -1, null, [], null, null, a.Frames ?? 0, []) { Action = a };
        var steps = new List<PlanStep> { Move(1, 1000), Act(2, new StepAction(StepAction.Chase, 300, 35)) };
        if (wait) steps.Add(Act(3, new StepAction(StepAction.Release, 20, WaitForControl: false)));
        steps.Add(Move(steps.Count + 1, 200));
        var plan = new InputPlan(index.CharacterId, "sequence:seq-1@v1", steps, 40, 20, 20, 3000, []);
        File.WriteAllText(Path.Combine(trial, "plan.json"), InputPlanner.ToJson(plan));
        string Step(int i, string kind, bool? connected, long start, long? end, double dist) =>
            $"{{\"index\":{i},\"kind\":\"{kind}\",\"outcome\":\"Done\",\"connected\":{(connected is { } c ? c ? "true" : "false" : "null")},\"startFrame\":{start},\"endFrame\":{(end is { } e ? e.ToString(CultureInfo.InvariantCulture) : "null")}," +
            $"\"distanceAtStart\":{dist.ToString(CultureInfo.InvariantCulture)},\"runtimeRules\":[{(kind == "Start" ? "\"runtime.transition-observed\"" : string.Empty)}]}}";
        var json = new List<string> { Step(1, "Start", true, 5, null, 30), Step(2, "Action", null, 32, 50, 120) };
        if (wait) json.Add(Step(3, "Action", null, 50, 70, 35));
        json.Add(Step(steps.Count, "Start", true, 72, null, 34));
        File.WriteAllText(Path.Combine(trial, "sequence.json"), "{\"steps\":[" + string.Join(",", json) + "]}");
        File.WriteAllText(Path.Combine(trial, "trace.jsonl"), BehaviorTraces.KnockdownChase().Jsonl(engine));
        var scope = new ExperimentScope(index.CharacterId, ExperimentScope.HashOf(index), engine, "kfm", "stages/ring.def", 40, "seq-1", 1, "Palm, chase, wait, jab", InputPlanner.Fingerprint(plan));
        var summary = new ExperimentSummary(id, DateTime.UtcNow, scope, "QCF x → Chase until within 35 → Wait 20f → x (LP)", 1, 1, false,
            [new ExperimentTrial(1, "rec-1", nameof(SequenceVerdict.ConnectedSequence), null, null, null, 2, 2, 113, 32, "standing", 18, 9.5, engine)]) { Directory = dir };
        store.Save(summary);
        return summary;
    }

    /// <summary>
    /// A fixture run as the probe records it: the opening knocks P2 down, the hand-over at frame 30, then the generated behavior — chase (9790) from 31,
    /// follow-up 200 at 60 (it connects at 63) — with the register published on the decision ticks. Variants: the opponent recovers first (fallback), the
    /// follow-up is published but another state executes (a conflict), or the chase is taken over without a decision.
    /// </summary>
    public static string Trace(string variant = "connect", string fingerprint = "FP")
    {
        var sb = new StringBuilder();
        sb.Append("{\"type\":\"meta\",\"frame\":0,\"schema\":\"ikemenlab.xray.trace/0\",\"probeVersion\":\"0.5-phase6-director\",\"capabilities\":{},\"hooks\":[\"hook:loop\"],\"planFingerprint\":\"" + fingerprint + "\",\"engineSha256\":\"ENGINEHASH\"}\n");
        sb.Append("{\"type\":\"driver\",\"frame\":1,\"event\":\"plan_start\"}\n");
        double why = 0, next = 0, dist = 0, tick = 0, down = 0, beh = 0;
        for (var f = 1; f <= 110; f++)
        {
            var p1State = f < 20 ? 1000 : f < 31 ? 0 : f < 60 ? 9790 : f < 75 ? 200 : 0;
            if (variant == "recover" && f >= 50) p1State = f < 52 ? 9790 : 0;
            if (variant == "steal" && f >= 45) p1State = f < 60 ? 210 : 0;
            if (variant == "unexecuted" && f >= 60 && f < 75) p1State = 5000;
            var x1 = f < 31 ? 0 : Math.Min(85, (f - 31) * 3);
            var distance = 120 - x1;
            var p2State = f < 15 ? 0 : f < 30 ? 5050 : f < 70 ? 5110 : f < 85 ? 5120 : 0;
            if (variant == "recover" && f >= 48) p2State = 0;
            var p2Type = p2State is 5050 ? "A" : p2State is 5110 or 5120 ? "L" : "S";
            var p2Move = p2State is 5050 or 5110 ? "H" : "I";
            var life = f >= 63 && variant == "connect" ? 950 : 1000;
            if (f == 31) (beh, why, next, dist, tick, down) = (1, 1, 9790, 89, 31, 1);
            if (f == 60 && variant is "connect" or "unexecuted") (why, next, dist, tick, down) = (2, 200, 34, 60, 2);
            if (f == 50 && variant == "recover") (why, next, dist, tick, down) = (3, 0, 60, 50, 0);
            var p1Move = p1State is 1000 or 200 or 210 ? "A" : p1State == 5000 ? "H" : "I";
            sb.Append(CultureInfo.InvariantCulture,
                $"{{\"type\":\"frame\",\"frame\":{f},\"round\":1,\"p1\":{{\"state\":{p1State},\"ctrl\":{(p1State == 0 ? "true" : "false")},\"stateType\":\"S\",\"moveType\":\"{p1Move}\",\"life\":1000,\"power\":0,\"x\":{x1},\"velX\":{(p1State == 9790 ? 3 : 0)},\"facing\":1,\"moveHit\":{(f >= 63 && p1State == 200 && variant == "connect" ? 1 : 0)},\"moveContact\":0,\"aiLevel\":{(f >= 30 ? 8 : 0)},\"localCoord\":320}}," +
                $"\"p2\":{{\"state\":{p2State},\"ctrl\":{(p2State == 0 ? "true" : "false")},\"stateType\":\"{p2Type}\",\"moveType\":\"{p2Move}\",\"life\":{life},\"x\":120,\"facing\":-1,\"hitFall\":{(p2State == 5050 ? "true" : "false")},\"localCoord\":320}}," +
                $"\"distance\":{distance},\"distanceSource\":\"engine:p2DistX\",\"dir\":{{\"beh\":{beh},\"why\":{why},\"next\":{next},\"dist\":{dist},\"tick\":{tick},\"down\":{down},\"roll\":512}}}}\n");
            if (f == 30) sb.Append("{\"type\":\"driver\",\"frame\":30,\"event\":\"handover\",\"detail\":\"P1 on AI level 8; P2 idle\"}\n");
        }

        sb.Append("{\"type\":\"driver\",\"frame\":110,\"event\":\"plan_complete\"}\n{\"type\":\"end\",\"frame\":110,\"reason\":\"planComplete\"}\n");
        return sb.ToString();
    }
}
