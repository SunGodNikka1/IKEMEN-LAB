namespace IKEMENLab.Core.XRay.Source;

public enum SourceRole { Def, Cmd, Cns, St, Air, Common }

/// <summary>A file the index was built from. <see cref="RelPath"/> uses '/' and is relative to the IKEMEN root when possible.</summary>
public sealed record SourceFile(int Id, string RelPath, SourceRole Role, bool IsCommon, string Hash);

/// <summary>1-based inclusive line span in one <see cref="SourceFile"/>.</summary>
public readonly record struct SourceRef(int FileId, int StartLine, int EndLine)
{
    public static SourceRef At(int fileId, int line) => new(fileId, line, line);
    public SourceRef Through(int endLine) => this with { EndLine = Math.Max(StartLine, endLine) };
}

public enum DiagnosticSeverity { Info, Warning, Error }

public sealed record Diagnostic(DiagnosticSeverity Severity, string Code, string Message, SourceRef? Source);

/// <summary>One line inside a block: <c>key = value</c>, or a bare line (AIR frames, stray text) with <see cref="HadEquals"/> false.</summary>
public sealed record RawEntry(string Key, string Value, int Line, bool HadEquals, string? TrailingComment);

/// <summary>A <c>[Header]</c> and the lines up to the next header. <see cref="Header"/> is empty for lines before the first header.</summary>
public sealed class RawBlock
{
    public required string Header { get; init; }
    public required int HeaderLine { get; init; }
    public int EndLine { get; set; }
    public List<RawEntry> Entries { get; } = [];
    /// <summary>Comment lines directly above the header (a blank line ends the run); often the author's name for the block.</summary>
    public List<string> LeadingComments { get; } = [];
    public SourceRef Span(int fileId) => new(fileId, HeaderLine, Math.Max(HeaderLine, EndLine));
}

public sealed class LexResult
{
    public List<RawBlock> Blocks { get; } = [];
    public List<Diagnostic> Diagnostics { get; } = [];
}
