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

    public PlaybackSession(ComboPlaybackService service) => _service = service;

    public event Action? Changed;

    public PlaybackState State { get; private set; } = PlaybackState.Idle;
    public string Phase { get; private set; } = string.Empty;
    public string? Error { get; private set; }
    public PlaybackRequest? Last { get; private set; }
    public PlaybackOutcome? Outcome { get; private set; }

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
            if (IsBusy) return;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
            Last = request;
            Outcome = null;
            Error = null;
            State = PlaybackState.Preparing;
            Phase = "Preparing the sandbox…";
        }

        Changed?.Invoke();
        try
        {
            var outcome = await Task.Run(() => _service.Play(request, OnPhase, token), token).ConfigureAwait(false);
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

        Changed?.Invoke();
    }

    public Task ReplayAsync() => Last is { } r ? PlayAsync(r) : Task.CompletedTask;

    public void Cancel()
    {
        lock (_gate) { if (IsBusy) { _cts?.Cancel(); Phase = "Stopping the engine…"; } }
        Changed?.Invoke();
    }

    private void OnPhase(string phase)
    {
        lock (_gate)
        {
            (State, Phase) = phase switch
            {
                "running" => (PlaybackState.Running, "Playing the combo in IKEMEN — watch the engine window…"),
                "judging" => (PlaybackState.Judging, "Checking what happened…"),
                _ => (PlaybackState.Preparing, "Preparing the sandbox…")
            };
        }

        Changed?.Invoke();
    }
}
