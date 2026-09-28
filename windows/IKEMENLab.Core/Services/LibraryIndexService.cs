using IKEMENLab.Core.Config;
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
        var characters = _characterIndexer.Index(root, out var charWarnings);
        var stages = _stageIndexer.Index(root, out var stageWarnings);
        var warnings = charWarnings.Concat(stageWarnings).ToList();

        // Registration status is derived read-only from the select.def IKEMEN actually loads.
        var config = IkemenConfigReader.Read(root);
        var selectDef = SelectDefIndex.Build(root, SelectDefLocator.Locate(root, config.Motif));
        if (selectDef.IsAvailable)
        {
            characters = characters
                .Select(c => c with { Status = selectDef.CharacterStatus(c.DefPath) })
                .ToList();
            stages = stages
                .Select(s => s with { Status = selectDef.StageStatus(s.RootRelativeDefPath) })
                .ToList();
        }

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
}
