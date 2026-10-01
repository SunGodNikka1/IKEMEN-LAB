namespace IKEMENLab.Core.XRay.Playback;

/// <summary>
/// One run's cancel-or-commit decision, made exactly once under one lock. The run is <c>Pending</c> until either
/// <see cref="Cancel"/> wins (the run must produce no result and no record) or <see cref="TryCommit"/> wins (the result is published and
/// a later <see cref="Cancel"/> is refused). The two cannot both succeed: both are state transitions out of Pending taken under
/// the same lock, and the publishing work itself runs inside that lock.
/// </summary>
public sealed class PlaybackCancellation : IDisposable
{
    private enum Phase { Pending, Cancelled, Committed }

    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenRegistration _link;
    private Phase _phase = Phase.Pending;

    /// <param name="external">An outside token (tests, other callers) whose cancellation is routed through <see cref="Cancel"/>.</param>
    public PlaybackCancellation(CancellationToken external = default)
    {
        if (external.CanBeCanceled) _link = external.Register(() => Cancel());
    }

    /// <summary>Signalled once a cancel has been accepted; the engine runner and the copy loops watch it to stop early.</summary>
    public CancellationToken Token => _cts.Token;

    public bool IsCommitted { get { lock (_lock) return _phase == Phase.Committed; } }
    public bool IsCancelled { get { lock (_lock) return _phase == Phase.Cancelled; } }

    /// <summary>True when the cancel is in force (accepted now, or already was): the run will produce no result. False when the result was already committed.</summary>
    public bool Cancel()
    {
        bool signal;
        lock (_lock)
        {
            if (_phase == Phase.Committed) return false;
            signal = _phase == Phase.Pending;
            _phase = Phase.Cancelled;
        }

        if (signal) _cts.Cancel();
        return true;
    }

    /// <summary>
    /// Runs <paramref name="publish"/> and marks the run committed, atomically with respect to <see cref="Cancel"/>. Returns false, without
    /// running it, when a cancel was accepted first. If <paramref name="publish"/> throws the run stays pending and the exception propagates.
    /// </summary>
    public bool TryCommit(Action publish)
    {
        lock (_lock)
        {
            if (_phase == Phase.Cancelled) return false;
            if (_phase == Phase.Pending)
            {
                publish();
                _phase = Phase.Committed;
            }

            return true;
        }
    }

    public void Dispose()
    {
        _link.Dispose();
        _cts.Dispose();
    }
}
