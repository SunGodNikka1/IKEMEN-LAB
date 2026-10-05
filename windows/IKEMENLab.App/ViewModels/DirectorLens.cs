using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.App.Services;
using IKEMENLab.Core.XRay.Behavior;
using IKEMENLab.Core.XRay.Director;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Sequences;

namespace IKEMENLab.App.ViewModels;

/// <summary>A taught behavior in the list: its plain card.</summary>
public sealed class DirectorCardRow(TaughtBehavior b)
{
    public TaughtBehavior Behavior { get; } = b;
    public string Id => Behavior.Id;
    public string Name => Behavior.Name;
    public string Status => DirectorText.Status(Behavior.Status);
    /// <summary>Draft, Testing, Ready, Live or Retired (the badge colour).</summary>
    public string StatusKind => Behavior.Status switch { TaughtStatus.ReadyForApproval => "Ready", var s => s.ToString() };
    public string When => DirectorText.When(Behavior.Spec.When);
    public string Do => DirectorText.Do(Behavior.Spec);
    public string Then => DirectorText.Then(Behavior.Spec);
    public string GiveUp => DirectorText.GiveUp(Behavior.Spec);
    public string Subtitle => $"rev {Behavior.Revision} · {Status} · {Behavior.Spec.FollowUpName}";
}

/// <summary>One choice in a wizard drop-down.</summary>
public sealed record Choice<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>A follow-up in the wizard: its name and whether it has runtime proof (only proven ones can be used).</summary>
public sealed record FollowUpRow(FollowUpChoice Choice)
{
    public string Text => (Choice.Proof.Proven ? "✓ " : "✗ ") + Choice.Name + (Choice.PowerCost > 0 ? $" ({Choice.PowerCost:0} power)" : string.Empty);
    public override string ToString() => Text;
}

/// <summary>An existing rule that can be suppressed (placement "suppress"): checkable.</summary>
public sealed class SuppressRow(OwnershipRow row) : ObservableObject
{
    private bool _checked;
    public OwnershipRow Row { get; } = row;
    public string Text => Row.Plain;
    public bool Checked { get => _checked; set => SetProperty(ref _checked, value); }
}

/// <summary>One run of the last test.</summary>
public sealed record DirectorRunRow(DirectorRunMetrics Metrics, string Text, string Detail);

/// <summary>A moment of a test run for "Why?": a decision of the generated behavior or a state change.</summary>
public sealed record DirectorMomentRow(long Frame, string Text, bool IsDecision);

/// <summary>
/// AI Director (Teach AI v1 — Knockdown Chase only). Teach from evidence (Sequence Lab, Watch &amp; Ask, Ability Lab), answer the plain questions, see the
/// ownership check and the generated rules, test in sandboxes, read exactly why the generated behavior did something, approve one exact build, then deploy
/// it — each a separate, explicit step. Draft and Testing never touch the installed character.
/// </summary>
public sealed class DirectorLens : XRayLens
{
    private readonly PlaybackSession _session;
    private SemanticIndex? _index;
    private DirectorService? _svc;
    private string? _engineSha;
    private DirectorCardRow? _selected;
    private DirectorAnalysis? _analysis;
    private DirectorTestReport? _report;
    private string _status = string.Empty;
    private bool _editing, _showEvidence, _showAdvanced;
    private TeachAiDraft? _draft;
    private TaughtBehavior? _editingBehavior;
    private Choice<KnockdownWhen>? _when;
    private Choice<FollowUpTiming>? _timing;
    private Choice<ChaseFrequency>? _frequency;
    private Choice<ChaseFallback>? _fallback;
    private Choice<OwnershipPlacement>? _placement;
    private FollowUpRow? _followUp;
    private string _chaseLimit = string.Empty, _attackDistance = string.Empty, _lead = string.Empty, _giveUp = string.Empty, _secondOpponent = string.Empty, _trials = "2";
    private DirectorRunRow? _whyRun;
    private TraceLog? _whyLog;
    private DirectorMomentRow? _moment;
    private DirectorWhyAnswer? _why;
    private string? _whyFallback;

    public DirectorLens(XRayViewModel owner) : base(owner)
    {
        _session = owner.PlaybackSession;
        _session.Changed += OnSessionChanged;
        SaveCommand = new RelayCommand(Save, () => _editing && _index is not null);
        CancelEditCommand = new RelayCommand(() => { Editing = false; _draft = null; Raise(); });
        EditCommand = new RelayCommand(Edit, () => _selected is not null && !_session.IsBusy && _selected.Behavior.Status is not (TaughtStatus.Live or TaughtStatus.Retired));
        TestCommand = new AsyncRelayCommand(TestAsync, () => _selected is not null && !_session.IsBusy && _analysis is { CanGenerate: true } && !_editing
                                                             && _selected.Behavior.Status is not (TaughtStatus.Live or TaughtStatus.Retired));
        CancelCommand = new RelayCommand(() => _session.Cancel(), () => _session.IsBusy);
        ToggleEvidenceCommand = new RelayCommand(() => ShowEvidence = !ShowEvidence);
        ToggleAdvancedCommand = new RelayCommand(() => ShowAdvanced = !ShowAdvanced);
        WhyCommand = new RelayCommand(Why, () => _moment is not null && _whyLog is not null);
        ApproveCommand = new RelayCommand(Approve, () => _selected?.Behavior is { Status: TaughtStatus.ReadyForApproval, Approval: null } && !_session.IsBusy);
        DeployCommand = new RelayCommand(Deploy, () => _selected?.Behavior is { Approval: not null } b && b.Status != TaughtStatus.Live && !_session.IsBusy);
        RollbackCommand = new RelayCommand(Rollback, () => _selected?.Behavior is { Deployment.RolledBack: false } && !_session.IsBusy);
        RetireCommand = new RelayCommand(Retire, () => _selected?.Behavior is { Status: not TaughtStatus.Retired } && !_session.IsBusy);
        OpenWorkingCopyCommand = new RelayCommand(() => Open(_svc?.Workspace.WorkingFolder), () => _svc?.Workspace.Exists == true);
        Whens = Enum.GetValues<KnockdownWhen>().Select(w => new Choice<KnockdownWhen>(w, DirectorText.When(w))).ToList();
        Timings = [new(FollowUpTiming.WhileDown, "While they are still down (a move that hits downed opponents)"), new(FollowUpTiming.AsTheyGetUp, "As they get up (meaty)")];
        Frequencies = Enum.GetValues<ChaseFrequency>().Select(f => new Choice<ChaseFrequency>(f, DirectorText.Frequency(f))).ToList();
        Fallbacks = Enum.GetValues<ChaseFallback>().Select(f => new Choice<ChaseFallback>(f, DirectorText.Fallback(f))).ToList();
        Placements = Enum.GetValues<OwnershipPlacement>().Select(p => new Choice<OwnershipPlacement>(p, DirectorText.Placement(p))).ToList();
    }

    public ObservableCollection<DirectorCardRow> Behaviors { get; } = [];
    public ObservableCollection<FollowUpRow> FollowUps { get; } = [];
    public ObservableCollection<SuppressRow> SuppressChoices { get; } = [];
    public ObservableCollection<OwnershipRow> OwnershipRows { get; } = [];
    public ObservableCollection<DirectorRunRow> Runs { get; } = [];
    public ObservableCollection<DirectorMomentRow> Moments { get; } = [];
    public IReadOnlyList<Choice<KnockdownWhen>> Whens { get; }
    public IReadOnlyList<Choice<FollowUpTiming>> Timings { get; }
    public IReadOnlyList<Choice<ChaseFrequency>> Frequencies { get; }
    public IReadOnlyList<Choice<ChaseFallback>> Fallbacks { get; }
    public IReadOnlyList<Choice<OwnershipPlacement>> Placements { get; }

    public ICommand SaveCommand { get; }
    public ICommand CancelEditCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ToggleEvidenceCommand { get; }
    public ICommand ToggleAdvancedCommand { get; }
    public ICommand WhyCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand DeployCommand { get; }
    public ICommand RollbackCommand { get; }
    public ICommand RetireCommand { get; }
    public ICommand OpenWorkingCopyCommand { get; }

    public DirectorService? Service => _svc;
    public string Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => _status.Length > 0;
    public bool IsBusy => _session.IsBusy;
    public string Headline => _session.IsBusy && _session.RunMode == PlaybackMode.Director ? _session.Phase
        : _selected is not null && _session.IsFor(DirectorTestJob.ScopePrefix + _selected.Id) && _session.State == PlaybackState.Finished && _session.DirectorResult is { } r
            ? DirectorTesting.Headline(r.Report)
        : _selected is not null && _session.IsFor(DirectorTestJob.ScopePrefix + _selected.Id) && _session.State == PlaybackState.Error ? "Could not test: " + _session.Error
        : _selected is not null && _session.IsFor(DirectorTestJob.ScopePrefix + _selected.Id) && _session.State == PlaybackState.Cancelled ? "Test cancelled."
        : string.Empty;
    public bool HasHeadline => Headline.Length > 0;

    public bool Editing { get => _editing; private set { if (SetProperty(ref _editing, value)) { OnPropertyChanged(nameof(NotEditing)); CommandManager.InvalidateRequerySuggested(); } } }
    public bool NotEditing => !_editing;
    public bool ShowEvidence { get => _showEvidence; set => SetProperty(ref _showEvidence, value); }
    public bool ShowAdvanced { get => _showAdvanced; set => SetProperty(ref _showAdvanced, value); }

    // ------------------------------------------------------------------ the selected behavior (simple card)

    public DirectorCardRow? SelectedBehavior
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            Analyze();
        }
    }

    public bool HasBehavior => _selected is not null;
    public string CardName => _selected?.Name ?? "Teach AI";
    public string CardWhen => _selected?.When ?? string.Empty;
    public string CardDo => _selected?.Do ?? string.Empty;
    public string CardThen => _selected?.Then ?? string.Empty;
    public string CardGiveUp => _selected?.GiveUp ?? string.Empty;
    public string CardStatus => _selected?.Status ?? string.Empty;
    public string CardStatusKind => _selected?.StatusKind ?? "Draft";
    public string CardSentence => _selected is null ? "Teach a Knockdown Chase from a Sequence Lab experiment, a recognised chase in Watch & Ask, or a move in the Ability Lab." : DirectorText.Sentence(_selected.Behavior);
    public string CardSource => _selected?.Behavior.Source.Text ?? string.Empty;

    public string ProofText => _analysis?.Proof.Text ?? string.Empty;
    public bool ProofOk => _analysis?.Proof.Proven == true;
    public string OwnershipSummary => _analysis?.Ownership.Summary ?? string.Empty;
    public string OwnershipKind => _analysis?.Ownership.Verdict switch { OwnershipVerdict.Refused => "Refused", null => "NotSeen", _ => "Owned" };
    public string Problems => _analysis is null || _analysis.Problems.Count == 0 ? string.Empty : string.Join("\n", _analysis.Problems.Select(p => "• " + p));
    public bool HasProblems => Problems.Length > 0;
    public string GeneratedRules => _analysis?.Code is not { } c ? string.Empty
        : string.Join("\n\n", c.Rules.Select(r => $"{r.Name}: {r.Purpose}\n    trigger: {r.Trigger}"));
    public string GeneratedCode => _analysis?.Code is not { } c ? string.Empty
        : $"; ---- in {_analysis.Facts.MinusOneFile} ([Statedef -1], {DirectorText.Placement(c.Placement).ToLowerInvariant()})\n{c.MinusOneBlock}\n" +
          $"; ---- {CharacterFacts.GeneratedFile} (new file)\n{c.StatesFile}\n; ---- the DEF's [Files]\n{c.DefLine}\n" +
          (c.Suppress.Count > 0 ? $"; ---- after the header of {string.Join(", ", c.Suppress.Select(s => s.Name))}\n{c.SuppressLine}\n" : string.Empty);
    public string DiffText => _svc?.Workspace.Info?.BehaviorId == _selected?.Id ? _svc?.Diff()?.Text ?? "No working copy yet: Test generates it." : "The working copy holds no build of this behavior yet: Test generates it.";
    public string WorkingCopyText => _svc?.Workspace.Info is { } i
        ? $"Working copy: {_svc.Workspace.WorkingFolder} (build {DirectorHash.Short(i.GeneratedBuildHash ?? i.BaseHash)})" + (_svc.Workspace.InstalledDrift() is { } drift ? " — " + drift : string.Empty)
        : "No working copy yet.";
    public string ApprovalText => _selected?.Behavior.Approval is { } a
        ? $"Approved {a.ApprovedUtc.ToLocalTime():g} by {a.By} for build {DirectorHash.Short(a.BuildHash)} (diff {DirectorHash.Short(a.DiffHash)})."
        : _selected?.Behavior.Status == TaughtStatus.ReadyForApproval ? "Ready for approval: review the change and the evidence, then approve this exact build." : "Not approved.";
    public string DeploymentText => _selected?.Behavior.Deployment is { } d
        ? $"Deployed {d.DeployedUtc.ToLocalTime():g} ({d.OperationIds.Count} file(s), manifest {d.ManifestPath})" + (d.RolledBack ? " — rolled back." : ".")
        : _selected?.Behavior.Approval is not null && _svc?.DeployBlocker(_selected.Behavior) is { } why ? "Cannot deploy: " + why : "Not deployed.";

    // ------------------------------------------------------------------ test report

    public string ReportHeadline => _report is null ? "Not tested yet." : DirectorTesting.Headline(_report);
    public string ReportFindings => _report is null ? string.Empty : string.Join("\n", _report.Findings.Select(f => "• " + f)
        .Concat(_report.Regressions.Select(g => $"• Regression {(g.Passed ? "held" : "FAILED")}: {g.Name} — {g.Text}")).Append(DirectorTestReport.ScopeText));

    // ------------------------------------------------------------------ wizard

    public Choice<KnockdownWhen>? When { get => _when; set => SetProperty(ref _when, value); }
    public Choice<FollowUpTiming>? Timing { get => _timing; set { if (SetProperty(ref _timing, value)) OnPropertyChanged(nameof(ShowLead)); } }
    public bool ShowLead => _timing?.Value == FollowUpTiming.AsTheyGetUp;
    public Choice<ChaseFrequency>? Frequency { get => _frequency; set => SetProperty(ref _frequency, value); }
    public Choice<ChaseFallback>? Fallback { get => _fallback; set => SetProperty(ref _fallback, value); }
    public Choice<OwnershipPlacement>? Placement { get => _placement; set { if (SetProperty(ref _placement, value)) OnPropertyChanged(nameof(ShowSuppress)); } }
    public bool ShowSuppress => _placement?.Value == OwnershipPlacement.SuppressSpecific;
    public FollowUpRow? FollowUp { get => _followUp; set { if (SetProperty(ref _followUp, value)) OnPropertyChanged(nameof(MeterText)); } }
    public string MeterText => _followUp?.Choice.PowerCost is > 0 and var p ? $"Meter rule: only with {p:0} power (this move costs it); if the power is gone, the fallback runs." : string.Empty;
    public string ChaseLimitText { get => _chaseLimit; set => SetProperty(ref _chaseLimit, value); }
    public string AttackDistanceText { get => _attackDistance; set => SetProperty(ref _attackDistance, value); }
    public string LeadText { get => _lead; set => SetProperty(ref _lead, value); }
    public string GiveUpText { get => _giveUp; set => SetProperty(ref _giveUp, value); }
    public string SecondOpponentText { get => _secondOpponent; set => SetProperty(ref _secondOpponent, value); }
    public string TrialsText { get => _trials; set => SetProperty(ref _trials, value); }
    public string DraftNotes => _draft is null ? string.Empty : string.Join("\n", _draft.Notes);
    public string DraftSource => _draft?.Source.Text ?? _editingBehavior?.Source.Text ?? string.Empty;

    // ------------------------------------------------------------------ Why

    public DirectorRunRow? WhyRun
    {
        get => _whyRun;
        set { if (SetProperty(ref _whyRun, value)) LoadMoments(); }
    }

    public DirectorMomentRow? Moment { get => _moment; set { if (SetProperty(ref _moment, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool HasWhy => _why is not null || _whyFallback is not null;
    public string WhyStatement => _why?.Statement ?? _whyFallback ?? string.Empty;
    public string WhyChecks => _why is null ? string.Empty : string.Join("\n", _why.Checks.Select(c => $"{c.Label}: {c.Value}" + (c.Ok is { } ok ? ok ? "  ✓" : "  ✗" : string.Empty)));
    public string WhyAction => _why is null ? string.Empty : (_why.Action is { } a ? "Action: " + a : string.Empty) + (_why.Next is { } n ? "\nNext: " + n : string.Empty);
    public string WhyVerification => _why?.Verification ?? (_why is { CauseKnown: false } ? "Cause not claimed." : string.Empty);
    public string WhyKind => _why?.CauseKnown == true ? "Exact" : "NotSeen";
    public string WhyBadge => _why?.CauseKnown == true ? "Exact (intention register, confirmed)" : _why is not null ? "Not a confirmed cause" : string.Empty;

    // ------------------------------------------------------------------ lens contract

    public override void Build(SemanticIndex index)
    {
        _index = index;
        _svc = new DirectorService(Owner.DirectorRoot, Owner.Root, index, Owner.Combos.Graph ?? Core.XRay.Combo.CandidateGraph.Build(index), Owner.SubjectFolder,
            Path.GetFileName(Owner.SubjectDef));
        _engineSha = EngineIdentity.Sha256(Owner.Combos.Playback.CheckSetup(showIssuesAsSetup: false)?.EnginePath);
        RefreshList(_selected?.Id);
    }

    public override void OnSelection(SemanticIndex index, string? id, IReadOnlySet<string> related) { }

    private IReadOnlyList<string> PlaybackRoots => [Owner.PlaybackService.StoreRoot];

    private void RefreshList(string? select)
    {
        if (_svc is null) return;
        Behaviors.Clear();
        foreach (var b in _svc.List().Select(_svc.Refresh)) Behaviors.Add(new DirectorCardRow(b));
        _selected = null;
        SelectedBehavior = Behaviors.FirstOrDefault(b => b.Id == select) ?? Behaviors.FirstOrDefault(b => b.Behavior.Status != TaughtStatus.Retired) ?? Behaviors.FirstOrDefault();
        if (_selected is null) Analyze();
    }

    private void Analyze()
    {
        _analysis = null;
        _report = null;
        if (_selected is not null && _svc is not null)
        {
            _analysis = _svc.Analyze(_selected.Behavior, _engineSha, Owner.ExperimentStore, PlaybackRoots);
            _report = _svc.LoadTest(_selected.Behavior.LastTestId);
        }

        OwnershipRows.Clear();
        foreach (var r in _analysis?.Ownership.Rows ?? []) OwnershipRows.Add(r);
        Runs.Clear();
        foreach (var m in _report?.Runs ?? [])
            Runs.Add(new DirectorRunRow(m, $"{m.Setup}: " + (m.Loaded
                    ? $"{m.Activations} chase(s), {m.FollowUps} follow-up(s), {m.Connected} connected, {m.Fallbacks} fallback(s), {m.GiveUps} give-up(s), {m.Conflicts} conflict(s)" + (m.Unfinished > 0 ? $", {m.Unfinished} still running when the recording ended" : string.Empty)
                    : "not tested — " + m.NotTestedWhy),
                string.Join("\n", m.Notes)));
        _whyRun = null;
        WhyRun = Runs.FirstOrDefault(r => r.Metrics.FollowUps > 0) ?? Runs.FirstOrDefault();
        Raise();
    }

    // ------------------------------------------------------------------ teaching

    /// <summary>Opens the wizard pre-filled from evidence (the Sequence Lab, Watch &amp; Ask or the Ability Lab call this).</summary>
    public void Teach(TeachAiDraft draft)
    {
        _draft = draft;
        _editingBehavior = null;
        Fill(draft.Spec, draft.Tests);
        Editing = true;
        Owner.ActiveLens = XRayLensKind.Director;
        Status = "Teaching a new Knockdown Chase — pre-filled from " + draft.Source.Text;
        Raise();
    }

    public void TeachFromExperiment(ExperimentSummary experiment)
    {
        if (_index is null || Owner.Combos.Graph is not { } graph) return;
        try { Teach(TeachAi.FromExperiment(_index, graph, experiment)); }
        catch (InvalidOperationException ex) { Status = "Cannot teach from this experiment: " + ex.Message; Owner.ActiveLens = XRayLensKind.Director; }
    }

    public void TeachFromEpisode(BehaviorRun run, BehaviorEpisode episode)
    {
        if (_index is null || Owner.Combos.Graph is not { } graph) return;
        try { Teach(TeachAi.FromEpisode(_index, graph, run, episode, TeachAi.DefaultOpening(_index, graph, Owner.ExperimentStore, _engineSha))); }
        catch (InvalidOperationException ex) { Status = "Cannot teach from this episode: " + ex.Message; Owner.ActiveLens = XRayLensKind.Director; }
    }

    public void TeachFromAbility(string abilityId)
    {
        if (_index is null || Owner.Combos.Graph is not { } graph) return;
        try { Teach(TeachAi.FromAbility(_index, graph, abilityId, TeachAi.DefaultOpening(_index, graph, Owner.ExperimentStore, _engineSha))); }
        catch (InvalidOperationException ex) { Status = "Cannot teach from this ability: " + ex.Message; Owner.ActiveLens = XRayLensKind.Director; }
    }

    private void Edit()
    {
        if (_selected is null) return;
        _draft = null;
        _editingBehavior = _selected.Behavior;
        Fill(_selected.Behavior.Spec, _selected.Behavior.Tests);
        Editing = true;
        Raise();
    }

    private void Fill(KnockdownChaseSpec s, TaughtTestPlan tests)
    {
        When = Whens.First(w => w.Value == s.When);
        Timing = Timings.First(t => t.Value == s.Timing);
        Frequency = Frequencies.First(f => f.Value == s.Frequency);
        Fallback = Fallbacks.First(f => f.Value == s.Fallback);
        Placement = Placements.First(p => p.Value == s.Placement);
        ChaseLimitText = s.ChaseLimit.ToString(CultureInfo.InvariantCulture);
        AttackDistanceText = s.AttackDistance.ToString(CultureInfo.InvariantCulture);
        LeadText = s.LeadFrames.ToString(CultureInfo.InvariantCulture);
        GiveUpText = s.GiveUpFrames.ToString(CultureInfo.InvariantCulture);
        SecondOpponentText = tests.SecondOpponent ?? string.Empty;
        TrialsText = tests.Trials.ToString(CultureInfo.InvariantCulture);
        FollowUps.Clear();
        if (_index is not null && Owner.Combos.Graph is { } graph)
            foreach (var c in TeachAi.Choices(_index, graph, _engineSha, Owner.ExperimentStore, PlaybackRoots)) FollowUps.Add(new FollowUpRow(c));
        FollowUp = FollowUps.FirstOrDefault(f => f.Choice.AbilityId == s.FollowUpAbilityId) ?? FollowUps.FirstOrDefault();
        SuppressChoices.Clear();
        if (_svc is not null && _index is not null)
            foreach (var r in DirectorOwnership.Analyze(_index, _svc.Facts(), s with { Placement = OwnershipPlacement.BeforeExisting }).Rows.Where(r => r.Suppressible))
                SuppressChoices.Add(new SuppressRow(r) { Checked = s.Suppress.Contains(r.ControllerId) });
    }

    private void Save()
    {
        if (_svc is null || _index is null) return;
        int Num(string text, string what) => int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new FormatException($"{what} must be a whole number.");
        try
        {
            var f = FollowUp?.Choice ?? throw new FormatException("Choose the follow-up.");
            if (!f.Proof.Proven) throw new FormatException(f.Proof.Text);
            var spec = new KnockdownChaseSpec(When!.Value, Num(ChaseLimitText, "The chase limit"), Num(AttackDistanceText, "The attack distance"), Timing!.Value,
                Num(LeadText, "The lead"), Num(GiveUpText, "Give up"), Frequency!.Value, f.AbilityId, f.EntryState, f.Name, Fallback!.Value, (int)Math.Round(f.PowerCost),
                Placement!.Value, Placement.Value == OwnershipPlacement.SuppressSpecific ? SuppressChoices.Where(s => s.Checked).Select(s => s.Row.ControllerId).ToList() : []);
            if (spec.Problems() is { Count: > 0 } problems) throw new FormatException(string.Join(" ", problems));
            var tests = (_draft?.Tests ?? _editingBehavior!.Tests) with
            {
                SecondOpponent = string.IsNullOrWhiteSpace(SecondOpponentText) ? null : SecondOpponentText.Trim(), Trials = Math.Clamp(Num(TrialsText, "Trials"), 1, 5)
            };
            var saved = _editingBehavior is { } existing ? _svc.Revise(existing, spec, tests) : _svc.Propose(_draft! with { Spec = spec, Tests = tests });
            Editing = false;
            _draft = null;
            Status = $"Saved {saved.Name} revision {saved.Revision} as a draft. Test it to generate the working copy.";
            RefreshList(saved.Id);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException) { Status = "Not saved: " + ex.Message; }
    }

    // ------------------------------------------------------------------ test, approve, deploy

    private async Task TestAsync()
    {
        if (_svc is null || _selected is null || _analysis is not { CanGenerate: true } analysis || Owner.Combos.Graph is not { } graph) return;
        var setup = Owner.Combos.Playback.CheckSetup(showIssuesAsSetup: false);
        var scope = DirectorTestJob.ScopePrefix + _selected.Id;
        if (setup is null || !setup.Ready)
        {
            _session.RefuseAttempt(scope, setup is null ? Owner.Combos.Playback.SetupSummary : string.Join(" ", setup.Issues), setup);
            Raise();
            return;
        }

        TaughtBehavior b;
        string hash;
        try { (b, hash) = _svc.GenerateWorkingCopy(analysis); }
        catch (InvalidOperationException ex) { Status = "Not tested: " + ex.Message; return; }
        var svc = _svc;
        var root = Owner.Root;
        var folder = Owner.SubjectFolder;
        var def = Owner.SubjectDef;
        var playback = Owner.PlaybackService;
        // Setups are resolved here, on the UI thread; the suite only looks them up.
        var second = b.Tests.SecondOpponent is { } opponent ? Owner.Combos.Playback.CheckSetup(showIssuesAsSetup: false, dummyOverride: opponent) : null;
        Status = $"Generated into the working copy (build {DirectorHash.Short(hash)}). Testing — IKEMEN windows open one after another and close by themselves.";
        var request = new DirectorTesting.SuiteRequest(b, graph, analysis.Facts, analysis.Code!, svc.Workspace.WorkingFolder, hash, root, folder, def, setup,
            dummy => dummy is not null && second is not null && string.Equals(dummy, b.Tests.SecondOpponent, StringComparison.OrdinalIgnoreCase) ? second : setup,
            svc.TestsRoot);
        await _session.RunDirectorAsync(new DirectorTestJob(b.Id, setup, $"Teach AI test · {b.Name}"), (gate, progress) =>
        {
            var report = DirectorTesting.Run(playback, request, gate, progress);
            return new DirectorTestOutcome(report, svc.RecordTest(b, report));
        });
    }

    private void Approve()
    {
        if (_svc is null || _selected is null || _analysis is null) return;
        var diff = _svc.Diff();
        var text = $"Approve this exact change?\n\n{DirectorText.Sentence(_selected.Behavior)}\n\nOwnership: {_analysis.Ownership.Summary}\n\nFiles: " +
                   string.Join(", ", diff?.Files.Select(f => $"{f.Kind} {f.Path} (+{f.AddedLines})") ?? []) +
                   $"\n\nTest: {ReportHeadline}\n\nApproving does not deploy. Deployment is a separate step.";
        if (!UserDialogs.Confirm(text, "Approve the taught behavior")) return;
        try
        {
            var b = _svc.Approve(_selected.Behavior, _analysis, Environment.UserName);
            Status = $"Approved build {DirectorHash.Short(b.Approval!.BuildHash)}. Deploy is now possible (and separate).";
            RefreshList(b.Id);
        }
        catch (InvalidOperationException ex) { Status = "Not approved: " + ex.Message; }
    }

    private void Deploy()
    {
        if (_svc is null || _selected is null) return;
        if (_svc.DeployBlocker(_selected.Behavior) is { } why) { Status = "Cannot deploy: " + why; Raise(); return; }
        var diff = _svc.Diff()!;
        var text = $"Deploy the approved build to the installed character?\n\n{_svc.InstalledFolder}\n\n" +
                   string.Join("\n", diff.Files.Select(f => $"{f.Kind}: {f.Path} (+{f.AddedLines} lines)")) +
                   "\n\nEach file goes through a verified backup and a manifest; Roll back restores them.";
        if (!UserDialogs.Confirm(text, "Deploy to IKEMEN")) return;
        try
        {
            var b = _svc.Deploy(_selected.Behavior, Owner.Mutations);
            Status = $"Deployed: {b.Deployment!.OperationIds.Count} file(s). Manifest {b.Deployment.ManifestPath}.";
            RefreshList(b.Id);
        }
        catch (InvalidOperationException ex) { Status = "Not deployed (nothing was left half-done): " + ex.Message; }
    }

    private void Rollback()
    {
        if (_svc is null || _selected is null) return;
        if (!UserDialogs.Confirm($"Roll back the deployment of {_selected.Name}? The installed files return to their backed-up content.", "Roll back")) return;
        try { var b = _svc.Rollback(_selected.Behavior, Owner.Mutations); Status = "Rolled back."; RefreshList(b.Id); }
        catch (InvalidOperationException ex) { Status = "Not rolled back: " + ex.Message; }
    }

    private void Retire()
    {
        if (_svc is null || _selected is null) return;
        if (!UserDialogs.Confirm($"Retire {_selected.Name} (rev {_selected.Behavior.Revision})? It is removed from the working copy" +
                                 (_selected.Behavior.Status == TaughtStatus.Live ? " and its deployment is rolled back." : "."), "Retire")) return;
        try { var b = _svc.Retire(_selected.Behavior, Owner.Mutations); Status = "Retired."; RefreshList(b.Id); }
        catch (InvalidOperationException ex) { Status = "Not retired: " + ex.Message; }
    }

    // ------------------------------------------------------------------ Why

    private void LoadMoments()
    {
        Moments.Clear();
        _whyLog = null;
        _why = null;
        _whyFallback = null;
        if (_whyRun is { } r && _report is not null && !string.IsNullOrEmpty(r.Metrics.RunId))
        {
            var dir = Directory.EnumerateDirectories(Path.Combine(_report.Directory, "runs"), r.Metrics.RunId, SearchOption.TopDirectoryOnly).FirstOrDefault();
            var trace = dir is null ? null : Path.Combine(dir, "trace.jsonl");
            if (trace is not null && File.Exists(trace))
            {
                _whyLog = TraceReader.ReadFile(trace);
                foreach (var d in r.Metrics.Decisions)
                    Moments.Add(new DirectorMomentRow(d.Frame, $"{d.Frame}: decision — {DirectorTesting.Reason(d.Why)} → {BehaviorText.State(_index, d.Next)}" +
                                                               (d.DistancePx is { } px ? $" at {px:0} px" : string.Empty) + (d.Executed ? string.Empty : " (NOT executed)"), true));
                var frames = _whyLog.Frames.OrderBy(f => f.Frame).ToList();
                for (var i = 1; i < frames.Count; i++)
                    if (frames[i].P1.State != frames[i - 1].P1.State && Moments.All(m => m.Frame != frames[i].Frame))
                        Moments.Add(new DirectorMomentRow(frames[i].Frame, $"{frames[i].Frame}: {BehaviorText.CharacterName(_index)} → {BehaviorText.State(_index, frames[i].P1.State)}", false));
                var sorted = Moments.OrderBy(m => m.Frame).ToList();
                Moments.Clear();
                foreach (var m in sorted) Moments.Add(m);
            }
        }

        _moment = null;
        Moment = Moments.FirstOrDefault(m => m.IsDecision);
        RaiseWhy();
    }

    private void Why()
    {
        if (_moment is not { } m) return;
        WhyAt(m.Frame);
    }

    /// <summary>Why at one frame of the selected test run (QA / tests).</summary>
    public void WhyAt(long frame)
    {
        if (_whyLog is null || _index is null || _selected is null || _analysis is null) return;
        _why = DirectorWhy.Explain(_index, _selected.Behavior, _analysis.Facts, _whyLog, frame);
        _whyFallback = null;
        if (!_why.Active) _whyFallback = DirectorWhy.NotGenerated;
        RaiseWhy();
    }

    private void RaiseWhy()
    {
        foreach (var n in new[] { nameof(HasWhy), nameof(WhyStatement), nameof(WhyChecks), nameof(WhyAction), nameof(WhyVerification), nameof(WhyKind), nameof(WhyBadge), nameof(WhyRun), nameof(Moment) })
            OnPropertyChanged(n);
        CommandManager.InvalidateRequerySuggested();
    }

    // ------------------------------------------------------------------ plumbing

    private void OnSessionChanged()
    {
        if (_session.IsClosed) return;
        if (_selected is not null && _session.IsFor(DirectorTestJob.ScopePrefix + _selected.Id) && _session.State == PlaybackState.Finished && _session.DirectorResult is { } r)
        {
            Status = string.Empty;
            RefreshList(r.Behavior.Id);
        }

        Raise();
    }

    private void Raise()
    {
        foreach (var n in new[]
                 {
                     nameof(IsBusy), nameof(Headline), nameof(HasHeadline), nameof(HasBehavior), nameof(CardName), nameof(CardWhen), nameof(CardDo), nameof(CardThen),
                     nameof(CardGiveUp), nameof(CardStatus), nameof(CardStatusKind), nameof(CardSentence), nameof(CardSource), nameof(ProofText), nameof(ProofOk),
                     nameof(OwnershipSummary), nameof(OwnershipKind), nameof(Problems), nameof(HasProblems), nameof(GeneratedRules), nameof(GeneratedCode),
                     nameof(DiffText), nameof(WorkingCopyText), nameof(ApprovalText), nameof(DeploymentText), nameof(ReportHeadline), nameof(ReportFindings),
                     nameof(DraftNotes), nameof(DraftSource), nameof(SelectedBehavior)
                 })
            OnPropertyChanged(n);
        CommandManager.InvalidateRequerySuggested();
    }

    private static void Open(string? folder)
    {
        if (folder is null || !Directory.Exists(folder)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { /* no shell */ }
    }
}
