using System.Reflection;
using System.Text.Json.Nodes;
using IKEMENLab.Core.XRay.Behavior;
using IKEMENLab.Core.XRay.Director;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Verify;
using IKEMENLab.Mcp.Lab;
using IKEMENLab.Mcp.Protocol;

namespace IKEMENLab.Mcp;

/// <summary>
/// The composition root: one <see cref="LabContext"/> (stores, settings, character cache), one <see cref="JobQueue"/> (one engine at a time, one playback
/// session), the Observe and Experiment tools, and the stdio <see cref="McpServer"/>. Tests build it with a fake engine runner and an isolated data folder.
/// </summary>
public sealed class McpApp
{
    public const string Instructions = """
        IKEMEN Lab · Character X-Ray: semantic access to a MUGEN/IKEMEN character and to real combat experiments, so you never need to read its CNS/CMD files.
        - Start with inspect_character and list_abilities. Refer to moves by the names shown (they include the user's own names) or by id (ability:1000, state:200).
        - Static answers ("basis": static) say what the files contain, not what happens in a match.
        - play_ability, preview_state, test_sequence and run_experiment launch one IKEMEN engine at a time and return a jobId at once; call get_job with
          wait_seconds (≤ 50) until the status is completed, failed or cancelled. If the desktop app or another client is using the engine, the job waits
          (queued) and says who holds it. cancel_job stops a job and its engine.
        - Sequence verdicts: "True combo" (the opponent never regained control between hits), "Connected sequence" (every attack connected but the opponent
          could act or recover for some frames — e.g. a chase after a knockdown), "Did not connect", "Could not test". Report them exactly; never upgrade one
          to another. A State Preview is never proof. Unknown stays unknown.
        - Experiments (×1/×10/×50) are stored with their exact scope (character files, engine, dummy, stage, sequence version); compare_experiments warns
          when two experiments did not run under the same setup.
        - Behavior (Watch & Ask): list_behaviors / inspect_behavior read the AI as combat behavior (Knockdown Chase, Anti-Air, Punish …) at three
          levels that must never be blurred: Possible (static AI logic suggests it — never runtime proof), Observed (watch_match recorded a complete
          episode on the current files and engine), Confirmed Pattern (the explicit repeat rule in the answer). Changed files or engine make runtime
          evidence stale. why_did_ai_do_this gives observed context and consistent static rules only: which AI controller fired is not recorded, so
          never say the AI did something "because" a rule fired.
        - Teach AI (Knockdown Chase only): propose_behavior makes a Draft from evidence, inspect_behavior_draft shows it (ownership, proof, generated
          rules), test_behavior runs it in sandboxes on a disposable copy. Approval and deployment are the user's, in the app; there is no tool for them.
          A generated behavior's Why is exact only where its intention register and the trace agree; existing AI keeps cause unknown.
        - Nothing here edits character files, the installed AI, or the app's settings.
        """;

    public McpApp(McpOptions options, TextWriter log, IEngineRunner? runner = null, RuntimeBroker? broker = null, string? sandboxBase = null)
    {
        Context = new LabContext(options, runner, broker, sandboxBase);
        Server = null!;
        Jobs = new JobQueue(Context, () => "MCP · " + (Server?.ClientName ?? "agent"));
        var observe = new ObserveTools(Context);
        var experiment = new ExperimentTools(Context, Jobs);
        var behavior = new BehaviorTools(Context, Jobs);
        var director = new DirectorTools(Context, Jobs);
        Server = new McpServer(BuildTools(observe, experiment, behavior, director), Instructions, log, Version);
    }

    public LabContext Context { get; }
    public JobQueue Jobs { get; }
    public McpServer Server { get; }

    public static string Version => typeof(McpApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.6.0";

    public Task<ShutdownResult> ShutdownAsync(TimeSpan? timeout = null) => Jobs.ShutdownAsync(timeout ?? TimeSpan.FromSeconds(30));

    // ------------------------------------------------------------------ tools

    private static JsonObject Character() => Schema.Str("The character: its folder name under chars/ (e.g. \"kfm\"), its folder path, or its .def path.");

    private static JsonObject Setup() => Schema.Object(new JsonObject
    {
        ["dummy"] = Schema.Str("Opponent character folder under chars/ (default: the app's Playback setup)."),
        ["stage"] = Schema.Str("Stage DEF, e.g. stages/kfm.def (default: the app's Playback setup)."),
        ["approach_distance"] = Schema.Int("How close you walk to the opponent before the first step (engine units; default 60).", 1, 1000)
    });

    private static JsonObject Wait() => Schema.Int("Wait up to this many seconds for the job to finish before answering (0 = answer at once with the jobId).", 0, ExperimentTools.MaxWaitSeconds, 0);

    private static JsonObject Steps()
    {
        var step = Schema.Object(new JsonObject
        {
            ["action"] = Schema.Enum("What to do.", "ability", "walk_forward", "walk_backward", "dash", "jump", "wait", "chase"),
            ["ability"] = Schema.Str("ability: the move, by name (e.g. \"Kung Fu Palm\") or id (ability:1000 / 1000)."),
            ["frames"] = Schema.Int("walk_forward / walk_backward / wait: how many frames.", 1, 600),
            ["distance"] = Schema.Int("chase: walk forward until within this distance (engine units).", 1, 1000),
            ["stop_if_opponent_recovers"] = Schema.Bool("chase: stop the run as soon as the opponent can act again.")
        }, "action");
        return new JsonObject
        {
            ["type"] = "array",
            ["description"] = "The sequence, first step an ability (from neutral). Example: [{\"action\":\"ability\",\"ability\":\"Kung Fu Palm\"},{\"action\":\"chase\",\"distance\":35}," +
                              "{\"action\":\"wait\",\"frames\":20},{\"action\":\"ability\",\"ability\":\"Normal A\"}]. A string item is a move name or a token like \"wait:20\".",
            ["minItems"] = 1,
            ["maxItems"] = ExperimentTools.MaxSteps,
            ["items"] = new JsonObject { ["anyOf"] = new JsonArray(step, new JsonObject { ["type"] = "string" }) }
        };
    }

    private static JsonObject Spec() => Schema.Str("Alternative to steps: the Sequence Lab's compact form, e.g. \"1000 > chase:35 > wait:20 > 200\" (ids, walk:N, back:N, wait:N, chase:D, chase:D!, dash, jump).");

    private static IReadOnlyList<McpTool> BuildTools(ObserveTools o, ExperimentTools x, BehaviorTools b, DirectorTools d)
    {
        static Func<ToolArgs, CancellationToken, Task<JsonObject>> Sync(Func<ToolArgs, JsonObject> f) => (a, _) => Task.FromResult(f(a));

        return
        [
            // ---------------------------------------------------------- Observe
            new("inspect_character", "Inspect character",
                "Overview of a character: name, ability counts by category, its most useful playable moves, the user's names, playback readiness and saved experiments. Small; start here.",
                Schema.Object(new JsonObject { ["character"] = Character() }, "character"), true, Sync(o.InspectCharacter)),
            new("list_abilities", "List abilities",
                "The character's moves (abilities) with name, id, category, command, entry state, literal damage/startup and whether Play Ability can perform it. Paged.",
                Schema.Object(new JsonObject
                {
                    ["character"] = Character(),
                    ["category"] = Schema.Enum("Only this category (default all).", ObserveTools.Categories),
                    ["search"] = Schema.Str("Only moves whose name or id contains this text."),
                    ["playable_only"] = Schema.Bool("Only moves Play Ability can perform from neutral.", false),
                    ["limit"] = Schema.Int("Page size.", 1, 100, 40),
                    ["offset"] = Schema.Int("Skip this many (from nextOffset).", 0, 10000, 0)
                }, "character"), true, Sync(o.ListAbilities)),
            new("inspect_ability", "Inspect ability",
                "One move in depth: categories (with confidence), commands, entry state, literal damage/startup, how Play Ability would press it (or why not), candidate follow-ups (cancels, chains, links) and where its source is.",
                Schema.Object(new JsonObject { ["character"] = Character(), ["ability"] = Schema.Str("The move: name (e.g. \"Kung Fu Palm\"), id (ability:1000) or entry state number.") },
                    "character", "ability"), true, Sync(o.InspectAbility)),
            new("explain_state", "Explain state",
                "One state: its Statedef settings, which moves it belongs to, what enters it and what it leads to (with the triggers in plain words), its HitDefs and spawns.",
                Schema.Object(new JsonObject { ["character"] = Character(), ["state"] = Schema.Str("State number (200), id (state:200), or a state/move name.") },
                    "character", "state"), true, Sync(o.ExplainState)),
            new("get_source", "Get source lines",
                $"Source lines of ONE object (an ability, a state with its controllers, a command, a controller id …) or an explicit line range of one of the character's files. Never a whole file: at most {ObserveTools.MaxSourceLines} lines ({ObserveTools.DefaultSourceLines} by default); 'nextStartLine' continues.",
                Schema.Object(new JsonObject
                {
                    ["character"] = Character(),
                    ["object"] = Schema.Str("An object id (state:1000, ability:1000, cmd:QCF_x, …) or a move/state name."),
                    ["file"] = Schema.Str("Instead of object: one of the character's files (as get_source/inspect results name it), with start_line."),
                    ["start_line"] = Schema.Int("First line (with file).", 1, 10000000),
                    ["end_line"] = Schema.Int("Last line (with file).", 1, 10000000),
                    ["max_lines"] = Schema.Int("At most this many lines.", 1, ObserveTools.MaxSourceLines, ObserveTools.DefaultSourceLines)
                }, "character"), true, Sync(o.GetSource)),
            new("list_experiments", "List experiments",
                "Saved Sequence Lab variants and recent experiments for this character (on its current files unless all_file_versions).",
                Schema.Object(new JsonObject
                {
                    ["character"] = Character(),
                    ["sequence_id"] = Schema.Str("Only this saved variant."),
                    ["limit"] = Schema.Int("At most this many experiments.", 1, 30, 10),
                    ["all_file_versions"] = Schema.Bool("Also experiments that ran on earlier versions of the character's files.", false)
                }, "character"), true, Sync(o.ListExperiments)),
            new("get_experiment_results", "Get experiment results",
                "An experiment's verdict(s), statistics (success / true-combo / connection rates, failure reasons, damage, end distance), its exact scope, and the last trial step by step.",
                Schema.Object(new JsonObject
                {
                    ["experiment_id"] = Schema.Str("From test_sequence, run_experiment or list_experiments."),
                    ["include_trials"] = Schema.Bool("Also one row per trial.", false)
                }, "experiment_id"), true, Sync(o.GetExperimentResults)),
            new("get_runtime_trace", "Get runtime trace",
                "What happened frame by frame in one recorded run: state changes (named), inputs, life changes and driver steps; optional sampled frames. Slices only (max_events ≤ 200, max_samples ≤ 120).",
                Schema.Object(new JsonObject
                {
                    ["run_id"] = Schema.Str("A runId from play_ability, preview_state or an experiment trial (or the app's own playback history)."),
                    ["character"] = Schema.Str("The character, to name its states (optional)."),
                    ["from_frame"] = Schema.Int("Only from this frame.", 0, int.MaxValue),
                    ["to_frame"] = Schema.Int("Only up to this frame.", 0, int.MaxValue),
                    ["max_events"] = Schema.Int("At most this many events.", 1, 200, 60),
                    ["samples"] = Schema.Bool("Also sampled frames (positions, states, distance).", false),
                    ["max_samples"] = Schema.Int("At most this many samples.", 1, 120, 30)
                }, "run_id"), true, Sync(o.GetRuntimeTrace)),

            // ---------------------------------------------------------- Observe: behavior (Watch & Ask)
            new("list_behaviors", "List behaviors",
                "The character's recognisable combat behaviors (Knockdown Chase, Anti-Air, Punish, Pressure, Retreat, …) with their evidence level: Possible (static AI logic suggests it), Observed (a complete episode was recorded in a watched match on the current files and engine) or Confirmed Pattern (repeated across runs and setups).",
                Schema.Object(new JsonObject
                {
                    ["character"] = Character(),
                    ["include_not_seen"] = Schema.Bool("Also behaviors with no evidence at all.", true)
                }, "character"), true, Sync(b.ListBehaviors)),
            new("inspect_behavior", "Inspect behavior",
                "One behavior card: plain conditions, actions and outcomes, evidence level, recorded episodes (situation → action → outcome), opponents/setups, stale evidence, static AI rules consistent with it, the states and abilities involved, and its limitations.",
                Schema.Object(new JsonObject { ["character"] = Character(), ["behavior"] = Schema.Str("The behavior's name or id (e.g. \"Knockdown Chase\").") },
                    "character", "behavior"), true, Sync(b.InspectBehavior)),
            new("why_did_ai_do_this", "Why did the AI do this?",
                "For one moment of a watched run: the observed context, what the fighter did next, which behavior pattern it matches, and the static AI rules consistent with it. Never names the rule that fired — controller attribution is not available.",
                Schema.Object(new JsonObject
                {
                    ["character"] = Character(), ["run_id"] = Schema.Str("A watched run (watch_match / inspect_behavior)."), ["frame"] = Schema.Int("The moment (a frame of that run).", 0, int.MaxValue)
                }, "character", "run_id", "frame"), true, Sync(b.Why)),
            new("watch_match", "Watch a match",
                "Records a match with the character on the engine's AI against the playback dummy (both on AI), then recognises behavior episodes in it. Launches one IKEMEN window; writes only a recording (never the character). Long-running: returns a jobId.",
                Schema.Object(new JsonObject
                {
                    ["character"] = Character(),
                    ["seconds"] = Schema.Int("How long to watch.", BehaviorWatch.MinSeconds, BehaviorWatch.MaxSeconds, BehaviorWatch.DefaultSeconds),
                    ["opponent_ai_level"] = Schema.Int("The dummy's AI level.", 1, 8, BehaviorWatch.DefaultOpponentAi),
                    ["subject_ai_level"] = Schema.Int("The character's AI level.", 1, 8, BehaviorWatch.DefaultSubjectAi),
                    ["setup"] = Setup(), ["wait_seconds"] = Wait()
                }, "character"), false, b.WatchMatch),

            // ---------------------------------------------------------- Teach AI (Knockdown Chase v1): drafts and sandbox tests only - no approve, no deploy
            new("propose_behavior", "Propose a taught behavior",
                "Teach AI v1 (Knockdown Chase only): turns evidence into a Draft - from a Sequence Lab experiment (from_experiment), a recognised Knockdown Chase in a watched run (from_watch = \"<run id>@<start frame>\") or a follow-up move. Returns the plain card (WHEN / DO / THEN / GIVE UP IF), the follow-up's runtime proof, the ownership check against the existing AI and the generated rules. Writes only the draft into IKEMEN Lab's data; never character files.",
                Schema.Object(new JsonObject
                {
                    ["character"] = Character(),
                    ["from_experiment"] = Schema.Str("A Sequence Lab experiment id (opening -> chase -> (wait) -> follow-up)."),
                    ["from_watch"] = Schema.Str("\"<watched run id>@<Knockdown Chase start frame>\" (inspect_behavior lists episodes)."),
                    ["follow_up"] = Schema.Str("The follow-up move by name or id (overrides the evidence's)."),
                    ["when"] = Schema.Enum("Which knockdown starts it.", "falling", "lying", "both"),
                    ["chase_limit"] = Schema.Int("Only chase when the knockdown is at most this far (px).", KnockdownChaseSpec.MinDistance, KnockdownChaseSpec.MaxDistance),
                    ["attack_distance"] = Schema.Int("Start the follow-up within this distance (px).", KnockdownChaseSpec.MinDistance, KnockdownChaseSpec.MaxDistance),
                    ["attack_timing"] = Schema.Enum("While they are down, or as they get up (meaty).", "while_down", "as_they_get_up"),
                    ["lead_frames"] = Schema.Int("As they get up: how many frames before they can act.", 0, KnockdownChaseSpec.MaxLead),
                    ["give_up_frames"] = Schema.Int("Give up after this many frames of chasing.", KnockdownChaseSpec.MinGiveUp, KnockdownChaseSpec.MaxGiveUp),
                    ["frequency"] = Schema.Enum("How often a knockdown is taken (rolled once per knockdown).", "always", "often", "sometimes"),
                    ["fallback"] = Schema.Enum("If the opponent recovers first.", "stop", "block", "retreat"),
                    ["placement"] = Schema.Enum("Ownership: before the existing AI, after it, or before it while suppressing chosen rules on knockdowns.", "before", "after", "suppress"),
                    ["suppress"] = new JsonObject { ["type"] = "array", ["items"] = Schema.Str("A State -1 rule id or name."), ["description"] = "With placement = suppress: the existing rules that yield on knockdowns." },
                    ["second_opponent"] = Schema.Str("A second opponent (chars/ folder) for the test, e.g. one with another localcoord."),
                    ["trials"] = Schema.Int("Controlled runs per setup when tested.", 1, 5, 2)
                }, "character"), false, Sync(d.Propose)),
            new("inspect_behavior_draft", "Inspect a taught behavior",
                "Without behavior_id: the character's taught behaviors and their status (Draft, Testing, Ready for approval, Live, Retired). With it: the card, follow-up proof, ownership, generated rules, the latest test and the working-copy diff; include_code adds the generated CNS.",
                Schema.Object(new JsonObject
                {
                    ["character"] = Character(), ["behavior_id"] = Schema.Str("A taught behavior id (kc-...)."), ["include_code"] = Schema.Bool("Include the generated CNS and the diff.", false)
                }, "character"), true, Sync(d.Inspect)),
            new("test_behavior", "Test a taught behavior",
                "Runs the behavior's test suite in sandboxes on a disposable staging copy (never the working copy or the installed character): the proven opening knocks the opponent down, the AI takes over (idle opponent, active opponent, optional second opponent), a natural match, and regressions (the proven sequence, the follow-up). Reports chases, follow-ups, connections, fallbacks, give-ups and conflicts - scoped to these setups. Long-running: returns a jobId.",
                Schema.Object(new JsonObject
                {
                    ["character"] = Character(), ["behavior_id"] = Schema.Str("A taught behavior id (kc-...)."), ["setup"] = Setup(), ["wait_seconds"] = Wait()
                }, "character", "behavior_id"), false, d.Test),

            // ---------------------------------------------------------- Experiment
            new("play_ability", "Play ability",
                "Performs one move in IKEMEN from neutral through its own command (Play Ability) and reports Performed / Not performed / Could not be tested, plus what followed (contact, damage, reaction, recovery, the situation when you could act again). Long-running: returns a jobId.",
                Schema.Object(new JsonObject
                {
                    ["character"] = Character(), ["ability"] = Schema.Str("The move: name, id or entry state number."), ["setup"] = Setup(), ["wait_seconds"] = Wait()
                }, "character", "ability"), false, x.PlayAbility),
            new("preview_state", "Preview state (not proof)",
                "Forces a state with the engine's changeState for a look (State Preview). NEVER proof that the move can be performed: proof is always false. Long-running: returns a jobId.",
                Schema.Object(new JsonObject
                {
                    ["character"] = Character(), ["state"] = Schema.Str("State number, id or name."), ["ability"] = Schema.Str("Instead of state: a move, whose entry state is previewed."),
                    ["setup"] = Setup(), ["wait_seconds"] = Wait()
                }, "character"), false, x.PreviewState),
            new("test_sequence", "Test sequence",
                "Plays a Sequence Lab sequence (ability → walk/dash/jump/wait/chase → ability …) ×N, each trial replaying it from neutral, and reports the Sequence Lab verdict per trial (True combo / Connected sequence / Did not connect / Could not test) with per-step results. plan_only shows how it would be played without launching. Long-running: returns a jobId.",
                Schema.Object(new JsonObject
                {
                    ["character"] = Character(), ["steps"] = Steps(), ["spec"] = Spec(),
                    ["trials"] = Schema.Int($"How many times (×1, ×10, ×50). Above {ExperimentTools.TrialsNeedingConsent} needs accept_cost.", 1, ExperimentTools.MaxTrials, 1),
                    ["accept_cost"] = Schema.Bool("Confirms a long run (each trial launches IKEMEN once, ~20–40 s).", false),
                    ["name"] = Schema.Str("A name for this sequence in the experiment record."),
                    ["plan_only"] = Schema.Bool("Only show how each step would be played (nothing is launched).", false),
                    ["setup"] = Setup(), ["wait_seconds"] = Wait()
                }, "character"), false, x.TestSequence),
            new("create_experiment", "Create experiment (save variant)",
                "Saves a named sequence variant (the same saved variants the Sequence Lab shows) and shows how it would be played. Changing the steps of an existing variant (sequence_id) saves a new version. Launches nothing.",
                Schema.Object(new JsonObject
                {
                    ["character"] = Character(), ["name"] = Schema.Str("The variant's name, e.g. \"Palm, chase, wait 20, jab\"."), ["steps"] = Steps(), ["spec"] = Spec(),
                    ["sequence_id"] = Schema.Str("Update this saved variant instead of creating one."), ["setup"] = Setup()
                }, "character", "name"), false, Sync(x.CreateExperiment)),
            new("run_experiment", "Run experiment",
                "Runs a saved variant (its current version) ×N trials into the experiment store; the same verdicts and statistics as test_sequence. Long-running: returns a jobId.",
                Schema.Object(new JsonObject
                {
                    ["character"] = Character(), ["sequence_id"] = Schema.Str("The saved variant (create_experiment / list_experiments)."),
                    ["trials"] = Schema.Int($"How many times (×1, ×10, ×50). Above {ExperimentTools.TrialsNeedingConsent} needs accept_cost.", 1, ExperimentTools.MaxTrials, 1),
                    ["accept_cost"] = Schema.Bool("Confirms a long run.", false), ["setup"] = Setup(), ["wait_seconds"] = Wait()
                }, "character", "sequence_id"), false, x.RunExperiment),
            new("compare_experiments", "Compare experiments",
                "Side-by-side comparison of two experiments (success, true combos, connections, most common failure, damage, end distance), with a warning when they did not run under the same setup.",
                Schema.Object(new JsonObject { ["experiment_a"] = Schema.Str("First experiment id."), ["experiment_b"] = Schema.Str("Second experiment id.") },
                    "experiment_a", "experiment_b"), true, Sync(o.CompareExperiments)),
            new("get_job", "Get job",
                "A job's status (queued / running / completed / failed / cancelled), progress, who holds the engine if it waits, and its result when done. Without job_id: the recent jobs.",
                Schema.Object(new JsonObject { ["job_id"] = Schema.Str("The jobId."), ["wait_seconds"] = Wait() }), true, x.GetJob),
            new("cancel_job", "Cancel job",
                "Cancels a job: a queued one at once; a running one stops its engine and deletes its sandbox (finished experiment trials are kept). Too late once the result is saved.",
                Schema.Object(new JsonObject { ["job_id"] = Schema.Str("The jobId.") }, "job_id"), false, Sync(x.CancelJob))
        ];
    }
}
