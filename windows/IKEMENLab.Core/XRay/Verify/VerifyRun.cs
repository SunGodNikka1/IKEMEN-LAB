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
    public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout) => Run(sandbox, timeout, CancellationToken.None);

    public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (!File.Exists(sandbox.ExePath)) return new EngineRunResult(null, false, $"The sandbox has no engine executable at {sandbox.ExePath}.");
        var psi = new ProcessStartInfo(sandbox.ExePath) { WorkingDirectory = sandbox.Root, UseShellExecute = false };
        foreach (var a in sandbox.Arguments) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new EngineRunResult(null, false, "The engine process did not start.");
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

/// <summary>Route → plan → disposable sandbox → engine → trace → verdict. The one path that can produce a RuntimeVerified route.</summary>
public static class VerifyRunner
{
    public static VerifyRunResult Run(CandidateGraph graph, ComboRoute route, VerifyRunRequest request, IEngineRunner runner, PlanOptions? planOptions = null)
    {
        request.Progress?.Invoke("planning");
        request.Cancel.ThrowIfCancellationRequested();
        var planned = InputPlanner.Plan(graph, route, planOptions);
        if (planned.Plan is null) throw new InvalidOperationException(planned.RefusedReason);

        request.Progress?.Invoke("preparing");
        var sandboxRequest = request.Sandbox with { Plan = planned.Plan, Cancel = request.Cancel, CleanupFailed = request.Sandbox.CleanupFailed ?? request.CleanupFailed };
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
            request.Collect?.Invoke(new VerifyArtifacts(planned.Plan, sandbox.Root, sandbox.TracePath, raw));
            var log = raw is null ? new TraceLog { Events = [], Issues = [] } : TraceReader.ReadFile(sandbox.TracePath);
            var report = RouteVerifier.Verify(planned.Plan, log);
            var notes = report.Notes.ToList();
            notes.AddRange(sandbox.Notes);
            if (File.Exists(sandbox.TracePath)) notes.Add("Trace sha256: " + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sandbox.TracePath))));
            if (engine.Error is not null) notes.Add("Engine: " + engine.Error);
            if (engine.TimedOut) notes.Add("The engine was stopped after the timeout.");
            report = report with { Notes = notes };
            request.Cancel.ThrowIfCancellationRequested();
            return new VerifyRunResult(report, keep ? sandbox.Root : null, keep ? sandbox.TracePath : null, engine) { Plan = planned.Plan, Log = log };
        }
        finally
        {
            if (!keep) RuntimeSandbox.RemoveReporting(sandbox.Root, sandboxRequest);
        }
    }
}
