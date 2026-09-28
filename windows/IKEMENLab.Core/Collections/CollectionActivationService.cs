using System.Text;
using IKEMENLab.Core.Config;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.SelectDef;
using IKEMENLab.Core.Settings;

namespace IKEMENLab.Core.Collections;

public interface ICollectionActivationService
{
    CollectionActivationPreview Preview(
        string ikemenRoot,
        CharacterCollection? collection,
        IReadOnlyList<CharacterEntry> index,
        bool isAllCharacters = false);

    CollectionActivationResult Activate(
        string ikemenRoot,
        CharacterCollection? collection,
        IReadOnlyList<CharacterEntry> index,
        bool isAllCharacters = false,
        bool dryRun = false);

    CollectionRosterStatus GetRosterStatus(
        string ikemenRoot,
        CharacterCollection? collection,
        IReadOnlyList<CharacterEntry> index,
        bool isAllCharacters = false);
}

/// <summary>
/// Activates a collection as the live [Characters] roster in one SafeMutation transaction.
/// Does not touch stages, config.ini, or character files.
/// </summary>
public sealed class CollectionActivationService : ICollectionActivationService
{
    private readonly ISafeMutationService _mutations;
    private readonly ActiveCollectionStore _activeStore;
    private readonly string _stagingRoot;

    public CollectionActivationService(
        ISafeMutationService? mutations = null,
        ActiveCollectionStore? activeStore = null,
        string? stagingRoot = null)
    {
        _mutations = mutations ?? new SafeMutationService();
        _activeStore = activeStore ?? new ActiveCollectionStore();
        _stagingRoot = stagingRoot ?? Path.Combine(AppDataPaths.GetAppDataDirectory(), "roster-staging");
        Directory.CreateDirectory(_stagingRoot);
    }

    public CollectionActivationPreview Preview(
        string ikemenRoot,
        CharacterCollection? collection,
        IReadOnlyList<CharacterEntry> index,
        bool isAllCharacters = false)
        => BuildPreview(ikemenRoot, collection, index, isAllCharacters);

    public CollectionRosterStatus GetRosterStatus(
        string ikemenRoot,
        CharacterCollection? collection,
        IReadOnlyList<CharacterEntry> index,
        bool isAllCharacters = false)
    {
        try
        {
            var root = IkemenPathGuard.NormalizeRoot(ikemenRoot);
            var config = IkemenConfigReader.Read(root);
            var location = SelectDefLocator.Locate(root, config.Motif);
            if (location.Path is null || !File.Exists(location.Path))
                return CollectionRosterStatus.CannotActivate;

            var desired = DesiredActiveKeys(root, collection, index, isAllCharacters, out var unsafeEmpty);
            if (unsafeEmpty)
                return CollectionRosterStatus.CannotActivate;

            var remembered = _activeStore.Get(root);
            var isRemembered = remembered is not null &&
                ((isAllCharacters && remembered.IsAllCharacters) ||
                 (!isAllCharacters && collection is not null &&
                  remembered.CollectionId == collection.Id && !remembered.IsAllCharacters));

            var matches = RosterMatchesDesired(root, index, desired);
            if (matches)
                return CollectionRosterStatus.Active;
            if (isRemembered)
                return CollectionRosterStatus.Modified;
            return CollectionRosterStatus.NotActive;
        }
        catch
        {
            return CollectionRosterStatus.CannotActivate;
        }
    }

    public CollectionActivationResult Activate(
        string ikemenRoot,
        CharacterCollection? collection,
        IReadOnlyList<CharacterEntry> index,
        bool isAllCharacters = false,
        bool dryRun = false)
    {
        try
        {
            var preview = BuildPreview(ikemenRoot, collection, index, isAllCharacters);
            if (!preview.CanActivate)
            {
                return new CollectionActivationResult
                {
                    Success = false,
                    Error = preview.Error ?? "Cannot activate this collection.",
                    Preview = preview,
                    SelectDefPath = preview.SelectDefPath,
                    Warning = preview.Warning
                };
            }

            var root = IkemenPathGuard.NormalizeRoot(ikemenRoot);
            var selectPath = preview.SelectDefPath!;
            IkemenPathGuard.EnsureInsideRoot(root, selectPath);

            var originalBytes = File.ReadAllBytes(selectPath);
            var originalText = DefFileReader.ReadFileContent(selectPath);
            if (originalText is null)
            {
                return Fail("select.def could not be decoded.", preview);
            }

            var content = originalText;
            var changes = 0;

            foreach (var defPath in preview.WillEnable)
            {
                var edit = SelectDefRosterEditor.Toggle(content, root, RosterContentKind.Character, defPath, RosterToggleAction.Enable);
                if (!edit.Success)
                    return Fail(edit.Error ?? "Enable edit rejected.", preview);
                if (edit.Changed)
                {
                    content = edit.Content;
                    changes++;
                }
            }

            foreach (var defPath in preview.WillDisable)
            {
                var edit = SelectDefRosterEditor.Toggle(content, root, RosterContentKind.Character, defPath, RosterToggleAction.Disable);
                if (!edit.Success)
                    return Fail(edit.Error ?? "Disable edit rejected.", preview);
                if (edit.Changed)
                {
                    content = edit.Content;
                    changes++;
                }
            }

            if (changes == 0)
            {
                if (!dryRun)
                    _activeStore.Set(root, collection?.Id, isAllCharacters);
                return new CollectionActivationResult
                {
                    Success = true,
                    Changed = false,
                    Description = "Roster already matches this collection.",
                    Warning = preview.Warning,
                    SelectDefPath = selectPath,
                    Preview = preview
                };
            }

            // Verify proposed document before write.
            try
            {
                _ = SelectDefReader.Parse(content);
            }
            catch (Exception ex)
            {
                return Fail("Proposed select.def failed to parse: " + ex.Message, preview);
            }

            foreach (var defPath in preview.WillEnable.Concat(preview.AlreadyActive))
            {
                if (SelectDefRosterEditor.ProbeStatus(content, root, RosterContentKind.Character, defPath) != ContentStatus.Active)
                    return Fail($"Proposed roster would not activate '{defPath}'.", preview);
            }

            foreach (var defPath in preview.WillDisable)
            {
                if (SelectDefRosterEditor.ProbeStatus(content, root, RosterContentKind.Character, defPath) != ContentStatus.Disabled)
                    return Fail($"Proposed roster would not disable '{defPath}'.", preview);
            }

            var stagingFile = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N") + ".def");
            var encoding = DetectEncoding(originalBytes);
            File.WriteAllText(stagingFile, content, encoding);

            try
            {
                var plan = _mutations.PlanReplaceFile(root, selectPath, stagingFile);
                if (!plan.IsValid)
                    return Fail(plan.RejectionReason ?? "ReplaceFile plan rejected.", preview, plan);

                if (dryRun)
                {
                    return new CollectionActivationResult
                    {
                        Success = true,
                        Changed = true,
                        Description = "Dry-run: collection activation planned.",
                        Warning = preview.Warning,
                        SelectDefPath = selectPath,
                        Preview = preview,
                        Plan = plan
                    };
                }

                var mutation = _mutations.ReplaceFile(root, selectPath, stagingFile);
                if (!mutation.Success)
                    return Fail(mutation.Error ?? "SafeMutation ReplaceFile failed.", preview, plan);

                // Post-write verification.
                var written = DefFileReader.ReadFileContent(selectPath);
                if (written is null)
                {
                    _mutations.Rollback(mutation.OperationId);
                    return Fail("Post-write read failed; rolled back.", preview, plan, mutation.OperationId);
                }

                try
                {
                    _ = SelectDefReader.Parse(written);
                }
                catch (Exception ex)
                {
                    _mutations.Rollback(mutation.OperationId);
                    return Fail("Post-write parse failed; rolled back: " + ex.Message, preview, plan, mutation.OperationId);
                }

                foreach (var defPath in preview.WillEnable.Concat(preview.AlreadyActive))
                {
                    if (SelectDefRosterEditor.ProbeStatus(written, root, RosterContentKind.Character, defPath) != ContentStatus.Active)
                    {
                        _mutations.Rollback(mutation.OperationId);
                        return Fail($"Post-write status for '{defPath}' was not Active; rolled back.", preview, plan, mutation.OperationId);
                    }
                }

                foreach (var defPath in preview.WillDisable)
                {
                    if (SelectDefRosterEditor.ProbeStatus(written, root, RosterContentKind.Character, defPath) != ContentStatus.Disabled)
                    {
                        _mutations.Rollback(mutation.OperationId);
                        return Fail($"Post-write status for '{defPath}' was not Disabled; rolled back.", preview, plan, mutation.OperationId);
                    }
                }

                // Stages / ExtraStages must be byte-equivalent in section sense — verify stage lines still parse.
                // Soft check: ExtraStages content names present in original remain present.
                if (!ExtraStagesPreserved(originalText, written))
                {
                    _mutations.Rollback(mutation.OperationId);
                    return Fail("ExtraStages section was altered; rolled back.", preview, plan, mutation.OperationId);
                }

                _activeStore.Set(root, collection?.Id, isAllCharacters);

                return new CollectionActivationResult
                {
                    Success = true,
                    Changed = true,
                    Description = $"Activated \"{preview.CollectionName}\" ({preview.WillEnable.Count} enabled, {preview.WillDisable.Count} disabled).",
                    Warning = preview.Warning,
                    SelectDefPath = selectPath,
                    OperationId = mutation.OperationId,
                    Preview = preview,
                    Plan = plan
                };
            }
            finally
            {
                try { if (File.Exists(stagingFile)) File.Delete(stagingFile); }
                catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            return new CollectionActivationResult { Success = false, Error = ex.Message };
        }
    }

    private CollectionActivationPreview BuildPreview(
        string ikemenRoot,
        CharacterCollection? collection,
        IReadOnlyList<CharacterEntry> index,
        bool isAllCharacters)
    {
        var name = isAllCharacters ? "All Characters" : collection?.Name ?? "(unknown)";
        try
        {
            var root = IkemenPathGuard.NormalizeRoot(ikemenRoot);
            var config = IkemenConfigReader.Read(root);
            var location = SelectDefLocator.Locate(root, config.Motif);
            if (location.Path is null || !File.Exists(location.Path))
            {
                return Blocked(name, collection, isAllCharacters, "Could not resolve an active select.def for this installation.");
            }

            var originalText = DefFileReader.ReadFileContent(location.Path);
            if (originalText is null)
            {
                return Blocked(name, collection, isAllCharacters, "select.def could not be decoded.", location.Path);
            }

            try
            {
                _ = SelectDefReader.Parse(originalText);
            }
            catch (Exception ex)
            {
                return Blocked(name, collection, isAllCharacters, "select.def is malformed: " + ex.Message, location.Path);
            }

            var desired = DesiredActiveKeys(root, collection, index, isAllCharacters, out var unsafeEmpty, out var missing, out var missingNames, out var presentMembers);
            if (unsafeEmpty)
            {
                return Blocked(name, collection, isAllCharacters,
                    "Every collection member is missing from disk. Activation would disable the whole roster with nothing to enable.",
                    location.Path,
                    missing: missing,
                    missingNames: missingNames);
            }

            var willEnable = new List<(string Def, string Name)>();
            var willDisable = new List<(string Def, string Name)>();
            var alreadyActive = new List<(string Def, string Name)>();
            var ambiguous = new List<(string Def, string Name)>();

            var desiredKeys = desired.ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in index)
            {
                var key = NormDef(root, entry.DefPath);
                var inCollection = desiredKeys.Contains(key);
                var status = entry.Status;

                if (inCollection)
                {
                    if (status == ContentStatus.Active)
                        alreadyActive.Add((entry.DefPath, entry.DisplayName));
                    else
                        willEnable.Add((entry.DefPath, entry.DisplayName));
                }
                else if (status == ContentStatus.Active)
                {
                    willDisable.Add((entry.DefPath, entry.DisplayName));
                }
            }

            // Probe edits on a working copy for ambiguity / fail-closed.
            // Include AlreadyActive so duplicate select.def lines are still detected.
            var content = originalText;
            foreach (var (def, display) in willEnable.Concat(alreadyActive))
            {
                var edit = SelectDefRosterEditor.Toggle(content, root, RosterContentKind.Character, def, RosterToggleAction.Enable);
                if (!edit.Success)
                {
                    if (!ambiguous.Any(a => a.Def.Equals(def, StringComparison.OrdinalIgnoreCase)))
                        ambiguous.Add((def, display));
                    continue;
                }

                if (edit.Changed) content = edit.Content;
            }

            foreach (var (def, display) in willDisable)
            {
                var edit = SelectDefRosterEditor.Toggle(content, root, RosterContentKind.Character, def, RosterToggleAction.Disable);
                if (!edit.Success)
                {
                    if (!ambiguous.Any(a => a.Def.Equals(def, StringComparison.OrdinalIgnoreCase)))
                        ambiguous.Add((def, display));
                    continue;
                }

                if (edit.Changed) content = edit.Content;
            }

            string? warning = null;
            if (missing.Count > 0)
                warning = $"{missing.Count} member(s) missing from disk will be skipped.";
            if (isAllCharacters == false && collection is { Kind: CollectionKind.Manual, Members.Count: 0 } && presentMembers == 0)
                warning = (warning is null ? "" : warning + " ") + "Collection is empty; all active characters will be disabled.";

            var canActivate = ambiguous.Count == 0;
            string? error = null;
            if (!canActivate)
                error = $"Ambiguous select.def entries for {ambiguous.Count} character(s). Resolve duplicates manually before activating.";

            return new CollectionActivationPreview
            {
                CollectionName = name,
                CollectionId = collection?.Id,
                IsAllCharacters = isAllCharacters,
                TotalResolvedMembers = presentMembers,
                WillEnable = willEnable.Select(x => x.Def).ToArray(),
                WillDisable = willDisable.Select(x => x.Def).ToArray(),
                AlreadyActive = alreadyActive.Select(x => x.Def).ToArray(),
                Missing = missing,
                Ambiguous = ambiguous.Select(x => x.Def).ToArray(),
                WillEnableNames = willEnable.Select(x => x.Name).ToArray(),
                WillDisableNames = willDisable.Select(x => x.Name).ToArray(),
                AlreadyActiveNames = alreadyActive.Select(x => x.Name).ToArray(),
                MissingNames = missingNames,
                AmbiguousNames = ambiguous.Select(x => x.Name).ToArray(),
                CanActivate = canActivate,
                Error = error,
                Warning = warning?.Trim(),
                SelectDefPath = location.Path
            };
        }
        catch (Exception ex)
        {
            return Blocked(name, collection, isAllCharacters, ex.Message);
        }
    }

    private static HashSet<string> DesiredActiveKeys(
        string root,
        CharacterCollection? collection,
        IReadOnlyList<CharacterEntry> index,
        bool isAllCharacters,
        out bool unsafeEmpty)
        => DesiredActiveKeys(root, collection, index, isAllCharacters, out unsafeEmpty, out _, out _, out _);

    private static HashSet<string> DesiredActiveKeys(
        string root,
        CharacterCollection? collection,
        IReadOnlyList<CharacterEntry> index,
        bool isAllCharacters,
        out bool unsafeEmpty,
        out IReadOnlyList<string> missing,
        out IReadOnlyList<string> missingNames,
        out int presentMembers)
    {
        unsafeEmpty = false;
        missing = [];
        missingNames = [];
        presentMembers = 0;

        if (isAllCharacters || collection is null && isAllCharacters)
        {
            var all = index.Select(c => NormDef(root, c.DefPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            presentMembers = all.Count;
            return all;
        }

        if (collection is null)
        {
            unsafeEmpty = true;
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var resolved = CollectionMembership.Resolve(collection, index);
        var miss = new List<string>();
        var missNames = new List<string>();
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in resolved)
        {
            if (m.Character is null)
            {
                miss.Add(m.Member.CharacterId);
                missNames.Add(m.Member.DisplayName);
                continue;
            }

            desired.Add(NormDef(root, m.Character.DefPath));
        }

        missing = miss;
        missingNames = missNames;
        presentMembers = desired.Count;

        // Manual collection with members listed but none on disk → unsafe.
        if (collection.Kind == CollectionKind.Manual && collection.Members.Count > 0 && desired.Count == 0)
            unsafeEmpty = true;

        return desired;
    }

    private static bool RosterMatchesDesired(string root, IReadOnlyList<CharacterEntry> index, HashSet<string> desired)
    {
        foreach (var entry in index)
        {
            var key = NormDef(root, entry.DefPath);
            var shouldBeActive = desired.Contains(key);
            var isActive = entry.Status == ContentStatus.Active;
            if (shouldBeActive != isActive)
                return false;
        }

        return true;
    }

    private static string NormDef(string root, string relative)
        => SelectDefRosterEditor.NormalizeKey(root, relative);

    private static bool ExtraStagesPreserved(string before, string after)
    {
        static IEnumerable<string> StageBodies(string text)
        {
            var lines = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
            var inStages = false;
            foreach (var line in lines)
            {
                var t = line.Trim(' ', '\t');
                if (t.StartsWith('[') && t.Contains(']'))
                {
                    var end = t.IndexOf(']');
                    var name = t[1..end].Trim();
                    inStages = name.Equals("ExtraStages", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inStages) continue;
                yield return line;
            }
        }

        return StageBodies(before).SequenceEqual(StageBodies(after));
    }

    private static CollectionActivationPreview Blocked(
        string name,
        CharacterCollection? collection,
        bool isAllCharacters,
        string error,
        string? selectDefPath = null,
        IReadOnlyList<string>? missing = null,
        IReadOnlyList<string>? missingNames = null)
        => new()
        {
            CollectionName = name,
            CollectionId = collection?.Id,
            IsAllCharacters = isAllCharacters,
            CanActivate = false,
            Error = error,
            SelectDefPath = selectDefPath,
            Missing = missing ?? [],
            MissingNames = missingNames ?? []
        };

    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    private static CollectionActivationResult Fail(
        string error,
        CollectionActivationPreview preview,
        OperationPlan? plan = null,
        string? operationId = null)
        => new()
        {
            Success = false,
            Error = error,
            Preview = preview,
            SelectDefPath = preview.SelectDefPath,
            Warning = preview.Warning,
            Plan = plan,
            OperationId = operationId
        };
}
