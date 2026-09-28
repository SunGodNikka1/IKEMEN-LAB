using IKEMENLab.Core.Config;
using IKEMENLab.Core.Library;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Screenpacks;
using IKEMENLab.Core.SelectDef;

namespace IKEMENLab.Core.Services;

/// <summary>
/// In-memory library index. Filesystem is the source of truth. No database.
/// </summary>
public sealed class LibraryIndexService
{
    private readonly IkemenInstallationValidator _validator = new();
    private readonly CharacterIndexer _characterIndexer = new();
    private readonly StageIndexer _stageIndexer = new();
    private readonly DateAddedTracker _dateAdded;
    private readonly PrimaryDefStore? _primaryDefs;

    /// <param name="dateAdded">
    /// Persistent Date Added tracker. Omit for read-only tools/tests: entries then carry a
    /// non-persisted estimate instead of an app-owned record.
    /// </param>
    /// <param name="primaryDefs">Saved DEF choices (app data). Omit for read-only tools/tests.</param>
    public LibraryIndexService(DateAddedTracker? dateAdded = null, PrimaryDefStore? primaryDefs = null)
    {
        _dateAdded = dateAdded ?? DateAddedTracker.EstimateOnly;
        _primaryDefs = primaryDefs;
    }

    public LibrarySnapshot Index(string rootPath)
    {
        var installation = _validator.Validate(rootPath);
        if (!installation.CanBrowse)
        {
            return new LibrarySnapshot
            {
                Installation = installation,
                Characters = Array.Empty<CharacterEntry>(),
                Stages = Array.Empty<StageEntry>(),
                Warnings =
                [
                    new IndexWarning
                    {
                        Path = installation.RootPath,
                        Message = "Installation structure incomplete: " +
                                  string.Join(", ", installation.MissingItems)
                    }
                ]
            };
        }

        var root = installation.RootPath;

        // Registration status is derived read-only from the select.def IKEMEN actually loads. It also
        // tells the character index which DEF of a folder the roster actually uses.
        var config = IkemenConfigReader.Read(root);
        var selectDef = SelectDefIndex.Build(root, SelectDefLocator.Locate(root, config.Motif));
        var appWarnings = new List<IndexWarning>();
        var characters = _characterIndexer.Index(root, out var charWarnings, new CharacterIndexContext
        {
            Roster = selectDef.IsAvailable ? selectDef.CharacterReferencesUnder : null,
            SavedChoices = ReadSavedChoices(root, appWarnings)
        });
        var stages = _stageIndexer.Index(root, out var stageWarnings);
        var warnings = charWarnings.Concat(stageWarnings).Concat(appWarnings).ToList();

        if (selectDef.IsAvailable)
        {
            stages = stages
                .Select(s => s with { Status = selectDef.StageStatus(s.RootRelativeDefPath) })
                .ToList();
        }

        (characters, stages) = ApplyDateAdded(root, characters, stages, warnings);

        return new LibrarySnapshot
        {
            Installation = installation,
            Characters = characters,
            Stages = stages,
            Warnings = warnings,
            IndexedAt = DateTimeOffset.UtcNow,
            Config = config,
            SelectDef = selectDef,
            Screenpacks = ScreenpackIndexer.Index(root, config.Motif)
        };
    }

    private IReadOnlyDictionary<string, string>? ReadSavedChoices(string root, List<IndexWarning> warnings)
    {
        if (_primaryDefs is null) return null;
        try
        {
            return _primaryDefs.Read(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add(new IndexWarning { Path = "IKEMEN Lab library data", Message = "Saved DEF choices unavailable: " + ex.Message });
            return null;
        }
    }

    private (IReadOnlyList<CharacterEntry>, IReadOnlyList<StageEntry>) ApplyDateAdded(
        string root, IReadOnlyList<CharacterEntry> characters, IReadOnlyList<StageEntry> stages, List<IndexWarning> warnings)
    {
        try
        {
            return _dateAdded.Apply(root, characters, stages, healthyScan: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // App-data trouble must never break browsing; fall back to (unsaved) estimates.
            warnings.Add(new IndexWarning { Path = "IKEMEN Lab library data", Message = "Date Added records unavailable: " + ex.Message });
            return DateAddedTracker.EstimateOnly.Apply(root, characters, stages, healthyScan: false);
        }
    }
}
