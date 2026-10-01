using System.Text.Encodings.Web;
using System.Text.Json;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Runtime;

namespace IKEMENLab.Cli;

/// <summary>The runtime compatibility spike: prepare a disposable sandbox, then read the JSONL trace back and link it to the static index.</summary>
internal static class RuntimeCommands
{
    private static readonly JsonWriterOptions Json = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static int Run(string command, CliApp.Options opts, TextWriter output, TextWriter error)
    {
        switch (command)
        {
            case "runtime-prepare": return Prepare(opts, output, error);
            case "runtime-report": return Report(opts, output, error);
            case "runtime-clean":
            {
                var path = opts.Positional.Count > 2 ? opts.Positional[2] : null;
                if (path is null) return CliApp.FailWith(output, error, 2, "runtime-clean needs a sandbox folder.");
                return RuntimeSandbox.Delete(Path.GetFullPath(path))
                    ? 0
                    : CliApp.FailWith(output, error, 3, "Not deleted: the folder is missing or is not an IKEMEN Lab runtime sandbox." + (RuntimeSandbox.LastFailure is { } why ? $" ({why})" : ""));
            }

            default:
                return CliApp.FailWith(output, error, 2, $"Unknown command '{command}'.");
        }
    }

    private static int Prepare(CliApp.Options opts, TextWriter output, TextWriter error)
    {
        var root = opts.Root;
        var subject = opts.Get("subject");
        var dummy = opts.Get("dummy");
        var stage = opts.Get("stage");
        if (root is null || subject is null || dummy is null || stage is null)
            return CliApp.FailWith(output, error, 2, "runtime-prepare needs --root, --subject, --dummy and --stage.");

        var frames = int.TryParse(opts.Get("frames"), out var f) && f > 0 ? f : 900;
        var subjectLoc = CharacterLocator.Locate(Path.Combine(root, "chars", subject), root);
        var dummyLoc = CharacterLocator.Locate(Path.Combine(root, "chars", dummy), root);
        if (subjectLoc is null || dummyLoc is null)
            return CliApp.FailWith(output, error, 3, "Could not find the subject or dummy character DEF under chars/.");

        RuntimeSandbox sandbox;
        try
        {
            sandbox = RuntimeSandbox.Create(new SandboxRequest(
                root, subject, subjectLoc.Entry.DefPath["chars/".Length..], dummy, dummyLoc.Entry.DefPath["chars/".Length..], stage, frames, opts.Get("out")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return CliApp.FailWith(output, error, 3, ex.Message);
        }

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Json))
        {
            w.WriteStartObject();
            w.WriteString("schema", SemanticIndex.SchemaVersion);
            w.WriteString("sandbox", sandbox.Root);
            w.WriteString("exe", sandbox.ExePath);
            w.WriteString("trace", sandbox.TracePath);
            w.WritePropertyName("arguments");
            w.WriteStartArray();
            foreach (var a in sandbox.Arguments) w.WriteStringValue(a);
            w.WriteEndArray();
            w.WritePropertyName("notes");
            w.WriteStartArray();
            foreach (var n in sandbox.Notes) w.WriteStringValue(n);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        output.WriteLine(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
        return 0; // the sandbox stays on disk until runtime-clean
    }

    private static int Report(CliApp.Options opts, TextWriter output, TextWriter error)
    {
        if (opts.Positional.Count < 3) return CliApp.FailWith(output, error, 2, "runtime-report needs a character.");
        var tracePath = opts.Get("trace");
        if (tracePath is null || !File.Exists(tracePath)) return CliApp.FailWith(output, error, 3, "--trace must name an existing JSONL file.");
        var located = CharacterLocator.Locate(opts.Positional[2], opts.Root);
        if (located is null) return CliApp.FailWith(output, error, 3, $"Could not find a character DEF at '{opts.Positional[2]}'.");

        var index = CharacterSemanticIndexer.Build(located.Root, located.Entry);
        var log = TraceReader.ReadFile(tracePath);
        var evidence = RuntimeLink.Associate(index, log);

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Json))
        {
            w.WriteStartObject();
            w.WriteString("schema", SemanticIndex.SchemaVersion);
            w.WriteString("character", index.CharacterId);
            w.WriteBoolean("validTrace", log.Frames.Any() && log.Meta is not null);
            w.WriteBoolean("associatedAtLeastOneState", evidence.Resolved.Any());
            w.WriteNumber("lines", log.LineCount);
            w.WriteNumber("frames", log.Frames.Count());
            w.WriteNumber("stateChanges", log.Events.OfType<StateChangeEvent>().Count());
            w.WriteNumber("lifeChanges", log.Events.OfType<LifeChangeEvent>().Count());

            w.WritePropertyName("meta");
            if (log.Meta is null) w.WriteNullValue();
            else
            {
                w.WriteStartObject();
                w.WriteString("engineVersion", log.Meta.EngineVersion);
                w.WriteString("engineSha256", log.Meta.EngineSha256);
                w.WriteString("engineExecutable", log.Meta.EngineExecutable);
                w.WriteString("engineSource", log.Meta.EngineSource);
                w.WriteString("planFingerprint", log.Meta.PlanFingerprint);
                w.WriteString("probeVersion", log.Meta.ProbeVersion);
                w.WriteString("platform", log.Meta.Platform);
                w.WritePropertyName("hooksRegistered");
                w.WriteStartArray();
                foreach (var h in log.Meta.HooksRegistered) w.WriteStringValue(h);
                w.WriteEndArray();
                w.WritePropertyName("supported");
                w.WriteStartArray();
                foreach (var kv in log.Meta.Capabilities.Where(c => c.Value).OrderBy(c => c.Key, StringComparer.Ordinal)) w.WriteStringValue(kv.Key);
                w.WriteEndArray();
                w.WritePropertyName("unsupported");
                w.WriteStartArray();
                foreach (var kv in log.Meta.Capabilities.Where(c => !c.Value).OrderBy(c => c.Key, StringComparer.Ordinal)) w.WriteStringValue(kv.Key);
                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WritePropertyName("states");
            w.WriteStartArray();
            foreach (var s in evidence.States)
            {
                w.WriteStartObject();
                w.WriteNumber("player", s.Player);
                w.WriteNumber("stateNo", s.StateNo);
                if (s.ObjectId is null) w.WriteNull("object"); else w.WriteString("object", s.ObjectId);
                w.WriteNumber("firstFrame", s.FirstFrame);
                w.WriteNumber("frames", s.Frames);
                w.WriteBoolean("commonState", s.IsCommonState);
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WritePropertyName("animations");
            w.WriteStartArray();
            foreach (var a in evidence.Animations)
            {
                w.WriteStartObject();
                w.WriteNumber("anim", a.AnimNo);
                if (a.ObjectId is null) w.WriteNull("object"); else w.WriteString("object", a.ObjectId);
                w.WriteNumber("frames", a.Frames);
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WritePropertyName("issues");
            w.WriteStartArray();
            foreach (var i in log.Issues)
            {
                w.WriteStartObject();
                w.WriteNumber("line", i.Line);
                w.WriteString("message", i.Message);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        output.WriteLine(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
        return 0;
    }
}
