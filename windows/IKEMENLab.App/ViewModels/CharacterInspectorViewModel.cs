using System.Windows.Input;
using System.Windows.Media;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.Core.Characters;
using IKEMENLab.Core.Models;

namespace IKEMENLab.App.ViewModels;

public sealed record AttributeRow(string Label, string ValueText, double Fraction, Brush Fill, string ToolTip);

public sealed record PaletteSwatch(Brush Fill, string ToolTip);

public sealed record MoveRow(string Name, string Notation, bool IsHyper);

public sealed record DefSegment(string Text, Brush Foreground, bool Bold);

public sealed record DefLine(IReadOnlyList<DefSegment> Segments);

/// <summary>Right-hand inspector for the selected character. Read-only except primary DEF choice and Delete.</summary>
public sealed class CharacterInspectorViewModel : ObservableObject
{
    private static readonly Brush LifeBrush = Frozen(Colors.White);
    private static readonly Brush StatBrush = Frozen(Color.FromRgb(0xA1, 0xA1, 0xAA));
    private static readonly Brush PowerBrush = Frozen(Color.FromArgb(0xB3, 0x0A, 0x84, 0xFF));
    private static readonly Brush SectionBrush = Frozen(Color.FromRgb(0x66, 0x99, 0xFF));
    private static readonly Brush KeyBrush = Frozen(Color.FromRgb(0xCC, 0x99, 0xFF));
    private static readonly Brush ValueBrush = Frozen(Colors.White);
    private static readonly Brush CommentBrush = Frozen(Color.FromRgb(0x71, 0x71, 0x7A));
    private static readonly Brush PlainBrush = Frozen(Color.FromRgb(0xA1, 0xA1, 0xAA));

    private ImageSource? _portrait;
    private bool _isLoading = true;
    private CharacterDetails? _details;
    private string? _selectedDef;
    private readonly Func<CharacterRowViewModel?, string?, Task>? _setPrimaryDef;
    private bool _suppressDefChange;

    public CharacterInspectorViewModel(
        CharacterRowViewModel row,
        Func<CharacterRowViewModel?, string?, Task>? setPrimaryDef = null,
        ICommand? deleteCommand = null,
        ICommand? openSpritesCommand = null,
        ICommand? openTuningCommand = null)
    {
        Row = row;
        OpenSpritesCommand = openSpritesCommand;
        OpenTuningCommand = openTuningCommand;
        _setPrimaryDef = setPrimaryDef;
        DeleteCommand = deleteCommand;
        _selectedDef = CurrentDefRelative();
        DefFileChoices = row.Entry.DefCandidates
            .Select(c =>
            {
                var prefix = "chars/" + row.Entry.Id + "/";
                return c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? c[prefix.Length..]
                    : System.IO.Path.GetFileName(c);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public CharacterRowViewModel Row { get; }

    /// <summary>Delete Character (the browser's command; parameter is <see cref="Row"/>).</summary>
    public ICommand? DeleteCommand { get; }

    public bool CanShowDelete => DeleteCommand is not null;

    /// <summary>Sprite Inspector for this character's SFF (parameter is <see cref="Row"/>).</summary>
    public ICommand? OpenSpritesCommand { get; }

    /// <summary>Size &amp; Stats editor for this character's CNS (parameter is <see cref="Row"/>).</summary>
    public ICommand? OpenTuningCommand { get; }

    public bool CanShowTools => OpenSpritesCommand is not null && OpenTuningCommand is not null;

    /// <summary>The package a delete removes: the character's top-level folder under chars/.</summary>
    public string PackageFolder => "chars/" + IKEMENLab.Core.Library.ContentIdentity.TopFolder(Row.Entry.Id);

    public string DeleteHint =>
        $"Deletes {PackageFolder} and removes its select.def entries (the characters after it move up). " +
        "A backup copy is kept in IKEMEN Lab's app data.";

    public string Name => Row.DisplayName;
    public string Author => Row.Author;

    public IReadOnlyList<string> DefFileChoices { get; }
    public bool ShowDefPicker => DefFileChoices.Count > 1;
    public bool NeedsDefChoice => Row.Entry.NeedsDefChoice;

    public string? SelectedDef
    {
        get => _selectedDef;
        set
        {
            if (_suppressDefChange) return;
            if (!SetProperty(ref _selectedDef, value) || value is null || _setPrimaryDef is null) return;
            if (string.Equals(value, CurrentDefRelative(), StringComparison.OrdinalIgnoreCase)) return;
            _ = ApplyPrimaryDefAsync(value);
        }
    }

    public string DefChoiceHint => NeedsDefChoice
        ? "Several DEFs could be primary — pick one. The character folder name does not change."
        : "Switching the primary DEF does not rename the character folder.";

    private async Task ApplyPrimaryDefAsync(string relative)
    {
        try
        {
            await _setPrimaryDef!(Row, relative);
        }
        catch
        {
            _suppressDefChange = true;
            _selectedDef = CurrentDefRelative();
            OnPropertyChanged(nameof(SelectedDef));
            _suppressDefChange = false;
        }
    }

    private string CurrentDefRelative()
    {
        var path = Row.Entry.DefPath.Replace('\\', '/');
        var prefix = "chars/" + Row.Entry.Id + "/";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? path[prefix.Length..]
            : System.IO.Path.GetFileName(path);
    }

    public ImageSource? Portrait
    {
        get => _portrait;
        set => SetProperty(ref _portrait, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string EngineLabel => _details?.EngineLabel ?? "\u2026";
    public string UpdatedText => string.IsNullOrEmpty(_details?.VersionDate) ? string.Empty : "Updated " + _details!.VersionDate;
    public string VersionText => string.IsNullOrEmpty(_details?.VersionDate) ? "\u2014" : _details!.VersionDate;

    public bool HasStats => _details?.Stats is not null;
    public string StatsNote => _details is null ? string.Empty : HasStats ? "Based on CNS data" : "CNS not found — stats unavailable";
    public IReadOnlyList<AttributeRow> Attributes { get; private set; } = [];

    public string PaletteHeader => $"Palettes ({_details?.Palettes.Count ?? 0})";
    public IReadOnlyList<PaletteSwatch> Palettes { get; private set; } = [];
    public string? MorePalettesText { get; private set; }
    public bool HasPalettes => Palettes.Count > 0;

    public IReadOnlyList<MoveRow> Moves { get; private set; } = [];
    public string? MoreMovesText { get; private set; }
    public bool HasMoves => Moves.Count > 0;

    public string DefFileName => System.IO.Path.GetFileName(Row.Entry.DefPath);
    public IReadOnlyList<DefLine> DefLines { get; private set; } = [];

    public void Apply(CharacterDetails details)
    {
        _details = details;

        if (details.Stats is { } s)
        {
            Attributes =
            [
                Attr("Life", s.Life, CharacterStats.MaxLife, LifeBrush),
                Attr("Atk", s.Attack, CharacterStats.MaxAttack, StatBrush),
                Attr("Def", s.Defence, CharacterStats.MaxDefence, StatBrush),
                Attr("Pow", s.Power, CharacterStats.MaxPower, PowerBrush)
            ];
        }

        Palettes = details.Palettes.Take(6)
            .Select(p => new PaletteSwatch(
                p.SwatchArgb is { } argb ? Frozen(Color.FromArgb(255, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)) : StatBrush,
                $"Palette {p.Number}: {p.Source}"))
            .ToList();
        MorePalettesText = details.Palettes.Count > 6 ? $"+{details.Palettes.Count - 6}" : null;

        Moves = details.Moves.Take(10).Select(m => new MoveRow(m.DisplayName, m.Notation, m.IsHyper)).ToList();
        MoreMovesText = details.Moves.Count > 10 ? $"+{details.Moves.Count - 10} more moves\u2026" : null;

        DefLines = Highlight(details.DefText);
        IsLoading = false;
        OnPropertyChanged(string.Empty);
    }

    private static AttributeRow Attr(string label, CnsValue value, int max, Brush fill)
        => new(label, value.Value.ToString(), Math.Clamp((double)value.Value / max, 0.01, 1), fill,
            value.IsEngineDefault ? (label + ": not set in [Data]; engine default " + value.Value) : (label + ": " + value.Value + " from [Data]"));

    /// <summary>macOS-style DEF highlighting: sections blue, keys purple, comments grey.</summary>
    public static IReadOnlyList<DefLine> Highlight(string? text)
    {
        if (text is null) return [new DefLine([new DefSegment("Unable to read definition file", CommentBrush, false)])];
        var lines = new List<DefLine>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n').Take(400))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(';'))
            {
                lines.Add(new DefLine([new DefSegment(line, CommentBrush, false)]));
            }
            else if (trimmed.StartsWith('[') && trimmed.Contains(']'))
            {
                lines.Add(new DefLine([new DefSegment(line, SectionBrush, true)]));
            }
            else
            {
                var eq = line.IndexOf('=');
                if (eq < 0)
                {
                    lines.Add(new DefLine([new DefSegment(line, PlainBrush, false)]));
                    continue;
                }

                var value = line[(eq + 1)..];
                var comment = value.IndexOf(';');
                var segments = new List<DefSegment> { new(line[..(eq + 1)], KeyBrush, false) };
                if (comment >= 0)
                {
                    segments.Add(new DefSegment(value[..comment], ValueBrush, false));
                    segments.Add(new DefSegment(value[comment..], CommentBrush, false));
                }
                else
                {
                    segments.Add(new DefSegment(value, ValueBrush, false));
                }

                lines.Add(new DefLine(segments));
            }
        }

        return lines;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
