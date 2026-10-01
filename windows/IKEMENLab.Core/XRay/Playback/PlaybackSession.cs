using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.Core.XRay.Playback;

/// <summary>How closing the owner went. <see cref="Clean"/> is false when the run did not stop in time or a sandbox could not be deleted.</summary>
public sealed record ShutdownResult(bool Clean, string? Problem, IReadOnlyList<string> LeftoverSandboxes);

/// <summary>
/// What became of one Play press. Every press gets its own <see cref="PlaybackSession.AttemptId"/>, even one refused before any engine was launched,
/// so a caller can tell "this press produced a runtime verdict" from "an older run's verdict is still on file".
/// </summary>
public enum AttemptState
{
    /// <summary>No Play has been pressed in this session.</summary>
    None,
    /// <summary>Play was accepted and the run is preparing (no engine yet).</summary>
    Started,
    /// <summary>Refused before launch (setup incomplete, engine lacks the X-Ray hook…). No runtime verification took place and none is implied.</summary>
    PreflightRefused,
    /// <summary>The sandbox is built and the engine is (about to be) running.</summary>
    RuntimeStarted,
    /// <summary>The run finished and committed a verdict (Verified, Failed or runtime Inconclusive). The verdict belongs to this attempt.</summary>
    VerdictProduced,
    Cancelled,
    /// <summary>The attempt failed for a reason other than a preflight refusal (e.g. the route cannot be scripted).</summary>
    Error
}

public enum VerdictWaitKind
{
    /// <summary>No Play has been pressed: there is nothing to wait for.</summary>
    NoAttempt,
    /// <summary>The latest attempt is still running.</summary>
    Pending,
    /// <summary>The latest attempt produced a runtime verdict (Verified, Failed or runtime Inconclusive) and it is the stored result.</summary>
    Verdict,
    /// <summary>The latest attempt was refused before any engine launch: there is no runtime verdict, whatever an earlier run left behind.</summary>
    RefusedPreflight,
    /// <summary>The latest attempt ended without a verdict (cancelled, or failed for another reason).</summary>
    EndedWithoutVerdict
}

public sealed record VerdictWait(VerdictWaitKind Kind, string Message);

public enum PlaybackState { Idle, Preparing, Running, Judging, Finished, Cancelled, Error }

/// <summary>
/// The state machine behind the Combo Lens's Play button, kept out of the UI so it can be tested: one run at a time, a phase text for
/// the status line, a Cancel that stops the engine, and the last request kept so Replay plays exactly the same route again.
/// <b>Delivery contract:</b> no subscriber begins after the session is closed — not a later queued notification, and not the remaining subscribers of the notification that
/// was being delivered when a subscriber closed it. A subscriber already running on another thread when the close is requested finishes first (the close waits for it);
/// a subscriber that itself closes the session completes normally. <see cref="Changed"/> is delivered through the <c>post</c> scheduler the owner supplies (the UI passes an asynchronous dispatcher
/// post), never by blocking the worker, and a notification still queued when the session closes is dropped when it finally runs.
/// </summary>
public sealed class PlaybackSession
{
    private readonly ComboPlaybackService _service;
    private readonly object _gate = new();
    /// <summary>Held while a notification is delivered to listeners and while the session closes (lock order: delivery, then gate). Makes "closed" and "a listener is running" mutually exclusive.</summary>
    private readonly object _delivery = new();
    private readonly Action<Action> _post;
    private PlaybackCancellation? _cancel;
    private Task _run = Task.CompletedTask;
    private volatile bool _closed;
    private readonly List<string> _leftovers = [];
    private Task<ShutdownResult>? _shutdown;
    private int _delivered, _dropped;
    private PlaybackRequest? _attemptRequest;
    private PlaybackSetup? _attemptSetup;

    /// <summary>Notifications that reached <see cref="Changed"/> listeners / that were queued but dropped because the session had closed by the time they ran. Direct signals for tests: unlike the state getters, these cannot change legitimately after a close.</summary>
    public int DeliveredNotifications => Volatile.Read(ref _delivered);
    public int DroppedNotifications => Volatile.Read(ref _dropped);

    /// <param name="post">How a notification reaches its listener. It must not block waiting for the listener's thread (use a dispatcher BeginInvoke, not Invoke). Null = call inline.</param>
    public PlaybackSession(ComboPlaybackService service, Action<Action>? post = null)
    {
        _service = service;
        _post = post ?? (a => a());
        _service.CleanupFailed += OnCleanupFailed;
    }

    private void OnCleanupFailed(string path, string? why)
    {
        lock (_gate) _leftovers.Add(why is null ? path : $"{path} ({why})");
    }

    public event Action? Changed;

    public PlaybackState State { get; private set; } = PlaybackState.Idle;
    public string Phase { get; private set; } = string.Empty;
    public string? Error { get; private set; }
    public PlaybackRequest? Last { get; private set; }
    public PlaybackOutcome? Outcome { get; private set; }

    /// <summary>The route this run (or last result) belongs to. Results and result actions are only ever shown against this route.</summary>
    public string? RunRouteKey => Last?.Route.Key;
    public bool IsFor(string? routeKey) => routeKey is not null && RunRouteKey == routeKey;
    public bool IsClosed => _closed;

    // Scoped views: every result and result action is asked for a specific route and answers only for the route the run belonged to.
    public bool HasResultFor(string? routeKey) => HasResult && IsFor(routeKey);
    public bool CanInspectFor(string? routeKey) => CanInspect && IsFor(routeKey);
    public bool CanReplayFor(string? routeKey) => CanReplay && IsFor(routeKey);
    /// <summary>The banner for <paramref name="routeKey"/>: the live phase while a run is in progress (naming its route), the result only for the run's own route, otherwise empty.</summary>
    public string HeadlineFor(string? routeKey) => IsBusy || IsFor(routeKey) ? Headline : string.Empty;

    // ---- attempt identity (every Play press, including refused ones)
    public int AttemptId { get; private set; }
    public AttemptState Attempt { get; private set; } = AttemptState.None;
    public string? AttemptIssue { get; private set; }
    public string? AttemptRouteKey { get; private set; }
    /// <summary>The attempt that produced the stored <see cref="Outcome"/>; null when there is none.</summary>
    public int? ResultAttemptId { get; private set; }
    public string? ResultRunId => Outcome?.Record.Id;
    public string? ResultRouteKey => Outcome?.Report.RouteKey;
    /// <summary>True only when the stored result was produced by the latest attempt. A result left over from an earlier attempt is never current.</summary>
    public bool ResultIsCurrent => Outcome is not null && ResultAttemptId == AttemptId;

    /// <summary>
    /// What "wait for a verdict" means right now: always about the LATEST attempt. A result stored by an earlier attempt never satisfies it, and a
    /// preflight-refused attempt is reported as having no runtime verdict.
    /// </summary>
    public VerdictWait VerdictForLatestAttempt()
    {
        lock (_gate)
        {
            return Attempt switch
            {
                AttemptState.None => new(VerdictWaitKind.NoAttempt, "no runtime verdict: no Play attempt has been made"),
                AttemptState.PreflightRefused => new(VerdictWaitKind.RefusedPreflight, $"no runtime verdict: attempt {AttemptId} refused during preflight: {AttemptIssue}"),
                AttemptState.VerdictProduced when ResultIsCurrent => new(VerdictWaitKind.Verdict, $"attempt {AttemptId} produced a runtime verdict (run {ResultRunId})"),
                AttemptState.Cancelled or AttemptState.Error => new(VerdictWaitKind.EndedWithoutVerdict, $"attempt {AttemptId} ended without a verdict ({Attempt})" + (AttemptIssue is null ? string.Empty : ": " + AttemptIssue)),
                _ => new(VerdictWaitKind.Pending, $"attempt {AttemptId} is {Attempt}")
            };
        }
    }

    public bool IsBusy => State is PlaybackState.Preparing or PlaybackState.Running or PlaybackState.Judging;
    public bool HasResult => Outcome is not null && State == PlaybackState.Finished;
    public bool CanInspect => HasResult && Outcome!.Report.Status != VerifyStatus.Verified;
    public bool CanReplay => !IsBusy && Last is not null;

    /// <summary>One line for the result banner.</summary>
    public string Headline
    {
        get
        {
            switch (State)
            {
                case PlaybackState.Idle: return string.Empty;
                case PlaybackState.Cancelled: return "Playback cancelled.";
                case PlaybackState.Error: return "Could not play: " + Error;
                case PlaybackState.Finished when Outcome is { } o:
                    if (o.Report.Status == VerifyStatus.Verified)
                    {
                        var c = o.Report.Continuity;
                        return $"Verified · {o.Report.Steps.Count} of {o.Report.Steps.Count} transitions observed" +
                               (c.FirstHitFrame is { } a && c.LastFrame is { } b ? $" · opponent stayed in hitstun, frames {a}–{b}" : string.Empty);
                    }

                    return o.Failure?.Headline ?? o.Report.Status.ToString();
                default: return Phase;
            }
        }
    }

    /// <summary>Plays the request. Never throws: failures become <see cref="PlaybackState.Error"/>, Cancel becomes <see cref="PlaybackState.Cancelled"/>.</summary>
    public async Task PlayAsync(PlaybackRequest request)
    {
        // The static evidence is captured once, now, from the graph this attempt was given; the diagnostic reads this copy, never the live files.
        if (request.Snapshot is null) request = request with { Snapshot = StaticSnapshot.Capture(request.Graph, request.Route) };
        PlaybackCancellation gate;
        int myAttempt;
        lock (_gate)
        {
            if (IsBusy || _closed) return;
            _cancel?.Dispose();
            _cancel = new PlaybackCancellation();
            gate = _cancel;
            Last = request;
            _attemptRequest = request;
            _attemptSetup = request.Setup;
            Outcome = null;
            ResultAttemptId = null;
            Error = null;
            AttemptId++;
            myAttempt = AttemptId;   // this call's own identity; nothing below reads the session's 'latest attempt' to describe it
            Attempt = AttemptState.Started;
            AttemptIssue = null;
            AttemptRouteKey = request.Route.Key;
            State = PlaybackState.Preparing;
            Phase = "Preparing the sandbox…";
        }

        Raise();
        try
        {
            var run = Task.Run(() => _service.Play(request, OnPhase, gate));
            lock (_gate) _run = run;
            var outcome = await run.ConfigureAwait(false);
            // The service has already committed this result (it is on disk); a Cancel that arrived after that point does not undo it.
            PlaybackDiagnostic.Source completed;
            lock (_gate)
            {
                Outcome = outcome; ResultAttemptId = myAttempt; Attempt = AttemptState.VerdictProduced; State = PlaybackState.Finished; Phase = string.Empty;
                // One immutable completed-attempt context, built in the same step that publishes the result: this attempt's identity, request, snapshot and outcome.
                completed = new PlaybackDiagnostic.Source(myAttempt, AttemptState.VerdictProduced, null, request.Route.Key, request.Setup, request, request.Snapshot, outcome, myAttempt);
            }

            SaveDiagnostic(completed);
        }
        catch (OperationCanceledException)
        {
            lock (_gate) { Attempt = AttemptState.Cancelled; State = PlaybackState.Cancelled; Phase = string.Empty; }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            lock (_gate)
            {
                // An incomplete setup is a preflight refusal, not a failed run.
                Attempt = request.Setup.Ready ? AttemptState.Error : AttemptState.PreflightRefused;
                AttemptIssue = ex.Message;
                Error = ex.Message; State = PlaybackState.Error; Phase = string.Empty;
            }
        }

        Raise();
    }

    /// <summary>
    /// Records a Play press that was refused before any run (the caller's preflight found the setup unusable). It gets its own attempt id, leaves any earlier
    /// result untouched but no longer current, and never produces a verdict. Ignored while a run is active.
    /// </summary>
    public void RefuseAttempt(string? routeKey, string issue, PlaybackSetup? setup = null)
    {
        lock (_gate)
        {
            if (IsBusy || _closed) return;
            _attemptRequest = null;
            _attemptSetup = setup;
            AttemptId++;
            Attempt = AttemptState.PreflightRefused;
            AttemptIssue = issue;
            AttemptRouteKey = routeKey;
        }

        Raise();
    }

    /// <summary>
    /// The diagnostic for the LATEST attempt, as a snapshot: a run's evidence, or — when the latest attempt was refused before launch, cancelled or failed — a diagnostic of
    /// that attempt alone. An earlier attempt's result is never attached to it. Null before any attempt.
    /// </summary>
    public PlaybackDiagnostic? DiagnosticForLatestAttempt()
    {
        PlaybackDiagnostic.Source src;
        lock (_gate)
        {
            if (AttemptId == 0) return null;
            src = new PlaybackDiagnostic.Source(AttemptId, Attempt, AttemptIssue, AttemptRouteKey, _attemptSetup, _attemptRequest, _attemptRequest?.Snapshot, Outcome, ResultAttemptId);
        }

        return PlaybackDiagnostic.Build(src);
    }

    /// <summary>The latest attempt's diagnostic, only when it belongs to <paramref name="routeKey"/> (the selected route); otherwise null.</summary>
    public PlaybackDiagnostic? DiagnosticFor(string? routeKey) => routeKey is not null && AttemptRouteKey == routeKey ? DiagnosticForLatestAttempt() : null;

    /// <summary>Test seam, called with "before-diagnostic-save" after the result is published and before the diagnostic is written. Null in production.</summary>
    public Action<string>? SaveSeam { get; init; }

    /// <summary>Writes the diagnostic of THIS completed attempt into ITS run folder. It reads nothing from the session, so a replay, refusal or new Play that starts meanwhile cannot change what is written.</summary>
    private void SaveDiagnostic(PlaybackDiagnostic.Source completed)
    {
        SaveSeam?.Invoke("before-diagnostic-save");
        try
        {
            var outcome = completed.Outcome!;
            if (!Directory.Exists(outcome.Record.Directory)) return;
            File.WriteAllText(outcome.Record.DiagnosticPath, PlaybackDiagnostic.Build(completed).ToJson(), new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the diagnostic can still be built on demand */ }
    }

    public Task ReplayAsync() => Last is { } r ? PlayAsync(r) : Task.CompletedTask;

    /// <summary>Asks the run to stop. Returns false when it is too late (the result was already committed) or nothing is running; the UI then does not claim to be stopping.</summary>
    public bool Cancel()
    {
        bool accepted;
        lock (_gate)
        {
            accepted = IsBusy && _cancel is { } c && c.Cancel();
            if (accepted) Phase = "Stopping the engine…";
        }

        if (accepted) Raise();
        return accepted;
    }

    /// <summary>
    /// The owner (the X-Ray window) is going away. Never blocks the caller: it marks the session closed at once (queued notifications become
    /// no-ops), cancels any run (the engine is killed, the sandbox deleted, no record committed unless the commit had already won) and returns a
    /// task that completes when the run has unwound, or reports that it did not within <paramref name="timeout"/> — a timeout or a sandbox that
    /// could not be deleted is returned, not swallowed.
    /// </summary>
    public Task<ShutdownResult> ShutdownAsync(TimeSpan timeout)
    {
        // Taking the delivery lock first means: when this returns, no listener callback is in flight and none can start. A callback that already
        // passed its closed check on another thread completes before the close takes effect, instead of running against a closed view afterwards.
        lock (_delivery)
        lock (_gate)
        {
            if (_shutdown is not null) return _shutdown;
            _closed = true;
            _cancel?.Cancel();
            var run = _run;
            _shutdown = WaitForRun(run, timeout);
            return _shutdown;
        }
    }

    private async Task<ShutdownResult> WaitForRun(Task run, TimeSpan timeout)
    {
        string? problem = null;
        var done = await Task.WhenAny(run, Task.Delay(timeout)).ConfigureAwait(false);
        if (done != run) problem = $"The playback did not stop within {timeout.TotalSeconds:0} s; its engine or sandbox may still be running.";
        else if (run.IsFaulted && run.Exception!.GetBaseException() is not OperationCanceledException and { } ex) problem = "The playback ended with an error while closing: " + ex.Message;

        string[] left;
        lock (_gate) left = _leftovers.ToArray();
        return new ShutdownResult(problem is null && left.Length == 0, problem, left);
    }

    /// <summary>Posts a notification without waiting for the listener. The closed check runs again when the post finally executes, so a notification queued before a close never reaches a closed view.</summary>
    private void Raise()
    {
        if (_closed) return;
        _post(() =>
        {
            // The closed check and the delivery are one step under the delivery lock; ShutdownAsync takes the same lock to close.
            lock (_delivery)
            {
                if (_closed) { Interlocked.Increment(ref _dropped); return; }
                Interlocked.Increment(ref _delivered);
                // Subscribers are invoked one by one with the closed check repeated before each, so a subscriber that closes the session
                // (the lock is reentrant on its own thread) stops the later subscribers of this same notification instead of letting a multicast Invoke run on.
                if (Changed is { } handlers)
                    foreach (var h in handlers.GetInvocationList())
                    {
                        if (_closed) break;
                        ((Action)h)();
                    }
            }
        });
    }

    private (PlaybackState, string) Running()
    {
        Attempt = AttemptState.RuntimeStarted;
        return (PlaybackState.Running, "Playing the combo in IKEMEN — watch the engine window…");
    }

    private void OnPhase(string phase)
    {
        lock (_gate)
        {
            if (_closed) return;
            (State, Phase) = phase switch
            {
                "running" => Running(),
                "judging" => (PlaybackState.Judging, "Checking what happened…"),
                _ => (PlaybackState.Preparing, "Preparing the sandbox…")
            };
        }

        Raise();
    }
}
