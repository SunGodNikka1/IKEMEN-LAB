using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.App.Views;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Sequences;
using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.App.ViewModels;

/// <summary>One step card of the Sequence Lab: what it is (with your names), how it will be played, its editable settings, and the latest result.</summary>
public sealed class SequenceStepRow : ObservableObject
{
    private readonly SequenceLabLens _lens;
    private SequenceAction _action;
    private string _label = string.Empty, _how = string.Empty, _result = string.Empty, _glyph = string.Empty, _framesText, _distanceText, _problem = string.Empty;
    private bool _failed;

    public SequenceStepRow(SequenceLabLens lens, SequenceAction action)
    {
        _lens = lens;
        _action = action;
        _framesText = action.Frames.ToString(CultureInfo.InvariantCulture);
        _distanceText = action.Distance.ToString(CultureInfo.InvariantCulture);
    }

    public SequenceAction Action => _action;
    public int Number { get; set; }
    public string Label { get => _label; set => SetProperty(ref _label, value); }
    public string How { get => _how; set => SetProperty(ref _how, value); }
    public string Result { get => _result; set { if (SetProperty(ref _result, value)) OnPropertyChanged(nameof(HasResult)); } }
    public bool HasResult => _result.Length > 0;
    public string Glyph { get => _glyph; set => SetProperty(ref _glyph, value); }
    public bool Failed { get => _failed; set => SetProperty(ref _failed, value); }
    public string Problem { get => _problem; set { if (SetProperty(ref _problem, value)) OnPropertyChanged(nameof(HasProblem)); } }
    public bool HasProblem => _problem.Length > 0;
    public bool IsFirst => Number == 1;
    /// <summary>The ↓ between cards: every card but the first.</summary>
    public bool ShowArrow => Number > 1;
    public void Renumbered() { OnPropertyChanged(nameof(Number)); OnPropertyChanged(nameof(IsFirst)); OnPropertyChanged(nameof(ShowArrow)); }

    public bool IsAbility => _action.Kind == SequenceActionKind.Ability;
    public bool HasFrames => _action.Kind is SequenceActionKind.WalkForward or SequenceActionKind.WalkBackward or SequenceActionKind.Wait;
    public bool IsChase => _action.Kind == SequenceActionKind.Chase;

    /// <summary>Ability steps: the ability, picked by name.</summary>
    public string? AbilityId
    {
        get => _action.AbilityId;
        set { if (value is not null && value != _action.AbilityId) Change(_action with { AbilityId = value }); }
    }

    public string FramesText
    {
        get => _framesText;
        set
        {
            if (!SetProperty(ref _framesText, value)) return;
            if (int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) Change(_action with { Frames = n });
            else Problem = "Frames must be a whole number.";
        }
    }

    public string DistanceText
    {
        get => _distanceText;
        set
        {
            if (!SetProperty(ref _distanceText, value)) return;
            if (int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) Change(_action with { Distance = n });
            else Problem = "Distance must be a whole number.";
        }
    }

    public bool StopIfOpponentRecovers
    {
        get => _action.StopIfOpponentRecovers;
        set { if (value != _action.StopIfOpponentRecovers) Change(_action with { StopIfOpponentRecovers = value }); }
    }

    private void Change(SequenceAction action)
    {
        _action = action;
        Problem = action.Problem() ?? string.Empty;
        OnPropertyChanged(nameof(AbilityId));
        OnPropertyChanged(nameof(StopIfOpponentRecovers));
        _lens.OnDraftEdited();
    }
}

public sealed record AbilityChoice(string Id, string Name);
public sealed record SavedChoice(string Id, string Name, int Version, string Steps)
{
    public string Title => $"{Name} (v{Version})";
}
public sealed record CompareRow(string Metric, string A, string B);

/// <summary>
/// Sequence Lab: build a sequence from an ability plus walk / wait / chase / dash / jump / more abilities, run it ×1, ×10 or ×50 in disposable matches
/// (each trial replays the whole sequence, through the window's one playback session), and read True Combo / Connected Sequence / Did Not Connect /
/// Could Not Test per step and per experiment. Results are shown only against the exact saved sequence version they ran.
/// </summary>
public sealed class SequenceLabLens : XRayLens
{
    private readonly PlaybackSession _session;
    private SemanticIndex? _index;
    private string _draftId = Sequence.NewId();
    private int _draftVersion;              // 0 = never saved
    private bool _dirty = true;
    private string _name = "New sequence";
    private string _planText = "Add an ability to start.", _warnings = string.Empty, _status = string.Empty;
    private SequencePlanResult? _planned;
    private AbilityChoice? _abilityToAdd;
    private string _framesToAdd = "10", _distanceToAdd = "35";
    private bool _stopToAdd, _confirm50, _showDetails;
    private SavedChoice? _selectedSaved, _compareA, _compareB;
    private string _compareWarning = string.Empty;
    private IReadOnlyList<CompareRow> _compareRows = [];

    public SequenceLabLens(XRayViewModel owner) : base(owner)
    {
        _session = owner.PlaybackSession;
        _session.Changed += OnSessionChanged;
        AddAbilityCommand = new RelayCommand(() => { if (_abilityToAdd is { } a) Add(SequenceAction.Ability(a.Id)); }, () => _abilityToAdd is not null);
        AddWalkForwardCommand = new RelayCommand(() => AddFrames(f => SequenceAction.Walk(f)));
        AddWalkBackCommand = new RelayCommand(() => AddFrames(f => SequenceAction.Walk(f, forward: false)));
        AddWaitCommand = new RelayCommand(() => AddFrames(SequenceAction.WaitFrames));
        AddChaseCommand = new RelayCommand(() =>
        {
            if (int.TryParse(_distanceToAdd.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var d)) Add(SequenceAction.ChaseTo(d, _stopToAdd));
            else Status = "Distance must be a whole number.";
        });
        AddDashCommand = new RelayCommand(() => Add(SequenceAction.Dash()));
        AddJumpCommand = new RelayCommand(() => Add(SequenceAction.Jump()));
        RemoveStepCommand = new RelayCommand(p => { if (p is SequenceStepRow r) { Steps.Remove(r); OnDraftEdited(); } });
        MoveUpCommand = new RelayCommand(p => Move(p as SequenceStepRow, -1));
        MoveDownCommand = new RelayCommand(p => Move(p as SequenceStepRow, +1));
        NewCommand = new RelayCommand(() => LoadDraft(Sequence.NewId(), 0, "New sequence", []));
        SaveCommand = new RelayCommand(() => Save(asNew: false), () => Steps.Count > 0);
        SaveAsNewCommand = new RelayCommand(() => Save(asNew: true), () => Steps.Count > 0);
        DeleteSavedCommand = new RelayCommand(DeleteSaved, () => _selectedSaved is not null);
        Run1Command = new AsyncRelayCommand(() => RunAsync(1), () => CanRun);
        Run10Command = new AsyncRelayCommand(() => RunAsync(10), () => CanRun);
        Run50Command = new AsyncRelayCommand(() => RunAsync(50), () => CanRun && _confirm50);
        CancelCommand = new RelayCommand(() => _session.Cancel(), () => _session.IsBusy);
        ViewTraceCommand = new RelayCommand(ViewTrace, () => ShownResult?.LastTrial is not null);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => ShownResult is not null);
        TeachAiCommand = new RelayCommand(() => { if (TeachableExperiment is { } e) Owner.Director.TeachFromExperiment(e); }, () => TeachableExperiment is not null && !_session.IsBusy);
        ToggleDetailsCommand = new RelayCommand(() => ShowDetails = !ShowDetails);
        CompareCommand = new RelayCommand(Compare, () => _compareA is not null && _compareB is not null);
    }

    public ObservableCollection<SequenceStepRow> Steps { get; } = [];
    public ObservableCollection<AbilityChoice> Abilities { get; } = [];
    public ObservableCollection<SavedChoice> Saved { get; } = [];

    public ICommand AddAbilityCommand { get; }
    public ICommand AddWalkForwardCommand { get; }
    public ICommand AddWalkBackCommand { get; }
    public ICommand AddWaitCommand { get; }
    public ICommand AddChaseCommand { get; }
    public ICommand AddDashCommand { get; }
    public ICommand AddJumpCommand { get; }
    public ICommand RemoveStepCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAsNewCommand { get; }
    public ICommand DeleteSavedCommand { get; }
    public ICommand Run1Command { get; }
    public ICommand Run10Command { get; }
    public ICommand Run50Command { get; }
    public ICommand CancelCommand { get; }
    public ICommand ViewTraceCommand { get; }
    public ICommand OpenFolderCommand { get; }
    /// <summary>Teach AI (Phase 6): a Knockdown Chase pre-filled from the shown experiment, when it succeeded and has a chase followed by an attack.</summary>
    public ICommand TeachAiCommand { get; }

    /// <summary>The shown experiment when it can seed a Knockdown Chase: at least one successful trial, and a chase step in the sequence.</summary>
    public ExperimentSummary? TeachableExperiment => ShownResult?.Summary is { Successes: > 0 } s && s.Steps.Contains("Chase", StringComparison.Ordinal) ? s : null;
    public ICommand ToggleDetailsCommand { get; }
    public ICommand CompareCommand { get; }

    public string Name { get => _name; set { if (SetProperty(ref _name, value)) _dirty = true; } }
    public string PlanText { get => _planText; private set => SetProperty(ref _planText, value); }
    public string Warnings { get => _warnings; private set { if (SetProperty(ref _warnings, value)) OnPropertyChanged(nameof(HasWarnings)); } }
    public bool HasWarnings => _warnings.Length > 0;
    public string Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(HasStatusText)); } }
    public bool HasStatusText => _status.Length > 0;
    public AbilityChoice? AbilityToAdd { get => _abilityToAdd; set { if (SetProperty(ref _abilityToAdd, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string FramesToAdd { get => _framesToAdd; set => SetProperty(ref _framesToAdd, value); }
    public string DistanceToAdd { get => _distanceToAdd; set => SetProperty(ref _distanceToAdd, value); }
    public bool StopToAdd { get => _stopToAdd; set => SetProperty(ref _stopToAdd, value); }
    public bool Confirm50 { get => _confirm50; set { if (SetProperty(ref _confirm50, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool ShowDetails { get => _showDetails; set => SetProperty(ref _showDetails, value); }

    /// <summary>The sequence's identity while it is saved and unchanged ("seq-…@v3"); null for an unsaved or edited draft.</summary>
    public string? SavedKey => _draftVersion > 0 && !_dirty ? $"{_draftId}@v{_draftVersion}" : null;
    public string VersionText => _draftVersion == 0 ? "not saved yet" : _dirty ? $"v{_draftVersion} (edited — runs save it as v{_draftVersion + 1})" : $"v{_draftVersion}";

    /// <summary>What ×10 and ×50 cost, measured from the last trial when there is one.</summary>
    public string CostText
    {
        get
        {
            var per = ShownResult?.Summary.AverageSeconds ?? _session.ExperimentResult?.Summary.AverageSeconds ?? LastMeasuredSeconds();
            return SequenceExperimentRunner.CostText(10, per) + "\n" + SequenceExperimentRunner.CostText(50, per);
        }
    }

    public bool CanRun => !_session.IsBusy && Steps.Count > 0 && _planned?.Plan is not null;
    public SavedChoice? SelectedSaved
    {
        get => _selectedSaved;
        set
        {
            if (!SetProperty(ref _selectedSaved, value) || value is null) return;
            var seq = SavedSequences().FirstOrDefault(s => s.Id == value.Id);
            if (seq is not null) LoadDraft(seq.Id, seq.Version, seq.Name, seq.Actions);
        }
    }

    public SavedChoice? CompareA { get => _compareA; set { if (SetProperty(ref _compareA, value)) CommandManager.InvalidateRequerySuggested(); } }
    public SavedChoice? CompareB { get => _compareB; set { if (SetProperty(ref _compareB, value)) CommandManager.InvalidateRequerySuggested(); } }
    public IReadOnlyList<CompareRow> CompareRows { get => _compareRows; private set { if (SetProperty(ref _compareRows, value)) OnPropertyChanged(nameof(HasCompare)); } }
    public bool HasCompare => _compareRows.Count > 0;
    public string CompareWarning { get => _compareWarning; private set { if (SetProperty(ref _compareWarning, value)) OnPropertyChanged(nameof(HasCompareWarning)); } }
    public bool HasCompareWarning => _compareWarning.Length > 0;

    // ------------------------------------------------------------------ results (only for the exact saved version that ran)

    private string? Scope => SavedKey is { } k ? Sequence.ScopePrefix + k : null;
    private ExperimentOutcome? ShownResult => Scope is { } s && _session.IsFor(s) && _session.State == PlaybackState.Finished ? _session.ExperimentResult : null;

    public bool IsBusy => _session.IsBusy;
    public string Headline => _session.IsBusy && _session.RunMode == PlaybackMode.Sequence ? _session.Phase
        : _session.Attempt == AttemptState.PreflightRefused && Scope is { } s && _session.AttemptRouteKey == s ? "Not started — playback setup is incomplete: " + _session.AttemptIssue
        : ShownResult is { } r ? ExperimentText.Headline(r.Summary, r.LastTrial?.Report)
        : Scope is { } sc && _session.IsFor(sc) && _session.State is PlaybackState.Cancelled ? "Run cancelled."
        : Scope is { } se && _session.IsFor(se) && _session.State is PlaybackState.Error ? "Could not run: " + _session.Error
        : string.Empty;
    public bool HasHeadline => Headline.Length > 0;

    /// <summary>Banner colour: TrueCombo (green), Connected (green outline), Failed (did not connect), Inconclusive, Busy, Neutral.</summary>
    public string ResultKind
    {
        get
        {
            if (_session.IsBusy && _session.RunMode == PlaybackMode.Sequence) return "Busy";
            if (ShownResult is not { } r) return Headline.Length > 0 ? "Inconclusive" : "Neutral";
            // Several trials: green only when every trial was a true combo, green outline when every trial connected, red when none did, amber when mixed.
            if (r.Summary.Requested > 1)
                return r.Summary.Successes == r.Summary.Completed ? (r.Summary.TrueCombos == r.Summary.Completed ? "TrueCombo" : "Connected")
                    : r.Summary.Successes == 0 && r.Summary.Untestable == 0 ? "Failed" : "Inconclusive";
            return r.LastTrial?.Report.Verdict switch
            {
                SequenceVerdict.TrueCombo => "TrueCombo",
                SequenceVerdict.ConnectedSequence => "Connected",
                SequenceVerdict.DidNotConnect => "Failed",
                _ => "Inconclusive"
            };
        }
    }

    public string ResultGlyph => ResultKind switch { "TrueCombo" => "✓✓", "Connected" => "✓", "Failed" => "✗", "Inconclusive" => "?", "Busy" => "▶", _ => "·" };
    public IReadOnlyList<string> ResultLines => ShownResult is { } r ? (r.Summary.Requested > 1 ? ExperimentText.Lines(r.Summary) : Single(r)) : [];
    public bool HasResultLines => ResultLines.Count > 0;
    public string DetailsText => ShownResult is { LastTrial: { } t } r
        ? ExperimentText.ScopeText(r.Summary.Scope) + "\n" + SequenceVerifier.Details(t.Report, t.Plan) + $"\nrecord: {t.Record.Directory}\nexperiment: {r.Summary.Directory}"
        : string.Empty;

    private static IReadOnlyList<string> Single(ExperimentOutcome r)
    {
        var lines = new List<string>();
        if (r.LastTrial?.Report is { } rep)
        {
            if (rep.Damage is { } d) lines.Add($"Damage: {d:0.##}");
            if (rep.End is { } e) lines.Add($"At the end (frame {e.Frame}): the opponent is {e.OpponentPosture}" + (e.Distance is { } dist ? $", {dist:0.##} away" : string.Empty) +
                                           (e.YouCanAct == true ? "; you can act" : string.Empty) + (e.YourPower is { } p ? $"; you have {p:0} meter" : string.Empty) + ".");
            if (rep.OutOfHitstunFrames is > 0) lines.Add($"Between the hits the opponent was out of hitstun for {rep.OutOfHitstunFrames} frame(s) and could act for {rep.OpponentCouldActFrames} of them.");
            lines.Add("Evidence: each move's start was observed by the route verifier's step check in this run (runtime.transition-observed); contact, damage and the opponent's state are measurements of this run.");
        }

        lines.Add(ExperimentText.ScopeText(r.Summary.Scope));
        return lines;
    }

    // ------------------------------------------------------------------ lens contract

    public override void Build(SemanticIndex index)
    {
        _index = index;
        Abilities.Clear();
        foreach (var a in index.Of(ObjectKind.Ability).Select(a => new AbilityChoice(a.Id, index.NameOf(a.Id))).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            Abilities.Add(a);
        RefreshSaved();
        Replan();
    }

    public override void RefreshNames(SemanticIndex index) => Build(index);
    public override void OnSelection(SemanticIndex index, string? id, IReadOnlySet<string> related) { }

    /// <summary>Try Follow-Up from the Ability Lab: a new draft that starts with <paramref name="abilityId"/>.</summary>
    public void StartFrom(string abilityId)
    {
        var name = _index is { } index && index.Get(abilityId) is not null ? index.NameOf(abilityId) : abilityId;
        LoadDraft(Sequence.NewId(), 0, name + " follow-up", [SequenceAction.Ability(abilityId)]);
        Status = "Started from " + name + ". Add what happens next, then Run.";
    }

    /// <summary>Replaces the draft (QA / tests: the compact spec form).</summary>
    public void LoadSpec(string spec, string? name = null) => LoadDraft(Sequence.NewId(), 0, name ?? "Sequence", SequenceSpec.Parse(spec));

    public void OnDraftEdited()
    {
        _dirty = true;
        Replan();
    }

    // ------------------------------------------------------------------ editing

    private void LoadDraft(string id, int version, string name, IReadOnlyList<SequenceAction> actions)
    {
        _draftId = id;
        _draftVersion = version;
        _name = name;
        OnPropertyChanged(nameof(Name));
        Steps.Clear();
        foreach (var a in actions) Steps.Add(new SequenceStepRow(this, a));
        _dirty = version == 0;
        Status = string.Empty;
        Replan();
    }

    private void Add(SequenceAction action)
    {
        Steps.Add(new SequenceStepRow(this, action));
        OnDraftEdited();
    }

    private void AddFrames(Func<int, SequenceAction> make)
    {
        if (int.TryParse(_framesToAdd.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var f)) Add(make(f));
        else Status = "Frames must be a whole number.";
    }

    private void Move(SequenceStepRow? row, int by)
    {
        if (row is null) return;
        var i = Steps.IndexOf(row);
        var j = i + by;
        if (i < 0 || j < 0 || j >= Steps.Count) return;
        Steps.Move(i, j);
        OnDraftEdited();
    }

    private Sequence Draft => new(_draftId, _name, Math.Max(1, _draftVersion), Steps.Select(s => s.Action).ToList());

    /// <summary>Re-plans the draft: every card shows how it will be played (or why not); the first refusing step is marked.</summary>
    private void Replan()
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].Number = i + 1;
            Steps[i].Label = SequenceLabels.Label(Steps[i].Action, _index);
            Steps[i].How = string.Empty;
            Steps[i].Problem = Steps[i].Action.Problem() ?? string.Empty;
            if (!IsShownRun()) { Steps[i].Result = string.Empty; Steps[i].Glyph = string.Empty; Steps[i].Failed = false; }
        }

        var graph = Owner.Combos.Graph;
        // The approach distance comes straight from the shared setup field: no install scan on every edit.
        PlaybackPreflight.TryParseApproach(Owner.Combos.Playback.ApproachDistance, out var approach, out _);
        _planned = graph is null || Steps.Count == 0 ? null : SequencePlanner.Plan(graph, Draft, approach);
        if (_planned is { } p)
        {
            foreach (var s in p.Steps) if (s.Index - 1 < Steps.Count) Steps[s.Index - 1].How = s.How;
            if (p.RefusedStep is { } rs && rs >= 1 && rs <= Steps.Count) Steps[rs - 1].Problem = p.Refused ?? "Cannot be played.";
            PlanText = p.Plan is not null ? $"Ready: {SequenceLabels.Chain(Steps.Select(s => s.Action), _index)}" : "Cannot run yet: " + p.Refused;
            Warnings = string.Join("\n", p.Warnings.Select(w => "⚠ " + w));
        }
        else
        {
            PlanText = Steps.Count == 0 ? "Add an ability to start (or use Try Follow-Up in the Ability Atlas)." : "Reading the character…";
            Warnings = string.Empty;
        }

        foreach (var name in new[] { nameof(SavedKey), nameof(VersionText), nameof(CanRun), nameof(Headline), nameof(HasHeadline), nameof(ResultKind), nameof(ResultGlyph),
                     nameof(ResultLines), nameof(HasResultLines), nameof(DetailsText), nameof(CostText) })
            OnPropertyChanged(name);
        foreach (var row in Steps) row.Renumbered();
        CommandManager.InvalidateRequerySuggested();
    }

    private bool IsShownRun() => ShownResult is not null;

    private IReadOnlyList<Sequence> SavedSequences() =>
        Owner.SequenceStore.Load(CharacterFolder).Sequences.Select(s =>
        {
            try { return s.ToSequence(); }
            catch (FormatException) { return null; }
        }).OfType<Sequence>().ToList();

    private string CharacterFolder => Path.Combine(Owner.Root, Owner.Entry.FolderPath);

    private void RefreshSaved()
    {
        Saved.Clear();
        foreach (var s in SavedSequences().OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            Saved.Add(new SavedChoice(s.Id, s.Name, s.Version, SequenceLabels.Chain(s.Actions, _index)));
    }

    private Sequence? Save(bool asNew)
    {
        if (Steps.Count == 0) return null;
        if (asNew) { _draftId = Sequence.NewId(); _draftVersion = 0; }
        var saved = Owner.SequenceStore.Save(CharacterFolder, Draft);
        _draftId = saved.Id;
        _draftVersion = saved.Version;
        _dirty = false;
        RefreshSaved();
        _selectedSaved = Saved.FirstOrDefault(s => s.Id == saved.Id);
        OnPropertyChanged(nameof(SelectedSaved));
        Status = $"Saved “{saved.Name}” as v{saved.Version}.";
        Replan();
        return saved;
    }

    private void DeleteSaved()
    {
        if (_selectedSaved is not { } s) return;
        Owner.SequenceStore.Delete(CharacterFolder, s.Id);
        _selectedSaved = null;
        OnPropertyChanged(nameof(SelectedSaved));
        RefreshSaved();
        if (s.Id == _draftId) { _draftVersion = 0; _dirty = true; }
        Replan();
    }

    // ------------------------------------------------------------------ running

    private async Task RunAsync(int trials)
    {
        // Results always belong to an exact saved version: an unsaved or edited draft is saved first.
        var sequence = SavedKey is null ? Save(asNew: false) : Draft with { Version = _draftVersion };
        if (sequence is null || Owner.Combos.Graph is not { } graph || _index is null) return;
        var setup = Owner.Combos.Playback.CheckSetup(showIssuesAsSetup: false);
        var scope = sequence.ScopeKey;
        if (setup is null || !setup.Ready)
        {
            _session.RefuseAttempt(scope, setup is null ? Owner.Combos.Playback.SetupSummary : string.Join(" ", setup.Issues), setup);
            Replan();
            return;
        }

        var (prepared, planned) = SequenceExperimentRunner.Prepare(graph, sequence, Owner.Root, Owner.SubjectFolder, Owner.SubjectDef, setup, trials);
        if (prepared is null) { Status = "Cannot run: " + planned.Refused; return; }
        Status = trials > 1 ? SequenceExperimentRunner.CostText(trials, LastMeasuredSeconds()) : string.Empty;
        await _session.RunSequenceAsync(prepared.Job, (gate, progress) => SequenceExperimentRunner.Run(Owner.PlaybackService, Owner.ExperimentStore, prepared, gate, progress));
    }

    private double? LastMeasuredSeconds() =>
        _index is null ? null : Owner.ExperimentStore.List(ExperimentScope.HashOf(_index)).FirstOrDefault(e => e.AverageSeconds is not null)?.AverageSeconds;

    private void ViewTrace()
    {
        if (ShownResult?.LastTrial is not { } t) return;
        var names = _index?.Names;
        new PlaybackTraceWindow(new PlaybackTraceViewModel(t, names is null ? null : names.RenamedState)) { Owner = Application.Current?.MainWindow }.Show();
    }

    private void OpenFolder()
    {
        if (ShownResult?.Summary.Directory is not { } dir || !Directory.Exists(dir)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { /* no shell to open it with */ }
    }

    private void Compare()
    {
        if (_compareA is null || _compareB is null || _index is null) return;
        var hash = ExperimentScope.HashOf(_index);
        ExperimentSummary? Latest(SavedChoice c) => Owner.ExperimentStore.List(hash, c.Id).FirstOrDefault(e => e.Scope.SequenceVersion == c.Version);
        var a = Latest(_compareA);
        var b = Latest(_compareB);
        if (a is null || b is null)
        {
            CompareRows = [];
            CompareWarning = $"No experiment yet for {(a is null ? _compareA.Title : _compareB.Title)} in its current version on these character files. Run it first.";
            return;
        }

        var (rows, warning) = ExperimentText.Compare(a, b);
        CompareRows = rows.Select(r => new CompareRow(r.Metric, r.A, r.B)).ToList();
        CompareWarning = warning ?? string.Empty;
    }

    // ------------------------------------------------------------------ session

    private void OnSessionChanged()
    {
        if (_session.IsClosed) return;
        if (ShownResult is { LastTrial: { } last })
        {
            foreach (var step in last.Report.Steps)
            {
                if (step.Index - 1 >= Steps.Count) continue;
                var row = Steps[step.Index - 1];
                row.Glyph = step.Outcome switch { SequenceStepOutcome.Done => step.Connected == false ? "✗" : "✓", SequenceStepOutcome.Failed => "✗", _ => "·" };
                row.Failed = step.Outcome == SequenceStepOutcome.Failed || step.Connected == false;
                row.Result = step.Why ?? step.Outcome switch
                {
                    SequenceStepOutcome.Done when step.Connected == true => $"Done — connected at frame {step.ContactFrame}" + (step.Damage is { } d ? $" ({d:0.##} damage)" : string.Empty),
                    SequenceStepOutcome.Done => $"Done (frame {step.StartFrame}" + (step.EndFrame is { } e ? $"–{e}" : string.Empty) + ")",
                    SequenceStepOutcome.NotReached => "Not reached",
                    _ => step.Reason ?? "Failed"
                };
            }
        }

        foreach (var name in new[] { nameof(IsBusy), nameof(CanRun), nameof(Headline), nameof(HasHeadline), nameof(ResultKind), nameof(ResultGlyph), nameof(ResultLines),
                     nameof(HasResultLines), nameof(DetailsText), nameof(CostText) })
            OnPropertyChanged(name);
        CommandManager.InvalidateRequerySuggested();
    }
}
