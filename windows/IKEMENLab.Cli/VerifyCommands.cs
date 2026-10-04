using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.Cli;

/// <summary>Milestone 3: plan a route's inputs, judge a recorded trace against it, run it in a disposable match, rank subjects.</summary>
internal static class VerifyCommands
{
    public static bool Handles(string command) => command is "verify-plan" or "verify-trace" or "runtime-verify" or "rank-subjects";

    public static int Run(string command, CliApp.Options opts, TextWriter output, TextWriter error) => command switch
    {
        "rank-subjects" => Rank(opts, output, error),
        _ => RunRoute(command, opts, output, error)
    };

    private static int RunRoute(string command, CliApp.Options opts, TextWriter output, TextWriter error)
    {
        if (opts.Positional.Count < 3) return CliApp.FailWith(output, error, 2, $"{command} needs a character.");
        var located = CharacterLocator.Locate(opts.Positional[2], opts.Root);
        if (located is null) return CliApp.FailWith(output, error, 3, $"Could not find a character DEF at '{opts.Positional[2]}'.");
        var index = CharacterSemanticIndexer.Build(located.Root, located.Entry);
        var graph = CandidateGraph.Build(index);

        var comboOptions = CliApp.BuildComboOptions(opts, out var problem);
        if (comboOptions is null) return CliApp.FailWith(output, error, 2, problem!);
        var routes = ComboSearch.Find(graph, comboOptions).Routes;
        var n = int.TryParse(opts.Get("route"), out var r) && r > 0 ? r : 1;
        if (routes.Count < n) return CliApp.FailWith(output, error, 3, $"The search found {routes.Count} route(s); --route {n} does not exist.");
        var route = routes[n - 1];

        var planOptions = new PlanOptions
        {
            ApproachDistance = int.TryParse(opts.Get("approach"), out var a) && a > 0 ? a : 60
        };
        var planned = InputPlanner.Plan(graph, route, planOptions);
        if (planned.Plan is null) return CliApp.FailWith(output, error, 3, "This route cannot be scripted: " + planned.RefusedReason);

        switch (command)
        {
            case "verify-plan":
                output.WriteLine(opts.Flag("lua") ? InputPlanner.ToLua(planned.Plan) : InputPlanner.ToJson(planned.Plan));
                return 0;

            case "verify-trace":
            {
                var tracePath = opts.Get("trace");
                if (tracePath is null || !File.Exists(tracePath)) return CliApp.FailWith(output, error, 3, "--trace must name an existing JSONL file.");
                var report = RouteVerifier.Verify(planned.Plan, TraceReader.ReadFile(tracePath));
                output.WriteLine(RouteVerifier.ToJson(report));
                return ExitFor(report);
            }

            default:
                return Live(opts, located, graph, route, planOptions, output, error);
        }
    }

    private static int Live(CliApp.Options opts, CharacterLocator.Located located, CandidateGraph graph, ComboRoute route, PlanOptions planOptions,
        TextWriter output, TextWriter error)
    {
        var dummy = opts.Get("dummy");
        var stage = opts.Get("stage");
        if (opts.Root is null || dummy is null || stage is null)
            return CliApp.FailWith(output, error, 2, "runtime-verify needs --root, --dummy and --stage.");
        var dummyLoc = CharacterLocator.Locate(Path.Combine(opts.Root, "chars", dummy), opts.Root);
        if (dummyLoc is null) return CliApp.FailWith(output, error, 3, "Could not find the dummy character DEF under chars/.");
        var adapter = opts.Get("adapter");
        if (adapter is not null && !File.Exists(adapter)) return CliApp.FailWith(output, error, 3, "--adapter must name an existing Lua file.");
        var subjectFolder = located.Entry.FolderPath.StartsWith("chars/", StringComparison.OrdinalIgnoreCase) ? located.Entry.FolderPath["chars/".Length..] : located.Entry.Id;

        var frames = int.TryParse(opts.Get("frames"), out var f) && f > 0 ? f : planOptions.MaxFrames;
        var seconds = int.TryParse(opts.Get("timeout"), out var t) && t > 0 ? t : 120;
        var request = new VerifyRunRequest(
            new SandboxRequest(located.Root, subjectFolder, located.Entry.DefPath["chars/".Length..], dummy, dummyLoc.Entry.DefPath["chars/".Length..], stage,
                frames, opts.Get("out"), AdapterPath: adapter, EngineExePath: opts.Get("engine"), EngineRuntimeDlls: opts.Get("engine-dlls")),
            TimeSpan.FromSeconds(seconds), opts.Flag("keep"));
        try
        {
            using var lease = CliApp.EngineLease("runtime-verify " + route.Key, out var busy);
            if (lease is null) return CliApp.FailWith(output, error, 3, busy!);
            var result = VerifyRunner.Run(graph, route, request, new ProcessEngineRunner(), planOptions);
            output.WriteLine(RouteVerifier.ToJson(result.Report));
            if (result.SandboxPath is { } kept) error.WriteLine($"Sandbox kept at {kept} (runtime-clean deletes it). Trace: {result.TracePath}");
            return ExitFor(result.Report);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return CliApp.FailWith(output, error, 3, ex.Message);
        }
    }

    /// <summary>0 verified, 4 failed, 5 inconclusive: a script can tell "the combo does not work" from "we could not test it".</summary>
    private static int ExitFor(VerificationReport r) => r.Status switch { VerifyStatus.Verified => 0, VerifyStatus.Failed => 4, _ => 5 };

    private static int Rank(CliApp.Options opts, TextWriter output, TextWriter error)
    {
        if (opts.Root is null) return CliApp.FailWith(output, error, 2, "rank-subjects needs --root.");
        var chars = Path.Combine(opts.Root, "chars");
        if (!Directory.Exists(chars)) return CliApp.FailWith(output, error, 3, "No chars/ folder under --root.");
        var limit = int.TryParse(opts.Get("limit"), out var l) && l > 0 ? l : 15;

        var scores = new List<SubjectScore>();
        var skipped = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(chars).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var located = CharacterLocator.Locate(dir, opts.Root);
                if (located is null) { skipped.Add(Path.GetFileName(dir) + ": no DEF"); continue; }
                var index = CharacterSemanticIndexer.Build(located.Root, located.Entry);
                scores.Add(SubjectRanker.Score(CandidateGraph.Build(index)) with { Character = located.Entry.Id });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                skipped.Add(Path.GetFileName(dir) + ": " + ex.Message);
            }
        }

        using var ms = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("schema", "ikemenlab.xray.subjects/1");
            w.WriteNumber("characters", scores.Count);
            w.WritePropertyName("ranking");
            w.WriteStartArray();
            foreach (var s in scores.OrderByDescending(s => s.Score).ThenBy(s => s.Character, StringComparer.OrdinalIgnoreCase).Take(limit))
            {
                w.WriteStartObject();
                w.WriteString("character", s.Character);
                w.WriteNumber("score", s.Score);
                w.WriteNumber("edges", s.Edges);
                w.WriteNumber("cleanEdges", s.CleanEdges);
                w.WriteNumber("cleanHitConfirmCancels", s.CleanHitConfirmCancels);
                w.WriteNumber("scriptableRoutes", s.ScriptableRoutes);
                w.WriteNumber("longestScriptableRoute", s.LongestScriptableRoute);
                w.WriteNumber("dynamicTargets", s.DynamicTargets);
                w.WriteNumber("aiEntryPoints", s.AiEntryPoints);
                w.WriteNumber("literalCommandStarts", s.LiteralCommandStarts);
                w.WriteNumber("linkEdges", s.LinkEdges);
                w.WriteBoolean("hasSuper", s.HasSuper);
                if (s.BestRoute is { } br) w.WriteString("bestRoute", string.Join(" > ", br.States)); else w.WriteNull("bestRoute");
                w.WritePropertyName("reasons");
                w.WriteStartArray();
                foreach (var x in s.Reasons) w.WriteStringValue(x);
                w.WriteEndArray();
                w.WritePropertyName("concerns");
                w.WriteStartArray();
                foreach (var x in s.Concerns) w.WriteStringValue(x);
                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WritePropertyName("skipped");
            w.WriteStartArray();
            foreach (var x in skipped) w.WriteStringValue(x);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        output.WriteLine(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
        return 0;
    }
}
