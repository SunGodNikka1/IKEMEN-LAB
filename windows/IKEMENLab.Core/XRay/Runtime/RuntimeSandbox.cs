using System.Reflection;
using System.Text;
using IKEMENLab.Core.Settings;

namespace IKEMENLab.Core.XRay.Runtime;

public enum ProbeInjection
{
    /// <summary>Only external/mods/xray_probe.lua (loaded by the engine's mods loader).</summary>
    Mods,
    /// <summary>Also dofile() it from the top of external/script/main.lua in the sandbox copy, for runs that skip the mods loader.</summary>
    ModsAndMainLua
}

public sealed record SandboxRequest(
    string SourceRoot,
    string SubjectFolder,
    string SubjectDef,
    string DummyFolder,
    string DummyDef,
    string StageDef,
    int MaxFrames = 900,
    string? BaseDirectory = null,
    ProbeInjection Injection = ProbeInjection.ModsAndMainLua,
    /// <summary>Milestone 3: play this input plan (verify mode). Null = observe only, both sides on the engine's own AI.</summary>
    Verify.InputPlan? Plan = null,
    /// <summary>Verify mode: an engine adapter (Lua) that defines __ikemenlab_xray_inject. Null installs the bundled adapter for __xraySetVirtualInput.</summary>
    string? AdapterPath = null,
    /// <summary>Use this engine binary in the sandbox instead of the one in the install (X-Ray sandbox build).</summary>
    string? EngineExePath = null,
    /// <summary>Directory of runtime DLLs the sandbox engine needs; copied next to it (a self-built Go engine).</summary>
    string? EngineRuntimeDlls = null)
{
    /// <summary>Ticks the engine keeps running after the plan ends before it exits (60 ≈ 1 s). A watched playback lingers on the result.</summary>
    public int? LingerFrames { get; init; }
    /// <summary>Checked between the copy steps so closing the app during preparation stops promptly; the half-built sandbox is deleted.</summary>
    public CancellationToken Cancel { get; init; }
    /// <summary>Called (path, reason) when a sandbox could not be deleted — after a failed or cancelled preparation as well as after a run.</summary>
    public Action<string, string?>? CleanupFailed { get; init; }
    /// <summary>Deletes a sandbox folder, returning (false, reason) on failure. Null = <see cref="RuntimeSandbox.Delete"/>. A seam so cleanup failure can be injected deterministically.</summary>
    public Func<string, (bool Ok, string? Why)>? Deleter { get; init; }
}

public static class RuntimeProbe
{
    /// <summary>The Lua probe shipped inside IKEMENLab.Core.</summary>
    public static string Source => Resource("xray_probe.lua");
    /// <summary>The plan executor (milestone 3).</summary>
    public static string Driver => Resource("xray_driver.lua");
    /// <summary>The bundled adapter: injects when the sandbox engine exposes __xraySetVirtualInput.</summary>
    public static string AdapterTemplate => Resource("xray_inject.lua");

    private static string Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                      ?? throw new InvalidOperationException($"The embedded {name} is missing.");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }
}

/// <summary>
/// A disposable copy of an IKEMEN install for running the probe: the engine, its data, ONE subject character, ONE dummy and ONE
/// stage. Nothing is ever written to the source install or its characters; the sandbox is refused if it would live inside the source.
/// </summary>
public sealed class RuntimeSandbox : IDisposable
{
    private static readonly string[] CopiedDirectories = ["data", "external", "font", "sound", "script", "lib"];

    private RuntimeSandbox(string root) => Root = root;

    public string Root { get; }
    public string TracePath => Path.Combine(Root, "xray_trace.jsonl");
    public string ExePath => Path.Combine(Root, Services.IkemenInstallationValidator.ExeFileName);
    public IReadOnlyList<string> Arguments { get; private set; } = [];
    public IReadOnlyList<string> Notes { get; private set; } = [];

    public static RuntimeSandbox Create(SandboxRequest request)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.SourceRoot));
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);

        var baseDir = Path.GetFullPath(request.BaseDirectory ?? Path.Combine(AppDataPaths.GetAppDataDirectory(), "runtime-sandboxes"));
        var root = Path.Combine(baseDir, Guid.NewGuid().ToString("N"));
        if (IsInside(source, root))
            throw new InvalidOperationException("The runtime sandbox must not be created inside the IKEMEN install it copies.");

        if (request.EngineExePath is { } requestedEngine && !File.Exists(requestedEngine))
            throw new FileNotFoundException("The explicitly requested sandbox engine does not exist; refusing to substitute the install engine.", requestedEngine);
        if (request.EngineRuntimeDlls is { } requestedDlls && !Directory.Exists(requestedDlls))
            throw new DirectoryNotFoundException(requestedDlls);
        var notes = new List<string>();
        var sandbox = new RuntimeSandbox(root);
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, MarkerFileName), "Disposable IKEMEN Lab runtime sandbox. Safe to delete.\n");

            // Engine files next to the exe (exe, dlls, ini…). save/ is recreated below; logs and backups are skipped.
            foreach (var file in Directory.EnumerateFiles(source))
            {
                var ext = Path.GetExtension(file);
                if (ext.Equals(".log", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bak", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(root, Path.GetFileName(file)), overwrite: true);
            }

            // A caller may run the sandbox on a different engine binary — the X-Ray verifier does, because it
            // needs the virtual-input override, which only the sandbox build carries. The production install is
            // never written to: only the copy inside this disposable sandbox is replaced, and the substitution
            // is recorded in the notes so a report can never mistake the two apart.
            if (request.EngineExePath is { } engine && File.Exists(engine))
            {
                // Overwrite the sandbox's own copy under the name the sandbox actually launches. Only this
                // disposable copy changes; the production install is never written to.
                var sandboxExe = Path.Combine(root, Services.IkemenInstallationValidator.ExeFileName);
                File.Copy(engine, sandboxExe, overwrite: true);

                // A self-built engine links against its own toolchain's SDL/FFmpeg DLLs, which the install does
                // not ship. Without them it exits with STATUS_DLL_NOT_FOUND before anything is traced.
                if (request.EngineRuntimeDlls is { } dllDir && Directory.Exists(dllDir))
                {
                    var dlls = 0;
                    foreach (var dll in Directory.EnumerateFiles(dllDir, "*.dll"))
                    {
                        File.Copy(dll, Path.Combine(root, Path.GetFileName(dll)), overwrite: true);
                        dlls++;
                    }
                    notes.Add($"Copied {dlls} engine runtime DLL(s) from {dllDir} into the sandbox.");
                }

                notes.Add($"Engine binary overridden for this sandbox: {Path.GetFullPath(engine)} " +
                          $"(sha256 {Sha256(engine)}) replacing the install's copy of {Path.GetFileName(sandboxExe)}. " +
                          "The production install is untouched and is NOT the binary that ran.");
            }

            if (!File.Exists(sandbox.ExePath)) throw new FileNotFoundException("The sandbox engine executable is missing.", sandbox.ExePath);
            notes.Add($"Actual sandbox executable: {sandbox.ExePath} (sha256 {Sha256(sandbox.ExePath)}); source: {Path.GetFullPath(request.EngineExePath ?? Path.Combine(source, Services.IkemenInstallationValidator.ExeFileName))}.");
            // Do not append to a trace copied from a previous run in the install root.
            if (File.Exists(sandbox.TracePath)) File.Delete(sandbox.TracePath);

            foreach (var dir in CopiedDirectories)
                CopyDirectory(Path.Combine(source, dir), Path.Combine(root, dir), request.Cancel);

            CopyDirectory(Path.Combine(source, "chars", request.SubjectFolder), Path.Combine(root, "chars", request.SubjectFolder), request.Cancel);
            if (!request.DummyFolder.Equals(request.SubjectFolder, StringComparison.OrdinalIgnoreCase))
                CopyDirectory(Path.Combine(source, "chars", request.DummyFolder), Path.Combine(root, "chars", request.DummyFolder), request.Cancel);
            request.Cancel.ThrowIfCancellationRequested();
            CopyStage(source, root, request.StageDef, notes);
            request.Cancel.ThrowIfCancellationRequested();

            var saveSource = Path.Combine(source, "save");
            Directory.CreateDirectory(Path.Combine(root, "save"));
            foreach (var name in new[] { "config.ini", "config.json" })
                if (File.Exists(Path.Combine(saveSource, name)))
                    File.Copy(Path.Combine(saveSource, name), Path.Combine(root, "save", name), overwrite: true);

            WriteSelectDef(root, request, notes);
            InstallProbe(root, request, notes);
            sandbox.Arguments = BuildArguments(request);
        }
        catch
        {
            // A failed or cancelled preparation must not silently leave a sandbox: the deletion result is reported like any other cleanup.
            RemoveReporting(root, request);
            throw;
        }

        sandbox.Notes = notes;
        return sandbox;
    }

    /// <summary>Deletes the sandbox at <paramref name="root"/> through the request's deleter and reports a failure to its <see cref="SandboxRequest.CleanupFailed"/> callback. True when nothing is left.</summary>
    public static bool RemoveReporting(string root, SandboxRequest request)
    {
        if (!Directory.Exists(root)) return true;
        var (ok, why) = request.Deleter is { } d ? d(root) : (Delete(root), LastFailure);
        if (!ok) request.CleanupFailed?.Invoke(root, why ?? "the folder could not be deleted");
        return ok;
    }

    public const string MarkerFileName = ".ikemenlab-runtime-sandbox";

    public void Dispose() => Delete(Root);

    /// <summary>Deletes a sandbox folder. Refuses anything that lacks the marker this class writes, so it can never remove a real install.</summary>
    public static bool Delete(string path)
    {
        var marker = Path.Combine(path, MarkerFileName);
        LastFailure = null;
        if (!Directory.Exists(path) || !File.Exists(marker))
        {
            LastFailure = !Directory.Exists(path) ? "folder does not exist" : $"no {MarkerFileName} marker";
            return false;
        }

        // The engine is usually still closing its own handles when this is first called, and a single
        // Directory.Delete can then remove part of the tree — including the marker — before failing. The
        // folder would be left in place and, without its marker, refuse every later attempt. So keep the
        // marker until the very end, and retry: the sandbox is disposable, waiting costs nothing.
        string? lastError = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (attempt > 0) Thread.Sleep(400);
                if (Directory.Exists(path))
                {
                    // Copied media keeps its read-only attribute, and Directory.Delete(recursive) refuses
                    // to remove a read-only file — it throws UnauthorizedAccessException and leaves the
                    // rest of the tree behind. Clear the attributes first, the way a user would.
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        var info = new FileInfo(file);
                        if (info.IsReadOnly)
                        {
                            info.IsReadOnly = false;
                            info.Attributes &= ~FileAttributes.ReadOnly;
                        }
                    }

                    Directory.Delete(path, recursive: true);
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = $"{ex.GetType().Name}: {ex.Message}";
                if (!Directory.Exists(path)) return true;
            }
        }

        // Out of retries. Restore the marker so the folder stays recognisable and can be cleaned later.
        try { File.WriteAllText(marker, "Disposable IKEMEN Lab runtime sandbox. Safe to delete.\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        LastFailure = lastError;
        return false;
    }

    /// <summary>Why the last <see cref="Delete"/> failed, for the CLI to print. Null when it did not fail.</summary>
    public static string? LastFailure { get; private set; }

    private static string Sha256(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    // ------------------------------------------------------------------ pieces

    private static IReadOnlyList<string> BuildArguments(SandboxRequest r) =>
        r.Plan is null
            ? ["-p1", r.SubjectFolder, "-p2", r.DummyFolder, "-s", r.StageDef.Replace('\\', '/'), "-p1.ai", "1", "-p2.ai", "1", "-nosound"]
            // Verify mode: nobody is on the engine's AI. P1 is fed by the driver, P2 is a dummy that receives no input.
            : ["-p1", r.SubjectFolder, "-p2", r.DummyFolder, "-s", r.StageDef.Replace('\\', '/'), "-nosound"];

    private static void InstallProbe(string root, SandboxRequest request, List<string> notes)
    {
        var mods = Path.Combine(root, "external", "mods");
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, "xray_probe.lua"), RuntimeProbe.Source, new UTF8Encoding(false));

        var trace = Path.Combine(root, "xray_trace.jsonl").Replace('\\', '/');
        var planConfig = "";
        if (request.Plan is not null)
        {
            File.WriteAllText(Path.Combine(mods, "xray_plan.lua"), Verify.InputPlanner.ToLua(request.Plan), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(mods, "xray_driver.lua"), RuntimeProbe.Driver, new UTF8Encoding(false));
            var adapter = request.AdapterPath is { } a ? File.ReadAllText(a) : RuntimeProbe.AdapterTemplate;
            File.WriteAllText(Path.Combine(mods, "xray_inject.lua"), adapter, new UTF8Encoding(false));
            if (request.AdapterPath is null)
                notes.Add("No input adapter was supplied: the bundled template still ships xray_inject.lua, so injection only " + "fails when the engine in the sandbox lacks __xraySetVirtualInput (an unpatched engine ends Inconclusive / InputInjectionUnavailable).");
            planConfig = ", plan = \"external/mods/xray_plan.lua\", driver = \"external/mods/xray_driver.lua\", adapter = \"external/mods/xray_inject.lua\"";
        }

        var linger = request.LingerFrames is { } lf && lf > 0 ? $", lingerFrames = {lf}" : string.Empty;
        static string LuaPath(string p) => p.Replace('\\', '/').Replace("\"", "\\\"");
        var actualEngine = Path.Combine(root, Services.IkemenInstallationValidator.ExeFileName);
        var engineSource = Path.GetFullPath(request.EngineExePath ?? Path.Combine(request.SourceRoot, Services.IkemenInstallationValidator.ExeFileName));
        var provenance = $", engineSha256 = \"{Sha256(actualEngine)}\", engineExecutable = \"{LuaPath(actualEngine)}\", engineSource = \"{LuaPath(engineSource)}\"";
        File.WriteAllText(Path.Combine(mods, "xray_config.lua"),
            $"return {{ trace = \"{trace}\", maxFrames = {request.MaxFrames}, character = \"{request.SubjectFolder.Replace("\"", "")}\", hooks = {{ \"loop\" }}{planConfig}{provenance}{linger} }}\n",
            new UTF8Encoding(false));

        if (request.Injection != ProbeInjection.ModsAndMainLua) return;
        var main = Path.Combine(root, "external", "script", "main.lua");
        if (!File.Exists(main))
        {
            notes.Add("external/script/main.lua was not found in the sandbox; the probe relies on the mods loader only.");
            return;
        }

        var text = File.ReadAllText(main);
        File.WriteAllText(main, "pcall(dofile, \"external/mods/xray_probe.lua\") -- IKEMEN Lab X-Ray (sandbox copy only)\n" + text, new UTF8Encoding(false));
    }

    private static void CopyStage(string source, string root, string stageDef, List<string> notes)
    {
        var rel = stageDef.Replace('\\', '/').TrimStart('/');
        var def = Path.Combine(source, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(def))
        {
            notes.Add($"Stage '{stageDef}' was not found in the source install.");
            return;
        }

        var stageDir = Path.GetDirectoryName(def)!;
        var stagesRoot = Path.Combine(source, "stages");
        if (!Path.GetFullPath(stageDir).Equals(Path.GetFullPath(stagesRoot), StringComparison.OrdinalIgnoreCase))
        {
            CopyDirectory(stageDir, Path.Combine(root, Path.GetRelativePath(source, stageDir)));
            return;
        }

        // Loose stage: the DEF and every file sharing its base name.
        var baseName = Path.GetFileNameWithoutExtension(def);
        Directory.CreateDirectory(Path.Combine(root, "stages"));
        foreach (var f in Directory.EnumerateFiles(stagesRoot, baseName + ".*"))
            File.Copy(f, Path.Combine(root, "stages", Path.GetFileName(f)), overwrite: true);
    }

    private static void WriteSelectDef(string root, SandboxRequest r, List<string> notes)
    {
        var path = Path.Combine(root, "data", "select.def");
        var original = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        string Entry(string folder, string def) =>
            Path.GetFileNameWithoutExtension(def).Equals(folder, StringComparison.OrdinalIgnoreCase) ? folder : def.Replace('\\', '/');
        var characters = $"{Entry(r.SubjectFolder, r.SubjectDef)}\n{Entry(r.DummyFolder, r.DummyDef)}\n";
        var stages = r.StageDef.Replace('\\', '/') + "\n";

        var text = ReplaceSection(original, "Characters", characters);
        text = ReplaceSection(text, "ExtraStages", stages);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        if (original.Length == 0) notes.Add("The install had no data/select.def; a minimal one was written in the sandbox.");
    }

    /// <summary>Replaces the body of <c>[name]</c> (or appends the section) leaving every other section untouched.</summary>
    public static string ReplaceSection(string text, string name, string body)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        var header = $"[{name}]";
        var start = lines.FindIndex(l => l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0) lines.Add(string.Empty);
            lines.Add(header);
            lines.AddRange(body.TrimEnd('\n').Split('\n'));
            lines.Add(string.Empty);
            return string.Join("\n", lines);
        }

        var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;
        lines.RemoveRange(start + 1, end - start - 1);
        lines.InsertRange(start + 1, body.TrimEnd('\n').Split('\n').Append(string.Empty));
        return string.Join("\n", lines);
    }

    private static void CopyDirectory(string from, string to, CancellationToken cancel = default)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            cancel.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }

    private static bool IsInside(string parent, string child)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(child).StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }
}
