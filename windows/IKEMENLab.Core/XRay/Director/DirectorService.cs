using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Behavior;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Sequences;

namespace IKEMENLab.Core.XRay.Director;

/// <summary>Everything the AI Director knows about a behavior right now: follow-up proof, ownership, the generated code (or why it cannot be generated).</summary>
public sealed record DirectorAnalysis(
    TaughtBehavior Behavior, CharacterFacts Facts, FollowUpProof Proof, OwnershipReport Ownership, GeneratedCode? Code, IReadOnlyList<string> Problems)
{
    /// <summary>Nothing blocks generating and testing it (ownership may still refuse promotion).</summary>
    public bool CanGenerate => Code is not null && Problems.Count == 0;
}

/// <summary>
/// The AI Director for one character (Teach AI v1: Knockdown Chase). Data lives in <c>&lt;data&gt;\ai-director\&lt;folder&gt;\</c>: <c>behaviors\</c>,
/// <c>tests\</c>, <c>deployments\</c> and the working copy. The installed character is only read, except by <see cref="Deploy"/> (after approval, through
/// SafeMutation with a manifest) and <see cref="Rollback"/>.
/// </summary>
public sealed class DirectorService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public DirectorService(string dataRoot, string ikemenRoot, SemanticIndex index, CandidateGraph graph, string characterFolder, string defFile)
    {
        Root = Path.Combine(Path.GetFullPath(dataRoot), Safe(characterFolder));
        IkemenRoot = ikemenRoot;
        Index = index;
        Graph = graph;
        CharacterFolder = characterFolder;
        DefFile = defFile;
        Workspace = new DirectorWorkspace(Root, characterFolder);
    }

    /// <summary>The default data root (the app's, <c>%LOCALAPPDATA%\IKEMEN Lab\ai-director</c>).</summary>
    public static string DefaultDataRoot => Path.Combine(AppDataPaths.GetAppDataDirectory(), "ai-director");

    public string Root { get; }
    public string IkemenRoot { get; }
    public SemanticIndex Index { get; }
    public CandidateGraph Graph { get; }
    public string CharacterFolder { get; }
    /// <summary>The DEF's file name inside the character folder.</summary>
    public string DefFile { get; }
    public DirectorWorkspace Workspace { get; }
    public string InstalledFolder => Path.Combine(IkemenRoot, "chars", CharacterFolder);
    public string TestsRoot => Path.Combine(Root, "tests");
    private string BehaviorsRoot => Path.Combine(Root, "behaviors");
    private string DeploymentsRoot => Path.Combine(Root, "deployments");

    // ------------------------------------------------------------------ behaviors

    public IReadOnlyList<TaughtBehavior> List()
    {
        if (!Directory.Exists(BehaviorsRoot)) return [];
        var list = new List<TaughtBehavior>();
        foreach (var f in Directory.EnumerateFiles(BehaviorsRoot, "*.json"))
        {
            try { if (JsonSerializer.Deserialize<TaughtBehavior>(File.ReadAllText(f), Json) is { } b) list.Add(b); }
            catch (Exception ex) when (ex is IOException or JsonException) { /* unreadable: skipped */ }
        }

        return list.OrderByDescending(b => b.UpdatedUtc).ToList();
    }

    public TaughtBehavior? Get(string id) =>
        id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? null : List().FirstOrDefault(b => b.Id == id);

    public TaughtBehavior Save(TaughtBehavior b)
    {
        Directory.CreateDirectory(BehaviorsRoot);
        var path = Path.Combine(BehaviorsRoot, b.Id + ".json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(b, Json));
        File.Move(tmp, path, overwrite: true);
        return b;
    }

    /// <summary>A new Draft from the wizard. v1 keeps one Knockdown Chase per character in the working copy, so a second draft is allowed but not two tested ones.</summary>
    public TaughtBehavior Propose(TeachAiDraft draft)
    {
        var now = DateTime.UtcNow;
        return Save(new TaughtBehavior(TaughtBehavior.NewId(), TaughtBehavior.KnockdownChaseFamily, Index.CharacterId, CharacterFolder, 1, TaughtStatus.Draft,
            draft.Spec, draft.Source, draft.Tests, now, now));
    }

    /// <summary>Any change to the model is a new revision and back to Draft: earlier tests and the approval no longer apply.</summary>
    public TaughtBehavior Revise(TaughtBehavior b, KnockdownChaseSpec spec, TaughtTestPlan? tests = null)
    {
        if (b.Status == TaughtStatus.Live) throw new InvalidOperationException("A live behavior cannot be edited; roll it back (or retire it) first.");
        if (spec == b.Spec && (tests is null || tests == b.Tests)) return b;
        return Save(b with
        {
            Spec = spec, Tests = tests ?? b.Tests, Revision = b.Revision + 1, Status = TaughtStatus.Draft, UpdatedUtc = DateTime.UtcNow,
            Approval = null, PassedTestId = null
        });
    }

    // ------------------------------------------------------------------ analysis

    /// <summary>The facts of the files the build starts from: the workspace base when it exists, else the installed character.</summary>
    public CharacterFacts Facts() => DirectorFacts.Read(Workspace.Exists ? Workspace.BaseFolder : InstalledFolder, DefFile, Index);

    public DirectorAnalysis Analyze(TaughtBehavior b, string? engineSha, ExperimentStore experiments, IEnumerable<string> playbackRoots)
    {
        var facts = Facts();
        var problems = b.Spec.Problems().ToList();
        var proof = TeachAi.Prove(Index, Graph, b.Spec.FollowUpAbilityId, engineSha, experiments, playbackRoots);
        if (!proof.Proven) problems.Add(proof.Text);
        var ownership = DirectorOwnership.Analyze(Index, facts, b.Spec);
        if (facts.MinusOneFile is null) problems.Add(ownership.Reasons.FirstOrDefault() ?? "There is no single State -1 for the generated decision.");
        if (!facts.HasWalkAnim) problems.Add("The character has no walk animation (action 20) for the chase.");
        if (b.Spec.Fallback == ChaseFallback.Retreat && !facts.HasWalkBackAnim && !facts.HasWalkAnim) problems.Add("Retreat needs a walk animation (21 or 20).");
        if (b.Tests.Opening.Count == 0) problems.Add("No opening is known for the controlled test: teach from a Sequence Lab experiment that knocks the opponent down.");
        GeneratedCode? code = null;
        if (b.Spec.Problems().Count == 0 && facts.MinusOneFile is not null)
            try { code = DirectorCompiler.Compile(b, facts, Index); }
            catch (InvalidOperationException ex) { problems.Add(ex.Message); }
        return new DirectorAnalysis(b, facts, proof, ownership, code, problems);
    }

    // ------------------------------------------------------------------ working copy

    /// <summary>Generates the behavior into the working copy (creating the workspace from the installed character the first time). Draft → Testing.</summary>
    public (TaughtBehavior Behavior, string BuildHash) GenerateWorkingCopy(DirectorAnalysis a)
    {
        if (!a.CanGenerate) throw new InvalidOperationException("It cannot be generated yet: " + string.Join(" ", a.Problems));
        foreach (var other in List().Where(o => o.Id != a.Behavior.Id && o.Status is TaughtStatus.Testing or TaughtStatus.ReadyForApproval or TaughtStatus.Live))
            throw new InvalidOperationException($"Another Knockdown Chase ({other.Id}, {DirectorText.Status(other.Status)}) owns the working copy. One owner per decision: retire it first.");
        Workspace.Ensure(InstalledFolder, DefFile);
        var hash = Workspace.Generate(a.Code, a.Facts, a.Behavior);
        var b = a.Behavior with { Status = a.Behavior.Status == TaughtStatus.Draft ? TaughtStatus.Testing : a.Behavior.Status, UpdatedUtc = DateTime.UtcNow };
        return (Save(Refresh(b)), hash);
    }

    /// <summary>A disposable copy of the build (base or installed + the generated code) for an MCP / pre-generation test; the working copy is not touched.</summary>
    public (string Folder, string BuildHash) StagingBuild(DirectorAnalysis a)
    {
        if (!a.CanGenerate) throw new InvalidOperationException("It cannot be generated yet: " + string.Join(" ", a.Problems));
        var folder = Path.Combine(Root, "staging", Guid.NewGuid().ToString("N"), CharacterFolder);
        var hash = DirectorWorkspace.Build(a.Facts.Folder, folder, a.Code, a.Facts);
        return (folder, hash);
    }

    public static void DeleteStaging(string folder)
    {
        var parent = Path.GetDirectoryName(folder);
        try { if (parent is not null) DirectorWorkspace.DeleteFolder(parent); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    public BuildDiff? Diff() => Workspace.Exists ? DirectorWorkspace.Diff(InstalledFolder, Workspace.WorkingFolder, CharacterFolder) : null;

    // ------------------------------------------------------------------ tests

    public DirectorTestReport? LoadTest(string? id) => id is null ? null : DirectorTesting.Load(Path.Combine(TestsRoot, id));

    public IReadOnlyList<DirectorTestReport> Tests(string behaviorId) =>
        !Directory.Exists(TestsRoot) ? []
        : Directory.EnumerateDirectories(TestsRoot).Select(DirectorTesting.Load).OfType<DirectorTestReport>().Where(r => r.BehaviorId == behaviorId)
            .OrderByDescending(r => r.CreatedUtc).ToList();

    /// <summary>Records a finished test and moves the status: Ready for approval only when it passed on exactly the build in the working copy.</summary>
    public TaughtBehavior RecordTest(TaughtBehavior b, DirectorTestReport report)
    {
        var current = Get(b.Id) ?? b;
        if (current.Revision != report.Revision || current.ModelHash != report.ModelHash) return current;   // the behavior changed while it ran
        var updated = current with
        {
            LastTestId = report.Id, PassedTestId = report.Passed ? report.Id : current.PassedTestId, UpdatedUtc = DateTime.UtcNow,
            Status = current.Status == TaughtStatus.Draft ? TaughtStatus.Testing : current.Status
        };
        return Save(Refresh(updated));
    }

    /// <summary>Ready for approval ⇔ a passing test of THIS revision ran on the build now in the working copy (byte-identical). Live and Retired stay.</summary>
    public TaughtBehavior Refresh(TaughtBehavior b)
    {
        if (b.Status is TaughtStatus.Live or TaughtStatus.Retired or TaughtStatus.Draft) return b;
        var info = Workspace.Info;
        var passed = LoadTest(b.PassedTestId);
        var ready = passed is { Passed: true } && passed.ModelHash == b.ModelHash && info?.GeneratedBuildHash == passed.BuildHash &&
                    info?.BehaviorId == b.Id && Workspace.Divergence() is null;
        var approval = ready && b.Approval is { } ap && ap.BuildHash == passed!.BuildHash ? b.Approval : null;
        return b with { Status = ready ? TaughtStatus.ReadyForApproval : TaughtStatus.Testing, Approval = approval };
    }

    // ------------------------------------------------------------------ approval, deployment

    /// <summary>
    /// The approval boundary: only a user action in the app calls this. It binds the approval to the exact build, diff, model and passing test; anything
    /// that changes one of them voids it.
    /// </summary>
    public TaughtBehavior Approve(TaughtBehavior b, DirectorAnalysis analysis, string by)
    {
        b = Refresh(Get(b.Id) ?? b);
        if (b.Status != TaughtStatus.ReadyForApproval) throw new InvalidOperationException($"It is {DirectorText.Status(b.Status)}, not ready for approval.");
        if (analysis.Ownership.Refused) throw new InvalidOperationException(analysis.Ownership.Summary);
        if (!analysis.Proof.Proven) throw new InvalidOperationException(analysis.Proof.Text);
        // What the user reviews (the code generated now) must be what the working copy holds; an older generation of the same model is tested but not what
        // is shown, so it is re-generated and re-tested first.
        if (analysis.Code?.Hash != Workspace.Info?.CodeHash)
            throw new InvalidOperationException("The working copy holds code generated by an earlier version of IKEMEN Lab; Test again to regenerate and test it before approving.");
        var report = LoadTest(b.PassedTestId)!;
        var diff = Diff() ?? throw new InvalidOperationException("There is no working copy.");
        return Save(b with
        {
            Approval = new ApprovalRecord(DateTime.UtcNow, by, report.BuildHash, diff.Hash, b.ModelHash, report.Id, DirectorText.Sentence(b)), UpdatedUtc = DateTime.UtcNow
        });
    }

    /// <summary>Why it cannot be deployed now, or null.</summary>
    public string? DeployBlocker(TaughtBehavior b)
    {
        b = Refresh(Get(b.Id) ?? b);
        if (b.Status == TaughtStatus.Live) return "It is already live.";
        if (b.Approval is not { } ap) return "It has not been approved.";
        if (Workspace.Divergence() is { } div) return div;
        if (Workspace.Info?.GeneratedBuildHash != ap.BuildHash) return "The working copy is no longer the approved build.";
        if (Workspace.InstalledDrift() is { } drift) return drift;
        if (Diff() is not { } diff || diff.Hash != ap.DiffHash) return "The change to deploy is no longer the approved diff.";
        return diff.Files.Any(f => f.Kind == "removed") ? "The build removes files; the Director never deletes character files." : null;
    }

    /// <summary>
    /// One-way deployment of the approved working copy to the installed character: each changed file through SafeMutation (verified backup, manifest,
    /// expected current hash), then a hash check of the install. Any failure rolls back what was done. Never called by MCP.
    /// </summary>
    public TaughtBehavior Deploy(TaughtBehavior b, ISafeMutationService mutation)
    {
        if (DeployBlocker(b) is { } why) throw new InvalidOperationException(why);
        b = Refresh(Get(b.Id) ?? b);
        var diff = Diff()!;
        var done = new List<string>();
        var deployed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var change in diff.Files)
            {
                var rel = change.Path[$"chars/{CharacterFolder}/".Length..];
                var target = Path.Combine(InstalledFolder, rel);
                var source = Path.Combine(Workspace.WorkingFolder, rel);
                var result = change.Kind == "added"
                    ? mutation.CreateFile(IkemenRoot, target, File.ReadAllBytes(source))
                    : mutation.ReplaceFile(IkemenRoot, target, source, expectedCurrentHash: change.InstalledHash);
                if (!result.Success) throw new InvalidOperationException($"{change.Path}: {result.Error}");
                done.Add(result.OperationId);
                deployed[change.Path] = change.BuildHash;
            }

            foreach (var (path, hash) in deployed)
                if (DirectorHash.OfBytes(File.ReadAllBytes(Path.Combine(InstalledFolder, path[$"chars/{CharacterFolder}/".Length..]))) != hash)
                    throw new InvalidOperationException($"{path} does not have the deployed content after the write.");
        }
        catch
        {
            foreach (var op in Enumerable.Reverse(done)) mutation.Rollback(op);
            throw;
        }

        var id = "deploy-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        Directory.CreateDirectory(DeploymentsRoot);
        var manifestPath = Path.Combine(DeploymentsRoot, id + ".json");
        var record = new DeploymentRecord(id, DateTime.UtcNow, IkemenRoot, CharacterFolder, b.Approval!.BuildHash, manifestPath, done, deployed);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            schema = "ikemenlab.director.deployment/1", deployment = record, behavior = b.Id, revision = b.Revision, model = b.ModelHash, approval = b.Approval,
            files = diff.Files, diffHash = diff.Hash
        }, Json));
        return Save(b with { Status = TaughtStatus.Live, Deployment = record, UpdatedUtc = DateTime.UtcNow });
    }

    /// <summary>Puts the installed files back exactly as they were before the deployment (SafeMutation's verified backups, newest first).</summary>
    public TaughtBehavior Rollback(TaughtBehavior b, ISafeMutationService mutation)
    {
        b = Get(b.Id) ?? b;
        if (b.Deployment is not { RolledBack: false } d) throw new InvalidOperationException("There is no deployment to roll back.");
        foreach (var op in Enumerable.Reverse(d.OperationIds))
        {
            var r = mutation.Rollback(op);
            if (!r.Success) throw new InvalidOperationException($"Rollback of {op} failed: {r.Error}. The manifest is {d.ManifestPath}.");
        }

        return Save(Refresh(b with { Status = TaughtStatus.Testing, Deployment = d with { RolledBack = true }, UpdatedUtc = DateTime.UtcNow }));
    }

    /// <summary>Retires a behavior: rolls back its deployment when it is live, and removes it from the working copy.</summary>
    public TaughtBehavior Retire(TaughtBehavior b, ISafeMutationService? mutation)
    {
        b = Get(b.Id) ?? b;
        if (b.Status == TaughtStatus.Live)
            b = Rollback(b, mutation ?? throw new InvalidOperationException("Retiring a live behavior needs the mutation service (it rolls the deployment back)."));
        if (Workspace.Exists && Workspace.Info?.BehaviorId == b.Id) Workspace.Generate(null, Facts(), null);
        return Save(b with { Status = TaughtStatus.Retired, Approval = null, UpdatedUtc = DateTime.UtcNow });
    }

    private static string Safe(string folder) => string.Concat(folder.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
