using System.Text;
using IKEMENLab.Core.Config;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.Settings;

namespace IKEMENLab.Core.SelectDef;

public interface IRosterActivationService
{
    RosterActivationResult SetCharacterEnabled(string ikemenRoot, string rootRelativeDefPath, bool enabled, bool dryRun = false);
    RosterActivationResult SetStageEnabled(string ikemenRoot, string rootRelativeDefPath, bool enabled, bool dryRun = false);
}

public sealed class RosterActivationResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public string? Description { get; init; }
    public string? SelectDefPath { get; init; }
    public string? OperationId { get; init; }
    public ContentStatus? ResultingStatus { get; init; }
    public bool Changed { get; init; }
    public OperationPlan? Plan { get; init; }
}

/// <summary>
/// Enables/disables characters and stages in the active select.def via SafeMutationService.
/// Never regenerates the whole file; never touches chars/, stages/, or config.ini.
/// </summary>
public sealed class RosterActivationService : IRosterActivationService
{
    private readonly ISafeMutationService _mutations;
    private readonly string _stagingRoot;

    public RosterActivationService(ISafeMutationService? mutations = null, string? stagingRoot = null)
    {
        _mutations = mutations ?? new SafeMutationService();
        _stagingRoot = stagingRoot ?? Path.Combine(AppDataPaths.GetAppDataDirectory(), "roster-staging");
        Directory.CreateDirectory(_stagingRoot);
    }

    public RosterActivationResult SetCharacterEnabled(string ikemenRoot, string rootRelativeDefPath, bool enabled, bool dryRun = false)
        => SetEnabled(ikemenRoot, rootRelativeDefPath, RosterContentKind.Character, enabled, dryRun);

    public RosterActivationResult SetStageEnabled(string ikemenRoot, string rootRelativeDefPath, bool enabled, bool dryRun = false)
        => SetEnabled(ikemenRoot, rootRelativeDefPath, RosterContentKind.Stage, enabled, dryRun);

    private RosterActivationResult SetEnabled(
        string ikemenRoot,
        string rootRelativeDefPath,
        RosterContentKind kind,
        bool enabled,
        bool dryRun)
    {
        try
        {
            var root = IkemenPathGuard.NormalizeRoot(ikemenRoot);
            var config = IkemenConfigReader.Read(root);
            var location = SelectDefLocator.Locate(root, config.Motif);
            if (location.Path is null || !File.Exists(location.Path))
            {
                return Fail("Could not resolve an active select.def for this installation.");
            }

            IkemenPathGuard.EnsureInsideRoot(root, location.Path);

            var originalText = DefFileReader.ReadFileContent(location.Path);
            if (originalText is null)
            {
                return Fail("select.def could not be decoded.", location.Path);
            }

            var originalBytes = File.ReadAllBytes(location.Path);
            var action = enabled ? RosterToggleAction.Enable : RosterToggleAction.Disable;
            var edit = SelectDefRosterEditor.Toggle(originalText, root, kind, rootRelativeDefPath, action);
            if (!edit.Success)
            {
                return Fail(edit.Error ?? "Roster edit rejected.", location.Path);
            }

            if (!edit.Changed)
            {
                return new RosterActivationResult
                {
                    Success = true,
                    Changed = false,
                    Description = edit.Description,
                    SelectDefPath = location.Path,
                    ResultingStatus = enabled ? ContentStatus.Active : ContentStatus.Disabled
                };
            }

            // Write proposed content to staging (outside IKEMEN root), then ReplaceFile.
            var stagingFile = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N") + ".def");
            var encoding = DetectEncoding(originalBytes);
            File.WriteAllText(stagingFile, edit.Content, encoding);

            try
            {
                var plan = _mutations.PlanReplaceFile(root, location.Path, stagingFile);
                if (!plan.IsValid)
                {
                    return Fail(plan.RejectionReason ?? "ReplaceFile plan rejected.", location.Path, plan);
                }

                if (dryRun)
                {
                    return new RosterActivationResult
                    {
                        Success = true,
                        Changed = true,
                        Description = "Dry-run: " + edit.Description,
                        SelectDefPath = location.Path,
                        Plan = plan,
                        ResultingStatus = enabled ? ContentStatus.Active : ContentStatus.Disabled
                    };
                }

                var mutation = _mutations.ReplaceFile(root, location.Path, stagingFile);
                if (!mutation.Success)
                {
                    return Fail(mutation.Error ?? "SafeMutation ReplaceFile failed.", location.Path, plan);
                }

                // Post-write verification.
                var written = DefFileReader.ReadFileContent(location.Path);
                if (written is null)
                {
                    _mutations.Rollback(mutation.OperationId);
                    return Fail("Post-write read failed; rolled back.", location.Path, plan, mutation.OperationId);
                }

                ContentStatus status;
                try
                {
                    _ = SelectDefReader.Parse(written);
                    status = SelectDefRosterEditor.ProbeStatus(written, root, kind, rootRelativeDefPath);
                }
                catch (Exception ex)
                {
                    _mutations.Rollback(mutation.OperationId);
                    return Fail("Post-write parse failed; rolled back: " + ex.Message, location.Path, plan, mutation.OperationId);
                }

                var expected = enabled ? ContentStatus.Active : ContentStatus.Disabled;
                if (status != expected)
                {
                    _mutations.Rollback(mutation.OperationId);
                    return Fail(
                        $"Post-write status was {status}, expected {expected}; rolled back.",
                        location.Path, plan, mutation.OperationId);
                }

                return new RosterActivationResult
                {
                    Success = true,
                    Changed = true,
                    Description = edit.Description,
                    SelectDefPath = location.Path,
                    OperationId = mutation.OperationId,
                    ResultingStatus = status,
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
            return Fail(ex.Message);
        }
    }

    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    private static RosterActivationResult Fail(
        string error,
        string? selectDefPath = null,
        OperationPlan? plan = null,
        string? operationId = null)
        => new()
        {
            Success = false,
            Error = error,
            SelectDefPath = selectDefPath,
            Plan = plan,
            OperationId = operationId
        };
}
