using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.Core.XRay.Playback;

/// <summary>How closing the owner went. <see cref="Clean"/> is false when the run did not stop in time or a sandbox could not be deleted.</summary>
public sealed record ShutdownResult(bool Clean, string? Problem, IReadOnlyList<string> LeftoverSandboxes);

public enum PlaybackState { Idle, Preparing, Running, Judging, Finished, Cancelled, Error }

/// <summary>
/// The state machine behind the Combo Lens's Play button, kept out of the UI so it can be tested: one run at a time, a phase text for
/// the status line, a Cancel that stops the engine, and the last request kept so Replay plays exactly the same route again.
/// <see cref="Changed"/> is delivered through the <c>post</c> scheduler the owner supplies (the UI passes an asynchronous dispatcher
/// post), never by blocking the worker, and a notification still queued when the session closes is dropped when it finally runs.
/// </summary>
public sealed class PlaybackSession
{
    private readonly ComboPlaybackService _service;
    private readonly object _gate = new();
    private readonly Action<Action> _post;
    private PlaybackCancellation? _cancel;
    private Task _run = Task.CompletedTask;
    private volatile bool _closed;
    private readonly List<string> _leftovers = [];
    private Task<ShutdownResult>? _shutdown;

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
        PlaybackCancellation gate;
        lock (_gate)
        {
            if (IsBusy || _closed) return;
            _cancel?.Dispose();
            _cancel = new PlaybackCancellation();
            gate = _cancel;
            Last = request;
            Outcome = null;
            Error = null;
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
            lock (_gate) { Outcome = outcome; State = PlaybackState.Finished; Phase = string.Empty; }
        }
        catch (OperationCanceledException)
        {
            lock (_gate) { State = PlaybackState.Cancelled; Phase = string.Empty; }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            lock (_gate) { Error = ex.Message; State = PlaybackState.Error; Phase = string.Empty; }
        }

        Raise();
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
        _post(() => { if (!_closed) Changed?.Invoke(); });
    }

    private void OnPhase(string phase)
    {
        lock (_gate)
        {
            if (_closed) return;
            (State, Phase) = phase switch
            {
                "running" => (PlaybackState.Running, "Playing the combo in IKEMEN — watch the engine window…"),
                "judging" => (PlaybackState.Judging, "Checking what happened…"),
                _ => (PlaybackState.Preparing, "Preparing the sandbox…")
            };
        }

        Raise();
    }
}
