using System.Text.Json;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Query;

namespace IKEMENLab.Cli;

/// <summary>
/// <c>ikemenlab xray …</c>: deterministic, read-only questions about a character, answered from the Character Semantic Index as
/// versioned JSON (<c>ikemenlab.xray/1</c>). Exit codes: 0 ok, 2 usage, 3 not found / unreadable.
/// </summary>
public static class CliApp
{
    private const string Usage = """
        usage: ikemenlab xray <command> <character> [args] [--root <ikemen root>] [--text] [--no-common]

          index        <character>                 the whole semantic index (objects, relationships, diagnostics)
          explain      <character> <id>            state:200, state:200/ctrl:1, cmd:x, var:var:20, helper:340, ability:200, anim:200 …
          var          <character> <var(20)|fvar:3>  every read/write/reset of one variable
          helper       <character> <340|helper:340>  a helper's spawns, states, HitDefs and variables
          ai-entry     <character>                 controllers that read AILevel
          find         <character> <throws|projectiles|counters|supers|specials|normals|mobility|summons|victim-states>
          transitions  <character> <200|state:200> ChangeState edges leaving a state, with their gates
          search       <character> <text>
          rules                                    the evidence rules behind every confidence level
          readiness    <character>                 how much the candidate graph can (and cannot) say about this character
          candidates   <character> [--from <state|neutral>]   candidate combo edges with gates, evidence and unmodelled conditions
          combos       <character> [--from <state>] [--max-moves N] [--meter N] [--max-frames N] [--top K]
                       [--strategy dfs|beam|best] [--beam N] [--repeats N] [--max-unmodelled N]
                       [--all-cancels] [--no-chains] [--allow-links]
                       deterministic search; every route is a CANDIDATE (never verified), with its weakest confidence and unmodelled conditions

          runtime-prepare --root R --subject <folder> --dummy <folder> --stage <stages/x.def> [--frames N]
                       builds a disposable sandbox with the Lua probe (never touches R) and prints how to launch it
          runtime-report  <character> --trace <file.jsonl>   parses a trace and links its states to the static index
          runtime-clean   <sandbox dir>            deletes a sandbox (only folders carrying the sandbox marker)

        <character> is a character folder or its .def. Confidence: StaticProven (literal in the files),
        Inferred (heuristic), Unknown. RuntimeVerified is reserved for a later milestone.
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            var opts = Options.Parse(args);
            if (opts.Positional.Count < 2 || !opts.Positional[0].Equals("xray", StringComparison.OrdinalIgnoreCase))
            {
                error.WriteLine(Usage);
                return 2;
            }

            var command = opts.Positional[1].ToLowerInvariant();
            if (command == "rules") { output.WriteLine(XRayJson.Rules()); return 0; }
            if (command.StartsWith("runtime-", StringComparison.Ordinal)) return RuntimeCommands.Run(command, opts, output, error);

            if (opts.Positional.Count < 3) { error.WriteLine(Usage); return 2; }
            var loaded = Load(opts.Positional[2], opts, output, error, out var index);
            if (loaded != 0) return loaded;

            var rest = opts.Positional.Skip(3).ToList();
            switch (command)
            {
                case "index":
                    output.WriteLine(XRayJson.Index(index!));
                    return 0;

                case "explain":
                {
                    if (rest.Count < 1) return Fail(output, error, 2, "explain needs an object id.");
                    var explanation = index!.Explain(rest[0]);
                    if (explanation is null) return Fail(output, error, 3, $"No object '{rest[0]}'. Try: search <character> <text>.");
                    output.WriteLine(opts.Text ? ExplainText(index, explanation) : XRayJson.ExplanationJson(index, explanation));
                    return 0;
                }

                case "var":
                {
                    if (rest.Count < 1) return Fail(output, error, 2, "var needs a variable such as var(20) or fvar:3.");
                    var id = index!.ResolveVariableId(rest[0]);
                    if (id is null) return Fail(output, error, 3, $"No variable '{rest[0]}' is used by this character.");
                    output.WriteLine(XRayJson.VarUsage(index, id, index.VarUsage(id)));
                    return 0;
                }

                case "helper":
                {
                    if (rest.Count < 1) return Fail(output, error, 2, "helper needs an id such as 340.");
                    var id = rest[0].StartsWith("helper:", StringComparison.Ordinal) ? rest[0] : "helper:" + rest[0];
                    var explanation = index!.Explain(id);
                    if (explanation is null) return Fail(output, error, 3, $"No helper '{rest[0]}'.");
                    output.WriteLine(opts.Text ? ExplainText(index, explanation) : XRayJson.ExplanationJson(index, explanation));
                    return 0;
                }

                case "ai-entry":
                    output.WriteLine(XRayJson.ObjectList(index!, "ai-entry", index!.AiEntryPoints()));
                    return 0;

                case "find":
                {
                    if (rest.Count < 1) return Fail(output, error, 2, "find needs a category.");
                    var label = rest[0].ToLowerInvariant() switch
                    {
                        "throws" => "Throw", "projectiles" => "Projectile", "counters" => "Counter", "supers" => "Super",
                        "specials" => "Special", "normals" => "Normal", "mobility" => "Mobility", "summons" => "Summon",
                        "victim-states" => "Victim-state hit", "defensive" => "Defensive", "modes" => "Mode", _ => null
                    };
                    if (label is null) return Fail(output, error, 2, $"Unknown category '{rest[0]}'.");
                    output.WriteLine(XRayJson.ObjectList(index!, "find " + rest[0], index!.AbilitiesLabelled(label)));
                    return 0;
                }

                case "transitions":
                {
                    if (rest.Count < 1) return Fail(output, error, 2, "transitions needs a state number.");
                    var id = rest[0].StartsWith("state:", StringComparison.Ordinal) ? rest[0] : "state:" + rest[0];
                    if (index!.Get(id) is null) return Fail(output, error, 3, $"No state '{rest[0]}'.");
                    output.WriteLine(XRayJson.Transitions(index, id, index.TransitionsFrom(id)));
                    return 0;
                }

                case "readiness":
                    output.WriteLine(ComboJson.Readiness(CandidateGraph.Build(index!)));
                    return 0;

                case "candidates":
                {
                    var graph = CandidateGraph.Build(index!);
                    var from = opts.Get("from");
                    if (from is null) { output.WriteLine(ComboJson.Edges(graph, null, graph.Edges)); return 0; }
                    var node = CandidateGraph.ResolveState(index!, from);
                    if (node is null) return Fail(output, error, 3, $"No state '{from}'.");
                    output.WriteLine(ComboJson.Edges(graph, node, graph.From(node)));
                    return 0;
                }

                case "combos":
                {
                    var graph = CandidateGraph.Build(index!);
                    var built = BuildComboOptions(opts, out var problem);
                    if (built is null) return Fail(output, error, 2, problem!);
                    output.WriteLine(ComboJson.Search(graph, ComboSearch.Find(graph, built)));
                    return 0;
                }

                case "search":
                    if (rest.Count < 1) return Fail(output, error, 2, "search needs text.");
                    output.WriteLine(XRayJson.ObjectList(index!, "search " + rest[0], index!.Search(rest[0])));
                    return 0;

                default:
                    error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            return Fail(output, error, 3, ex.Message);
        }
    }

    private static ComboOptions? BuildComboOptions(Options opts, out string? problem)
    {
        string? error = null;
        int? Int(string name, int? fallback = null)
        {
            var raw = opts.Get(name);
            if (raw is null) return fallback;
            if (int.TryParse(raw, out var n) && n >= 0) return n;
            error = $"--{name} needs a non-negative whole number.";
            return null;
        }

        var maxMoves = Int("max-moves", 4);
        var meter = Int("meter", 0);
        var top = Int("top", 20);
        var repeats = Int("repeats", 1);
        var beam = Int("beam", 200);
        var maxFrames = Int("max-frames");
        var maxUnmodelled = Int("max-unmodelled");
        problem = error;
        if (problem is not null) return null;

        var strategy = (opts.Get("strategy") ?? "dfs").ToLowerInvariant() switch
        {
            "dfs" => ComboStrategy.Dfs,
            "beam" => ComboStrategy.Beam,
            "best" or "best-first" or "bestfirst" => ComboStrategy.BestFirst,
            _ => (ComboStrategy?)null
        };
        if (strategy is null) { problem = "--strategy must be dfs, beam or best."; return null; }

        return new ComboOptions
        {
            From = opts.Get("from"),
            MaxMoves = Math.Clamp(maxMoves!.Value, 1, 12),
            StartMeter = meter!.Value,
            MaxFrames = maxFrames,
            HitConfirmOnly = !opts.Flag("all-cancels"),
            AllowChains = !opts.Flag("no-chains"),
            AllowLinks = opts.Flag("allow-links"),
            MaxRepeats = Math.Max(1, repeats!.Value),
            Top = Math.Clamp(top!.Value, 1, 1000),
            Strategy = strategy.Value,
            BeamWidth = Math.Max(1, beam!.Value),
            MaxUnmodelledPerEdge = maxUnmodelled
        };
    }

    private static int Load(string target, Options opts, TextWriter output, TextWriter error, out SemanticIndex? index)
    {
        index = null;
        var resolved = CharacterLocator.Locate(target, opts.Root);
        if (resolved is null) return Fail(output, error, 3, $"Could not find a character DEF at '{target}'.");
        index = CharacterSemanticIndexer.Build(resolved.Root, resolved.Entry, new XRayOptions { IncludeCommonStates = !opts.NoCommon });
        if (index.Diagnostics.Any(d => d.Code == "def.unreadable")) return Fail(output, error, 3, $"The DEF '{target}' could not be read.");
        return 0;
    }

    private static int Fail(TextWriter output, TextWriter error, int code, string message)
    {
        error.WriteLine(message);
        output.WriteLine(JsonSerializer.Serialize(new { schema = SemanticIndex.SchemaVersion, error = message }));
        return code;
    }

    private static string ExplainText(SemanticIndex index, Explanation e)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{e.Subject.Id}  {e.Subject.Name}  [{e.Subject.Kind}]");
        foreach (var l in e.Subject.Labels) sb.AppendLine($"  label ({l.Confidence}): {l.Text}   <{l.RuleId}>");
        foreach (var s in e.Sections)
        {
            sb.AppendLine();
            sb.AppendLine(s.Title);
            foreach (var it in s.Items)
            {
                var where = it.Source is { } src && index.FileOf(src) is { } f ? $"  ({f.RelPath}:{src.StartLine})" : string.Empty;
                sb.AppendLine($"  [{it.Confidence?.ToString() ?? "-"}] {it.Name}  {it.Id}{(it.Note is null ? string.Empty : "  — " + it.Note)}{where}");
            }
        }

        return sb.ToString();
    }

    internal sealed record Options(List<string> Positional, string? Root, bool Text, bool NoCommon, Dictionary<string, string> Named, HashSet<string> Flags)
    {
        public string? Get(string name) => Named.TryGetValue(name, out var v) ? v : null;
        public bool Flag(string name) => Flags.Contains(name);

        public static Options Parse(string[] args)
        {
            var pos = new List<string>();
            var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? root = null;
            var text = false;
            var noCommon = false;
            var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--root" when i + 1 < args.Length: root = args[++i]; break;
                    case "--text": text = true; break;
                    case "--no-common": noCommon = true; break;
                    case "--all-cancels" or "--no-chains" or "--allow-links": flags.Add(args[i][2..]); break;
                    case var a when a.StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length:
                        named[a[2..]] = args[++i];
                        break;
                    default: pos.Add(args[i]); break;
                }
            }

            return new Options(pos, root, text, noCommon, named, flags);
        }
    }

    internal static int FailWith(TextWriter output, TextWriter error, int code, string message) => Fail(output, error, code, message);
}

/// <summary>Finds the IKEMEN root and character DEF for a folder or DEF path.</summary>
internal static class CharacterLocator
{
    public sealed record Located(string Root, CharacterEntry Entry);

    public static Located? Locate(string target, string? explicitRoot)
    {
        var full = Path.GetFullPath(target);
        string? def = null;
        if (File.Exists(full) && full.EndsWith(".def", StringComparison.OrdinalIgnoreCase)) def = full;
        else if (Directory.Exists(full))
        {
            var folder = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var named = Path.Combine(full, folder + ".def");
            def = File.Exists(named) ? named : Directory.EnumerateFiles(full, "*.def").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(p => File.ReadAllText(p).Contains("[Files]", StringComparison.OrdinalIgnoreCase));
        }

        if (def is null) return null;
        var charDir = Path.GetDirectoryName(def)!;
        var root = explicitRoot is not null ? Path.GetFullPath(explicitRoot) : FindRoot(charDir);
        var rel = Path.GetRelativePath(root, def).Replace('\\', '/');
        var folderRel = Path.GetRelativePath(root, charDir).Replace('\\', '/');
        var id = folderRel.StartsWith("chars/", StringComparison.OrdinalIgnoreCase) ? folderRel["chars/".Length..] : Path.GetFileName(charDir);
        var parsed = IKEMENLab.Core.Parsing.DefFileReader.ReadFileContent(def) is { } text ? IKEMENLab.Core.Parsing.DefParser.Parse(text) : null;
        var name = parsed?.Value("displayname", "info") ?? parsed?.Value("name", "info") ?? Path.GetFileNameWithoutExtension(def);
        return new Located(root, new CharacterEntry
        {
            Id = id, DisplayName = name, Name = parsed?.Value("name", "info") ?? name,
            Author = parsed?.Value("author", "info") ?? string.Empty, VersionDate = string.Empty, DefPath = rel, FolderPath = folderRel
        });
    }

    private static string FindRoot(string charDir)
    {
        for (var dir = new DirectoryInfo(charDir); dir?.Parent is not null; dir = dir.Parent)
            if (dir.Parent.Name.Equals("chars", StringComparison.OrdinalIgnoreCase) && dir.Parent.Parent is { } root)
                return root.FullName;
        return Path.GetDirectoryName(charDir) ?? charDir;
    }
}
