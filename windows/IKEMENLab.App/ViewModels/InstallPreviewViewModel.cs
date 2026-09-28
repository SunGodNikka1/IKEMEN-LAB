using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.Core.Install;

namespace IKEMENLab.App.ViewModels;

public sealed class InstallPreviewViewModel : ObservableObject
{
    private string _summary = string.Empty;
    private bool _isBusy;
    private string? _statusMessage;
    private readonly string _ikemenRoot;

    public InstallPreviewViewModel(InspectBatchResult inspect, string? ikemenRoot = null)
    {
        Inspect = inspect;
        _ikemenRoot = ikemenRoot ?? string.Empty;
        foreach (var item in inspect.Items)
            Rows.Add(new InstallPreviewRowViewModel(item, RefreshSummary, _ikemenRoot));

        foreach (var failure in inspect.Failures)
            Failures.Add($"{System.IO.Path.GetFileName(failure.SourceInput)}: {failure.Reason}");

        UpdateSummary();
        InstallCommand = new RelayCommand(
            () =>
            {
                ApplyDecisionsToItems();
                if (!CanConfirmInstall(out var reason))
                {
                    StatusMessage = reason;
                    return;
                }

                DialogResult = true;
            },
            () => !IsBusy && CanConfirmInstall(out _));
        CancelCommand = new RelayCommand(() => DialogResult = false, () => !IsBusy);
    }

    public InspectBatchResult Inspect { get; }
    public ObservableCollection<InstallPreviewRowViewModel> Rows { get; } = [];
    public ObservableCollection<string> Failures { get; } = [];
    public ICommand InstallCommand { get; }
    public ICommand CancelCommand { get; }

    public bool? DialogResult
    {
        get => _dialogResult;
        private set => SetProperty(ref _dialogResult, value);
    }

    private bool? _dialogResult;

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool HasFailures => Failures.Count > 0;
    public bool HasRows => Rows.Count > 0;

    public void ApplyDecisionsToItems()
    {
        foreach (var row in Rows)
            row.ApplyDecision();
    }

    public void RefreshSummary()
    {
        UpdateSummary();
        CommandManager.InvalidateRequerySuggested();
    }

    private bool CanConfirmInstall(out string reason)
    {
        reason = string.Empty;
        if (!Rows.Any(r => r.WillInstall))
        {
            reason = "Nothing selected to install.";
            return false;
        }

        foreach (var row in Rows.Where(r => r.WillInstall))
        {
            if (row.HasProblem)
            {
                reason = row.ProblemText ?? "Fix folder name problems before installing.";
                return false;
            }

            if (row.NeedsDefChoice)
            {
                reason = "Choose which DEF each ambiguous character uses.";
                return false;
            }

            if (row.NeedsLayoutConfirmation)
            {
                reason = "Confirm installing without the loose files outside the character folder.";
                return false;
            }

            if (row.NeedsDecision && !row.ReplaceSelected && !row.SkipSelected)
            {
                reason = "Choose Replace, Rename, or Skip for every existing destination.";
                return false;
            }
        }

        return true;
    }

    private void UpdateSummary()
    {
        var ready = Rows.Count(r => r.WillInstall && !r.HasProblem && !r.NeedsDefChoice && !r.NeedsLayoutConfirmation);
        var needCollision = Rows.Count(r => r.NeedsDecision && !r.ReplaceSelected && !r.SkipSelected);
        var needDef = Rows.Count(r => r.NeedsDefChoice);
        var needLayout = Rows.Count(r => r.NeedsLayoutConfirmation);
        var problems = Rows.Count(r => r.HasProblem);
        Summary = $"{Rows.Count} item(s) detected · {ready} ready · {Failures.Count} input failure(s)" +
                  (needCollision > 0 ? $" · {needCollision} need Replace/Rename/Skip" : string.Empty) +
                  (needDef > 0 ? $" · {needDef} need DEF choice" : string.Empty) +
                  (needLayout > 0 ? $" · {needLayout} need layout confirm" : string.Empty) +
                  (problems > 0 ? $" · {problems} need rename" : string.Empty);
        OnPropertyChanged(nameof(HasFailures));
        OnPropertyChanged(nameof(HasRows));
    }
}

public sealed class InstallPreviewRowViewModel : ObservableObject
{
    private bool _replaceSelected;
    private bool _skipSelected;
    private bool _layoutConfirmed;
    private string? _selectedDef;
    private string _renameText = string.Empty;
    private string? _renameError;
    private readonly Action _changed;
    private readonly string _ikemenRoot;

    public InstallPreviewRowViewModel(InstallPlanItem item, Action changed, string ikemenRoot)
    {
        Item = item;
        _changed = changed;
        _ikemenRoot = ikemenRoot;
        _selectedDef = item.SelectedDef ?? item.Package.PrimaryDef?.RelativePath;
        _layoutConfirmed = item.LayoutConfirmed;
        _renameText = item.DestinationName;
        if (item.Decision == InstallItemDecision.Skip)
            _skipSelected = true;
        else if (item.Decision == InstallItemDecision.Replace)
            _replaceSelected = true;

        ApplyRenameCommand = new RelayCommand(_ => TryRename(), _ => CanRename);
    }

    public InstallPlanItem Item { get; }
    public ICommand ApplyRenameCommand { get; }

    public string KindLabel => Item.KindLabel;
    public string DisplayName => Item.Package.DisplayName;
    public string Source => Item.Package.SourceInput;
    public string Target => Item.TargetDirectory;
    public string DestinationFolder => Item.DestinationName;

    public string DefLabel
    {
        get
        {
            var def = SelectedDefRelative ?? Item.Package.PrimaryDef?.RelativePath;
            if (string.IsNullOrEmpty(def))
                return System.IO.Path.GetFileName(Item.Package.DefPath);
            return def;
        }
    }

    public string NameSourceText => Item.Package.Kind != InstallContentKind.Character
        ? string.Empty
        : Item.Package.NameSource switch
        {
            FolderNameSource.CharacterFolder => "Folder name kept from package",
            FolderNameSource.ArchiveName => "Named after archive (flat files)",
            FolderNameSource.DroppedFolder => "Named after dropped folder",
            _ => string.Empty
        };

    public bool HasNameSource => !string.IsNullOrEmpty(NameSourceText);

    public string WrappersText => Item.Package.Wrappers.Count == 0
        ? string.Empty
        : "Wrappers dropped: " + string.Join(", ", Item.Package.Wrappers);

    public bool HasWrappers => Item.Package.Wrappers.Count > 0;

    public IReadOnlyList<string> DefChoices =>
        Item.Package.DefCandidates.Select(c => c.RelativePath).ToList();

    public bool ShowDefChoice => Item.Package.Kind == InstallContentKind.Character && DefChoices.Count > 1;

    public string? SelectedDefRelative
    {
        get => _selectedDef;
        set
        {
            if (!SetProperty(ref _selectedDef, value)) return;
            Item.SelectedDef = value;
            OnPropertyChanged(nameof(DefLabel));
            OnPropertyChanged(nameof(NeedsDefChoice));
            _changed();
        }
    }

    public bool NeedsDefChoice => Item.Package.RequiresDefChoice && string.IsNullOrWhiteSpace(SelectedDefRelative);

    public bool NeedsLayoutConfirmation => Item.Package.RequiresLayoutConfirmation && !LayoutConfirmed;

    public string LeftOutFilesText => Item.Package.LeftOutFiles.Count == 0
        ? string.Empty
        : "Not installed (outside character folder): " + string.Join(", ", Item.Package.LeftOutFiles);

    public bool HasLeftOutFiles => Item.Package.LeftOutFiles.Count > 0;

    public bool LayoutConfirmed
    {
        get => _layoutConfirmed;
        set
        {
            if (!SetProperty(ref _layoutConfirmed, value)) return;
            Item.LayoutConfirmed = value;
            OnPropertyChanged(nameof(NeedsLayoutConfirmation));
            _changed();
        }
    }

    public string? ProblemText => Item.Problem;
    public bool HasProblem => !string.IsNullOrWhiteSpace(Item.Problem);

    public string ReplaceWarningsText => Item.ReplaceWarnings.Count == 0
        ? string.Empty
        : string.Join(" · ", Item.ReplaceWarnings);

    public bool HasReplaceWarnings => Item.ReplaceWarnings.Count > 0;

    public bool DestinationExists => Item.DestinationExists;
    public bool NeedsDecision => Item.Decision == InstallItemDecision.NeedsDecision || Item.DestinationExists;
    public bool IsNew => !Item.DestinationExists;
    public bool IsCharacter => Item.Package.Kind == InstallContentKind.Character;
    public bool ShowRename => IsCharacter && (NeedsDecision || HasProblem);

    public string StatusText => HasProblem ? "Needs rename" : IsNew ? "New" : "Exists";

    public string WarningsText => Item.Package.Warnings.Count == 0
        ? string.Empty
        : string.Join(" · ", Item.Package.Warnings);

    public bool HasWarnings => Item.Package.Warnings.Count > 0;

    public string RenameText
    {
        get => _renameText;
        set
        {
            if (SetProperty(ref _renameText, value))
                OnPropertyChanged(nameof(CanRename));
        }
    }

    public string? RenameError
    {
        get => _renameError;
        private set => SetProperty(ref _renameError, value);
    }

    public bool CanRename =>
        IsCharacter &&
        !string.IsNullOrWhiteSpace(_ikemenRoot) &&
        !string.IsNullOrWhiteSpace(RenameText) &&
        !string.Equals(RenameText.Trim(), Item.DestinationName, StringComparison.OrdinalIgnoreCase);

    public bool ReplaceSelected
    {
        get => _replaceSelected;
        set
        {
            if (!SetProperty(ref _replaceSelected, value)) return;
            if (value) SkipSelected = false;
            OnPropertyChanged(nameof(WillInstall));
            _changed();
        }
    }

    public bool SkipSelected
    {
        get => _skipSelected;
        set
        {
            if (!SetProperty(ref _skipSelected, value)) return;
            if (value) ReplaceSelected = false;
            OnPropertyChanged(nameof(WillInstall));
            _changed();
        }
    }

    public bool WillInstall =>
        !SkipSelected && !HasProblem && (IsNew || ReplaceSelected);

    public void ApplyDecision()
    {
        if (SkipSelected)
        {
            Item.Decision = InstallItemDecision.Skip;
            return;
        }

        if (IsNew)
        {
            Item.Decision = InstallItemDecision.InstallNew;
            return;
        }

        if (ReplaceSelected)
            Item.Decision = InstallItemDecision.Replace;
        else
            Item.Decision = InstallItemDecision.NeedsDecision;
    }

    private void TryRename()
    {
        if (string.IsNullOrWhiteSpace(_ikemenRoot))
        {
            RenameError = "Installation root is not available.";
            return;
        }

        var error = InstallPlanRules.Rename(Item, RenameText.Trim(), _ikemenRoot);
        if (error is not null)
        {
            RenameError = error;
            return;
        }

        InstallPlanRules.Refresh([Item], _ikemenRoot);
        RenameError = null;
        _replaceSelected = false;
        _skipSelected = false;
        OnPropertyChanged(nameof(DestinationFolder));
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(DestinationExists));
        OnPropertyChanged(nameof(NeedsDecision));
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ProblemText));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(ShowRename));
        OnPropertyChanged(nameof(WillInstall));
        OnPropertyChanged(nameof(ReplaceSelected));
        OnPropertyChanged(nameof(SkipSelected));
        OnPropertyChanged(nameof(CanRename));
        _changed();
    }
}
