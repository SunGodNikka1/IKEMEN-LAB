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
    ProbeInjection Injection = ProbeInjection.ModsAndMainLua);

public static class RuntimeProbe
{
    /// <summary>The Lua probe shipped inside IKEMENLab.Core.</summary>
    public static string Source
    {
        get
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("xray_probe.lua")
                          ?? throw new InvalidOperationException("The embedded xray_probe.lua is missing.");
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }
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

            foreach (var dir in CopiedDirectories)
                CopyDirectory(Path.Combine(source, dir), Path.Combine(root, dir));

            CopyDirectory(Path.Combine(source, "chars", request.SubjectFolder), Path.Combine(root, "chars", request.SubjectFolder));
            if (!request.DummyFolder.Equals(request.SubjectFolder, StringComparison.OrdinalIgnoreCase))
                CopyDirectory(Path.Combine(source, "chars", request.DummyFolder), Path.Combine(root, "chars", request.DummyFolder));
            CopyStage(source, root, request.StageDef, notes);

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
            sandbox.Dispose();
            throw;
        }

        sandbox.Notes = notes;
        return sandbox;
    }

    public const string MarkerFileName = ".ikemenlab-runtime-sandbox";

    public void Dispose() => Delete(Root);

    /// <summary>Deletes a sandbox folder. Refuses anything that lacks the marker this class writes, so it can never remove a real install.</summary>
    public static bool Delete(string path)
    {
        try
        {
            if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, MarkerFileName))) return false;
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // best effort; the folder is disposable
        }
    }

    // ------------------------------------------------------------------ pieces

    private static IReadOnlyList<string> BuildArguments(SandboxRequest r) =>
    [
        "-p1", r.SubjectFolder, "-p2", r.DummyFolder, "-s", r.StageDef.Replace('\\', '/'),
        "-p1.ai", "1", "-p2.ai", "1", "-nosound"
    ];

    private static void InstallProbe(string root, SandboxRequest request, List<string> notes)
    {
        var mods = Path.Combine(root, "external", "mods");
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, "xray_probe.lua"), RuntimeProbe.Source, new UTF8Encoding(false));

        var trace = Path.Combine(root, "xray_trace.jsonl").Replace('\\', '/');
        File.WriteAllText(Path.Combine(mods, "xray_config.lua"),
            $"return {{ trace = \"{trace}\", maxFrames = {request.MaxFrames}, character = \"{request.SubjectFolder.Replace("\"", "")}\", hooks = {{ \"loop\" }} }}\n",
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

    private static void CopyDirectory(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    private static bool IsInside(string parent, string child)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(child).StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }
}
