using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.Core.XRay.Behavior;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;

namespace IKEMENLab.App.ViewModels;

/// <summary>A recognised-behavior card (read-only): the behavior in plain words, its evidence level, and — behind Details — the technical evidence.</summary>
public sealed class BehaviorCardRow(BehaviorCard card, SemanticIndex index)
{
    public BehaviorCard Card { get; } = card;
    public string Id => Card.Template.Id;
    public string Name => Card.Template.Name;
    public string Category => Card.Template.Category;
    public string Summary => Card.Template.Summary;
    public string Level => Card.LevelName;
    /// <summary>Confirmed, Observed, Possible or NotSeen (the badge colour).</summary>
    public string LevelKind => Card.Level switch { BehaviorLevel.ConfirmedPattern => "Confirmed", BehaviorLevel.Observed => "Observed", BehaviorLevel.Possible => "Possible", _ => "NotSeen" };
    public string LevelMeaning => Card.LevelMeaning;
    public string Counts => Card.Episodes.Count == 0
        ? Card.StaticRules.Count > 0 ? $"{Card.StaticRules.Count} static rule(s), no runtime episode yet" : "no evidence yet"
        : $"{Card.Episodes.Count} episode(s) · {Card.Runs} run(s) · {Card.Setups.Count} setup(s)";
    public string Conditions => string.Join(" · ", Card.Template.Conditions.Select(BehaviorConditions.Name));
    public string Actions => string.Join("; ", Card.Template.Actions);
    public string Outcomes => Card.Outcomes.Count == 0 ? string.Join("; ", Card.Template.Outcomes)
        : string.Join(" · ", Card.Outcomes.OrderByDescending(kv => kv.Value).Select(kv => $"{BehaviorText.Outcome(kv.Key)} ×{kv.Value}"));
    public string Setups => Card.Setups.Count == 0 ? "—" : string.Join("; ", Card.Setups);
    public string Stale => Card.StaleEpisodes == 0 ? string.Empty : $"{Card.StaleEpisodes} older episode(s) no longer count: {string.Join("; ", Card.StaleReasons)}.";
    public bool HasStale => Card.StaleEpisodes > 0;
    public IReadOnlyList<string> Limitations => Card.Template.Limitations.Concat(Card.Unknowns).ToList();
    public IReadOnlyList<string> StaticRules => Card.StaticRules.Take(6).Select(r => BehaviorWhy.RuleText(index, r)).ToList();
    public string StaticNote => Card.StaticRules.Count == 0
        ? "No AI rule with these recognised conditions was found (AI that decides through variables is not read this way — that is not evidence of absence)."
        : "These static rules are consistent with it; which one actually fires is not known.";
    public IReadOnlyList<BehaviorLink> Links => Card.ActionStates.Select(s => BehaviorLink.Of(index, s)).ToList();
    public string Evidence =>
        $"Pattern: {Card.Template.Complete}\nConfirmed Pattern rule: {Card.Rule.Text}.\nRanges: close < {BehaviorConditions.CloseMax:0}, medium ≤ {BehaviorConditions.MediumMax:0} (engine units).\n" +
        string.Join("\n", Card.Episodes.Take(10).Select(e => $"run {e.Run.Id} frames {e.Episode.StartFrame}–{e.Episode.EndFrame} {e.Run.SetupText}: {e.Episode.Outcome}"));
}

/// <summary>A link from a behavior card down to the state (and ability) that took part.</summary>
public sealed record BehaviorLink(string Id, string Text)
{
    public static BehaviorLink Of(SemanticIndex index, int state)
    {
        var id = "state:" + state.ToString(CultureInfo.InvariantCulture);
        var ability = index.Of(ObjectKind.Ability).FirstOrDefault(a => a.Prop("entryState") == id);
        return new BehaviorLink(index.Get(id) is null ? string.Empty : id,
            BehaviorText.State(index, state) + (ability is null ? string.Empty : $" — ability {index.NameOf(ability.Id)}"));
    }
}

/// <summary>A watched run in the picker.</summary>
public sealed record WatchRunChoice(BehaviorRun Run, string Title, bool Stale);

/// <summary>One recognised episode: what triggered it, what happened, what followed, and its evidence badge.</summary>
public sealed record EpisodeRow(BehaviorEpisode Episode, string Behavior, string Badge, string BadgeKind, string Situation, string Action, string Outcome, string Frames,
    IReadOnlyList<string> Steps);

/// <summary>One line of the semantic timeline.</summary>
public sealed record TimelineRow(long Frame, string Text, bool IsEpisode, string FrameText);

/// <summary>
/// Watch &amp; Ask (Phase 5): watch the character fight on the engine's AI, read what it did as combat behavior (Knockdown Chase, Anti-Air, Punish …)
/// instead of state numbers, ask "Why?" about a moment, and see each behavior's evidence level (Possible / Observed / Confirmed Pattern). Read-only:
/// nothing here edits the character or its AI. Runs go through the window's one playback session into the watched-run store.
/// </summary>
public sealed class WatchAskLens : XRayLens
{
    private readonly PlaybackSession _session;
    private SemanticIndex? _index;
    private string _secondsText = BehaviorWatch.DefaultSeconds.ToString(CultureInfo.InvariantCulture);
    private string _opponentAiText = BehaviorWatch.DefaultOpponentAi.ToString(CultureInfo.InvariantCulture);
    private string _status = string.Empty;
    private BehaviorCardRow? _selectedCard;
    private WatchRunChoice? _selectedRun;
    private EpisodeRow? _selectedEpisode;
    private TimelineRow? _selectedMoment;
    private TraceLog? _log;
    private WhyAnswer? _why;
    private bool _showAllEvents, _showDetails;
    private IReadOnlyList<TimelineRow> _allEvents = [];
    private string? _engineSha;

    public WatchAskLens(XRayViewModel owner) : base(owner)
    {
        _session = owner.PlaybackSession;
        _session.Changed += OnSessionChanged;
        WatchCommand = new AsyncRelayCommand(WatchAsync, () => !_session.IsBusy && _index is not null);
        CancelCommand = new RelayCommand(() => _session.Cancel(), () => _session.IsBusy);
        WhyCommand = new RelayCommand(Why, () => _selectedMoment is not null && _selectedRun is not null && _log is not null);
        ToggleAllEventsCommand = new RelayCommand(() => ShowAllEvents = !ShowAllEvents);
        ToggleDetailsCommand = new RelayCommand(() => ShowDetails = !ShowDetails);
        ShowLinkCommand = new RelayCommand(p => { if (p is BehaviorLink { Id.Length: > 0 } l) Owner.Select(l.Id); });
        OpenFolderCommand = new RelayCommand(OpenFolder, () => _selectedRun is not null);
        TeachAiCommand = new RelayCommand(() => { if (_selectedRun is { } r && _selectedEpisode is { } e) Owner.Director.TeachFromEpisode(r.Run, e.Episode); },
            () => _selectedEpisode?.Episode.Template == BehaviorTemplates.KnockdownChase && _selectedRun is { Stale: false } && !_session.IsBusy);
    }

    public ObservableCollection<BehaviorCardRow> Cards { get; } = [];
    public ObservableCollection<WatchRunChoice> Runs { get; } = [];
    public ObservableCollection<EpisodeRow> Episodes { get; } = [];
    public ObservableCollection<TimelineRow> Timeline { get; } = [];

    public ICommand WatchCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand WhyCommand { get; }
    public ICommand ToggleAllEventsCommand { get; }
    public ICommand ToggleDetailsCommand { get; }
    public ICommand ShowLinkCommand { get; }
    public ICommand OpenFolderCommand { get; }
    /// <summary>Teach AI (Phase 6): a Knockdown Chase pre-filled from the selected recognised chase.</summary>
    public ICommand TeachAiCommand { get; }

    public string SecondsText { get => _secondsText; set => SetProperty(ref _secondsText, value); }
    public string OpponentAiText { get => _opponentAiText; set => SetProperty(ref _opponentAiText, value); }
    public string Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => _status.Length > 0;
    public bool IsBusy => _session.IsBusy;
    public bool ShowDetails { get => _showDetails; set => SetProperty(ref _showDetails, value); }

    public bool ShowAllEvents
    {
        get => _showAllEvents;
        set { if (SetProperty(ref _showAllEvents, value)) RefreshTimeline(); }
    }

    private string Scope => WatchJob.ScopePrefix + Owner.SubjectFolder;
    public string Headline => _session.IsBusy && _session.RunMode == PlaybackMode.Watch ? _session.Phase
        : _session.IsFor(Scope) && _session.State == PlaybackState.Finished && _session.WatchResult is { } w ? BehaviorText.Headline(w.Run)
        : _session.IsFor(Scope) && _session.State == PlaybackState.Cancelled ? "Watching cancelled."
        : _session.IsFor(Scope) && _session.State == PlaybackState.Error ? "Could not watch: " + _session.Error
        : _session.Attempt == AttemptState.PreflightRefused && _session.AttemptRouteKey == Scope ? "Not started — playback setup is incomplete: " + _session.AttemptIssue
        : string.Empty;
    public bool HasHeadline => Headline.Length > 0;

    // ------------------------------------------------------------------ cards

    public BehaviorCardRow? SelectedCard
    {
        get => _selectedCard;
        set { if (SetProperty(ref _selectedCard, value)) OnPropertyChanged(nameof(HasCard)); }
    }

    public bool HasCard => _selectedCard is not null;

    // ------------------------------------------------------------------ runs, episodes, timeline

    public WatchRunChoice? SelectedRun
    {
        get => _selectedRun;
        set
        {
            if (!SetProperty(ref _selectedRun, value)) return;
            LoadRun();
        }
    }

    public EpisodeRow? SelectedEpisode
    {
        get => _selectedEpisode;
        set
        {
            if (!SetProperty(ref _selectedEpisode, value)) return;
            // The card of the behavior being shown follows the episode.
            if (value is not null && Cards.FirstOrDefault(c => c.Id == value.Episode.Template) is { } card) SelectedCard = card;
            foreach (var n in new[] { nameof(HasEpisode), nameof(CurrentBehavior), nameof(CurrentBadge), nameof(CurrentBadgeKind), nameof(CurrentSituation),
                         nameof(CurrentAction), nameof(CurrentOutcome), nameof(CurrentSteps), nameof(DetailsText) })
                OnPropertyChanged(n);
            RefreshTimeline();
        }
    }

    public bool HasEpisode => _selectedEpisode is not null;
    public string CurrentBehavior => _selectedEpisode?.Behavior ?? (_selectedRun is null ? "Watch a match to see its behavior." : "No behavior episode was recognised in this run.");
    public string CurrentBadge => _selectedEpisode?.Badge ?? string.Empty;
    public string CurrentBadgeKind => _selectedEpisode?.BadgeKind ?? "NotSeen";
    public string CurrentSituation => _selectedEpisode?.Situation ?? string.Empty;
    public string CurrentAction => _selectedEpisode?.Action ?? string.Empty;
    public string CurrentOutcome => _selectedEpisode?.Outcome ?? string.Empty;
    public IReadOnlyList<string> CurrentSteps => _selectedEpisode?.Steps ?? [];

    public TimelineRow? SelectedMoment
    {
        get => _selectedMoment;
        set { if (SetProperty(ref _selectedMoment, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    // ------------------------------------------------------------------ Why

    public bool HasWhy => _why is not null;
    public string WhySummary => _why?.Summary ?? string.Empty;
    public string WhyContext => _why?.Context ?? string.Empty;
    public string WhyNext => _why?.Action ?? "Nothing recognisable happened in the next 1.5 s.";
    public string WhyConditions => _why is null ? string.Empty : string.Join(" · ", _why.ConditionsHeld.Select(BehaviorConditions.Name));
    public IReadOnlyList<string> WhyRules => _why is null || _index is null ? [] : _why.ConsistentRules.Select(r => BehaviorWhy.RuleText(_index, r)).ToList();
    public string WhyCause => _why?.CausalStatement ?? string.Empty;

    public string DetailsText
    {
        get
        {
            if (_selectedRun is not { } r) return string.Empty;
            var d = r.Run.Detection;
            var lines = new List<string>
            {
                $"run {r.Run.Id} · {r.Run.SetupText} · {r.Run.Seconds} s · {d.Frames} samples · {d.Rounds} round(s)",
                $"control: {d.Control}",
                $"character files {r.Run.CharacterHash} · engine {r.Run.EngineSha256 ?? "not recorded"} · detector v{r.Run.DetectorVersion}",
                r.Stale ? "STALE: " + BehaviorCatalog.StaleReason(r.Run, _index is null ? string.Empty : Core.XRay.Sequences.ExperimentScope.HashOf(_index), _engineSha) : "current: these files and this engine"
            };
            if (d.Missing.Count > 0) lines.Add("not readable on this build: " + string.Join("; ", d.Missing));
            lines.AddRange(d.Notes.Select(n => "note: " + n));
            if (_selectedEpisode is { } e)
            {
                lines.Add($"episode {e.Episode.Template} round {e.Episode.Round} frames {e.Episode.StartFrame}–{e.Episode.EndFrame}, outcome {e.Episode.Outcome}");
                foreach (var s in e.Episode.Steps)
                    lines.Add($"  {s.Frame}: {s.Code}" + (s.State is { } st ? $" state {st}" : string.Empty) + (s.From is { } f ? $" from {f:0.#}" : string.Empty) + (s.To is { } to ? $" to {to:0.#}" : string.Empty));
                lines.Add("  conditions: " + string.Join(", ", e.Episode.Conditions));
                lines.AddRange(e.Episode.Notes.Select(n => "  note: " + n));
            }

            lines.Add("record: " + r.Run.Directory);
            return string.Join("\n", lines);
        }
    }

    // ------------------------------------------------------------------ lens contract

    public override void Build(SemanticIndex index)
    {
        _index = index;
        _engineSha = EngineIdentity.Sha256(Owner.Combos.Playback.CheckSetup(showIssuesAsSetup: false)?.EnginePath);
        RefreshCards();
        RefreshRuns(_selectedRun?.Run.Id);
    }

    public override void OnSelection(SemanticIndex index, string? id, IReadOnlySet<string> related) { }

    private void RefreshCards()
    {
        if (_index is null || Owner.Combos.Graph is not { } graph) return;
        var keep = _selectedCard?.Id;
        var runs = Owner.BehaviorStore.List(_index.CharacterId, StaticBehavior.SuperStates(_index));
        var cards = BehaviorCatalog.Build(_index, graph, runs, Core.XRay.Sequences.ExperimentScope.HashOf(_index), _engineSha);
        Cards.Clear();
        foreach (var c in cards.OrderByDescending(c => c.Level).ThenBy(c => c.Template.Name, StringComparer.CurrentCulture)) Cards.Add(new BehaviorCardRow(c, _index));
        SelectedCard = Cards.FirstOrDefault(c => c.Id == keep) ?? Cards.FirstOrDefault();
    }

    private void RefreshRuns(string? select)
    {
        if (_index is null) return;
        var hash = Core.XRay.Sequences.ExperimentScope.HashOf(_index);
        Runs.Clear();
        foreach (var r in Owner.BehaviorStore.List(_index.CharacterId, StaticBehavior.SuperStates(_index)))
        {
            var stale = BehaviorCatalog.StaleReason(r, hash, _engineSha) is not null;
            Runs.Add(new WatchRunChoice(r, $"{r.CreatedUtc.ToLocalTime():MMM d HH:mm} · {r.SetupText} · {r.Detection.Episodes.Count} episode(s)" + (stale ? " · stale" : string.Empty), stale));
        }

        _selectedRun = null;
        SelectedRun = Runs.FirstOrDefault(r => r.Run.Id == select) ?? Runs.FirstOrDefault();
        if (_selectedRun is null) LoadRun();
    }

    private void LoadRun()
    {
        Episodes.Clear();
        _allEvents = [];
        _log = null;
        _why = null;
        if (_selectedRun is { } r && _index is not null)
        {
            _log = Owner.BehaviorStore.Trace(r.Run);
            var levels = Cards.ToDictionary(c => c.Id, c => c);
            foreach (var e in r.Run.Detection.Episodes)
            {
                var (situation, action, outcome) = BehaviorText.Parts(e, _index);
                var card = levels.TryGetValue(e.Template, out var c) ? c : null;
                // A run where the engine reported P1 off the AI never counts as AI evidence, whatever its card says.
                var badge = r.Stale ? "Stale" : !r.Run.Detection.AiControlled ? "Not AI evidence" : card?.Level ?? "Observed";
                var kind = r.Stale || !r.Run.Detection.AiControlled ? "NotSeen" : card?.LevelKind ?? "Observed";
                Episodes.Add(new EpisodeRow(e, BehaviorTemplates.Get(e.Template).Name, badge, kind, situation, action, outcome, $"frames {e.StartFrame}–{e.EndFrame}",
                    BehaviorText.Steps(e, _index)));
            }

            if (_log is not null)
                _allEvents = BehaviorTimeline.Build(_log, r.Run.Detection)
                    .Select(t => new TimelineRow(t.Frame, BehaviorText.Event(t, _index), t.Kind is "episode-start" or "episode-end", t.Frame.ToString(CultureInfo.InvariantCulture))).ToList();
        }

        // The most telling episode first: a knockdown chase if there is one.
        _selectedEpisode = null;
        SelectedEpisode = Episodes.OrderBy(e => e.Episode.Template == BehaviorTemplates.KnockdownChase ? 0 : 1).ThenBy(e => e.Episode.StartFrame).FirstOrDefault();
        if (_selectedEpisode is null) { RefreshTimeline(); foreach (var n in new[] { nameof(HasEpisode), nameof(CurrentBehavior), nameof(DetailsText) }) OnPropertyChanged(n); }
        RaiseWhy();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>The timeline: by default the selected episode with a little context around it; with All events, the whole run.</summary>
    private void RefreshTimeline()
    {
        Timeline.Clear();
        IEnumerable<TimelineRow> rows = _allEvents;
        if (!_showAllEvents && _selectedEpisode is { } e)
            rows = _allEvents.Where(t => t.Frame >= e.Episode.StartFrame - 30 && t.Frame <= e.Episode.EndFrame + 30);
        foreach (var t in rows.Take(400)) Timeline.Add(t);
    }

    // ------------------------------------------------------------------ actions

    private async Task WatchAsync()
    {
        if (_index is null) return;
        var setup = Owner.Combos.Playback.CheckSetup(showIssuesAsSetup: false);
        if (setup is null || !setup.Ready)
        {
            _session.RefuseAttempt(Scope, setup is null ? Owner.Combos.Playback.SetupSummary : string.Join(" ", setup.Issues), setup);
            Raise();
            return;
        }

        var seconds = int.TryParse(_secondsText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : BehaviorWatch.DefaultSeconds;
        var opponent = int.TryParse(_opponentAiText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : BehaviorWatch.DefaultOpponentAi;
        var request = BehaviorWatch.Request(Owner.Root, _index, Owner.SubjectFolder, Owner.SubjectDef, setup, seconds, BehaviorWatch.DefaultSubjectAi, opponent, Owner.CharacterName);
        Status = $"Watching {request.Seconds} s: {Owner.CharacterName} on AI level {request.SubjectAiLevel} vs {setup.Dummy} on AI level {request.OpponentAiLevel}. An IKEMEN window plays the match and closes by itself.";
        var index = _index;
        await _session.WatchAsync(BehaviorWatch.Job(request), (gate, progress) => BehaviorWatch.Run(Owner.PlaybackService, Owner.BehaviorStore, request, index, gate, progress));
    }

    private void Why()
    {
        if (_selectedMoment is not { } m || _selectedRun is not { } r || _log is null || _index is null || Owner.Combos.Graph is not { } graph) return;
        _why = BehaviorWhy.Explain(_index, graph, r.Run, _log, m.Frame);
        RaiseWhy();
    }

    /// <summary>Why for a frame (QA / tests).</summary>
    public void WhyAt(long frame)
    {
        SelectedMoment = _allEvents.FirstOrDefault(t => t.Frame == frame) ?? new TimelineRow(frame, string.Empty, false, frame.ToString(CultureInfo.InvariantCulture));
        Why();
    }

    private void RaiseWhy()
    {
        foreach (var n in new[] { nameof(HasWhy), nameof(WhySummary), nameof(WhyContext), nameof(WhyNext), nameof(WhyConditions), nameof(WhyRules), nameof(WhyCause) })
            OnPropertyChanged(n);
    }

    private void OpenFolder()
    {
        if (_selectedRun?.Run.Directory is not { } dir || !Directory.Exists(dir)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { /* no shell */ }
    }

    private void OnSessionChanged()
    {
        if (_session.IsClosed) return;
        if (_session.IsFor(Scope) && _session.State == PlaybackState.Finished && _session.WatchResult is { } w && _index is not null)
        {
            Status = string.Empty;
            RefreshCards();
            RefreshRuns(w.Run.Id);
        }
        else if (_session.IsFor(Scope) && _session.State is PlaybackState.Cancelled or PlaybackState.Error) Status = string.Empty;

        Raise();
    }

    private void Raise()
    {
        foreach (var n in new[] { nameof(IsBusy), nameof(Headline), nameof(HasHeadline) }) OnPropertyChanged(n);
        CommandManager.InvalidateRequerySuggested();
    }
}
