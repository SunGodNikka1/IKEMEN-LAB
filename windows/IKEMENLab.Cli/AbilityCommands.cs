using System.Text.Encodings.Web;
using System.Text.Json;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.Cli;

/// <summary>
/// Phase 2: Play Ability (one ability from neutral through its own command, judged by the route verifier's step check) and State Preview (a forced
/// state, never proof). <c>ability-plan</c> is static; <c>runtime-ability</c> plays it in a disposable match, like <c>runtime-verify</c>.
/// </summary>
internal static class AbilityCommands
{
    public static bool Handles(string command) => command is "ability-plan" or "runtime-ability";

    public static int Run(string command, CliApp.Options opts, TextWriter output, TextWriter error)
    {
        if (opts.Positional.Count < 4) return CliApp.FailWith(output, error, 2, $"{command} needs a character and an ability (ability:200 or 200), or a state (state:200) with --preview.");
        var loaded = CliApp.Load(opts.Positional[2], opts, output, error, out var index);
        if (loaded != 0) return loaded;
        var located = CharacterLocator.Locate(opts.Positional[2], opts.Root)!;
        var graph = CandidateGraph.Build(index!);

        var target = opts.Positional[3].Trim();
        var preview = opts.Flag("preview") || target.StartsWith("state:", StringComparison.Ordinal);
        var abilityId = target.StartsWith("ability:", StringComparison.Ordinal) ? target : target.StartsWith("state:", StringComparison.Ordinal) ? null : "ability:" + target;
        var approach = int.TryParse(opts.Get("approach"), out var a) && a > 0 ? a : PlaybackPreflight.DefaultApproachDistance;

        AbilityPathResult? path = null;
        string? stateId = null;
        InputPlan? plan;
        string? refused;
        if (preview)
        {
            stateId = target.StartsWith("state:", StringComparison.Ordinal) ? target : index!.Get(abilityId!)?.Prop("entryState");
            if (stateId is null) return CliApp.FailWith(output, error, 3, $"{target} is not an ability with an entry state.");
            var planned = StatePreview.Plan(index!, stateId, approach, AbilityPlayback.TailFramesFor(StatePreview.AnimTicks(index!, stateId)));
            plan = planned.Plan;
            refused = planned.RefusedReason;
        }
        else
        {
            path = AbilityPlayback.Resolve(graph, abilityId!);
            refused = path.Refused;
            plan = path.Path is { } p ? InputPlanner.Plan(graph, p.Route, AbilityPlayback.PlanOptionsFor(graph, p.Route, approach)).Plan : null;
        }

        if (command == "ability-plan")
        {
            output.WriteLine(PlanJson(index!, abilityId, stateId, preview, path, plan, refused));
            return plan is null ? 3 : 0;
        }

        if (plan is null) return CliApp.FailWith(output, error, 3, (preview ? "This state cannot be previewed: " : "This ability cannot be played: ") + refused);
        return Live(opts, located, plan, abilityId, preview, output, error);
    }

    private static int Live(CliApp.Options opts, CharacterLocator.Located located, InputPlan plan, string? abilityId, bool preview, TextWriter output, TextWriter error)
    {
        var dummy = opts.Get("dummy");
        var stage = opts.Get("stage");
        if (opts.Root is null || dummy is null || stage is null)
            return CliApp.FailWith(output, error, 2, "runtime-ability needs --root, --dummy and --stage.");
        var dummyLoc = CharacterLocator.Locate(Path.Combine(opts.Root, "chars", dummy), opts.Root);
        if (dummyLoc is null) return CliApp.FailWith(output, error, 3, "Could not find the dummy character DEF under chars/.");
        var adapter = opts.Get("adapter");
        if (adapter is not null && !File.Exists(adapter)) return CliApp.FailWith(output, error, 3, "--adapter must name an existing Lua file.");
        var subjectFolder = located.Entry.FolderPath.StartsWith("chars/", StringComparison.OrdinalIgnoreCase) ? located.Entry.FolderPath["chars/".Length..] : located.Entry.Id;
        var seconds = int.TryParse(opts.Get("timeout"), out var t) && t > 0 ? t : 120;
        var request = new VerifyRunRequest(
            new SandboxRequest(located.Root, subjectFolder, located.Entry.DefPath["chars/".Length..], dummy, dummyLoc.Entry.DefPath["chars/".Length..], stage,
                plan.MaxFrames, opts.Get("out"), AdapterPath: adapter, EngineExePath: opts.Get("engine"), EngineRuntimeDlls: opts.Get("engine-dlls")),
            TimeSpan.FromSeconds(seconds), opts.Flag("keep"));
        try
        {
            var run = VerifyRunner.Execute(plan, request, new ProcessEngineRunner());
            if (run.SandboxPath is { } kept) error.WriteLine($"Sandbox kept at {kept} (runtime-clean deletes it). Trace: {run.TracePath}");
            if (preview)
            {
                var report = StatePreview.Read(plan, run.Log) with { RunNotes = run.Notes };
                output.WriteLine(StatePreview.ToJson(report));
                return report.Status switch { PreviewStatus.Forced or PreviewStatus.ForcedNotSeen => 0, PreviewStatus.Refused => 4, _ => 5 };
            }

            var entry = RouteVerifier.Verify(plan, run.Log);
            var ability = AbilityVerifier.Judge(abilityId!, plan, run.Log, entry with { Notes = entry.Notes.Concat(run.Notes).ToList() });
            output.WriteLine(AbilityVerifier.ToJson(ability));
            return ability.Status switch { AbilityStatus.Performed => 0, AbilityStatus.NotPerformed => 4, _ => 5 };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return CliApp.FailWith(output, error, 3, ex.Message);
        }
    }

    private static string PlanJson(SemanticIndex index, string? abilityId, string? stateId, bool preview, AbilityPathResult? path, InputPlan? plan, string? refused)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            void S(string n, string? v) { if (v is null) w.WriteNull(n); else w.WriteString(n, v); }
            w.WriteStartObject();
            w.WriteString("schema", "ikemenlab.xray.ability-plan/1");
            w.WriteString("mode", preview ? "preview" : "ability");
            if (preview) w.WriteBoolean("proof", false);
            S("ability", abilityId);
            if (abilityId is not null && index.Get(abilityId) is not null) w.WriteString("name", index.NameOf(abilityId));
            var entry = path?.Path?.EntryStateId ?? stateId ?? (abilityId is null ? null : index.Get(abilityId)?.Prop("entryState"));
            S("entryState", entry);
            if (entry is not null && index.Get(entry) is not null) w.WriteString("entryStateName", index.NameOf(entry));
            if (path?.Path is { } p)
            {
                w.WriteString("edge", p.Edge.Id);
                w.WriteString("controller", p.Edge.ControllerId);
                S("command", p.Command);
                w.WriteString("confidence", p.Edge.Confidence.ToString());
                w.WritePropertyName("alternatives");
                w.WriteStartArray();
                foreach (var x in p.Alternatives) w.WriteStringValue(x);
                w.WriteEndArray();
                w.WritePropertyName("warnings");
                w.WriteStartArray();
                foreach (var x in p.Warnings) w.WriteStringValue(x);
                w.WriteEndArray();
            }

            S("refused", refused);
            if (path is not null) w.WriteBoolean("canPreview", path.CanPreview);
            if (plan is null) w.WriteNull("plan");
            else
            {
                w.WritePropertyName("plan");
                w.WriteRawValue(InputPlanner.ToJson(plan));
            }

            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
