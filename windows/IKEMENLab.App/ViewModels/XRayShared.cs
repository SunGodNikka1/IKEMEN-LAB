using System.IO;
using System.Windows.Media;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Source;

namespace IKEMENLab.App.ViewModels;

/// <summary>
/// The one place that decides how confidence looks. Every lens uses it, so a heuristic can never be drawn like a proven fact:
/// filled disc + solid line = StaticProven, half disc + dashed = Inferred, "?" + dotted = Unknown.
/// </summary>
public static class ConfidenceStyle
{
    private static readonly Brush Proven = Frozen(Color.FromRgb(0x34, 0xC7, 0x59));
    private static readonly Brush InferredBrush = Frozen(Color.FromRgb(0xFF, 0xB8, 0x4D));
    private static readonly Brush UnknownBrush = Frozen(Color.FromRgb(0x8E, 0x8E, 0x99));
    private static readonly Brush Runtime = Frozen(Color.FromRgb(0x0A, 0x84, 0xFF));

    public static string Glyph(Confidence? c) => c switch
    {
        Confidence.StaticProven => "●",
        Confidence.Inferred => "◐",
        Confidence.RuntimeVerified => "◆",
        Confidence.Unknown => "?",
        _ => "·"
    };

    public static string Label(Confidence? c) => c switch
    {
        Confidence.StaticProven => "Static proven",
        Confidence.Inferred => "Inferred",
        Confidence.RuntimeVerified => "Runtime verified",
        Confidence.Unknown => "Unknown",
        _ => string.Empty
    };

    public static Brush Brush(Confidence? c) => c switch
    {
        Confidence.StaticProven => Proven,
        Confidence.Inferred => InferredBrush,
        Confidence.RuntimeVerified => Runtime,
        _ => UnknownBrush
    };

    /// <summary>Stroke dash pattern in units of line width; null = solid.</summary>
    public static DoubleCollection? Dash(Confidence? c)
    {
        var dash = c switch
        {
            Confidence.Inferred => new DoubleCollection { 4, 3 },
            Confidence.Unknown => new DoubleCollection { 1, 3 },
            _ => null
        };
        dash?.Freeze();
        return dash;
    }

    public static string Legend => "● static proven (the files say it literally)   ◐ inferred (a heuristic)   ? unknown";

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>One selectable line in a lens or in the details panel. Highlighting is driven by the shared selection.</summary>
public sealed class XRayRow : ObservableObject
{
    private bool _isHighlighted;

    public XRayRow(string id, string title, string? subtitle = null, Confidence? confidence = null, string? group = null, int groupOrder = 0, string? tooltip = null)
    {
        Id = id;
        Title = title;
        Subtitle = subtitle ?? string.Empty;
        Confidence = confidence;
        Group = group ?? string.Empty;
        GroupOrder = groupOrder;
        Tooltip = tooltip ?? string.Empty;
    }

    public string Id { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Group { get; }
    public int GroupOrder { get; }
    public string Tooltip { get; }
    public Confidence? Confidence { get; }
    public string Glyph => ConfidenceStyle.Glyph(Confidence);
    public Brush GlyphBrush => ConfidenceStyle.Brush(Confidence);
    public bool HasConfidence => Confidence is not null;
    public bool HasSubtitle => Subtitle.Length > 0;
    public bool IsHighlighted { get => _isHighlighted; set => SetProperty(ref _isHighlighted, value); }
}

/// <summary>A line of a source file, with the span the selected object occupies marked.</summary>
public sealed record SourceLine(int Number, string Text, bool InSpan);

public sealed class DetailSection
{
    public DetailSection(string title, IReadOnlyList<XRayRow> items)
    {
        Title = title + $" ({items.Count})";
        Items = items;
    }

    public string Title { get; }
    public IReadOnlyList<XRayRow> Items { get; }
}

public sealed record LabelChip(string Text, string Category, string Glyph, Brush Brush, string Tooltip);

/// <summary>Base for the six lenses. Each one is a view of the same <see cref="SemanticIndex"/> and reacts to the same selection.</summary>
public abstract class XRayLens : ObservableObject
{
    protected XRayViewModel Owner { get; }
    protected XRayLens(XRayViewModel owner) => Owner = owner;

    public abstract void Build(SemanticIndex index);
    public abstract void OnSelection(SemanticIndex index, string? id, IReadOnlySet<string> related);

    protected static string Short(string id)
    {
        var slash = id.LastIndexOf('/');
        return slash >= 0 ? id[(slash + 1)..] : id;
    }

    protected static string FileLine(SemanticIndex index, SourceRef? source)
    {
        if (source is not { } s || index.FileOf(s) is not { } file) return string.Empty;
        var name = Path.GetFileName(file.RelPath);
        return s.StartLine == s.EndLine ? $"{name}:{s.StartLine}" : $"{name}:{s.StartLine}–{s.EndLine}";
    }
}
