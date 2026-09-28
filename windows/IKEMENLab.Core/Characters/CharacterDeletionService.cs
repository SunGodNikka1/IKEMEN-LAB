using System.Text;
using IKEMENLab.Core.Config;
using IKEMENLab.Core.Library;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.SelectDef;
using IKEMENLab.Core.Settings;

namespace IKEMENLab.Core.Characters;

/// <summary>What deleting a character would do; shown to the user before anything is changed.</summary>
public sealed class CharacterDeletionPlan
{
    public required bool CanDelete { get; init; }

    /// <summary>Why the character cannot be deleted (nothing was changed).</summary>
    public string? Error { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>The character package, root-relative with '/' separators ("chars/Gaara").</summary>
    public required string PackageFolder { get; init; }

    public required string PackageFullPath { get; init; }

    /// <summary>Total size of the files in the package, when it could be measured.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>The select.def IKEMEN loads; null when the installation has none.</summary>
    public string? SelectDefPath { get; init; }

    /// <summary>Roster lines (active and commented out) that belong to the package and will be removed.</summary>
    public IReadOnlyList<RosterLineReference> RosterEntries { get; init; } = [];

    /// <summary>The text of the confirmation shown before deleting: name, folder and affected roster lines.</summary>
    public string ConfirmationMessage(int maxLines = 8)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Delete \"{DisplayName}\" from your IKEMEN installation?");
        sb.AppendLine();
        sb.AppendLine("Folder: " + PackageFolder);
        sb.AppendLine(PackageFullPath);
        if (SizeBytes is { } size) sb.AppendLine("Size: " + FormatSize(size));
        sb.AppendLine();

        if (RosterEntries.Count == 0)
        {
            sb.AppendLine(SelectDefPath is null
                ? "No select.def was found, so there are no roster entries to remove."
                : "select.def does not list this character; the roster is unchanged.");
        }
        else
        {
            sb.AppendLine(RosterEntries.Count == 1
                ? "This select.def roster entry is removed completely; the characters after it move up one slot:"
                : $"These {RosterEntries.Count} select.def roster entries are removed completely; the characters after them move up:");
            foreach (var entry in RosterEntries.Take(maxLines))
            {
                sb.AppendLine($"    line {entry.LineNumber}: {entry.Text.Trim()}{(entry.IsCommented ? "   (disabled)" : string.Empty)}");
            }

            if (RosterEntries.Count > maxLines) sb.AppendLine($"    \u2026and {RosterEntries.Count - maxLines} more");
        }

        sb.AppendLine();
        sb.Append("The character folder is deleted, not just disabled. IKEMEN Lab keeps a backup copy in its app data folder.");
        return sb.ToString();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} bytes"
    };
}

public sealed class CharacterDeletionResult
{
    public required bool Success { get; init; }

    /// <summary>Actionable failure message; says whether anything was left changed.</summary>
    public string? Error { get; init; }

    public required CharacterDeletionPlan Plan { get; init; }
    public IReadOnlyList<RosterLineReference> RemovedRosterEntries { get; init; } = [];
    public string? SelectDefOperationId { get; init; }
    public string? FolderOperationId { get; init; }

    /// <summary>select.def had been updated and was put back because the folder could not be deleted.</summary>
    public bool SelectDefRestored { get; init; }

    /// <summary>Non-fatal notes after a successful delete (cleanup or app-data bookkeeping that failed).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public interface ICharacterDeletionService
{
    CharacterDeletionPlan Preview(string ikemenRoot, CharacterEntry character);
    CharacterDeletionResult Delete(string ikemenRoot, CharacterEntry character);
}

/// <summary>
/// Deletes a character package (its top-level folder under chars/) and every select.def roster line that
/// belongs to it, through <see cref="ISafeMutationService"/>:
/// <list type="number">
///   <item>select.def is rewritten without the package's lines (active and commented), removing them
///         completely so later characters move up one slot. Nothing else in the file changes.</item>
///   <item>The folder is deleted (verified backup first, then one atomic rename out of chars/).</item>
///   <item>If the folder cannot be deleted, select.def is rolled back, so the installation is left exactly
///         as it was — never a roster without the folder's lines while the folder is still half there.</item>
///   <item>Only after both succeeded are the package's app-owned Date Added record and saved primary-DEF
///         choice retired.</item>
/// </list>
/// select.def is written first on purpose: an interruption between the steps leaves a complete,
/// unregistered character folder (which IKEMEN simply does not list), never roster entries pointing at a
/// deleted folder.
/// </summary>
public sealed class CharacterDeletionService : ICharacterDeletionService
{
    private readonly ISafeMutationService _mutations;
    private readonly DateAddedTracker _dateAdded;
    private readonly PrimaryDefStore? _primaryDefs;
    private readonly string _stagingRoot;

    public CharacterDeletionService(
        ISafeMutationService? mutations = null,
        DateAddedTracker? dateAdded = null,
        PrimaryDefStore? primaryDefs = null,
        string? stagingRoot = null)
    {
        _mutations = mutations ?? new SafeMutationService();
        _dateAdded = dateAdded ?? DateAddedTracker.EstimateOnly;
        _primaryDefs = primaryDefs;
        _stagingRoot = stagingRoot ?? Path.Combine(AppDataPaths.GetAppDataDirectory(), "roster-staging");
    }

    public CharacterDeletionPlan Preview(string ikemenRoot, CharacterEntry character)
        => Preview(ikemenRoot, character.Id, character.DisplayName);

    public CharacterDeletionResult Delete(string ikemenRoot, CharacterEntry character)
        => Delete(ikemenRoot, character.Id, character.DisplayName);

    /// <param name="characterId">Library id ("Gaara", or "Muzan/Muzan" for a nested-only layout); the
    /// package is its top-level folder.</param>
    public CharacterDeletionPlan Preview(string ikemenRoot, string characterId, string? displayName = null)
    {
        var top = ContentIdentity.TopFolder(characterId ?? string.Empty).Trim();
        var name = string.IsNullOrWhiteSpace(displayName) ? top : displayName!;
        var packageFolder = "chars/" + top;

        CharacterDeletionPlan Refuse(string error, string fullPath = "") => new()
        {
            CanDelete = false,
            Error = error,
            DisplayName = name,
            PackageFolder = packageFolder,
            PackageFullPath = fullPath
        };

        string root;
        try
        {
            root = IkemenPathGuard.NormalizeRoot(ikemenRoot);
        }
        catch (ArgumentException ex)
        {
            return Refuse(ex.Message);
        }

        if (top.Length == 0 || top is "." or ".." || top.IndexOfAny(['/', '\\', ':']) >= 0 ||
            IkemenLabStaging.IsStagingName(top))
        {
            return Refuse($"'{characterId}' is not a character folder under chars/.");
        }

        var charsDir = Path.Combine(root, "chars");
        var full = Path.GetFullPath(Path.Combine(charsDir, top));
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(charsDir), StringComparison.OrdinalIgnoreCase))
        {
            return Refuse($"'{characterId}' is not a character folder under chars/.", full);
        }

        if (!Directory.Exists(full))
        {
            return Refuse($"The folder {packageFolder} no longer exists. Refresh the library.", full);
        }

        try
        {
            IkemenPathGuard.EnsureInsideRoot(root, full);
            IkemenPathGuard.EnsureNoReparseEscape(root, full);
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            {
                return Refuse($"{packageFolder} is a link (junction or symbolic link). IKEMEN Lab will not delete it; " +
                              "remove the link in Explorer instead.", full);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Refuse(ex.Message, full);
        }

        string? selectPath;
        IReadOnlyList<RosterLineReference> references = [];
        try
        {
            selectPath = LocateSelectDef(root);
            if (selectPath is not null)
            {
                var text = DefFileReader.Decode(File.ReadAllBytes(selectPath), out _);
                if (text is null)
                {
                    return Refuse("select.def could not be decoded, so IKEMEN Lab cannot tell which roster entries " +
                                  "belong to this character. Nothing was deleted.", full);
                }

                references = SelectDefRosterEditor.FindPackageReferences(text, root, packageFolder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Refuse("select.def could not be read: " + ex.Message + " Nothing was deleted.", full);
        }

        return new CharacterDeletionPlan
        {
            CanDelete = true,
            DisplayName = name,
            PackageFolder = packageFolder,
            PackageFullPath = full,
            SizeBytes = MeasureSize(full),
            SelectDefPath = selectPath,
            RosterEntries = references
        };
    }

    public CharacterDeletionResult Delete(string ikemenRoot, string characterId, string? displayName = null)
    {
        var plan = Preview(ikemenRoot, characterId, displayName);
        if (!plan.CanDelete) return Fail(plan, plan.Error ?? "This character cannot be deleted.");

        var root = IkemenPathGuard.NormalizeRoot(ikemenRoot);
        var folderPlan = _mutations.PlanDeleteDirectory(root, plan.PackageFullPath);
        if (!folderPlan.IsValid)
        {
            return Fail(plan, $"Cannot delete {plan.PackageFolder}: {folderPlan.RejectionReason} Nothing was changed.");
        }

        if (plan.SelectDefPath is null)
        {
            return DeleteFolderAndRetire(root, plan, removed: [], selectOperation: null);
        }

        // Hold select.def for the whole transaction: a roster toggle in this process must not slip in
        // between our write and a possible rollback (which would otherwise discard it).
        using var gate = TargetWriteGate.Enter(plan.SelectDefPath);

        byte[] originalBytes;
        try
        {
            originalBytes = File.ReadAllBytes(plan.SelectDefPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(plan, "select.def could not be read: " + ex.Message + " Nothing was deleted.");
        }

        var originalText = DefFileReader.Decode(originalBytes, out var encoding);
        if (originalText is null)
        {
            return Fail(plan, "select.def could not be decoded. Nothing was deleted.");
        }

        var removal = SelectDefRosterEditor.RemovePackageEntries(originalText, root, plan.PackageFolder);
        if (!removal.Success)
        {
            return Fail(plan, (removal.Error ?? "The roster edit was rejected.") + " Nothing was deleted.");
        }

        if (!removal.Changed)
        {
            return DeleteFolderAndRetire(root, plan, removed: [], selectOperation: null);
        }

        byte[] proposed;
        try
        {
            proposed = DefFileReader.Encode(removal.Content, encoding);
        }
        catch (EncoderFallbackException)
        {
            return Fail(plan, "The edited select.def cannot be written in its own text encoding. Nothing was deleted.");
        }

        Directory.CreateDirectory(_stagingRoot);
        var stagingFile = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N") + ".def");
        try
        {
            File.WriteAllBytes(stagingFile, proposed);
            var selectPlan = _mutations.PlanReplaceFile(root, plan.SelectDefPath, stagingFile);
            if (!selectPlan.IsValid)
            {
                return Fail(plan, $"Cannot update select.def: {selectPlan.RejectionReason} Nothing was deleted.");
            }

            var write = _mutations.ReplaceFile(root, plan.SelectDefPath, stagingFile,
                expectedCurrentHash: TargetWriteGate.Sha256Hex(originalBytes));
            if (!write.Success)
            {
                return Fail(plan, $"Could not update select.def: {write.Error} Nothing was deleted.",
                    selectOperation: write.OperationId);
            }

            return DeleteFolderAndRetire(root, plan, removal.Removed, write.OperationId);
        }
        finally
        {
            try { if (File.Exists(stagingFile)) File.Delete(stagingFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private CharacterDeletionResult DeleteFolderAndRetire(
        string root, CharacterDeletionPlan plan, IReadOnlyList<RosterLineReference> removed, string? selectOperation)
    {
        MutationResult folder;
        try
        {
            folder = _mutations.DeleteDirectory(root, plan.PackageFullPath);
        }
        catch (Exception ex)
        {
            return UndoRoster(plan, selectOperation, ex.Message, folderOperation: null);
        }

        if (!folder.Success)
        {
            return UndoRoster(plan, selectOperation, folder.Error ?? "The folder could not be deleted.", folder.OperationId);
        }

        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(folder.Manifest.ErrorDetail)) warnings.Add(folder.Manifest.ErrorDetail!);

        // App-owned bookkeeping is retired only now that the package is really gone.
        var top = plan.PackageFolder["chars/".Length..];
        try
        {
            _dateAdded.Forget(root, [ContentIdentity.ForCharacterFolder(top)]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            warnings.Add("The Date Added record could not be removed: " + ex.Message);
        }

        try
        {
            _primaryDefs?.Remove(root, top);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            warnings.Add("The saved primary-DEF choice could not be removed: " + ex.Message);
        }

        return new CharacterDeletionResult
        {
            Success = true,
            Plan = plan,
            RemovedRosterEntries = removed,
            SelectDefOperationId = selectOperation,
            FolderOperationId = folder.OperationId,
            Warnings = warnings
        };
    }

    /// <summary>The folder was not deleted: put select.def back so nothing is left half-done.</summary>
    private CharacterDeletionResult UndoRoster(
        CharacterDeletionPlan plan, string? selectOperation, string folderError, string? folderOperation)
    {
        var message = $"Could not delete {plan.PackageFolder}: {folderError}";
        if (selectOperation is null)
        {
            return Fail(plan, message, folderOperation: folderOperation);
        }

        MutationResult rollback;
        try
        {
            rollback = _mutations.Rollback(selectOperation);
        }
        catch (Exception ex)
        {
            return Fail(plan, message + $" select.def could not be restored automatically ({ex.Message}); " +
                              $"its backup is recorded in IKEMEN Lab operation {selectOperation}.",
                selectOperation, folderOperation);
        }

        if (!rollback.Success)
        {
            var backup = _mutations.LoadManifest(selectOperation)?.BackupPath;
            return Fail(plan, message + $" select.def could not be restored automatically ({rollback.Error}); " +
                              $"its backup is at '{backup ?? "(unknown)"}'.",
                selectOperation, folderOperation);
        }

        return new CharacterDeletionResult
        {
            Success = false,
            Error = message + " select.def was restored, so nothing was changed.",
            Plan = plan,
            SelectDefOperationId = selectOperation,
            FolderOperationId = folderOperation,
            SelectDefRestored = true
        };
    }

    private static string? LocateSelectDef(string root)
    {
        var config = IkemenConfigReader.Read(root);
        var location = SelectDefLocator.Locate(root, config.Motif);
        if (location.Path is null || !File.Exists(location.Path)) return null;
        IkemenPathGuard.EnsureInsideRoot(root, location.Path);
        return location.Path;
    }

    private static long? MeasureSize(string folder)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true
            };
            return new DirectoryInfo(folder).EnumerateFiles("*", options).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static CharacterDeletionResult Fail(
        CharacterDeletionPlan plan, string error, string? selectOperation = null, string? folderOperation = null)
        => new()
        {
            Success = false,
            Error = error,
            Plan = plan,
            SelectDefOperationId = selectOperation,
            FolderOperationId = folderOperation
        };
}
