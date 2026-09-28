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

    public InstallPreviewViewModel(InspectBatchResult inspect)
    {
        Inspect = inspect;
        foreach (var item in inspect.Items)
            Rows.Add(new InstallPreviewRowViewModel(item));

        foreach (var failure in inspect.Failures)
            Failures.Add($"{System.IO.Path.GetFileName(failure.SourceInput)}: {failure.Reason}");

        UpdateSummary();
        InstallCommand = new RelayCommand(
            () =>
            {
                ApplyDecisionsToItems();
                if (Rows.Any(r => r.NeedsDecision && !r.ReplaceSelected && !r.SkipSelected))
                {
                    StatusMessage = "Choose Replace or Skip for every existing destination.";
                    return;
                }

                DialogResult = true;
            },
            () => !IsBusy &&
                  Rows.Any(r => r.WillInstall) &&
                  Rows.All(r => r.IsNew || r.ReplaceSelected || r.SkipSelected));
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

    private void UpdateSummary()
    {
        var ready = Rows.Count(r => r.WillInstall);
        var need = Rows.Count(r => r.NeedsDecision && !r.ReplaceSelected && !r.SkipSelected);
        Summary = $"{Rows.Count} item(s) detected · {ready} ready · {Failures.Count} input failure(s)" +
                  (need > 0 ? $" · {need} need Replace/Skip" : string.Empty);
        OnPropertyChanged(nameof(HasFailures));
        OnPropertyChanged(nameof(HasRows));
    }

    public void RefreshSummary() => UpdateSummary();
}

public sealed class InstallPreviewRowViewModel : ObservableObject
{
    private bool _replaceSelected;
    private bool _skipSelected;

    public InstallPreviewRowViewModel(InstallPlanItem item)
    {
        Item = item;
        if (item.Decision == InstallItemDecision.Skip)
            _skipSelected = true;
        else if (item.Decision == InstallItemDecision.Replace)
            _replaceSelected = true;
    }

    public InstallPlanItem Item { get; }

    public string KindLabel => Item.KindLabel;
    public string DisplayName => Item.Package.DisplayName;
    public string Source => Item.Package.SourceInput;
    public string Target => Item.TargetDirectory;
    public string DefName => System.IO.Path.GetFileName(Item.Package.DefPath);
    public bool DestinationExists => Item.DestinationExists;
    public bool NeedsDecision => Item.Decision == InstallItemDecision.NeedsDecision || Item.DestinationExists;
    public bool IsNew => !Item.DestinationExists;
    public string StatusText => IsNew ? "New" : "Exists";
    public string WarningsText => Item.Package.Warnings.Count == 0
        ? string.Empty
        : string.Join(" · ", Item.Package.Warnings);
    public bool HasWarnings => Item.Package.Warnings.Count > 0;

    public bool ReplaceSelected
    {
        get => _replaceSelected;
        set
        {
            if (!SetProperty(ref _replaceSelected, value)) return;
            if (value) SkipSelected = false;
            OnPropertyChanged(nameof(WillInstall));
            CommandManager.InvalidateRequerySuggested();
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
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool WillInstall =>
        IsNew || (DestinationExists && ReplaceSelected);

    public void ApplyDecision()
    {
        if (IsNew)
        {
            Item.Decision = InstallItemDecision.InstallNew;
            return;
        }

        if (ReplaceSelected)
            Item.Decision = InstallItemDecision.Replace;
        else if (SkipSelected)
            Item.Decision = InstallItemDecision.Skip;
        else
            Item.Decision = InstallItemDecision.NeedsDecision;
    }
}
