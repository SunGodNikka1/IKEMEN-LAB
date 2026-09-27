namespace IKEMENLab.Core.SelectDef;

public enum SelectDefSection
{
    Characters,
    ExtraStages
}

public enum SelectDefEntryKind
{
    /// <summary>A character/stage reference.</summary>
    Content,

    /// <summary>"randomselect" slot.</summary>
    RandomSelect,

    /// <summary>"empty" slot.</summary>
    Empty,

    /// <summary>A line IKEMEN would reject (e.g. a bare "kfm.def" without a folder).</summary>
    Invalid
}

/// <summary>
/// One roster line from [Characters] or [ExtraStages].
/// </summary>
public sealed record SelectDefEntry
{
    public required SelectDefSection Section { get; init; }

    /// <summary>1-based line number in the source file.</summary>
    public required int LineNumber { get; init; }

    /// <summary>First comma field exactly as written (trimmed, original separators).</summary>
    public required string RawName { get; init; }

    public required SelectDefEntryKind Kind { get; init; }

    /// <summary>True when the whole line was commented out with a leading ';'.</summary>
    public required bool IsCommented { get; init; }

    /// <summary>
    /// Candidate DEF paths relative to the IKEMEN root, forward slashes, in IKEMEN lookup order.
    /// Empty for non-content kinds.
    /// </summary>
    public IReadOnlyList<string> CandidateDefPaths { get; init; } = Array.Empty<string>();

    /// <summary>The candidate that exists on disk, if resolution against a root was performed.</summary>
    public string? ResolvedDefPath { get; init; }

    /// <summary>
    /// True when the DEF lives inside an existing .zip (IKEMEN can load archived content).
    /// The archive exists but its contents are not inspected, so the entry is neither missing nor indexed.
    /// </summary>
    public bool IsInArchive { get; init; }
}

public sealed class SelectDefDocument
{
    public required string Path { get; init; }
    public required IReadOnlyList<SelectDefEntry> Entries { get; init; }

    public IEnumerable<SelectDefEntry> Characters => Entries.Where(e => e.Section == SelectDefSection.Characters);
    public IEnumerable<SelectDefEntry> Stages => Entries.Where(e => e.Section == SelectDefSection.ExtraStages);
}
