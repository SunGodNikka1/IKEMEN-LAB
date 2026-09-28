using System.Windows.Input;
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
    private bool _canToggleStatus;
    private bool _isToggling;

    public CharacterRowViewModel(CharacterEntry entry)
    {
        Entry = entry;
        Initial = string.IsNullOrEmpty(entry.DisplayName) ? "?" : entry.DisplayName[..1].ToUpperInvariant();
        PathText = entry.DefPath.StartsWith("chars/", StringComparison.OrdinalIgnoreCase) ? entry.DefPath[6..] : entry.DefPath;
        var date = VersionDateFormatter.Format(entry.VersionDate);
        DateText = string.IsNullOrEmpty(date) ? "—" : date;
        DateAddedText = IKEMENLab.Core.Services.DateAddedSort.FormatAddedLabel(entry.DateAddedUtc, entry.DateAddedSource);
        _dateAddedShort = IKEMENLab.Core.Services.DateAddedSort.FormatAddedShort(entry.DateAddedUtc, entry.DateAddedSource);
    }

    private readonly string? _dateAddedShort;
    private bool _showDateAdded;

    /// <summary>
    /// Set while the browser sorts by Date Added, so the date column shows the value the list is
    /// ordered by instead of the DEF's version date.
    /// </summary>
    public bool ShowDateAdded
    {
        get => _showDateAdded;
        set
        {
            if (SetProperty(ref _showDateAdded, value))
            {
                OnPropertyChanged(nameof(DateColumnText));
                OnPropertyChanged(nameof(DateColumnToolTip));
            }
        }
    }

    public string DateColumnText => ShowDateAdded ? _dateAddedShort ?? "Unknown" : DateText;

    public string? DateColumnToolTip => ShowDateAdded
        ? DateAddedText ?? "Date Added unknown"
        : DateText == "—" ? null : "Version date " + DateText;

    public CharacterEntry Entry { get; }
    public string DisplayName => Entry.DisplayName;
    public string Author => Entry.Author;
    public string PathText { get; }
    public string DateText { get; }
    /// <summary>"Added Sep 27, 2026" / "Estimated added …" when Date Added is known; otherwise null.</summary>
    public string? DateAddedText { get; }
    public bool HasDateAdded => DateAddedText is not null;
    public string Initial { get; }
    public ContentStatus Status => Entry.Status;
    public bool IsActive => Entry.Status == ContentStatus.Active;

    /// <summary>
    /// Re-reads the bound status. A clicked switch shows its new position before the roster write
    /// finishes; if the write fails (or changes nothing) this snaps it back to the real state.
    /// </summary>
    public void RefreshStatusBinding() => OnPropertyChanged(nameof(IsActive));
    public bool IsDisabled => Entry.Status == ContentStatus.Disabled;
    public bool IsUnregistered => Entry.Status == ContentStatus.Unregistered;

    public bool CanToggleStatus
    {
        get => _canToggleStatus && !_isToggling;
        set
        {
            if (SetProperty(ref _canToggleStatus, value))
                OnPropertyChanged(nameof(StatusToolTip));
        }
    }

    public bool IsToggling
    {
        get => _isToggling;
        set
        {
            if (SetProperty(ref _isToggling, value))
            {
                OnPropertyChanged(nameof(CanToggleStatus));
                OnPropertyChanged(nameof(StatusToolTip));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>macOS row dimming: active 1.0, disabled 0.7, unregistered 0.6.</summary>
    public double RowOpacity => Entry.Status switch
    {
        ContentStatus.Active => 1.0,
        ContentStatus.Disabled => 0.7,
        _ => 0.6
    };

    public string StatusToolTip
    {
        get
        {
            if (IsToggling) return "Updating select.def…";
            if (!CanToggleStatus)
            {
                return Entry.Status switch
                {
                    ContentStatus.Active => "Enabled in select.def (roster toggle unavailable)",
                    ContentStatus.Disabled => "Commented out in select.def (roster toggle unavailable)",
                    _ => "Not listed in select.def (roster toggle unavailable)"
                };
            }

            return Entry.Status switch
            {
                ContentStatus.Active => "Enabled — click to disable in select.def",
                ContentStatus.Disabled => "Disabled — click to re-enable in select.def",
                _ => "Unregistered — click to add to select.def"
            };
        }
    }

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
