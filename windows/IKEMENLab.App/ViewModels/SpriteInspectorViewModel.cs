using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.App.Services;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Sprites;
using Microsoft.Win32;

namespace IKEMENLab.App.ViewModels;

/// <summary>"Original" (the SFF's own palette) or one of the character's palettes.</summary>
public sealed record PaletteChoice(string Label, uint[]? Colors);

public sealed record FindingRow(string Glyph, string Message, Brush Foreground);

/// <summary>Sprite table, zoomable preview with the sprite's axis, palette swap and PNG export for one SFF.</summary>
public sealed class SpriteInspectorViewModel : ObservableObject, IDisposable
{
    private static readonly Brush ErrorBrush = Frozen(Color.FromRgb(0xFF, 0x6B, 0x6B));
    private static readonly Brush WarnBrush = Frozen(Color.FromRgb(0xFF, 0xB8, 0x4D));
    private static readonly Brush InfoBrush = Frozen(Color.FromRgb(0xA1, 0xA1, 0xAA));

    private readonly string _sffPath;
    private readonly string? _root;
    private readonly CharacterEntry? _character;
    private readonly object _sffLock = new();
    private SffFile? _sff;
    private List<SpriteInfo> _all = [];
    private SpriteInfo? _selected;
    private PaletteChoice _palette;
    private ImageSource? _preview;
    private byte[]? _previewPng;
    private string _filter = string.Empty;
    private string _summary = "Reading sprites…";
    private string _previewNote = string.Empty;
    private double _zoom = 3;
    private bool _showAxis = true;
    private bool _isLoading = true;
    private int _renderGeneration;
    private int _previewWidth;
    private int _previewHeight;

    public SpriteInspectorViewModel(string title, string sffPath, string? root = null, CharacterEntry? character = null)
    {
        Title = title;
        _sffPath = sffPath;
        _root = root;
        _character = character;
        _palette = OriginalPalette;
        Palettes = [OriginalPalette];
        ExportCommand = new RelayCommand(Export, () => _previewPng is not null);
    }

    private static readonly PaletteChoice OriginalPalette = new("Original palette", null);

    public string Title { get; }
    public string FileName => System.IO.Path.GetFileName(_sffPath);
    public ICommand ExportCommand { get; }

    public ObservableCollection<SpriteInfo> Sprites { get; } = [];
    public ObservableCollection<FindingRow> Findings { get; } = [];
    public ObservableCollection<PaletteChoice> Palettes { get; }

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public ImageSource? Preview { get => _preview; private set => SetProperty(ref _preview, value); }
    public string PreviewNote { get => _previewNote; private set => SetProperty(ref _previewNote, value); }

    public string Filter
    {
        get => _filter;
        set { if (SetProperty(ref _filter, value)) ApplyFilter(); }
    }

    public double Zoom
    {
        get => _zoom;
        set
        {
            if (!SetProperty(ref _zoom, Math.Round(value))) return;
            OnPropertyChanged(nameof(ZoomText));
            RaiseGeometry();
        }
    }

    public string ZoomText => $"{_zoom:0}×";

    public bool ShowAxis { get => _showAxis; set => SetProperty(ref _showAxis, value); }

    public SpriteInfo? SelectedSprite
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(DetailText));
            _ = RenderAsync();
        }
    }

    public PaletteChoice SelectedPalette
    {
        get => _palette;
        set { if (SetProperty(ref _palette, value ?? OriginalPalette)) _ = RenderAsync(); }
    }

    public bool ShowPaletteChoice => Palettes.Count > 1;

    // Preview geometry: the image is drawn at Zoom times its size; the axis marks the sprite's origin.
    public double PreviewWidth => _previewWidth * _zoom;
    public double PreviewHeight => _previewHeight * _zoom;
    public double AxisLeft => (_selected?.AxisX ?? 0) * _zoom;
    public double AxisTop => (_selected?.AxisY ?? 0) * _zoom;

    public string DetailText => _selected is not { } s
        ? "Select a sprite"
        : $"{s.Id}   {s.Width}×{s.Height}px   axis {s.AxisX},{s.AxisY}   {s.Encoding}   {s.StoredBytes:N0} bytes" +
          (s.LinkedTo is { } link ? $"   linked to #{link}" : string.Empty);

    public async Task LoadAsync()
    {
        try
        {
            var loaded = await Task.Run(ReadSff);

            if (loaded.Sff is null || loaded.Report is null)
            {
                Summary = "This file is not a readable SFF sprite file.";
                return;
            }

            lock (_sffLock) _sff = loaded.Sff;
            _all = loaded.Report.Sprites.ToList();
            foreach (var p in loaded.Palettes) Palettes.Add(new PaletteChoice(p.Label, p.Colors));
            OnPropertyChanged(nameof(ShowPaletteChoice));

            Summary = $"{loaded.Report.VersionText} · {loaded.Report.Sprites.Count:N0} sprites · " +
                      $"{loaded.Report.Groups.Count:N0} groups · {FormatBytes(loaded.Report.FileBytes)}" +
                      (loaded.Report.Version == 2 ? $" · {loaded.Report.PaletteCount} palettes" : string.Empty);
            foreach (var f in loaded.Report.Findings)
            {
                Findings.Add(f.Severity switch
                {
                    FindingSeverity.Error => new FindingRow("✖", f.Message, ErrorBrush),
                    FindingSeverity.Warning => new FindingRow("⚠", f.Message, WarnBrush),
                    _ => new FindingRow("ℹ", f.Message, InfoBrush)
                });
            }

            ApplyFilter();
            SelectedSprite = _all.FirstOrDefault(s => s.Group == 0 && s.Number == 0) ?? _all.FirstOrDefault();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private sealed record LoadedSff(SffFile? Sff, SpriteReport? Report, IReadOnlyList<PreviewPalette> Palettes);

    private sealed record RenderedSprite(byte[]? Png, int Width, int Height);

    private LoadedSff ReadSff()
    {
        var sff = SffFile.Open(_sffPath);
        if (sff is null) return new LoadedSff(null, null, new List<PreviewPalette>());
        var report = SpriteInspector.Inspect(sff, _sffPath, verifyDecode: true, isCharacter: _character is not null);
        IReadOnlyList<PreviewPalette> palettes = new List<PreviewPalette>();
        if (_root is not null && _character is not null)
        {
            try { palettes = SpriteInspector.PalettesFor(_root, _character); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        return new LoadedSff(sff, report, palettes);
    }

    private RenderedSprite Render(SpriteInfo info, uint[]? colors)
    {
        lock (_sffLock)
        {
            if (_sff is null || info.Index >= _sff.Sprites.Count) return new RenderedSprite(null, 0, 0);
            var image = _sff.Decode(_sff.Sprites[info.Index], colors);
            return image is null ? new RenderedSprite(null, 0, 0) : new RenderedSprite(Png.Encode(image), image.Width, image.Height);
        }
    }

    private void ApplyFilter()
    {
        var text = _filter.Trim();
        IEnumerable<SpriteInfo> items = _all;
        if (text.Length > 0)
        {
            var parts = text.Split(new[] { ',', ' ', '-', ':' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1 && int.TryParse(parts[0], out var group))
                items = items.Where(s => s.Group == group);
            else if (parts.Length == 2 && int.TryParse(parts[0], out var g) && int.TryParse(parts[1], out var n))
                items = items.Where(s => s.Group == g && s.Number == n);
            else
                items = items.Where(s => s.Encoding.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        Sprites.Clear();
        foreach (var s in items) Sprites.Add(s);
    }

    private async Task RenderAsync()
    {
        var generation = ++_renderGeneration;
        var info = _selected;
        var colors = _palette.Colors;
        if (info is null)
        {
            SetPreview(null, null, 0, 0, string.Empty);
            return;
        }

        var result = await Task.Run(() => Render(info, colors));

        if (generation != _renderGeneration) return; // a newer selection is already rendering
        if (result.Png is null)
        {
            SetPreview(null, null, 0, 0, "This sprite cannot be decoded.");
            return;
        }

        SetPreview(ArtworkLoader.ToImage(result.Png), result.Png, result.Width, result.Height, string.Empty);
    }

    private void SetPreview(ImageSource? image, byte[]? png, int width, int height, string note)
    {
        Preview = image;
        _previewPng = png;
        _previewWidth = width;
        _previewHeight = height;
        PreviewNote = note;
        RaiseGeometry();
        CommandManager.InvalidateRequerySuggested();
    }

    private void RaiseGeometry()
    {
        OnPropertyChanged(nameof(PreviewWidth));
        OnPropertyChanged(nameof(PreviewHeight));
        OnPropertyChanged(nameof(AxisLeft));
        OnPropertyChanged(nameof(AxisTop));
    }

    private void Export()
    {
        if (_previewPng is null || _selected is not { } s) return;
        string? path = QaExportPath;
        if (path is null)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export sprite",
                Filter = "PNG image (*.png)|*.png",
                FileName = $"{System.IO.Path.GetFileNameWithoutExtension(_sffPath)}_{s.Group}_{s.Number}.png"
            };
            if (dialog.ShowDialog() != true) return;
            path = dialog.FileName;
        }

        try
        {
            File.WriteAllBytes(path, _previewPng);
            LastExportPath = path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            UserDialogs.Warn("The sprite could not be saved: " + e.Message, "Export sprite");
        }
    }

    /// <summary>When set (QA scripts), Export writes here instead of opening a Save dialog.</summary>
    public static string? QaExportPath { get; set; }

    /// <summary>Path of the last successful PNG export (tests/QA).</summary>
    public string? LastExportPath { get; private set; }

    public void Dispose()
    {
        lock (_sffLock)
        {
            _sff?.Dispose();
            _sff = null;
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.0} MB" : $"{Math.Max(1, bytes / 1024):N0} KB";

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
