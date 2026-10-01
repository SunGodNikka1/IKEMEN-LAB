using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.Core.XRay.Playback;

public enum PlaybackState { Idle, Preparing, Running, Judging, Finished, Cancelled, Error }

/// <summary>
/// The state machine behind the Combo Lens's Play button, kept out of the UI so it can be tested: one run at a time, a phase text for
/// the status line, a Cancel that stops the engine, and the last request kept so Replay plays exactly the same route again.
/// <see cref="Changed"/> may be raised on a background thread; the UI marshals it.
/// </summary>
public sealed class PlaybackSession
{
    private readonly ComboPlaybackService _service;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task _run = Task.CompletedTask;
    private volatile bool _closed;

    public PlaybackSession(ComboPlaybackService service) => _service = service;

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
        CancellationToken token;
        lock (_gate)
        {
            if (IsBusy || _closed) return;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
            Last = request;
            Outcome = null;
            Error = null;
            State = PlaybackState.Preparing;
            Phase = "Preparing the sandbox…";
        }

        Raise();
        try
        {
            var run = Task.Run(() => _service.Play(request, OnPhase, token));
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

    public void Cancel()
    {
        lock (_gate) { if (IsBusy) { _cts?.Cancel(); Phase = "Stopping the engine…"; } }
        Raise();
    }

    /// <summary>
    /// The owner (the X-Ray window) is going away: stop any run, kill its engine, let the sandbox clean up, and never raise
    /// <see cref="Changed"/> again so nothing touches a closed window. Waits (bounded) for the run to finish unwinding.
    /// </summary>
    public bool Shutdown(TimeSpan wait)
    {
        Task run;
        lock (_gate)
        {
            _closed = true;
            _cts?.Cancel();
            run = _run;
        }

        try { return run.Wait(wait); }
        catch (AggregateException) { return true; }   // a cancelled or failed run has unwound; the exception is the point of Cancel
    }

    private void Raise()
    {
        if (!_closed) Changed?.Invoke();
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
