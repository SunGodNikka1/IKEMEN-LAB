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

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for every tracked shutdown. This is bounded best-effort cleanup at process exit, not a guarantee:
    /// a shutdown that has not finished when the wait ends (each may itself take up to its own timeout) is reported in the result, and an engine
    /// it was still stopping may outlive the process. Safe on the UI thread: these tasks never need the UI thread to finish.
    /// </summary>
    public static ExitReport WaitAll(TimeSpan timeout)
    {
        var tasks = Pending.ToArray();
        var unfinished = 0;
        var problems = new List<string>();
        var deadline = DateTime.UtcNow + timeout;
        foreach (var t in tasks)
        {
            var left = deadline - DateTime.UtcNow;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            try
            {
                if (!t.Wait(left)) { unfinished++; continue; }
                if (!t.Result.Clean) problems.Add(t.Result.Problem ?? string.Join("; ", t.Result.LeftoverSandboxes));
            }
            catch (AggregateException ex) { problems.Add(ex.GetBaseException().Message); }
        }

        return new ExitReport(unfinished, problems);
    }
}

/// <summary>What the exit-time wait observed: how many playback shutdowns had not finished, and the problems finished ones reported.</summary>
public sealed record ExitReport(int Unfinished, IReadOnlyList<string> Problems)
{
    public bool Complete => Unfinished == 0;
    public string? Message => Complete ? null
        : $"{Unfinished} combo playback cleanup(s) had not finished when IKEMEN Lab exited; an engine process or a sandbox folder (under runtime-sandboxes, safe to delete) may remain.";
}
