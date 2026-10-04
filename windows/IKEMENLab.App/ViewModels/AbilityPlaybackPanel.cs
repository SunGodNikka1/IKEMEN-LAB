using System.Windows;
using System.Windows.Input;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.App.Views;
using IKEMENLab.Core.XRay.Playback;

namespace IKEMENLab.App.ViewModels;

/// <summary>
/// The Ability Lab bar of the Ability Atlas: <b>Play Ability</b> performs the selected ability from neutral through its own command (the same planner,
/// sandbox, driver and route verifier as Play Combo, one step long), and <b>Preview State</b> forces its entry state for a look, which is never proof.
/// Both run through the one shared <see cref="PlaybackSession"/>, so only one engine runs at a time across the window. A result is shown only against
/// the ability (or previewed state) it belongs to.
/// </summary>
public sealed class AbilityPlaybackPanel : ObservableObject
{
    private readonly XRayViewModel _owner;
    private readonly PlaybackSession _session;
    private string? _abilityId;
    private object? _pathGraph;
    private AbilityPathResult? _path;
    private bool _showFailure;
    private FailureInspection? _failure;

    public AbilityPlaybackPanel(XRayViewModel owner, PlaybackSession session)
    {
        _owner = owner;
        _session = session;
        _session.Changed += OnSessionChanged;

        PlayAbilityCommand = new AsyncRelayCommand(PlayAsync, () => CanPlayAbility);
        PreviewStateCommand = new AsyncRelayCommand(PreviewAsync, () => CanPreviewState);
        CancelCommand = new RelayCommand(() => _session.Cancel(), () => _session.IsBusy);
        ReplayCommand = new AsyncRelayCommand(ReplayAsync, () => ShownScope is { } s && _session.CanReplayFor(s));
        InspectFailureCommand = new RelayCommand(InspectFailure, () => ShownScope is { } s && _session.CanInspectFor(s));
        ViewTraceCommand = new RelayCommand(ViewTrace, () => ShownScope is { } s && _session.HasResultFor(s));
        CopyDiagnosticCommand = new RelayCommand(CopyDiagnostic, () => AttemptScope is { } s && _session.DiagnosticFor(s) is not null);
        TryFollowUpCommand = new RelayCommand(() =>
        {
            if (_abilityId is null) return;
            _owner.SequenceLab.StartFrom(_abilityId);
            _owner.ActiveLens = XRayLensKind.Sequences;
        }, () => _abilityId is not null && Path?.Path is not null);
        OpenSetupCommand = new RelayCommand(() =>
        {
            _owner.Combos.Playback.ShowSetup = true;
            _owner.ActiveLens = XRayLensKind.Combos;
        });
    }

    public ICommand PlayAbilityCommand { get; }
    public ICommand PreviewStateCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ReplayCommand { get; }
    public ICommand InspectFailureCommand { get; }
    public ICommand ViewTraceCommand { get; }
    public ICommand CopyDiagnosticCommand { get; }
    public ICommand OpenSetupCommand { get; }
    /// <summary>Opens the Sequence Lab with a new sequence that starts with this ability.</summary>
    public ICommand TryFollowUpCommand { get; }

    // ------------------------------------------------------------------ selection

    /// <summary>The ability the Atlas has selected (null when none).</summary>
    public string? AbilityId => _abilityId;

    /// <summary>Called by the Atlas when its selected ability changes, and after a rename (labels only).</summary>
    public void OnAbilityChanged(string? abilityId)
    {
        if (_abilityId != abilityId)
        {
            _abilityId = abilityId;
            _path = null;
            ShowFailure = false;
        }

        RaiseAll();
    }

    /// <summary>How Play Ability would perform the selected ability, or why it cannot. Computed from the Combos lens's candidate graph of the same index.</summary>
    public AbilityPathResult? Path
    {
        get
        {
            var graph = _owner.Combos.Graph;
            if (_abilityId is null || graph is null) return null;
            if (_path is null || !ReferenceEquals(_pathGraph, graph))
            {
                _path = AbilityPlayback.Resolve(graph, _abilityId, new Core.XRay.Verify.PlanOptions());
                _pathGraph = graph;
            }

            return _path;
        }
    }

    /// <summary>The entry state of the selected ability (what State Preview forces).</summary>
    public string? EntryStateId => _abilityId is null ? null : _owner.Index?.Get(_abilityId)?.Prop("entryState");

    private string? PlayScope => _abilityId is null ? null : AbilityPlayback.ScopeKey(_abilityId);
    private string? PreviewScope => EntryStateId is { } s ? StatePreview.ScopeKey(s) : null;

    /// <summary>The scope of the session's latest job when it belongs to the selected ability (its Play Ability run or its entry state's preview).</summary>
    public string? ShownScope => _session.RunRouteKey is { } k && (k == PlayScope || k == PreviewScope) ? k : null;

    /// <summary>The scope of the latest attempt (a refused press included) when it belongs to the selected ability.</summary>
    public string? AttemptScope => _session.AttemptRouteKey is { } k && (k == PlayScope || k == PreviewScope) ? k : null;

    /// <summary>The latest press for this ability was refused before any engine launch (playback setup incomplete).</summary>
    private bool LatestRefused => _session.Attempt == AttemptState.PreflightRefused && AttemptScope is not null;

    public bool HasAbility => _abilityId is not null;
    public string AbilityTitle => _abilityId is not null && _owner.Index is { } index ? index.NameOf(_abilityId) : "Select an ability";

    public string PathText
    {
        get
        {
            if (_abilityId is null) return "Select an ability to play it.";
            if (Path is not { } p) return "Reading the ability…";
            if (p.Path is not { } path) return "Play Ability is not available: " + p.Refused;
            var others = path.Alternatives.Count == 0 ? string.Empty : $" · {path.Alternatives.Count} other command path(s) not used";
            return $"Plays {StateLabel(path.EntryStateId)} from neutral by pressing “{path.Command}” (controller {path.Edge.ControllerId}){others}.";
        }
    }

    public string PathWarnings => Path?.Path is { Warnings.Count: > 0 } p ? string.Join("\n", p.Warnings.Select(w => "⚠ " + w)) : string.Empty;
    public bool HasPathWarnings => PathWarnings.Length > 0;
    public string SetupSummary => _owner.Combos.Playback.SetupSummary;

    public bool CanPlayAbility => !_session.IsBusy && Path?.Path is not null;
    public bool CanPreviewState => !_session.IsBusy && EntryStateId is { } s && _owner.Index is { } index && StatePreview.CanPreview(index, s, out _);

    // ------------------------------------------------------------------ result

    public bool IsBusy => _session.IsBusy;
    public bool HasResult => ShownScope is { } s && _session.HasResultFor(s);
    public bool CanInspect => ShownScope is { } s && _session.CanInspectFor(s);
    public string Headline => _session.IsBusy ? _session.Headline
        : LatestRefused ? "Not started — playback setup is incomplete: " + _session.AttemptIssue
        : ShownScope is { } s ? _session.HeadlineFor(s) : string.Empty;
    public bool HasStatus => Headline.Length > 0;

    /// <summary>Banner kind: Verified (performed), Failed (not performed), Inconclusive, Preview (never a verdict), Busy, Neutral.</summary>
    public string ResultKind
    {
        get
        {
            if (_session.IsBusy) return "Busy";
            if (LatestRefused) return "Inconclusive";
            if (ShownScope is null) return "Neutral";
            if (_session.State == PlaybackState.Error) return "Inconclusive";
            if (_session.State != PlaybackState.Finished) return "Neutral";
            if (_session.PreviewResult is not null) return "Preview";
            return _session.Outcome?.Ability?.Status switch
            {
                AbilityStatus.Performed => "Verified",
                AbilityStatus.NotPerformed => "Failed",
                AbilityStatus.Inconclusive => "Inconclusive",
                _ => "Neutral"
            };
        }
    }

    public string ResultGlyph => ResultKind switch { "Verified" => "✓", "Failed" => "✗", "Inconclusive" => "?", "Preview" => "◇", "Busy" => "▶", _ => "·" };
    public bool IsPreviewResult => ResultKind == "Preview";

    /// <summary>The result card under the banner: what followed the move, and what the evidence is (or that a preview is not evidence).</summary>
    public IReadOnlyList<string> ResultLines
    {
        get
        {
            if (ShownScope is null || LatestRefused || _session.State != PlaybackState.Finished) return [];
            var names = _session.LastJob?.Snapshot is { } snap ? (Func<string, string?>)snap.NameOf : null;
            if (_session.PreviewResult is { } p)
            {
                var lines = new List<string> { PreviewReport.NotProof };
                if (p.Report.Detail is { Length: > 0 } d) lines.Add(d);
                if (p.Report.Observed is { } po) lines.AddRange(AbilityText.Details(po, names));
                lines.AddRange(p.Report.Notes);
                return lines;
            }

            if (_session.Outcome?.Ability is not { } a) return [];
            var result = new List<string>();
            if (a.Status == AbilityStatus.Performed)
            {
                result.Add($"Started via “{a.Command}”: input at frame {a.InputFrame}, {StateLabel(a.EntryStateId)} began at frame {a.EntryFrame}.");
                if (a.Observed is { } o) result.AddRange(AbilityText.Details(o, names));
                result.Add("Evidence: the move starting from its command was observed by the route verifier's step check in this run (runtime.transition-observed). " +
                           "Contact, damage and recovery are measurements of this run against " + (_session.Outcome.Record.Dummy ?? "the dummy") +
                           $" at approach distance {_session.Outcome.Record.ApproachDistance}; nothing in the static graph changes.");
            }
            else if (a.Detail is { Length: > 0 } detail) result.Add(detail);

            result.AddRange(a.Notes);
            return result;
        }
    }

    public bool HasResultLines => ResultLines.Count > 0;

    public bool ShowFailure { get => _showFailure && ShownScope is { } s && _session.CanInspectFor(s); private set { if (SetProperty(ref _showFailure, value)) OnPropertyChanged(); } }
    public string FailureWhy => _failure?.Why ?? string.Empty;
    public string FailureHints => _failure is null ? string.Empty : string.Join("\n", _failure.Hints.Select(h => "• " + h));

    // ------------------------------------------------------------------ actions

    private PlaybackSetup? CheckSetup(string scope)
    {
        var setup = _owner.Combos.Playback.CheckSetup(showIssuesAsSetup: false);
        OnPropertyChanged(nameof(SetupSummary));
        if (setup is null || !setup.Ready)
        {
            // A refused press is still an attempt, with its own identity; it never inherits an earlier verdict. The banner says why.
            _session.RefuseAttempt(scope, setup is null ? SetupSummary : string.Join(" ", setup.Issues), setup);
            RaiseAll();
            return null;
        }

        return setup;
    }

    private async Task PlayAsync()
    {
        if (Path?.Path is not { } path || _owner.Combos.Graph is not { } graph || PlayScope is not { } scope) return;
        if (CheckSetup(scope) is not { } setup) return;
        ShowFailure = false;
        var request = new PlaybackRequest(_owner.Root, _owner.SubjectFolder, _owner.SubjectDef, graph, path.Route, setup, $"Play Ability · {AbilityTitle}")
        {
            Mode = PlaybackMode.Ability,
            AbilityId = path.AbilityId
        };
        await _session.PlayAsync(request);
    }

    private async Task PreviewAsync()
    {
        if (EntryStateId is not { } state || _owner.Index is not { } index || PreviewScope is not { } scope) return;
        if (CheckSetup(scope) is not { } setup) return;
        ShowFailure = false;
        await _session.PreviewAsync(new PreviewRequest(_owner.Root, _owner.SubjectFolder, _owner.SubjectDef, index, state, setup, $"State Preview (not proof) · {StateLabel(state)}"));
    }

    private async Task ReplayAsync()
    {
        if (ShownScope is not { } scope || !_session.CanReplayFor(scope)) return;
        if (CheckSetup(scope) is not { } setup) return;
        ShowFailure = false;
        switch (_session.LastJob)
        {
            case PlaybackRequest r: await _session.PlayAsync(r with { Setup = setup }); break;
            case PreviewRequest p: await _session.PreviewAsync(p with { Setup = setup }); break;
        }
    }

    private void InspectFailure()
    {
        if (ShownScope is not { } s || !_session.CanInspectFor(s) || _session.Outcome?.Failure is not { } failure) return;
        _failure = failure;
        ShowFailure = true;
        OnPropertyChanged(nameof(FailureWhy));
        OnPropertyChanged(nameof(FailureHints));
    }

    private void ViewTrace()
    {
        if (ShownScope is not { } s || !_session.HasResultFor(s)) return;
        var names = _owner.Index?.Names;
        Func<int?, string?>? stateName = names is null ? null : names.RenamedState;
        PlaybackTraceViewModel? vm = _session.PreviewResult is { } p ? new PlaybackTraceViewModel(p, stateName)
            : _session.Outcome is { } o ? new PlaybackTraceViewModel(o, stateName) : null;
        if (vm is not null) new PlaybackTraceWindow(vm) { Owner = Application.Current?.MainWindow }.Show();
    }

    /// <summary>The latest attempt's diagnostic when it belongs to the selected ability (text, then JSON); empty otherwise.</summary>
    public string DiagnosticText()
    {
        var d = AttemptScope is { } s ? _session.DiagnosticFor(s) : null;
        return d is null ? string.Empty : d.ToText() + "\n--- JSON (" + PlaybackDiagnostic.SchemaVersion + ") ---\n" + d.ToJson() + "\n";
    }

    private void CopyDiagnostic()
    {
        var text = DiagnosticText();
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { /* the clipboard is busy; nothing was copied */ }
    }

    // ------------------------------------------------------------------ plumbing

    private string StateLabel(string stateId) => AbilityText.StateLabel(stateId, id => _owner.Index is { } index && index.Get(id) is not null && index.Names.IsRenamed(id) ? index.NameOf(id) : null);

    private void OnSessionChanged()
    {
        if (_session.IsClosed) return;   // the window is gone: never touch it
        if (ShownScope is null) ShowFailure = false;
        RaiseAll();
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(AbilityId), nameof(HasAbility), nameof(AbilityTitle), nameof(Path), nameof(PathText), nameof(PathWarnings), nameof(HasPathWarnings),
                     nameof(SetupSummary), nameof(CanPlayAbility), nameof(CanPreviewState), nameof(IsBusy), nameof(HasResult), nameof(CanInspect), nameof(Headline), nameof(HasStatus), nameof(ResultKind),
                     nameof(ResultGlyph), nameof(IsPreviewResult), nameof(ResultLines), nameof(HasResultLines), nameof(ShowFailure), nameof(EntryStateId), nameof(ShownScope), nameof(AttemptScope)
                 })
            OnPropertyChanged(name);
        CommandManager.InvalidateRequerySuggested();
    }
}
