using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;

namespace IKEMENLab.Mcp.Lab;

public enum JobStatus { Queued, Running, Completed, Failed, Cancelled }

/// <summary>What a job's work produced: its final status, its result (null when there is none) and the error text for a failed job.</summary>
public sealed record JobOutcome(JobStatus Status, JsonObject? Result, string? Error);

/// <summary>One long-running request (Play Ability, State Preview, a sequence experiment). Its state is what get_job reports.</summary>
public sealed partial class LabJob
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal LabJob(string id, string kind, string what, string? estimate, Func<PlaybackSession, Task<JobOutcome>> work)
    {
        Id = id;
        Kind = kind;
        What = what;
        Estimate = estimate;
        Work = work;
    }

    public string Id { get; }
    public string Kind { get; }
    public string What { get; }
    public string? Estimate { get; }
    internal Func<PlaybackSession, Task<JobOutcome>> Work { get; }
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;
    public DateTime? StartedUtc { get; private set; }
    public DateTime? FinishedUtc { get; private set; }
    public JobStatus Status { get; private set; } = JobStatus.Queued;
    public string? Phase { get; private set; }
    public string? WaitingFor { get; private set; }
    public string? Error { get; private set; }
    public JsonObject? Result { get; private set; }
    public int? Trial { get; private set; }
    public int? Trials { get; private set; }
    public bool IsFinished => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;
    /// <summary>The session's attempt counter when this job was handed to it: the session has started the job once its counter moved past this.</summary>
    internal int SessionAttempt { get; set; } = -1;
    private int _cancelPending;

    /// <summary>A cancel that arrived after the job started but before the session went busy; applied as soon as it does.</summary>
    internal void RequestPendingCancel() => Interlocked.Exchange(ref _cancelPending, 1);

    /// <summary>True once (and clears it) when a pending cancel is waiting.</summary>
    internal bool TakePendingCancel() => Interlocked.Exchange(ref _cancelPending, 0) == 1;
    public Task Finished => _done.Task;

    internal void Waiting(string? holder) { lock (_gate) WaitingFor = holder; }

    internal bool Start()
    {
        lock (_gate)
        {
            if (Status != JobStatus.Queued) return false;
            Status = JobStatus.Running;
            StartedUtc = DateTime.UtcNow;
            WaitingFor = null;
            Phase = "Preparing the sandbox…";
            return true;
        }
    }

    internal void Progress(string phase)
    {
        lock (_gate)
        {
            if (Status != JobStatus.Running || string.IsNullOrEmpty(phase)) return;
            Phase = phase;
            var m = TrialPhase().Match(phase);
            if (m.Success)
            {
                Trial = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                Trials = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Ends the job once; later calls are ignored. Returns false when it had already ended.</summary>
    internal bool Finish(JobStatus status, JsonObject? result, string? error)
    {
        lock (_gate)
        {
            if (IsFinished) return false;
            Status = status;
            Result = result;
            Error = error;
            Phase = null;
            WaitingFor = null;
            FinishedUtc = DateTime.UtcNow;
        }

        _done.TrySetResult();
        return true;
    }

    public JsonObject ToJson(bool withResult = true, int? queuePosition = null)
    {
        lock (_gate)
        {
            var o = new JsonObject
            {
                ["jobId"] = Id,
                ["kind"] = Kind,
                ["what"] = What,
                ["status"] = Status.ToString().ToLowerInvariant()
            };
            if (Phase is not null) o["phase"] = Phase;
            if (Trials is { } n) o["progress"] = new JsonObject { ["trial"] = Trial, ["of"] = n };
            if (queuePosition is > 0 && Status == JobStatus.Queued) o["queuedBehind"] = queuePosition;
            if (WaitingFor is not null) o["waitingForEngine"] = "The engine is busy: " + WaitingFor + ". This job starts as soon as it is free (cancel_job cancels it).";
            if (Estimate is not null && !IsFinished) o["estimate"] = Estimate;
            o["created"] = CreatedUtc.ToString("HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
            if (StartedUtc is { } s && FinishedUtc is { } f) o["seconds"] = Math.Round((f - s).TotalSeconds, 1);
            else if (StartedUtc is { } s2) o["runningSeconds"] = Math.Round((DateTime.UtcNow - s2).TotalSeconds, 1);
            if (Error is not null) o["error"] = Error;
            if (withResult && Result is not null) o["result"] = Result.DeepClone();
            if (!IsFinished) o["next"] = "Call get_job with this jobId (wait_seconds up to 50 waits for it to finish).";
            return o;
        }
    }

    [GeneratedRegex(@"^Trial (\d+) of (\d+)")]
    private static partial Regex TrialPhase();
}

/// <summary>
/// Runs MCP's long jobs one at a time through ONE <see cref="PlaybackSession"/> (the same state machine the X-Ray window uses), each under a lease on the
/// machine-wide <see cref="RuntimeBroker"/>: while the desktop app, the CLI or another MCP session holds the engine, the next job stays queued and says
/// who it is waiting for — it never races them. Cancel stops the engine (the session's own cancel); closing the server cancels everything and waits for
/// the engine and its sandbox to be cleaned up.
/// </summary>
public sealed class JobQueue
{
    public const int KeepFinishedJobs = 50;

    private readonly LabContext _ctx;
    private readonly Func<string> _client;
    private readonly object _gate = new();
    private readonly List<LabJob> _jobs = [];
    private readonly Queue<LabJob> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private PlaybackSession? _session;
    /// <summary>Written under the queue lock; read without it by the session's change handler, which must never take the queue lock (lock order).</summary>
    private volatile LabJob? _running;

    public JobQueue(LabContext ctx, Func<string> clientName)
    {
        _ctx = ctx;
        _client = clientName;
        _worker = Task.Run(WorkAsync);
    }

    /// <summary>How often a queued job checks whether the engine became free.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public LabJob Enqueue(string kind, string what, string? estimate, Func<PlaybackSession, Task<JobOutcome>> work)
    {
        var job = new LabJob("job-" + Guid.NewGuid().ToString("N")[..8], kind, what, estimate, work);
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) throw new InvalidOperationException("The server is shutting down.");
            _jobs.Add(job);
            _queue.Enqueue(job);
            var finished = _jobs.Where(j => j.IsFinished).ToList();
            foreach (var old in finished.Take(Math.Max(0, finished.Count - KeepFinishedJobs))) _jobs.Remove(old);
        }

        _signal.Release();
        return job;
    }

    public LabJob? Get(string id)
    {
        lock (_gate) return _jobs.FirstOrDefault(j => j.Id == id);
    }

    public IReadOnlyList<LabJob> Recent(int count)
    {
        lock (_gate) return _jobs.AsEnumerable().Reverse().Take(count).ToList();
    }

    /// <summary>How many jobs run or wait ahead of <paramref name="job"/>.</summary>
    public int Ahead(LabJob job)
    {
        lock (_gate)
        {
            var ahead = _running is not null && _running != job ? 1 : 0;
            foreach (var j in _queue)
            {
                if (j == job) return ahead;
                if (j.Status == JobStatus.Queued) ahead++;
            }

            return 0;
        }
    }

    /// <summary>Cancels a job: a queued one at once; a running one through the session's Cancel (refused when its result was already saved).</summary>
    public (bool Accepted, string Message) Cancel(string id)
    {
        var job = Get(id);
        if (job is null) return (false, $"No job '{id}'.");
        if (job.IsFinished) return (false, $"The job already ended ({job.Status.ToString().ToLowerInvariant()}).");
        if (job.Status == JobStatus.Queued && job.Finish(JobStatus.Cancelled, null, null)) return (true, "Cancelled before it started; nothing was launched.");
        lock (_gate)
        {
            if (_running == job && _session is { } s)
            {
                const string stopping = "Stopping: the engine is being closed and its sandbox deleted. Trials already finished stay recorded.";
                const string tooLate = "Too late to cancel: the result was already saved.";
                if (s.Cancel()) return (true, stopping);
                if (s.AttemptId == job.SessionAttempt)
                {
                    // Handed over but not yet taken by the session: cancel it the moment the session goes busy (before any engine launch).
                    job.RequestPendingCancel();
                    // The session may have gone busy (and raised its change) between the two checks: apply it now if so.
                    if (s.IsBusy && job.TakePendingCancel() && !s.Cancel()) return (false, tooLate);
                    return (true, "Stopping before the engine starts.");
                }

                // The session took the job meanwhile: it is either busy now or already done.
                return s.IsBusy && s.Cancel() ? (true, stopping) : (false, tooLate);
            }
        }

        return (false, "The job is finishing; it can no longer be cancelled.");
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            try { await _signal.WaitAsync(_stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            LabJob? job;
            lock (_gate) job = _queue.Count > 0 ? _queue.Dequeue() : null;
            if (job is null || job.IsFinished) continue;
            await RunAsync(job).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(LabJob job)
    {
        RuntimeLease? lease = null;
        try
        {
            if (_ctx.Broker is { } broker)
            {
                var since = DateTime.UtcNow;
                while (true)
                {
                    if (job.IsFinished || _stop.IsCancellationRequested) return;   // cancelled while waiting
                    lease = broker.TryAcquire(RuntimeHolder.Now(_client(), job.What), out var busy);
                    if (lease is not null) break;
                    job.Waiting((busy ?? new RuntimeHolder("another IKEMEN Lab process", "a playback", 0, DateTime.UtcNow)).Describe());
                    if (DateTime.UtcNow - since > _ctx.Options.QueueTimeout)
                    {
                        job.Finish(JobStatus.Failed, null, new RuntimeBusyException(busy).Message + $" This job waited {_ctx.Options.QueueTimeout.TotalMinutes:0} minutes and gave up.");
                        return;
                    }

                    try { await Task.Delay(PollInterval, _stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }

            PlaybackSession session;
            lock (_gate)
            {
                if (!job.Start()) return;
                _running = job;
                session = _session ??= NewSession();
                job.SessionAttempt = session.AttemptId;
            }

            JobOutcome outcome;
            try { outcome = await job.Work(session).ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                outcome = new JobOutcome(JobStatus.Failed, null, ex.Message);
            }

            job.Finish(outcome.Status, outcome.Result, outcome.Error);
        }
        finally
        {
            lock (_gate) if (_running == job) _running = null;
            lease?.Dispose();
            if (!job.IsFinished) job.Finish(JobStatus.Cancelled, null, "The server stopped before the job could run.");
        }
    }

    private PlaybackSession NewSession()
    {
        var session = new PlaybackSession(_ctx.Runs) { ClientName = _client() };
        session.Changed += () =>
        {
            var job = _running;
            if (job is null) return;
            if (session.IsBusy && job.TakePendingCancel()) session.Cancel();
            job.Progress(session.Phase);
        };
        return session;
    }

    /// <summary>Stops taking jobs, cancels queued and running ones, and waits for the engine and its sandbox to be cleaned up.</summary>
    public async Task<ShutdownResult> ShutdownAsync(TimeSpan timeout)
    {
        List<LabJob> pending;
        PlaybackSession? session;
        lock (_gate)
        {
            _stop.Cancel();
            pending = _jobs.Where(j => !j.IsFinished).ToList();
            session = _session;
        }

        foreach (var j in pending.Where(j => j.Status == JobStatus.Queued)) j.Finish(JobStatus.Cancelled, null, "The server stopped before the job could run.");
        var result = session is null ? new ShutdownResult(true, null, []) : await session.ShutdownAsync(timeout).ConfigureAwait(false);
        await Task.WhenAny(_worker, Task.Delay(timeout)).ConfigureAwait(false);
        return result;
    }
}
