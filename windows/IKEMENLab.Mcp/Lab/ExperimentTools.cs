using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Sequences;
using IKEMENLab.Mcp.Protocol;

namespace IKEMENLab.Mcp.Lab;

/// <summary>
/// Experiment tier: Play Ability, State Preview and Sequence Lab runs. Planning, playback, verdicts and experiment records are Core's (the same planner,
/// session, verifiers and stores the X-Ray window uses); this class only turns tool arguments into those calls and their results into small JSON.
/// Long work becomes a job (see <see cref="JobQueue"/>). Nothing here writes character files or settings.
/// </summary>
internal sealed class ExperimentTools(LabContext ctx, JobQueue jobs)
{
    public const int MaxTrials = 50, TrialsNeedingConsent = 10, MaxSteps = 20, MaxWaitSeconds = 50;

    private const string RunEstimate = "about 15–40 s: one IKEMEN window opens, plays and closes by itself";

    // ------------------------------------------------------------------ play_ability / preview_state

    public async Task<JsonObject> PlayAbility(ToolArgs a, CancellationToken cancel)
    {
        var c = ctx.Load(a.Text("character"));
        var abilityId = Resolve.Ability(c, a.Text("ability"));
        var name = c.Index.NameOf(abilityId);
        var path = c.PathOf(abilityId);
        if (path.Path is not { } p)
            return new JsonObject
            {
                ["started"] = false,
                ["ability"] = Semantic.Ref(c.Index, abilityId),
                ["status"] = "Cannot be played",
                ["why"] = path.Refused,
                ["canPreview"] = path.CanPreview,
                ["next"] = path.CanPreview ? "preview_state can force its entry state for a look (never proof)." : null
            };

        var setup = ReadySetup(c, a);
        var request = new PlaybackRequest(c.Root, c.SubjectFolder, c.SubjectDef, c.Graph, p.Route, setup, $"Play Ability · {name}") { Mode = PlaybackMode.Ability, AbilityId = abilityId };
        var abilityRef = Semantic.Ref(c.Index, abilityId);
        var job = jobs.Enqueue("play_ability", $"Play Ability · {name}", RunEstimate, session => OnSession(session, () => session.PlayAsync(request), () =>
        {
            var outcome = session.Outcome!;
            var ability = outcome.Ability!;
            var performed = ability.Status == AbilityStatus.Performed;
            Func<string, string?>? names = session.LastJob?.Snapshot is { } snap ? snap.NameOf : null;
            var r = new JsonObject
            {
                ["kind"] = "play_ability",
                ["ability"] = abilityRef.DeepClone(),
                ["status"] = Semantic.AbilityStatusText(ability.Status),
                ["statusCode"] = ability.Status.ToString(),
                ["headline"] = session.Headline
            };
            if (performed)
            {
                r["started"] = $"Pressed “{ability.Command}” at frame {ability.InputFrame}; {AbilityText.StateLabel(ability.EntryStateId, names)} began at frame {ability.EntryFrame}.";
                if (ability.Observed is { } o)
                {
                    r["observed"] = Semantic.Observation(c.Index, o);
                    r["details"] = Semantic.Strings(AbilityText.Details(o, names));
                }
            }
            else
            {
                r["why"] = ability.Detail ?? PlaybackInspector.Describe(ability.Reason);
                if (outcome.Failure is { Hints.Count: > 0 } f) r["hints"] = Semantic.Strings(f.Hints);
            }

            if (ability.Notes.Count > 0) r["notes"] = Semantic.Strings(ability.Notes);
            r["runId"] = outcome.Record.Id;
            r["provenance"] = Provenance(c, outcome.Record, ability.EngineSha256, ability.PlanFingerprint);
            r["evidence"] = performed
                ? "Performed cites only the route verifier's step check (runtime.transition-observed) in this run. Contact, damage, reaction and recovery are measurements of this one run against this dummy; nothing in the static graph changes."
                : ability.Status == AbilityStatus.NotPerformed
                    ? "The inputs were fed and the move did not start in this run (the route verifier's step check)."
                    : "This run could not show either way; nothing is claimed.";
            r["next"] = performed ? "test_sequence tries a follow-up from here; get_runtime_trace with runId shows the frames." : "get_runtime_trace with runId shows what happened.";
            return new JobOutcome(JobStatus.Completed, r, null);
        }));
        return await Started(job, a, cancel).ConfigureAwait(false);
    }

    public async Task<JsonObject> PreviewState(ToolArgs a, CancellationToken cancel)
    {
        var c = ctx.Load(a.Text("character"));
        string stateId;
        if (a.OptText("state") is { } s) stateId = Resolve.State(c, s);
        else if (a.OptText("ability") is { } ab) stateId = c.Index.Get(Resolve.Ability(c, ab))?.Prop("entryState") ?? throw new ToolError("That ability has no entry state.");
        else throw new ToolError("Pass 'state' (a state number, id or name) or 'ability' (its entry state is previewed).");
        if (!StatePreview.CanPreview(c.Index, stateId, out var why)) throw new ToolError($"{c.Index.NameOf(stateId)} cannot be previewed: {why}");

        var setup = ReadySetup(c, a);
        var label = AbilityText.StateLabel(stateId, id => c.Index.Get(id) is not null && c.Index.Names.IsRenamed(id) ? c.Index.NameOf(id) : null);
        var request = new PreviewRequest(c.Root, c.SubjectFolder, c.SubjectDef, c.Index, stateId, setup, $"State Preview (not proof) · {label}");
        var stateRef = Semantic.Ref(c.Index, stateId);
        var job = jobs.Enqueue("preview_state", $"State Preview (not proof) · {label}", RunEstimate, session => OnSession(session, () => session.PreviewAsync(request), () =>
        {
            var outcome = session.PreviewResult!;
            var report = outcome.Report;
            Func<string, string?>? names = session.LastJob?.Snapshot is { } snap ? snap.NameOf : null;
            var r = new JsonObject
            {
                ["kind"] = "preview_state",
                ["proof"] = false,
                ["notProof"] = PreviewReport.NotProof,
                ["state"] = stateRef.DeepClone(),
                ["status"] = report.Status switch
                {
                    PreviewStatus.Forced => "Forced (a look, not proof)",
                    PreviewStatus.ForcedNotSeen => "Forced, but it left the state on its first tick (a look, not proof)",
                    PreviewStatus.Refused => "Refused by the engine",
                    _ => "Could not run"
                },
                ["statusCode"] = report.Status.ToString(),
                ["headline"] = session.Headline
            };
            if (report.Detail is { Length: > 0 } d) r["detail"] = d;
            if (report.Observed is { } o)
            {
                r["observed"] = Semantic.Observation(c.Index, o);
                r["details"] = Semantic.Strings(AbilityText.Details(o, names));
            }

            if (report.Notes.Count > 0) r["notes"] = Semantic.Strings(report.Notes);
            r["runId"] = outcome.Record.Id;
            r["provenance"] = Provenance(c, outcome.Record, report.EngineSha256, report.PlanFingerprint);
            return new JobOutcome(JobStatus.Completed, r, null);
        }));
        return await Started(job, a, cancel).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ sequences

    public async Task<JsonObject> TestSequence(ToolArgs a, CancellationToken cancel)
    {
        var c = ctx.Load(a.Text("character"));
        var actions = ParseSteps(c, a);
        var spec = SequenceSpec.Format(actions);
        var chain = SequenceLabels.Chain(actions, c.Index);
        // An unsaved test is still an exact sequence version: the same steps always get the same id, so its experiments stay comparable.
        var sequence = new Sequence("mcp-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(spec)))[..10].ToLowerInvariant(), a.OptText("name") ?? chain, 1, actions);
        if (a.Bool("plan_only")) return PlanJson(c, sequence, ApproachOf(c, a));
        return await RunSequence(c, sequence, a, cancel).ConfigureAwait(false);
    }

    public JsonObject CreateExperiment(ToolArgs a)
    {
        var c = ctx.Load(a.Text("character"));
        var name = a.Text("name");
        if (name.Length > 80) throw new ToolError("'name' can be at most 80 characters.");
        var actions = ParseSteps(c, a);
        var id = a.OptText("sequence_id");
        if (id is not null && ctx.Sequences.Load(c.Folder).Sequences.All(s => s.Id != id))
            throw new ToolError($"No saved variant '{id}' for this character.", "Omit sequence_id to save a new variant; list_experiments shows the saved ones.");
        var saved = ctx.Sequences.Save(c.Folder, new Sequence(id ?? Sequence.NewId(), name, 1, actions));
        var o = PlanJson(c, saved, ApproachOf(c, a));
        o["saved"] = $"Saved as variant “{saved.Name}” v{saved.Version} (also visible in the Sequence Lab). A later change to its steps saves a new version; results always name the version they ran.";
        o["next"] = "run_experiment with this sequenceId runs it; compare_experiments compares two experiments.";
        return o;
    }

    public async Task<JsonObject> RunExperiment(ToolArgs a, CancellationToken cancel)
    {
        var c = ctx.Load(a.Text("character"));
        var id = a.Text("sequence_id");
        var saved = ctx.Sequences.Load(c.Folder).Sequences.FirstOrDefault(s => s.Id == id)
                    ?? throw new ToolError($"No saved variant '{id}' for this character.", "create_experiment saves one; list_experiments shows them.");
        Sequence sequence;
        try { sequence = saved.ToSequence(); }
        catch (FormatException ex) { throw new ToolError($"The saved variant '{id}' cannot be read: {ex.Message}"); }
        return await RunSequence(c, sequence, a, cancel).ConfigureAwait(false);
    }

    private async Task<JsonObject> RunSequence(LoadedCharacter c, Sequence sequence, ToolArgs a, CancellationToken cancel)
    {
        var trials = a.Int("trials", 1, 1, MaxTrials);
        var perTrial = ctx.Experiments.List(c.ContentHash).FirstOrDefault(e => e.AverageSeconds is not null)?.AverageSeconds;
        var cost = SequenceExperimentRunner.CostText(trials, perTrial);
        if (trials > TrialsNeedingConsent && !a.Bool("accept_cost"))
            return new JsonObject
            {
                ["started"] = false,
                ["cost"] = cost,
                ["message"] = $"×{trials} is a long run. Call again with accept_cost: true to start it (or use ≤ {TrialsNeedingConsent} trials)."
            };

        var setup = ReadySetup(c, a);
        var (prepared, planned) = SequenceExperimentRunner.Prepare(c.Graph, sequence, c.Root, c.SubjectFolder, c.SubjectDef, setup, trials);
        if (prepared is null)
            return new JsonObject
            {
                ["started"] = false,
                ["status"] = "Cannot be played",
                ["why"] = planned.Refused,
                ["refusedStep"] = planned.RefusedStep,
                ["steps"] = StepPlans(planned)
            };

        var job = jobs.Enqueue("sequence", (trials > 1 ? $"×{trials} · " : string.Empty) + prepared.Chain, trials > 1 ? cost : RunEstimate, session =>
            OnSession(session, () => session.RunSequenceAsync(prepared.Job, (gate, progress) =>
                SequenceExperimentRunner.Run(ctx.Runs, ctx.Experiments, prepared, gate, progress, ExperimentSummary.McpOrigin)), () =>
            {
                var x = session.ExperimentResult!;
                var s = x.Summary;
                var r = new JsonObject
                {
                    ["kind"] = "sequence",
                    ["experimentId"] = s.Id,
                    ["sequence"] = new JsonObject { ["name"] = sequence.Name, ["id"] = sequence.Id, ["version"] = sequence.Version },
                    ["steps"] = prepared.Chain,
                    ["headline"] = session.Headline
                };
                if (x.LastTrial is { } t) r[trials > 1 ? "lastTrial" : "run"] = Semantic.SequenceRun(t.Report, t.Record.Id);
                if (trials > 1)
                {
                    r["stats"] = Semantic.Stats(s);
                    r["lines"] = Semantic.Strings(ExperimentText.Lines(s));
                }

                r["scope"] = Semantic.Scope(s.Scope);
                r["next"] = "get_experiment_results / compare_experiments take this experimentId; get_runtime_trace takes a runId.";
                return s.Stopped
                    ? new JobOutcome(JobStatus.Cancelled, r, $"Stopped after {s.Completed} of {s.Requested} trial(s); the finished trials are kept.")
                    : new JobOutcome(JobStatus.Completed, r, null);
            }));
        var started = await Started(job, a, cancel).ConfigureAwait(false);
        if (trials > 1) started["cost"] = cost;
        return started;
    }

    /// <summary>The typed steps (or the compact spec), with abilities resolved by name or id.</summary>
    internal static IReadOnlyList<SequenceAction> ParseSteps(LoadedCharacter c, ToolArgs a)
    {
        var steps = a.OptArray("steps");
        var spec = a.OptText("spec");
        if ((steps is null) == (spec is null))
            throw new ToolError("Pass either 'steps' (typed actions) or 'spec' (\"1000 > chase:35 > wait:20 > 200\").");
        List<SequenceAction> actions;
        if (spec is not null)
        {
            try { actions = SequenceSpec.Parse(spec).ToList(); }
            catch (FormatException ex) { throw new ToolError(ex.Message); }
        }
        else
        {
            actions = [];
            var n = 0;
            foreach (var item in steps!)
            {
                n++;
                actions.Add(item switch
                {
                    JsonObject o => Typed(c, new ToolArgs(o), n),
                    JsonValue v when v.TryGetValue<string>(out var text) => Shorthand(c, text, n),
                    _ => throw new ToolError($"Step {n} must be an object such as {{\"action\":\"wait\",\"frames\":20}}.")
                });
            }
        }

        if (actions.Count == 0) throw new ToolError("The sequence has no steps.");
        if (actions.Count > MaxSteps) throw new ToolError($"At most {MaxSteps} steps.");
        for (var i = 0; i < actions.Count; i++)
            if (actions[i].Problem() is { } problem) throw new ToolError($"Step {i + 1}: {problem}");
        return actions;
    }

    private static SequenceAction Typed(LoadedCharacter c, ToolArgs s, int n)
    {
        var action = s.OptText("action") ?? throw new ToolError($"Step {n} needs 'action'.");
        try
        {
            return action.ToLowerInvariant().Replace('-', '_') switch
            {
                "ability" => SequenceAction.Ability(Resolve.Ability(c, s.Text("ability"))),
                "walk_forward" => SequenceAction.Walk(s.Int("frames", 0, 1, SequenceAction.MaxFrames)),
                "walk_backward" => SequenceAction.Walk(s.Int("frames", 0, 1, SequenceAction.MaxFrames), forward: false),
                "wait" => SequenceAction.WaitFrames(s.Int("frames", 0, 1, SequenceAction.MaxFrames)),
                "chase" => SequenceAction.ChaseTo(s.Int("distance", 0, 1, SequenceAction.MaxDistance), s.Bool("stop_if_opponent_recovers")),
                "dash" => SequenceAction.Dash(),
                "jump" => SequenceAction.Jump(),
                _ => throw new ToolError($"Step {n}: unknown action '{action}'. Use ability, walk_forward, walk_backward, dash, jump, wait or chase.")
            };
        }
        catch (ToolError ex) when (!ex.Message.StartsWith("Step ", StringComparison.Ordinal))
        {
            throw new ToolError($"Step {n}: {ex.Message}", ex.Hint);
        }
    }

    /// <summary>A string step: a spec token ("wait:20", "chase:35!", "dash", "1000") or an ability name ("Kung Fu Palm").</summary>
    private static SequenceAction Shorthand(LoadedCharacter c, string text, int n)
    {
        var t = text.Trim();
        var head = t.Split(':', 2)[0].ToLowerInvariant();
        if ((t.Contains(':') && head is "walk" or "back" or "wait" or "chase" or "ability") || head is "dash" or "jump")
        {
            try { return SequenceSpec.Parse(t)[0]; }
            catch (FormatException ex) { throw new ToolError($"Step {n}: {ex.Message}"); }
        }

        try { return SequenceAction.Ability(Resolve.Ability(c, t)); }
        catch (ToolError ex) { throw new ToolError($"Step {n}: {ex.Message}", ex.Hint); }
    }

    private JsonObject PlanJson(LoadedCharacter c, Sequence sequence, int approach)
    {
        var planned = SequencePlanner.Plan(c.Graph, sequence, approach);
        var o = new JsonObject
        {
            ["sequenceId"] = sequence.Id,
            ["name"] = sequence.Name,
            ["version"] = sequence.Version,
            ["steps"] = SequenceLabels.Chain(sequence.Actions, c.Index),
            ["spec"] = SequenceSpec.Format(sequence.Actions),
            ["ready"] = planned.Plan is not null,
            ["plan"] = StepPlans(planned)
        };
        if (planned.Refused is not null) { o["why"] = planned.Refused; o["refusedStep"] = planned.RefusedStep; }
        if (planned.Warnings.Count > 0) o["warnings"] = Semantic.Strings(planned.Warnings);
        o["basis"] = "How the Sequence Lab would play it (static plan; nothing was launched).";
        return o;
    }

    private static JsonArray StepPlans(SequencePlanResult planned) =>
        new(planned.Steps.Select(s => (JsonNode)new JsonObject { ["step"] = s.Index, ["label"] = s.Label, ["how"] = s.How }).ToArray());

    // ------------------------------------------------------------------ jobs

    public async Task<JsonObject> GetJob(ToolArgs a, CancellationToken cancel)
    {
        var id = a.OptText("job_id");
        if (id is null)
            return new JsonObject { ["jobs"] = new JsonArray(jobs.Recent(10).Select(j => (JsonNode)j.ToJson(withResult: false)).ToArray()) };
        var job = jobs.Get(id) ?? throw new ToolError($"No job '{id}'.", "Jobs live as long as this server process; results stay in the experiment store and the run records.");
        var wait = a.Int("wait_seconds", 0, 0, MaxWaitSeconds);
        if (wait > 0 && !job.IsFinished)
            await Task.WhenAny(job.Finished, Task.Delay(TimeSpan.FromSeconds(wait), cancel)).ConfigureAwait(false);
        return job.ToJson(queuePosition: jobs.Ahead(job));
    }

    public JsonObject CancelJob(ToolArgs a)
    {
        var id = a.Text("job_id");
        var (accepted, message) = jobs.Cancel(id);
        var job = jobs.Get(id);
        var o = new JsonObject { ["jobId"] = id, ["accepted"] = accepted, ["message"] = message };
        if (job is not null) o["status"] = job.Status.ToString().ToLowerInvariant();
        return o;
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>Runs one session job and reads its result. A job the session did not take (it was closing) is never read as someone else's result.</summary>
    internal static async Task<JobOutcome> OnSession(PlaybackSession session, Func<Task> start, Func<JobOutcome> read)
    {
        var before = session.AttemptId;
        await start().ConfigureAwait(false);
        if (session.AttemptId == before)
            return new JobOutcome(JobStatus.Cancelled, null, session.IsClosed ? "The server is shutting down; nothing was launched." : "The playback session did not take the job.");
        return session.State switch
        {
            PlaybackState.Finished => read(),
            PlaybackState.Cancelled => new JobOutcome(JobStatus.Cancelled, null, "Cancelled: the engine was stopped and its sandbox deleted; no result was saved."),
            PlaybackState.Error => new JobOutcome(JobStatus.Failed, null, session.Error),
            _ => new JobOutcome(JobStatus.Failed, null, $"The run ended in an unexpected state ({session.State}).")
        };
    }

    /// <summary>The immediate answer for a new job: its state (queued or running), who holds the engine if it must wait, and — with wait_seconds — the result.</summary>
    private Task<JsonObject> Started(LabJob job, ToolArgs a, CancellationToken cancel) => Started(ctx, jobs, job, a, cancel);

    internal static async Task<JsonObject> Started(LabContext ctx, JobQueue jobs, LabJob job, ToolArgs a, CancellationToken cancel)
    {
        var wait = a.Int("wait_seconds", 0, 0, MaxWaitSeconds);
        if (wait > 0) await Task.WhenAny(job.Finished, Task.Delay(TimeSpan.FromSeconds(wait), cancel)).ConfigureAwait(false);
        var o = job.ToJson(queuePosition: jobs.Ahead(job));
        if (!job.IsFinished && job.WaitingFor is null && ctx.Broker?.CurrentHolder() is { } holder && holder.ProcessId != Environment.ProcessId)
            o["waitingForEngine"] = "The engine is busy: " + holder.Describe() + ". This job starts as soon as it is free (cancel_job cancels it).";
        return o;
    }

    private PlaybackSetup ReadySetup(LoadedCharacter c, ToolArgs a) => ReadySetup(ctx, c, a);

    internal static PlaybackSetup ReadySetup(LabContext ctx, LoadedCharacter c, ToolArgs a)
    {
        var setup = ctx.Setup(c, Override(a));
        if (!setup.Ready)
            throw new ToolError("Playback is not set up: " + string.Join(" ", setup.Issues),
                "Set the X-Ray engine, dummy and stage in IKEMEN Lab (X-Ray → Combos → Playback setup), start the server with --engine/--dummy/--stage, or pass 'setup'.");
        return setup;
    }

    private int ApproachOf(LoadedCharacter c, ToolArgs a) => ctx.Setup(c, Override(a)).ApproachDistance;

    private static SetupOverride? Override(ToolArgs a) =>
        a.OptObject("setup") is { } s ? new SetupOverride(s.OptText("dummy"), s.OptText("stage"), s.OptInt("approach_distance", PlaybackPreflight.MinApproachDistance, PlaybackPreflight.MaxApproachDistance)) : null;

    private static JsonObject Provenance(LoadedCharacter c, PlaybackRecord record, string? engineSha, string? planFingerprint) => new()
    {
        ["characterFiles"] = c.ContentHash,
        ["engineSha256"] = engineSha ?? record.EngineSha256,
        ["planFingerprint"] = planFingerprint,
        ["dummy"] = record.Dummy,
        ["stage"] = record.Stage,
        ["approachDistance"] = record.ApproachDistance,
        ["seconds"] = record.Seconds
    };
}
