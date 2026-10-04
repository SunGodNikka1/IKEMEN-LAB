using System.Globalization;
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Names;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Sequences;
using IKEMENLab.Core.XRay.Verify;
using IKEMENLab.Core.XRay.Workbench;
using IKEMENLab.Mcp.Protocol;

namespace IKEMENLab.Mcp.Lab;

/// <summary>One character as the tools see it: the same semantic index, user names and candidate graph the X-Ray window builds.</summary>
public sealed class LoadedCharacter
{
    public required CharacterLocator.Located Located { get; init; }
    public required SemanticIndex Index { get; init; }
    public required CandidateGraph Graph { get; init; }
    public required CharacterNames Names { get; set; }
    public required string ContentHash { get; init; }
    /// <summary>The sizes and times of the files the index read: a change means the index is rebuilt.</summary>
    public required string FileStamp { get; init; }
    internal string NamesStamp { get; set; } = string.Empty;

    public string Root => Located.Root;
    public string Folder => Path.Combine(Located.Root, Located.Entry.FolderPath);
    public string SubjectFolder => Located.Entry.FolderPath.StartsWith("chars/", StringComparison.OrdinalIgnoreCase) ? Located.Entry.FolderPath["chars/".Length..] : Located.Entry.FolderPath;
    public string SubjectDef => Located.Entry.DefPath.StartsWith("chars/", StringComparison.OrdinalIgnoreCase) ? Located.Entry.DefPath["chars/".Length..] : Located.Entry.DefPath;
    public string DisplayName => Located.Entry.DisplayName;

    private readonly Dictionary<string, AbilityPathResult> _paths = new(StringComparer.Ordinal);

    /// <summary>How Play Ability would perform an ability (cached per index; the graph never changes for one load).</summary>
    public AbilityPathResult PathOf(string abilityId)
    {
        lock (_paths)
        {
            if (!_paths.TryGetValue(abilityId, out var p)) _paths[abilityId] = p = AbilityPlayback.Resolve(Graph, abilityId, new PlanOptions());
            return p;
        }
    }
}

/// <summary>Per-call playback overrides (dummy, stage, approach distance). The engine comes from the app's settings or the server's arguments.</summary>
public sealed record SetupOverride(string? Dummy, string? Stage, int? ApproachDistance);

/// <summary>
/// Everything the tools share: the app-owned stores (all under one data directory), the app's settings (read, never written), the IKEMEN root, the
/// MCP run store and the character cache. Character files are only ever read.
/// </summary>
public sealed class LabContext
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LoadedCharacter> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="runner">The engine runner (null = the real engine process).</param>
    /// <param name="broker">The one-engine broker the job queue leases from (null with a test runner = none; the real runner always uses the shared broker).</param>
    public LabContext(McpOptions options, IEngineRunner? runner = null, RuntimeBroker? broker = null, string? sandboxBase = null)
    {
        Options = options;
        DataDirectory = options.ResolvedDataDirectory;
        NameStore = new NameOverlayStore(Path.Combine(DataDirectory, "xray", "names"));
        Sequences = new SequenceStore(Path.Combine(DataDirectory, "xray", "sequences"));
        Experiments = new ExperimentStore(Path.Combine(DataDirectory, "xray-experiments"));
        AppPlaybackRoot = Path.Combine(DataDirectory, "xray-playback");
        SettingsPath = Path.Combine(DataDirectory, "settings.json");
        var real = runner is null;
        // MCP's own Play Ability / Preview State records: the same record format and service, in their own store with their own keep-newest limit,
        // so agent runs never evict the user's Play Combo / Play Ability history. The job queue holds the engine lease, so the service takes none.
        Runs = new ComboPlaybackService(runner ?? new ProcessEngineRunner(), Path.Combine(DataDirectory, "xray-mcp-runs"),
            sandboxBase ?? Path.Combine(DataDirectory, "runtime-sandboxes")) { Broker = null };
        Broker = broker ?? (real ? RuntimeBroker.Shared : null);
    }

    public McpOptions Options { get; }
    public string DataDirectory { get; }
    public NameOverlayStore NameStore { get; }
    public SequenceStore Sequences { get; }
    public ExperimentStore Experiments { get; }
    public ComboPlaybackService Runs { get; }
    /// <summary>The user's own Play Combo / Play Ability history: read (for traces), never written.</summary>
    public string AppPlaybackRoot { get; }
    public string SettingsPath { get; }
    public RuntimeBroker? Broker { get; }

    /// <summary>The app's settings as they are now. Read-only: the server never saves them.</summary>
    public AppSettings Settings() => new JsonSettingsStore(SettingsPath).Load();

    public string? IkemenRoot => Options.IkemenRoot is { Length: > 0 } r ? Path.GetFullPath(r) : Settings().IkemenRoot is { Length: > 0 } s ? Path.GetFullPath(s) : null;

    /// <summary>
    /// Loads a character by folder name under chars/ ("kfm"), folder path or DEF path. The index is rebuilt when any of its files changed; the user's names
    /// are re-read when the names file changed (renames made in the app show up on the next call).
    /// </summary>
    public LoadedCharacter Load(string character)
    {
        var located = Locate(character);
        var key = Path.GetFullPath(Path.Combine(located.Root, located.Entry.DefPath));
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out var loaded) || loaded.FileStamp != Stamp(loaded.Index, located.Root))
            {
                var index = CharacterSemanticIndexer.Build(located.Root, located.Entry);
                if (index.Diagnostics.Any(d => d.Code == "def.unreadable")) throw new ToolError($"The DEF of '{character}' could not be read.");
                var folder = Path.Combine(located.Root, located.Entry.FolderPath);
                loaded = new LoadedCharacter
                {
                    Located = located, Index = index, Graph = CandidateGraph.Build(index), Names = CharacterNames.Load(NameStore, index, folder),
                    ContentHash = ExperimentScope.HashOf(index), FileStamp = Stamp(index, located.Root)
                };
                loaded.NamesStamp = NamesStamp(loaded.Folder);
                _cache[key] = loaded;
            }
            else if (NamesStamp(loaded.Folder) is var names && names != loaded.NamesStamp)
            {
                loaded.Names = CharacterNames.Load(NameStore, loaded.Index, loaded.Folder);
                loaded.NamesStamp = names;
            }

            return loaded;
        }
    }

    private CharacterLocator.Located Locate(string character)
    {
        var root = IkemenRoot;
        var looksLikePath = Path.IsPathRooted(character) || character.Contains('/') || character.Contains('\\') || character.EndsWith(".def", StringComparison.OrdinalIgnoreCase);
        CharacterLocator.Located? located = null;
        if (looksLikePath)
        {
            var full = Path.IsPathRooted(character) || root is null ? character : Path.Combine(root, character);
            located = CharacterLocator.Locate(full, null);
        }
        else if (root is not null)
        {
            var dir = Path.Combine(root, "chars", character);
            if (!Directory.Exists(dir))
            {
                // Folder names are case-insensitive on Windows already; also accept the character's own folder spelled with spaces/underscores swapped.
                var alt = Directory.Exists(Path.Combine(root, "chars"))
                    ? Directory.EnumerateDirectories(Path.Combine(root, "chars")).FirstOrDefault(d =>
                        string.Equals(Path.GetFileName(d).Replace('_', ' '), character.Replace('_', ' '), StringComparison.OrdinalIgnoreCase))
                    : null;
                if (alt is not null) dir = alt;
            }

            if (Directory.Exists(dir)) located = CharacterLocator.Locate(dir, root);
        }
        else
        {
            throw new ToolError("The IKEMEN folder is not known.", "Start the server with --root <IKEMEN folder>, set it in IKEMEN Lab, or pass the character's full folder path.");
        }

        return located ?? throw new ToolError($"No character '{character}' was found" + (root is null ? "." : $" under {Path.Combine(root, "chars")}."),
            "Pass the folder name under chars/ (for example \"kfm\"), the character folder path, or its .def path.");
    }

    /// <summary>The file set and their sizes/times: cheap, and changes whenever a file the index read changes.</summary>
    private static string Stamp(SemanticIndex index, string root) => string.Join("|", index.Files.Select(f =>
    {
        var path = Path.IsPathRooted(f.RelPath) ? f.RelPath : Path.Combine(root, f.RelPath);
        var info = new FileInfo(path);
        return info.Exists ? $"{f.RelPath}:{info.Length.ToString(CultureInfo.InvariantCulture)}:{info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}" : f.RelPath + ":missing";
    }));

    private string NamesStamp(string folder)
    {
        var info = new FileInfo(NameStore.PathFor(folder));
        return info.Exists ? info.Length.ToString(CultureInfo.InvariantCulture) + ":" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : "none";
    }

    /// <summary>The playback setup for <paramref name="c"/>: the app's settings, then the server's arguments, then this call's overrides. Nothing is launched.</summary>
    public PlaybackSetup Setup(LoadedCharacter c, SetupOverride? overrides = null)
    {
        var s = Settings();
        var settings = new AppSettings
        {
            XRayEnginePath = Options.EnginePath ?? s.XRayEnginePath,
            XRayEngineDlls = Options.EngineDlls ?? s.XRayEngineDlls,
            XRayDummy = overrides?.Dummy ?? Options.Dummy ?? s.XRayDummy,
            XRayStage = overrides?.Stage ?? Options.Stage ?? s.XRayStage,
            XRayApproachDistance = overrides?.ApproachDistance?.ToString(CultureInfo.InvariantCulture) ?? s.XRayApproachDistance
        };
        return PlaybackPreflight.Check(c.Root, c.SubjectFolder, settings);
    }
}
