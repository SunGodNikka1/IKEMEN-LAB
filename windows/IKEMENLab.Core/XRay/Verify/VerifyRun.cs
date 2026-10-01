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

/// <summary>Launches the sandbox's own copy of the engine, working directory = the sandbox, killed when the timeout passes.</summary>
public sealed class ProcessEngineRunner : IEngineRunner
{
    public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout)
    {
        if (!File.Exists(sandbox.ExePath)) return new EngineRunResult(null, false, $"The sandbox has no engine executable at {sandbox.ExePath}.");
        var psi = new ProcessStartInfo(sandbox.ExePath) { WorkingDirectory = sandbox.Root, UseShellExecute = false };
        foreach (var a in sandbox.Arguments) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new EngineRunResult(null, false, "The engine process did not start.");
            if (p.WaitForExit(timeout)) return new EngineRunResult(p.ExitCode, false, null);
            try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* already gone */ }
            return new EngineRunResult(null, true, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new EngineRunResult(null, false, ex.Message);
        }
    }
}

public sealed record VerifyRunRequest(
    SandboxRequest Sandbox,
    TimeSpan Timeout,
    bool KeepSandbox = false);

public sealed record VerifyRunResult(VerificationReport Report, string? SandboxPath, string? TracePath, EngineRunResult Engine);

/// <summary>Route → plan → disposable sandbox → engine → trace → verdict. The one path that can produce a RuntimeVerified route.</summary>
public static class VerifyRunner
{
    public static VerifyRunResult Run(CandidateGraph graph, ComboRoute route, VerifyRunRequest request, IEngineRunner runner, PlanOptions? planOptions = null)
    {
        var planned = InputPlanner.Plan(graph, route, planOptions);
        if (planned.Plan is null) throw new InvalidOperationException(planned.RefusedReason);

        var sandboxRequest = request.Sandbox with { Plan = planned.Plan };
        var sandbox = RuntimeSandbox.Create(sandboxRequest);
        var keep = request.KeepSandbox;
        try
        {
            var engine = runner.Run(sandbox, request.Timeout);
            var log = File.Exists(sandbox.TracePath) ? TraceReader.ReadFile(sandbox.TracePath) : new TraceLog { Events = [], Issues = [] };
            var report = RouteVerifier.Verify(planned.Plan, log);
            var notes = report.Notes.ToList();
            notes.AddRange(sandbox.Notes);
            if (File.Exists(sandbox.TracePath)) notes.Add("Trace sha256: " + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sandbox.TracePath))));
            if (engine.Error is not null) notes.Add("Engine: " + engine.Error);
            if (engine.TimedOut) notes.Add("The engine was stopped after the timeout.");
            report = report with { Notes = notes };
            return new VerifyRunResult(report, keep ? sandbox.Root : null, keep ? sandbox.TracePath : null, engine);
        }
        finally
        {
            if (!keep) sandbox.Dispose();
        }
    }
}
