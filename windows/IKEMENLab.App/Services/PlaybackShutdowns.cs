using System.Collections.Concurrent;
using IKEMENLab.Core.XRay.Playback;

namespace IKEMENLab.App.Services;

/// <summary>
/// Remembers playback shutdowns started by closing X-Ray windows so the application can wait (bounded) for them before the process exits —
/// otherwise exiting right after a close could leave a sandbox engine running. Closing a window itself never waits.
/// </summary>
public static class PlaybackShutdowns
{
    private static readonly ConcurrentBag<Task<ShutdownResult>> Pending = new();

    public static void Track(Task<ShutdownResult> shutdown) => Pending.Add(shutdown);

    /// <summary>Waits for every tracked shutdown. Safe on the UI thread: these tasks never need the UI thread to finish.</summary>
    public static bool WaitAll(TimeSpan timeout)
    {
        var tasks = Pending.ToArray();
        try { return Task.WaitAll(tasks.Cast<Task>().ToArray(), timeout); }
        catch (AggregateException) { return true; }   // a faulted shutdown is finished; its problem was already reported
    }
}
