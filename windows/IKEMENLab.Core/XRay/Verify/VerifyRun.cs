using System.Diagnostics;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Runtime;

namespace IKEMENLab.Core.XRay.Verify;

public sealed record EngineRunResult(int? ExitCode, bool TimedOut, string? Error);

/// <summary>Runs the engine in a prepared sandbox. A seam so the orchestration is testable without an engine.</summary>
public interface IEngineRunner
{
    EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout);
}

/// <summary>A runner that can also be stopped from outside (the app's Cancel button): the engine is killed and the call throws <see cref="OperationCanceledException"/>.</summary>
public interface ICancellableEngineRunner : IEngineRunner
{
    EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout, CancellationToken cancel);
}

/// <summary>Launches the sandbox's own copy of the engine, working directory = the sandbox, killed when the timeout passes or the run is cancelled.</summary>
public sealed class ProcessEngineRunner : ICancellableEngineRunner
{
    /// <summary>
    /// How the engine is started. It never shares the caller's standard streams: under the MCP server they are the protocol pipes (an engine reading stdin
    /// would swallow a client's request, anything it printed would corrupt the stream), and under the CLI stdout is the JSON result. It gets its own pipes.
    /// </summary>
    public static ProcessStartInfo StartInfo(string exePath, string workingDirectory, IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        return psi;
    }

    public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout) => Run(sandbox, timeout, CancellationToken.None);

    public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (!File.Exists(sandbox.ExePath)) return new EngineRunResult(null, false, $"The sandbox has no engine executable at {sandbox.ExePath}.");
        var psi = StartInfo(sandbox.ExePath, sandbox.Root, sandbox.Arguments);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new EngineRunResult(null, false, "The engine process did not start.");
            // Drain (and drop) whatever the engine prints so it can never block on a full pipe. Its stdin stays open and empty.
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, _) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            // Poll so a Cancel is honoured within a fraction of a second without a second thread owning the process.
            var deadline = DateTime.UtcNow + timeout;
            while (!p.WaitForExit(200))
            {
                var cancelled = cancel.IsCancellationRequested;
                if (!cancelled && DateTime.UtcNow < deadline) continue;
                try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* already gone */ }
                p.WaitForExit(2000);
                if (cancelled) throw new OperationCanceledException(cancel);
                return new EngineRunResult(null, true, null);
            }

            return new EngineRunResult(p.ExitCode, false, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new EngineRunResult(null, false, ex.Message);
        }
    }
}

/// <summary>What a caller can still read before the sandbox is deleted.</summary>
public sealed record VerifyArtifacts(InputPlan Plan, string SandboxRoot, string TracePath, string? RawTrace);

public sealed record VerifyRunRequest(
    SandboxRequest Sandbox,
    TimeSpan Timeout,
    bool KeepSandbox = false)
{
    /// <summary>Phase names as the run advances: planning, preparing, running, judging. For a progress display only.</summary>
    public Action<string>? Progress { get; init; }
    /// <summary>Called with the plan and the raw trace after the engine exits and before the sandbox is deleted.</summary>
    public Action<VerifyArtifacts>? Collect { get; init; }
    public CancellationToken Cancel { get; init; }
    /// <summary>Called (path, reason) when the sandbox could not be deleted after the run.</summary>
    public Action<string, string?>? CleanupFailed { get; init; }
}

public sealed record VerifyRunResult(VerificationReport Report, string? SandboxPath, string? TracePath, EngineRunResult Engine)
{
    public InputPlan? Plan { get; init; }
    public TraceLog? Log { get; init; }
}

/// <summary>One engine run of a prepared plan, before anything is judged: the trace as read, and the run's own notes (sandbox, trace hash, engine problems).</summary>
public sealed record EngineRun(InputPlan Plan, TraceLog Log, EngineRunResult Engine, IReadOnlyList<string> Notes, string? SandboxPath, string? TracePath);

/// <summary>Route → plan → disposable sandbox → engine → trace → verdict. The one path that can produce a RuntimeVerified route.</summary>
public static class VerifyRunner
{
    public static VerifyRunResult Run(CandidateGraph graph, ComboRoute route, VerifyRunRequest request, IEngineRunner runner, PlanOptions? planOptions = null)
    {
        request.Progress?.Invoke("planning");
        request.Cancel.ThrowIfCancellationRequested();
        var planned = InputPlanner.Plan(graph, route, planOptions);
        if (planned.Plan is null) throw new InvalidOperationException(planned.RefusedReason);

        var run = Execute(planned.Plan, request, runner);
        var report = RouteVerifier.Verify(planned.Plan, run.Log);
        report = report with { Notes = report.Notes.Concat(run.Notes).ToList() };
        request.Cancel.ThrowIfCancellationRequested();
        return new VerifyRunResult(report, run.SandboxPath, run.TracePath, run.Engine) { Plan = planned.Plan, Log = run.Log };
    }

    /// <summary>
    /// Plays an already-built plan in a disposable sandbox and reads the trace back; it judges nothing. <see cref="Run"/> judges the result with
    /// <see cref="RouteVerifier"/>; a state preview reads it with its own non-verdict reader. The sandbox is removed (and a failed removal reported)
    /// unless the request keeps it.
    /// </summary>
    public static EngineRun Execute(InputPlan plan, VerifyRunRequest request, IEngineRunner runner)
    {
        request.Progress?.Invoke("preparing");
        var sandboxRequest = request.Sandbox with { Plan = plan, Cancel = request.Cancel, CleanupFailed = request.Sandbox.CleanupFailed ?? request.CleanupFailed };
        var sandbox = RuntimeSandbox.Create(sandboxRequest);
        var keep = request.KeepSandbox;
        try
        {
            request.Cancel.ThrowIfCancellationRequested();
            request.Progress?.Invoke("running");
            var engine = runner is ICancellableEngineRunner cancellable
                ? cancellable.Run(sandbox, request.Timeout, request.Cancel)
                : runner.Run(sandbox, request.Timeout);
            // Commit boundary: a cancel requested before the result exists means there is no result. Once the verdict is
            // published (the caller persists it) a later cancel no longer takes it back.
            request.Cancel.ThrowIfCancellationRequested();
            request.Progress?.Invoke("judging");
            request.Cancel.ThrowIfCancellationRequested();
            var raw = File.Exists(sandbox.TracePath) ? File.ReadAllText(sandbox.TracePath) : null;
            request.Collect?.Invoke(new VerifyArtifacts(plan, sandbox.Root, sandbox.TracePath, raw));
            var log = raw is null ? new TraceLog { Events = [], Issues = [] } : TraceReader.ReadFile(sandbox.TracePath);
            var notes = new List<string>(sandbox.Notes);
            if (File.Exists(sandbox.TracePath)) notes.Add("Trace sha256: " + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sandbox.TracePath))));
            if (engine.Error is not null) notes.Add("Engine: " + engine.Error);
            if (engine.TimedOut) notes.Add("The engine was stopped after the timeout.");
            request.Cancel.ThrowIfCancellationRequested();
            return new EngineRun(plan, log, engine, notes, keep ? sandbox.Root : null, keep ? sandbox.TracePath : null);
        }
        finally
        {
            if (!keep) RuntimeSandbox.RemoveReporting(sandbox.Root, sandboxRequest);
        }
    }
}
