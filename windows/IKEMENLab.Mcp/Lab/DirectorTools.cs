using System.Globalization;
using System.Text.Json.Nodes;
using IKEMENLab.Core.XRay.Behavior;
using IKEMENLab.Core.XRay.Director;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Sequences;
using IKEMENLab.Mcp.Protocol;

namespace IKEMENLab.Mcp.Lab;

/// <summary>
/// Teach AI v1 (Knockdown Chase) through MCP — the minimum: propose a draft, inspect it, test it. An assistant never edits the working copy or the installed
/// character: <c>propose_behavior</c> writes only a Draft into IKEMEN Lab's data, <c>test_behavior</c> builds a disposable staging copy for the sandbox. There
/// is no approve or deploy tool; approval and deployment are the user's, in the app.
/// </summary>
internal sealed class DirectorTools(LabContext ctx, JobQueue jobs)
{
    private DirectorService Service(LoadedCharacter c) =>
        new(ctx.DirectorRoot, c.Root, c.Index, c.Graph, c.SubjectFolder, Path.GetFileName(c.SubjectDef));

    private string? Engine(LoadedCharacter c) => EngineIdentity.Sha256(ctx.Setup(c).EnginePath);

    private IReadOnlyList<string> PlaybackRoots => [ctx.AppPlaybackRoot, ctx.Runs.StoreRoot];

    // ------------------------------------------------------------------ propose_behavior

    public JsonObject Propose(ToolArgs a)
    {
        var c = ctx.Load(a.Text("character"));
        var engine = Engine(c);
        TeachAiDraft draft;
        if (a.OptText("from_experiment") is { } expId)
        {
            var e = ctx.Experiments.List(c.ContentHash).FirstOrDefault(x => x.Id == expId)
                    ?? throw new ToolError($"No experiment '{expId}' on these character files.", "list_experiments shows them.");
            try { draft = TeachAi.FromExperiment(c.Index, c.Graph, e); }
            catch (InvalidOperationException ex) { throw new ToolError(ex.Message); }
        }
        else if (a.OptText("from_watch") is { } spec)
        {
            var parts = spec.Split('@');
            if (parts.Length != 2 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var frame))
                throw new ToolError("from_watch is \"<run id>@<episode start frame>\" (inspect_behavior lists them).");
            var run = ctx.Behavior.Get(parts[0], StaticBehavior.SuperStates(c.Index)) ?? throw new ToolError($"No watched run '{parts[0]}'.");
            var episode = run.Detection.Episodes.FirstOrDefault(x => x.Template == BehaviorTemplates.KnockdownChase && x.StartFrame == frame)
                          ?? throw new ToolError($"Run {parts[0]} has no Knockdown Chase starting at frame {frame}.");
            try { draft = TeachAi.FromEpisode(c.Index, c.Graph, run, episode, TeachAi.DefaultOpening(c.Index, c.Graph, ctx.Experiments, engine)); }
            catch (InvalidOperationException ex) { throw new ToolError(ex.Message); }
        }
        else
        {
            var follow = a.OptText("follow_up") ?? throw new ToolError("Give from_experiment, from_watch or follow_up.");
            var id = Resolve.Ability(c, follow);
            draft = TeachAi.FromAbility(c.Index, c.Graph, id, TeachAi.DefaultOpening(c.Index, c.Graph, ctx.Experiments, engine));
        }

        var spec2 = Overrides(c, draft.Spec, a);
        var tests = draft.Tests with { SecondOpponent = a.OptText("second_opponent") ?? draft.Tests.SecondOpponent, Trials = a.Int("trials", draft.Tests.Trials, 1, 5) };
        var svc = Service(c);
        var b = svc.Propose(draft with { Spec = spec2, Tests = tests });
        var analysis = svc.Analyze(b, engine, ctx.Experiments, PlaybackRoots);
        var o = Card(c, b, analysis, svc);
        o["draftNotes"] = Semantic.Strings(draft.Notes);
        o["next"] = analysis.CanGenerate
            ? "test_behavior runs it in a sandbox (a disposable copy). Approval and deployment happen only in the app (X-Ray → AI Director)."
            : "Fix the problems listed (or teach from a Sequence Lab experiment that proves the follow-up).";
        return o;
    }

    private static KnockdownChaseSpec Overrides(LoadedCharacter c, KnockdownChaseSpec s, ToolArgs a)
    {
        var follow = a.OptText("follow_up") is { } f ? Resolve.Ability(c, f) : s.FollowUpAbilityId;
        var followState = follow == s.FollowUpAbilityId ? s.FollowUpState
            : c.Graph.Move(c.Index.Get(follow)?.Prop("entryState") ?? string.Empty)?.Number ?? throw new ToolError($"{follow} has no playable entry state.");
        var suppress = SuppressIds(c, a) ?? s.Suppress;
        return s with
        {
            When = a.OptText("when") switch { null => s.When, "falling" => KnockdownWhen.Falling, "lying" => KnockdownWhen.Lying, "both" => KnockdownWhen.Both, var x => throw new ToolError($"when: '{x}' (falling, lying, both).") },
            ChaseLimit = a.Int("chase_limit", s.ChaseLimit, KnockdownChaseSpec.MinDistance, KnockdownChaseSpec.MaxDistance),
            AttackDistance = a.Int("attack_distance", s.AttackDistance, KnockdownChaseSpec.MinDistance, KnockdownChaseSpec.MaxDistance),
            Timing = a.OptText("attack_timing") switch { null => s.Timing, "while_down" => FollowUpTiming.WhileDown, "as_they_get_up" => FollowUpTiming.AsTheyGetUp, var x => throw new ToolError($"attack_timing: '{x}' (while_down, as_they_get_up).") },
            LeadFrames = a.Int("lead_frames", s.LeadFrames, 0, KnockdownChaseSpec.MaxLead),
            GiveUpFrames = a.Int("give_up_frames", s.GiveUpFrames, KnockdownChaseSpec.MinGiveUp, KnockdownChaseSpec.MaxGiveUp),
            Frequency = a.OptText("frequency") switch { null => s.Frequency, "always" => ChaseFrequency.Always, "often" => ChaseFrequency.Often, "sometimes" => ChaseFrequency.Sometimes, var x => throw new ToolError($"frequency: '{x}' (always, often, sometimes).") },
            Fallback = a.OptText("fallback") switch { null => s.Fallback, "stop" => ChaseFallback.StopAndReassess, "block" => ChaseFallback.Block, "retreat" => ChaseFallback.Retreat, var x => throw new ToolError($"fallback: '{x}' (stop, block, retreat).") },
            Placement = a.OptText("placement") switch { null => s.Placement, "before" => OwnershipPlacement.BeforeExisting, "after" => OwnershipPlacement.AfterExisting, "suppress" => OwnershipPlacement.SuppressSpecific, var x => throw new ToolError($"placement: '{x}' (before, after, suppress).") },
            Suppress = suppress, FollowUpAbilityId = follow, FollowUpState = followState, FollowUpName = c.Index.NameOf(follow),
            MeterCost = follow == s.FollowUpAbilityId ? s.MeterCost : (int)Math.Round(c.Graph.Move("state:" + followState.ToString(CultureInfo.InvariantCulture))?.PowerCost ?? 0)
        };
    }

    /// <summary>Rules to suppress, by id ("state:-1/ctrl:3") or by their State -1 name.</summary>
    private static IReadOnlyList<string>? SuppressIds(LoadedCharacter c, ToolArgs a) =>
        a.OptArray("suppress")?.Select(n => n is JsonValue v && v.TryGetValue<string>(out var text) ? text : throw new ToolError("'suppress' holds rule ids or names."))
            .Select(text => c.Index.Get(text) is { Kind: Core.XRay.Model.ObjectKind.Controller } ? text
                : c.Index.ControllersOf("state:-1").FirstOrDefault(x => x.Name.Equals(text, StringComparison.OrdinalIgnoreCase))?.Id
                  ?? throw new ToolError($"No State -1 rule '{text}'."))
            .ToList();

    // ------------------------------------------------------------------ inspect_behavior_draft

    public JsonObject Inspect(ToolArgs a)
    {
        var c = ctx.Load(a.Text("character"));
        var svc = Service(c);
        if (a.OptText("behavior_id") is not { } id)
            return new JsonObject
            {
                ["character"] = c.DisplayName,
                ["taughtBehaviors"] = new JsonArray(svc.List().Select(b => (JsonNode)new JsonObject
                {
                    ["id"] = b.Id, ["name"] = b.Name, ["status"] = DirectorText.Status(b.Status), ["revision"] = b.Revision, ["summary"] = DirectorText.Sentence(b)
                }).ToArray()),
                ["next"] = "inspect_behavior_draft with behavior_id shows one; propose_behavior teaches a new one."
            };
        var behavior = svc.Get(id) ?? throw new ToolError($"No taught behavior '{id}'.");
        behavior = svc.Refresh(behavior);
        var analysis = svc.Analyze(behavior, Engine(c), ctx.Experiments, PlaybackRoots);
        var o = Card(c, behavior, analysis, svc);
        if (a.Bool("include_code", false) && analysis.Code is { } code)
            o["generatedCode"] = new JsonObject
            {
                ["stateMinus1Block"] = Clip(code.MinusOneBlock), ["statesFile"] = Clip(code.StatesFile), ["defLine"] = code.DefLine,
                ["suppressLine"] = code.Suppress.Count > 0 ? code.SuppressLine : null
            };
        if (svc.Diff() is { } diff && svc.Workspace.Info?.BehaviorId == behavior.Id)
            o["workingCopy"] = new JsonObject
            {
                ["buildHash"] = svc.Workspace.Info?.GeneratedBuildHash, ["files"] = new JsonArray(diff.Files.Select(f => (JsonNode)$"{f.Kind} {f.Path} (+{f.AddedLines} lines)").ToArray()),
                ["diff"] = a.Bool("include_code", false) ? Clip(diff.Text) : null
            };
        return o;
    }

    private static string Clip(string text) => text.Length <= 24000 ? text : text[..24000] + "\n… (truncated)";

    // ------------------------------------------------------------------ test_behavior

    public async Task<JsonObject> Test(ToolArgs a, CancellationToken cancel)
    {
        var c = ctx.Load(a.Text("character"));
        var svc = Service(c);
        var b = svc.Get(a.Text("behavior_id")) ?? throw new ToolError($"No taught behavior '{a.Text("behavior_id")}'.");
        var setup = ExperimentTools.ReadySetup(ctx, c, a);
        var analysis = svc.Analyze(b, Engine(c), ctx.Experiments, PlaybackRoots);
        if (!analysis.CanGenerate) throw new ToolError("It cannot be tested yet: " + string.Join(" ", analysis.Problems));
        var runs = DirectorTesting.Setups(b, setup).Sum(s => s.Fixture ? b.Tests.Trials : 1) + (b.Tests.SourceSequenceSpec is null ? 1 : 2);
        var job = jobs.Enqueue("teach-test", $"Teach AI test · {b.Name} ({b.Id} rev {b.Revision})",
            $"{runs} IKEMEN runs of about 15–40 s each, one after another, in a disposable copy (staging); the working copy and the installed character are not touched", session =>
                ExperimentTools.OnSession(session, () => session.RunDirectorAsync(new DirectorTestJob(b.Id, setup, $"Teach AI test · {b.Name}"), (gate, progress) =>
                {
                    var (folder, hash) = svc.StagingBuild(analysis);
                    try
                    {
                        var request = new DirectorTesting.SuiteRequest(b, c.Graph, analysis.Facts, analysis.Code!, folder, hash, c.Root, c.SubjectFolder, c.SubjectDef, setup,
                            dummy => ctx.Setup(c, new SetupOverride(dummy, null, null)), svc.TestsRoot);
                        var report = DirectorTesting.Run(ctx.Runs, request, gate, progress);
                        return new DirectorTestOutcome(report, svc.RecordTest(b, report));
                    }
                    finally { DirectorService.DeleteStaging(folder); }
                }), () =>
                {
                    var r = session.DirectorResult!;
                    var o = Report(c, r.Report, r.Behavior);
                    // A test here ran on a disposable staging build, so it never makes the behavior Ready for approval: approval binds to the working copy,
                    // which only the app's Test generates and tests.
                    o["next"] = (r.Report.Passed ? "Passed on a disposable staging build. " : "Not passed — see findings. ") +
                                "Approval binds to the working copy: the user runs Test in the app (X-Ray → AI Director), then reviews and approves there.";
                    return new JobOutcome(JobStatus.Completed, o, null);
                }));
        return await ExperimentTools.Started(ctx, jobs, job, a, cancel).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ shapes

    private JsonObject Card(LoadedCharacter c, TaughtBehavior b, DirectorAnalysis a, DirectorService svc)
    {
        var s = b.Spec;
        var last = svc.LoadTest(b.LastTestId);
        return new JsonObject
        {
            ["id"] = b.Id,
            ["name"] = b.Name,
            ["status"] = DirectorText.Status(b.Status),
            ["revision"] = b.Revision,
            ["card"] = new JsonObject
            {
                ["when"] = DirectorText.When(s.When), ["do"] = DirectorText.Do(s), ["then"] = DirectorText.Then(s), ["giveUpIf"] = DirectorText.GiveUp(s),
                ["howOften"] = DirectorText.Frequency(s.Frequency), ["ownership"] = DirectorText.Placement(s.Placement)
            },
            ["inPlainWords"] = DirectorText.Sentence(b),
            ["taughtFrom"] = b.Source.Text,
            ["followUpProof"] = new JsonObject { ["proven"] = a.Proof.Proven, ["evidence"] = a.Proof.Text },
            ["ownership"] = new JsonObject
            {
                ["verdict"] = a.Ownership.Verdict.ToString(), ["summary"] = a.Ownership.Summary,
                ["competingRules"] = new JsonArray(a.Ownership.Competitors.Take(12).Select(r => (JsonNode)r.Plain).ToArray()),
                ["canInterruptTheChase"] = new JsonArray(a.Ownership.StealRisks.Select(r => (JsonNode)$"{r.Plain} ({r.ControllerId}, {r.File}:{r.Line})").ToArray()),
                ["reasons"] = Semantic.Strings(a.Ownership.Reasons)
            },
            ["problems"] = a.Problems.Count == 0 ? null : Semantic.Strings(a.Problems),
            ["generated"] = a.Code is null ? null : new JsonObject
            {
                ["rules"] = new JsonArray(a.Code.Rules.Select(r => (JsonNode)new JsonObject { ["rule"] = r.Name, ["does"] = r.Purpose, ["trigger"] = r.Trigger }).ToArray()),
                ["states"] = $"chase State {a.Code.ChaseState}" + (s.Fallback == ChaseFallback.Retreat ? $", retreat State {a.Code.RetreatState}" : string.Empty),
                ["codeHash"] = DirectorHash.Short(a.Code.Hash), ["file"] = CharacterFacts.GeneratedFile, ["stateMinus1In"] = a.Facts.MinusOneFile
            },
            ["lastTest"] = last is null ? null : Report(c, last, b),
            ["approval"] = b.Approval is null ? "none (approval is the user's, in the app)" : $"approved {b.Approval.ApprovedUtc:u} for build {DirectorHash.Short(b.Approval.BuildHash)}",
            ["deployment"] = b.Deployment is null ? "none" : $"{b.Deployment.Id}{(b.Deployment.RolledBack ? " (rolled back)" : string.Empty)}"
        };
    }

    private static JsonObject Report(LoadedCharacter c, DirectorTestReport r, TaughtBehavior b) => new()
    {
        ["testId"] = r.Id,
        ["passed"] = r.Passed,
        ["headline"] = DirectorTesting.Headline(r),
        ["status"] = DirectorText.Status(b.Status),
        ["build"] = DirectorHash.Short(r.BuildHash),
        ["runs"] = new JsonArray(r.Runs.Select(m => (JsonNode)new JsonObject
        {
            ["setup"] = m.Setup, ["runId"] = m.RunId, ["tested"] = m.Loaded, ["notTested"] = m.NotTestedWhy, ["knockdowns"] = m.Knockdowns, ["chases"] = m.Activations,
            ["followUps"] = m.FollowUps, ["connected"] = m.Connected, ["fallbacks"] = m.Fallbacks, ["giveUps"] = m.GiveUps, ["conflicts"] = m.Conflicts, ["unfinished"] = m.Unfinished,
            ["damage"] = m.Damage, ["notes"] = m.Notes.Count == 0 ? null : Semantic.Strings(m.Notes.Take(6))
        }).ToArray()),
        ["regressions"] = new JsonArray(r.Regressions.Select(g => (JsonNode)$"{(g.Passed ? "held" : "FAILED")}: {g.Name} — {g.Text}").ToArray()),
        ["findings"] = Semantic.Strings(r.Findings),
        ["scope"] = DirectorTestReport.ScopeText
    };
}
