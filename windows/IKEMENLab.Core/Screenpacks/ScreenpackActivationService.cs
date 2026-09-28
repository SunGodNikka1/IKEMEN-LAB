using IKEMENLab.Core.Config;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.Screenpacks;
using IKEMENLab.Core.SelectDef;

namespace IKEMENLab.Core.Screenpacks;

public sealed class ScreenpackActivationPreview
{
    public required string ScreenpackName { get; init; }
    public required string CurrentMotif { get; init; }
    public required string NewMotif { get; init; }
    public required string SystemDefPath { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool CanActivate { get; init; }
    public string? Error { get; init; }
    public bool AlreadyActive { get; init; }
}

public sealed class ScreenpackActivationResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public string? Description { get; init; }
    public string? OperationId { get; init; }
    public bool Changed { get; init; }
    public ScreenpackActivationPreview? Preview { get; init; }
    public OperationPlan? Plan { get; init; }
}

public interface IScreenpackActivationService
{
    ScreenpackActivationPreview Preview(string ikemenRoot, ScreenpackEntry screenpack);
    ScreenpackActivationResult Activate(string ikemenRoot, ScreenpackEntry screenpack, bool dryRun = false);
}

/// <summary>Activates a screenpack by writing Motif in save/config via SafeMutation.</summary>
public sealed class ScreenpackActivationService : IScreenpackActivationService
{
    private readonly IIkemenConfigMutationService _config;

    public ScreenpackActivationService(IIkemenConfigMutationService? config = null)
    {
        _config = config ?? new IkemenConfigMutationService();
    }

    public ScreenpackActivationPreview Preview(string ikemenRoot, ScreenpackEntry screenpack)
    {
        try
        {
            var root = IkemenPathGuard.NormalizeRoot(ikemenRoot);
            var defRelative = SelectDefReader.NormalizeSeparators(screenpack.DefPath);
            var defFull = Path.GetFullPath(Path.Combine(root, defRelative.Replace('/', Path.DirectorySeparatorChar)));

            if (!File.Exists(defFull))
                return Blocked(screenpack, "", defRelative, "system.def does not exist.");

            IkemenPathGuard.EnsureInsideRoot(root, defFull);
            if (!defFull.StartsWith(Path.GetFullPath(Path.Combine(root, "data")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !SamePath(defFull, Path.Combine(root, "data", "system.def")))
            {
                // Allow data/system.def and data/<folder>/system.def only.
                var dataRoot = Path.GetFullPath(Path.Combine(root, "data"));
                if (!defFull.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase))
                    return Blocked(screenpack, "", defRelative, "Screenpack must live under the installation data/ folder.");
            }

            var parsed = DefParser.ParseFile(defFull);
            if (parsed is null)
                return Blocked(screenpack, "", defRelative, "system.def could not be parsed.");

            var warnings = new List<string>();
            var components = ScreenpackIndexer.DetectComponents(parsed, root, Path.GetDirectoryName(defFull)!);
            if (components == ScreenpackComponents.None)
                warnings.Add("No standard screenpack sections/files detected; activation may still work.");
            if (ScreenpackIndexer.ResolveMotifFile(root, Path.GetDirectoryName(defFull)!, parsed.Value("spr", "files")) is null)
                warnings.Add("Sprite (spr) from [Files] could not be resolved.");

            var config = IkemenConfigReader.Read(root);
            if (!config.Exists)
                return Blocked(screenpack, config.Motif ?? "", defRelative, "No writable save/config.ini (or config.json) found.");

            var current = string.IsNullOrWhiteSpace(config.Motif)
                ? SelectDefLocator.DefaultMotif
                : SelectDefReader.NormalizeSeparators(config.Motif!);

            var already = PathsEqual(root, current, defRelative);
            var motifPreview = _config.Preview(root, ConfigValueKind.Motif, defRelative);
            if (!motifPreview.CanApply && !already)
                return Blocked(screenpack, current, defRelative, motifPreview.Error ?? "Config Motif write is not safe.", warnings);

            return new ScreenpackActivationPreview
            {
                ScreenpackName = screenpack.Name,
                CurrentMotif = current,
                NewMotif = defRelative,
                SystemDefPath = defRelative,
                Warnings = warnings,
                CanActivate = true,
                AlreadyActive = already
            };
        }
        catch (Exception ex)
        {
            return Blocked(screenpack, "", screenpack.DefPath, ex.Message);
        }
    }

    public ScreenpackActivationResult Activate(string ikemenRoot, ScreenpackEntry screenpack, bool dryRun = false)
    {
        var preview = Preview(ikemenRoot, screenpack);
        if (!preview.CanActivate)
        {
            return new ScreenpackActivationResult
            {
                Success = false,
                Error = preview.Error ?? "Cannot activate screenpack.",
                Preview = preview
            };
        }

        if (preview.AlreadyActive)
        {
            return new ScreenpackActivationResult
            {
                Success = true,
                Changed = false,
                Description = "Screenpack is already active.",
                Preview = preview
            };
        }

        var result = _config.SetMotif(ikemenRoot, preview.NewMotif, dryRun);
        if (!result.Success)
        {
            return new ScreenpackActivationResult
            {
                Success = false,
                Error = result.Error,
                Preview = preview,
                Plan = result.Plan,
                OperationId = result.OperationId
            };
        }

        if (!dryRun && result.Changed)
        {
            var reRead = IkemenConfigReader.Read(IkemenPathGuard.NormalizeRoot(ikemenRoot));
            var active = SelectDefReader.NormalizeSeparators(reRead.Motif ?? "");
            if (!PathsEqual(IkemenPathGuard.NormalizeRoot(ikemenRoot), active, preview.NewMotif))
            {
                if (!string.IsNullOrEmpty(result.OperationId))
                    // Config service already verified Motif; this is a belt-and-suspenders path compare.
                    return new ScreenpackActivationResult
                    {
                        Success = false,
                        Error = "Active motif did not match the requested screenpack after write.",
                        Preview = preview,
                        OperationId = result.OperationId,
                        Plan = result.Plan
                    };
            }
        }

        return new ScreenpackActivationResult
        {
            Success = true,
            Changed = result.Changed,
            Description = result.Changed
                ? $"Activated \"{preview.ScreenpackName}\" (Motif = {preview.NewMotif})."
                : "Already active.",
            Preview = preview,
            OperationId = result.OperationId,
            Plan = result.Plan
        };
    }

    private static bool PathsEqual(string root, string a, string b)
    {
        var fa = Path.GetFullPath(Path.Combine(root, SelectDefReader.NormalizeSeparators(a).Replace('/', Path.DirectorySeparatorChar)));
        var fb = Path.GetFullPath(Path.Combine(root, SelectDefReader.NormalizeSeparators(b).Replace('/', Path.DirectorySeparatorChar)));
        return string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static ScreenpackActivationPreview Blocked(
        ScreenpackEntry screenpack,
        string current,
        string neu,
        string error,
        IReadOnlyList<string>? warnings = null)
        => new()
        {
            ScreenpackName = screenpack.Name,
            CurrentMotif = current,
            NewMotif = neu,
            SystemDefPath = neu,
            Warnings = warnings ?? [],
            CanActivate = false,
            Error = error
        };
}
