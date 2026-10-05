using System.Globalization;
using System.Text.Json.Nodes;
using IKEMENLab.Core.XRay.Behavior;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Sequences;
using IKEMENLab.Mcp.Protocol;

namespace IKEMENLab.Mcp.Lab;

/// <summary>
/// Observe tier, Phase 5: behavior recognition (Watch &amp; Ask). The cards, levels, episodes and Why answers are Core's
/// (<see cref="BehaviorCatalog"/>, <see cref="BehaviorDetector"/>, <see cref="BehaviorWhy"/>); this class only shapes them into small JSON.
/// Nothing here edits the character or its AI.
/// </summary>
internal sealed class BehaviorTools(LabContext ctx, JobQueue jobs)
{
    private (LoadedCharacter C, IReadOnlyList<BehaviorRun> Runs, IReadOnlyList<BehaviorCard> Cards, string? Engine) Cards(string character)
    {
        var c = ctx.Load(character);
        var engine = EngineIdentity.Sha256(ctx.Setup(c).EnginePath);
        var runs = ctx.Behavior.List(c.Index.CharacterId, StaticBehavior.SuperStates(c.Index));
        return (c, runs, BehaviorCatalog.Build(c.Index, c.Graph, runs, c.ContentHash, engine), engine);
    }

    // ------------------------------------------------------------------ list_behaviors

    public JsonObject ListBehaviors(ToolArgs a)
    {
        var (c, runs, cards, engine) = Cards(a.Text("character"));
        var all = a.Bool("include_not_seen", true);
        var stale = runs.Count(r => BehaviorCatalog.StaleReason(r, c.ContentHash, engine) is not null);
        var notAi = runs.Count(r => BehaviorCatalog.StaleReason(r, c.ContentHash, engine) is null && !r.Detection.AiControlled);
        var current = runs.Count - stale - notAi;
        return new JsonObject
        {
            ["character"] = c.DisplayName,
            ["watchedRuns"] = new JsonObject { ["onTheseFilesAndEngine"] = current, ["stale"] = stale, ["notOnTheAi"] = notAi > 0 ? notAi : null },
            ["engineKnown"] = engine is not null,
            ["behaviors"] = new JsonArray(cards.Where(x => all || x.Level != BehaviorLevel.NotSeen).Select(x => (JsonNode)new JsonObject
            {
                ["name"] = x.Template.Name,
                ["id"] = x.Template.Id,
                ["category"] = x.Template.Category,
                ["level"] = x.LevelName,
                ["episodes"] = x.Episodes.Count,
                ["runs"] = x.Runs,
                ["setups"] = x.Setups.Count,
                ["staticRules"] = x.StaticRules.Count,
                ["staleEpisodes"] = x.StaleEpisodes > 0 ? x.StaleEpisodes : null,
                ["summary"] = x.Template.Summary
            }).ToArray()),
            ["levels"] = "Possible = static AI logic suggests it (never runtime proof) · Observed = at least one complete episode recorded on the current files and engine · " +
                         "Confirmed Pattern = " + ConfirmedRule.Default.Text + ".",
            ["next"] = current == 0 ? "watch_match records a match to look for runtime episodes." : "inspect_behavior explains one; why_did_ai_do_this explains a moment of a watched run."
        };
    }

    // ------------------------------------------------------------------ inspect_behavior

    public JsonObject InspectBehavior(ToolArgs a)
    {
        var (c, _, cards, engine) = Cards(a.Text("character"));
        var spec = a.Text("behavior").Trim();
        var card = cards.FirstOrDefault(x => x.Template.Id.Equals(spec, StringComparison.OrdinalIgnoreCase) || x.Template.Name.Equals(spec, StringComparison.OrdinalIgnoreCase))
                   ?? cards.FirstOrDefault(x => x.Template.Name.Contains(spec, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ToolError($"No behavior '{spec}'.", "Behaviors: " + string.Join(", ", BehaviorTemplates.All.Select(t => t.Name)) + ".");
        var index = c.Index;
        var t = card.Template;
        var o = new JsonObject
        {
            ["name"] = t.Name,
            ["id"] = t.Id,
            ["category"] = t.Category,
            ["summary"] = t.Summary,
            ["level"] = card.LevelName,
            ["levelMeaning"] = card.LevelMeaning,
            ["pattern"] = t.Complete,
            ["conditions"] = Semantic.Strings(t.Conditions.Select(x => $"{BehaviorConditions.Name(x)} — {BehaviorConditions.Info(x).Meaning}")),
            ["actions"] = Semantic.Strings(t.Actions),
            ["outcomes"] = Semantic.Strings(t.Outcomes)
        };
        o["runtime"] = new JsonObject
        {
            ["episodes"] = card.Episodes.Count,
            ["runs"] = card.Runs,
            ["setups"] = Semantic.Strings(card.Setups),
            ["outcomes"] = new JsonObject(card.Outcomes.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["incompleteSituations"] = card.Incomplete,
            ["recent"] = new JsonArray(card.Episodes.OrderByDescending(e => e.Run.CreatedUtc).ThenBy(e => e.Episode.StartFrame).Take(8).Select(e => (JsonNode)Episode(index, e.Run, e.Episode)).ToArray()),
            ["staleEpisodes"] = card.StaleEpisodes,
            ["staleReasons"] = Semantic.Strings(card.StaleReasons)
        };
        o["static"] = new JsonObject
        {
            ["supportsPossible"] = card.StaticSupports,
            ["rules"] = new JsonArray(card.StaticRules.Take(8).Select(r => (JsonNode)new JsonObject
            {
                ["rule"] = BehaviorWhy.RuleText(index, r),
                ["role"] = r.Role == "rule" ? null : r.Role,
                ["confidence"] = Semantic.ConfidenceText(r.Confidence),
                ["source"] = ObserveTools.SourceOf(index, r.ControllerId)
            }).ToArray()),
            ["note"] = card.StaticRules.Count == 0
                ? "No AI rule with these recognised conditions was found. AI that decides through variables or computed values cannot be read this way; that is not evidence of absence."
                : "These rules are consistent with the behavior. Which rule actually fires is not known (no controller attribution)."
        };
        o["linksTo"] = new JsonArray(card.ActionStates.Select(s => (JsonNode)Link(index, s)).ToArray());
        o["limitations"] = Semantic.Strings(t.Limitations.Concat(card.Unknowns));
        o["evidence"] = new JsonObject
        {
            ["confirmedRule"] = card.Rule.Text,
            ["engineKnown"] = engine is not null,
            ["characterFiles"] = c.ContentHash,
            ["engineSha256"] = engine,
            ["ranges"] = $"close < {BehaviorConditions.CloseMax:0}, medium ≤ {BehaviorConditions.MediumMax:0}, long > {BehaviorConditions.MediumMax:0} (engine units, 320-wide scale)"
        };
        return o;
    }

    private static JsonObject Link(SemanticIndex index, int state)
    {
        var id = "state:" + state.ToString(CultureInfo.InvariantCulture);
        var o = Semantic.Ref(index, id);
        if (index.Get(id) is null) o["note"] = "not a state of this character (an engine common state or the opponent's)";
        var ability = index.Of(ObjectKind.Ability).FirstOrDefault(x => x.Prop("entryState") == id);
        if (ability is not null) o["ability"] = Semantic.Ref(index, ability.Id);
        return o;
    }

    internal static JsonObject Episode(SemanticIndex index, BehaviorRun run, BehaviorEpisode e)
    {
        var (situation, action, outcome) = BehaviorText.Parts(e, index);
        return new JsonObject
        {
            ["behavior"] = BehaviorTemplates.Get(e.Template).Name,
            ["runId"] = run.Id,
            ["frames"] = $"{e.StartFrame}–{e.EndFrame}",
            ["round"] = e.Round,
            ["situation"] = situation,
            ["action"] = action,
            ["outcome"] = outcome,
            ["chain"] = BehaviorText.Chain(e, index),
            ["setup"] = run.SetupText,
            ["notes"] = e.Notes.Count == 0 ? null : Semantic.Strings(e.Notes)
        };
    }

    // ------------------------------------------------------------------ why_did_ai_do_this

    public JsonObject Why(ToolArgs a)
    {
        var c = ctx.Load(a.Text("character"));
        var id = a.Text("run_id");
        var run = ctx.Behavior.Get(id, StaticBehavior.SuperStates(c.Index)) ?? throw new ToolError($"No watched run '{id}'.", "watch_match records one; list_behaviors / inspect_behavior name the runs behind each episode.");
        if (run.Character != c.Index.CharacterId) throw new ToolError($"Run {id} watched {run.Character}, not {c.Index.CharacterId}.");
        var log = ctx.Behavior.Trace(run) ?? throw new ToolError($"Run {id} has no trace.");
        var frame = a.Int("frame", 0, 0, int.MaxValue);
        var why = BehaviorWhy.Explain(c.Index, c.Graph, run, log, frame);
        var engine = EngineIdentity.Sha256(ctx.Setup(c).EnginePath);
        var timeline = BehaviorTimeline.Build(log, run.Detection).Where(e => e.Frame >= why.Frame - 60 && e.Frame <= why.Frame + 120).Take(16)
            .Select(e => (JsonNode)new JsonObject { ["frame"] = e.Frame, ["event"] = BehaviorText.Event(e, c.Index) }).ToArray();
        return new JsonObject
        {
            ["frame"] = why.Frame,
            ["summary"] = why.Summary,
            ["observedContext"] = why.Context,
            ["conditionsHeld"] = Semantic.Strings(why.ConditionsHeld.Select(BehaviorConditions.Name)),
            ["whatHappenedNext"] = why.Action,
            ["matchesPattern"] = new JsonArray(why.Matches.Select(m => (JsonNode)Episode(c.Index, run, m)).ToArray()),
            ["consistentStaticRules"] = new JsonArray(why.ConsistentRules.Select(r => (JsonNode)BehaviorWhy.RuleText(c.Index, r)).ToArray()),
            ["cause"] = new JsonObject { ["known"] = why.CauseKnown, ["statement"] = why.CausalStatement },
            ["nearby"] = new JsonArray(timeline),
            ["runEvidence"] = new JsonObject
            {
                ["setup"] = run.SetupText,
                ["control"] = run.Detection.Control,
                ["current"] = BehaviorCatalog.StaleReason(run, c.ContentHash, engine) is not { } stale ? "on the current character files and engine" : "stale: " + stale
            }
        };
    }

    // ------------------------------------------------------------------ watch_match

    public async Task<JsonObject> WatchMatch(ToolArgs a, CancellationToken cancel)
    {
        var c = ctx.Load(a.Text("character"));
        var seconds = a.Int("seconds", BehaviorWatch.DefaultSeconds, BehaviorWatch.MinSeconds, BehaviorWatch.MaxSeconds);
        var opponentAi = a.Int("opponent_ai_level", BehaviorWatch.DefaultOpponentAi, 1, 8);
        var subjectAi = a.Int("subject_ai_level", BehaviorWatch.DefaultSubjectAi, 1, 8);
        var setup = ExperimentTools.ReadySetup(ctx, c, a);
        var request = BehaviorWatch.Request(c.Root, c.Index, c.SubjectFolder, c.SubjectDef, setup, seconds, subjectAi, opponentAi, c.DisplayName);
        var job = jobs.Enqueue("watch", request.Summary, $"about {seconds + 15} s: one IKEMEN window plays the match and closes by itself", session =>
            ExperimentTools.OnSession(session, () => session.WatchAsync(BehaviorWatch.Job(request), (gate, progress) =>
                BehaviorWatch.Run(ctx.Runs, ctx.Behavior, request, c.Index, gate, progress, ExperimentSummary.McpOrigin)), () =>
            {
                var w = session.WatchResult!;
                var d = w.Run.Detection;
                var r = new JsonObject
                {
                    ["kind"] = "watch",
                    ["runId"] = w.Run.Id,
                    ["headline"] = session.Headline,
                    ["episodes"] = new JsonArray(d.Episodes.Take(12).Select(e => (JsonNode)Episode(c.Index, w.Run, e)).ToArray()),
                    ["byBehavior"] = new JsonObject(d.Episodes.GroupBy(e => e.Template).Select(g => KeyValuePair.Create(BehaviorTemplates.Get(g.Key).Name, (JsonNode?)g.Count()))),
                    ["control"] = d.Control,
                    ["frames"] = d.Frames,
                    ["rounds"] = d.Rounds,
                    ["notReadable"] = d.Missing.Count == 0 ? null : Semantic.Strings(d.Missing),
                    ["notes"] = d.Notes.Count == 0 ? null : Semantic.Strings(d.Notes),
                    ["evidence"] = "Each episode is a complete situation → action → outcome chain recorded in this run. It is runtime observation of this match, not a claim about which AI rule fired.",
                    ["next"] = "list_behaviors shows the evidence levels; why_did_ai_do_this explains a frame of this run."
                };
                if (d.Episodes.Count > 12) r["moreEpisodes"] = d.Episodes.Count - 12;
                return new JobOutcome(JobStatus.Completed, r, null);
            }));
        return await ExperimentTools.Started(ctx, jobs, job, a, cancel).ConfigureAwait(false);
    }
}
