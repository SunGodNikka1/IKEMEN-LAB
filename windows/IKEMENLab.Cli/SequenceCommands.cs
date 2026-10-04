using System.Text.Encodings.Web;
using System.Text.Json;
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Sequences;
using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.Cli;

/// <summary>
/// Phase 3: Sequence Lab from the command line. <c>sequence-plan</c> shows how a sequence would be played (or why not) without launching anything;
/// <c>runtime-sequence</c> runs it N times in disposable matches. Trials go to the isolated experiment store, never the playback history.
/// </summary>
internal static class SequenceCommands
{
    public static bool Handles(string command) => command is "sequence-plan" or "runtime-sequence";

    public static int Run(string command, CliApp.Options opts, TextWriter output, TextWriter error)
    {
        if (opts.Positional.Count < 4) return CliApp.FailWith(output, error, 2, $"{command} needs a character and a sequence, e.g. \"1000 > chase:35 > 200\".");
        var loaded = CliApp.Load(opts.Positional[2], opts, output, error, out var index);
        if (loaded != 0) return loaded;
        var located = CharacterLocator.Locate(opts.Positional[2], opts.Root)!;
        var graph = CandidateGraph.Build(index!);
        IReadOnlyList<SequenceAction> actions;
        try { actions = SequenceSpec.Parse(opts.Positional[3]); }
        catch (FormatException ex) { return CliApp.FailWith(output, error, 2, ex.Message); }

        var approach = int.TryParse(opts.Get("approach"), out var a) && a > 0 ? a : PlaybackPreflight.DefaultApproachDistance;
        var sequence = new Sequence(opts.Get("id") ?? "cli", opts.Get("name") ?? "CLI sequence", 1, actions);
        var planned = SequencePlanner.Plan(graph, sequence, approach);
        if (command == "sequence-plan")
        {
            output.WriteLine(PlanJson(index!, sequence, planned));
            return planned.Plan is null ? 3 : 0;
        }

        if (planned.Plan is null) return CliApp.FailWith(output, error, 3, $"This sequence cannot be played: {planned.Refused}");
        if (opts.Root is null || opts.Get("dummy") is not { } dummy || opts.Get("stage") is not { } stage)
            return CliApp.FailWith(output, error, 2, "runtime-sequence needs --root, --dummy and --stage.");
        var trials = int.TryParse(opts.Get("trials"), out var t) && t > 0 ? Math.Min(t, 50) : 1;
        var subjectFolder = located.Entry.FolderPath.StartsWith("chars/", StringComparison.OrdinalIgnoreCase) ? located.Entry.FolderPath["chars/".Length..] : located.Entry.Id;
        var setup = PlaybackPreflight.Check(opts.Root, subjectFolder, new AppSettings
        {
            XRayEnginePath = opts.Get("engine") ?? Path.Combine(opts.Root, "Ikemen_GO.exe"), XRayEngineDlls = opts.Get("engine-dlls"),
            XRayDummy = dummy, XRayStage = stage, XRayApproachDistance = approach.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        if (!setup.Ready) return CliApp.FailWith(output, error, 3, "Playback is not set up: " + string.Join(" ", setup.Issues));

        var store = new ExperimentStore(opts.Get("store"));
        var playback = new ComboPlaybackService(new ProcessEngineRunner(), Path.Combine(store.Root, "_unused-playback"), opts.Get("out"));
        var labels = planned.Steps.Select(s => s.Label).ToList();
        var request = new SequenceRunRequest(opts.Root, subjectFolder, located.Entry.DefPath["chars/".Length..], planned.Plan, labels, setup, SequenceLabels.Chain(actions, index));
        var scope = new ExperimentScope(index!.CharacterId, ExperimentScope.HashOf(index), null, setup.Dummy, setup.Stage, setup.ApproachDistance, sequence.Id, sequence.Version,
            sequence.Name, InputPlanner.Fingerprint(planned.Plan));
        error.WriteLine(SequenceExperimentRunner.CostText(trials, null));
        try
        {
            var outcome = SequenceExperimentRunner.Run(playback, store, request, scope, request.Summary, trials, new PlaybackCancellation(),
                p => { if (p.StartsWith("trial:", StringComparison.Ordinal)) error.WriteLine("  " + p); });
            using var doc = JsonDocument.Parse(ExperimentStore.ToJson(outcome.Summary));
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                foreach (var p in doc.RootElement.EnumerateObject()) p.WriteTo(w);
                w.WriteString("directory", outcome.Summary.Directory);
                w.WriteString("headline", ExperimentText.Headline(outcome.Summary, outcome.LastTrial?.Report));
                if (outcome.LastTrial is { } last)
                {
                    w.WritePropertyName("lastTrial");
                    JsonDocument.Parse(SequenceVerifier.ToJson(last.Report)).RootElement.WriteTo(w);
                }

                w.WriteEndObject();
            }

            output.WriteLine(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
            var s = outcome.Summary;
            return s.Untestable > 0 ? 5 : s.Successes == s.Completed ? 0 : 4;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return CliApp.FailWith(output, error, 3, ex.Message);
        }
    }

    private static string PlanJson(SemanticIndex index, Sequence sequence, SequencePlanResult planned)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("schema", "ikemenlab.xray.sequence-plan/1");
            w.WriteString("sequence", SequenceSpec.Format(sequence.Actions));
            w.WriteString("chain", SequenceLabels.Chain(sequence.Actions, index));
            w.WritePropertyName("steps");
            w.WriteStartArray();
            foreach (var s in planned.Steps)
            {
                w.WriteStartObject();
                w.WriteNumber("index", s.Index);
                w.WriteString("kind", s.Action.Kind.ToString());
                w.WriteString("label", s.Label);
                w.WriteString("how", s.How);
                if (s.Edge is { } e) { w.WriteString("edge", e.Id); w.WriteString("controller", e.ControllerId); }
                if (s.StateId is { } st) w.WriteString("state", st);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WritePropertyName("warnings");
            w.WriteStartArray();
            foreach (var x in planned.Warnings) w.WriteStringValue(x);
            w.WriteEndArray();
            if (planned.Refused is null) w.WriteNull("refused"); else w.WriteString("refused", planned.Refused);
            if (planned.RefusedStep is { } rs) w.WriteNumber("refusedStep", rs); else w.WriteNull("refusedStep");
            if (planned.Plan is null) w.WriteNull("plan");
            else
            {
                w.WritePropertyName("plan");
                w.WriteRawValue(InputPlanner.ToJson(planned.Plan));
            }

            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
