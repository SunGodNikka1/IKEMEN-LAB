using IKEMENLab.Core.Models;

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

        var characters = _characterIndexer.Index(installation.RootPath, out var charWarnings);
        var stages = _stageIndexer.Index(installation.RootPath, out var stageWarnings);
        var warnings = charWarnings.Concat(stageWarnings).ToList();

        return new LibrarySnapshot
        {
            Installation = installation,
            Characters = characters,
            Stages = stages,
            Warnings = warnings,
            IndexedAt = DateTimeOffset.UtcNow
        };
    }
}
