using IKEMENLab.Core.Config;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.SelectDef;

namespace IKEMENLab.Core.Install;

/// <summary>
/// Destination rules shared by the installer and the install preview: folder names are kept exactly,
/// so they are validated rather than rewritten; one destination can be claimed by only one item per
/// batch; and Rename is the third answer to a collision next to Replace and Skip.
/// </summary>
public static class InstallPlanRules
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>Null when <paramref name="name"/> can be a character folder IKEMEN's select.def can list.</summary>
    public static string? ValidateFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "The folder name is empty.";
        if (name is "." or "..") return "The folder name is not valid.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return $"'{name}' contains characters Windows does not allow in folder names.";
        if (name.IndexOfAny([',', ';']) >= 0) return $"select.def cannot list a folder named '{name}' (it contains ',' or ';').";
        if (name.StartsWith('[')) return $"select.def cannot list a folder whose name starts with '['.";
        if (name != name.Trim() || name.EndsWith('.')) return $"'{name}' starts or ends with a space, or ends with a dot, which Windows does not keep.";
        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(name))) return $"'{name}' is a reserved Windows device name.";
        if (name.StartsWith(IkemenLabStaging.Prefix, StringComparison.OrdinalIgnoreCase)) return $"'{name}' is reserved for IKEMEN Lab's temporary folders.";
        return null;
    }

    /// <summary>
    /// Points a character item at chars/<paramref name="newName"/>. Returns an error and changes nothing
    /// when the name cannot be used. Renaming onto an existing folder asks Replace/Skip again.
    /// </summary>
    public static string? Rename(InstallPlanItem item, string newName, string ikemenRoot)
    {
        if (item.Package.Kind != InstallContentKind.Character) return "Only character folders can be renamed.";
        var error = ValidateFolderName(newName);
        if (error is not null) return error;

        var root = IkemenPathGuard.NormalizeRoot(ikemenRoot);
        item.TargetDirectory = Path.Combine(root, "chars", newName);
        item.DestinationExists = Directory.Exists(item.TargetDirectory);
        item.Decision = item.DestinationExists ? InstallItemDecision.NeedsDecision : InstallItemDecision.InstallNew;
        return null;
    }

    /// <summary>
    /// Re-checks every item: name validity, whether its destination now exists, and whether an earlier
    /// item in the batch already claims the same destination (the later one must be renamed or skipped).
    /// Also lists DEFs select.def uses that a replacement would remove.
    /// </summary>
    public static void Refresh(IReadOnlyList<InstallPlanItem> items, string ikemenRoot)
    {
        var root = IkemenPathGuard.NormalizeRoot(ikemenRoot);
        var stagesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, "stages")));
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SelectDefIndex? select = null;

        foreach (var item in items)
        {
            item.Problem = null;
            item.ReplaceWarnings = [];
            if (item.Decision == InstallItemDecision.Skip) continue;
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.TargetDirectory));
            if (string.Equals(target, stagesRoot, StringComparison.OrdinalIgnoreCase)) continue; // flat stage files

            if (item.Package.Kind == InstallContentKind.Character)
            {
                if (ValidateFolderName(item.DestinationName) is { } nameError)
                {
                    item.Problem = nameError + " Rename it.";
                    continue;
                }

                var exists = Directory.Exists(target);
                if (exists != item.DestinationExists)
                {
                    item.DestinationExists = exists;
                    if (exists && item.Decision == InstallItemDecision.InstallNew) item.Decision = InstallItemDecision.NeedsDecision;
                    if (!exists && item.Decision is InstallItemDecision.NeedsDecision or InstallItemDecision.Replace)
                        item.Decision = InstallItemDecision.InstallNew;
                }
            }

            if (!claimed.Add(target))
            {
                item.Problem = $"Another item in this install also goes to {Path.GetRelativePath(root, target).Replace('\\', '/')}. " +
                               "Rename or skip one of them.";
                continue;
            }

            if (item.Package.Kind == InstallContentKind.Character && item.DestinationExists)
                item.ReplaceWarnings = RosterLossWarnings(item, root, ref select);
        }
    }

    private static IReadOnlyList<string> RosterLossWarnings(InstallPlanItem item, string root, ref SelectDefIndex? select)
    {
        try
        {
            select ??= SelectDefIndex.Build(root, SelectDefLocator.Locate(root, IkemenConfigReader.Read(root).Motif));
            if (!select.IsAvailable) return [];

            var prefixLength = ("chars/" + item.DestinationName + "/").Length;
            var refs = select.CharacterReferencesUnder("chars/" + item.DestinationName);
            var warnings = new List<string>();
            foreach (var (path, active) in refs.Active.Select(p => (p, true)).Concat(refs.Disabled.Select(p => (p, false))))
            {
                if (path.Length <= prefixLength) continue;
                var inPackage = Path.Combine(item.Package.PackageRoot, path[prefixLength..].Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(inPackage)) continue;
                warnings.Add(active
                    ? $"select.def uses {path["chars/".Length..]}, which the new version does not include."
                    : $"select.def lists {path["chars/".Length..]} (commented out), which the new version does not include.");
            }

            return warnings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return [];
        }
    }
}
