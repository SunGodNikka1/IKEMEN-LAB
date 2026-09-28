using System.Windows.Media;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.Core.Characters;
using IKEMENLab.Core.Models;

namespace IKEMENLab.App.ViewModels;

/// <summary>One character in the browser (list row or grid card).</summary>
public sealed class CharacterRowViewModel : ObservableObject
{
    private ImageSource? _thumbnail;
    private string _featuresText = string.Empty;

    public CharacterRowViewModel(CharacterEntry entry)
    {
        Entry = entry;
        Initial = string.IsNullOrEmpty(entry.DisplayName) ? "?" : entry.DisplayName[..1].ToUpperInvariant();
        PathText = entry.DefPath.StartsWith("chars/", StringComparison.OrdinalIgnoreCase) ? entry.DefPath[6..] : entry.DefPath;
        var date = VersionDateFormatter.Format(entry.VersionDate);
        DateText = string.IsNullOrEmpty(date) ? "—" : date;
    }

    public CharacterEntry Entry { get; }
    public string DisplayName => Entry.DisplayName;
    public string Author => Entry.Author;
    public string PathText { get; }
    public string DateText { get; }
    public string Initial { get; }
    public ContentStatus Status => Entry.Status;
    public bool IsActive => Entry.Status == ContentStatus.Active;
    public bool IsDisabled => Entry.Status == ContentStatus.Disabled;
    public bool IsUnregistered => Entry.Status == ContentStatus.Unregistered;

    /// <summary>macOS row dimming: active 1.0, disabled 0.7, unregistered 0.6.</summary>
    public double RowOpacity => Entry.Status switch
    {
        ContentStatus.Active => 1.0,
        ContentStatus.Disabled => 0.7,
        _ => 0.6
    };

    public string StatusToolTip => Entry.Status switch
    {
        ContentStatus.Active => "Enabled in select.def (read-only view)",
        ContentStatus.Disabled => "Commented out in select.def (read-only view)",
        _ => "Not listed in select.def (read-only view)"
    };

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    /// <summary>"INTRO · SFX · AI" once the background feature scan reaches this character.</summary>
    public string FeaturesText
    {
        get => _featuresText;
        set
        {
            if (SetProperty(ref _featuresText, value)) OnPropertyChanged(nameof(HasFeatures));
        }
    }

    public bool HasFeatures => !string.IsNullOrEmpty(_featuresText);

    public bool Matches(string query)
        => DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
           Author.Contains(query, StringComparison.OrdinalIgnoreCase) ||
           Entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
           Entry.DefPath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
           FeaturesText.Contains(query, StringComparison.OrdinalIgnoreCase);
}
