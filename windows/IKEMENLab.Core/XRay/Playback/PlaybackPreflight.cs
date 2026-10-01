using System.Text;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.Settings;

namespace IKEMENLab.Core.XRay.Playback;

/// <summary>What a playback would use, and what is still missing. Nothing is launched to find this out.</summary>
public sealed record PlaybackSetup(
    string Root, string? Dummy, string? DummyDef, string? Stage, string? EnginePath, string? EngineDlls, bool? EngineHasVirtualInput,
    IReadOnlyList<string> Issues, IReadOnlyList<string> Notes)
{
    public bool Ready => Issues.Count == 0 && Dummy is not null && DummyDef is not null && Stage is not null && EnginePath is not null;
}

/// <summary>
/// Resolves the dummy, stage and sandbox engine for a playback, from the user's choices (<see cref="AppSettings"/>) or sensible defaults,
/// and says what is missing. The engine is the one thing that cannot be defaulted: the production engine has no way to receive scripted input.
/// </summary>
public static class PlaybackPreflight
{
    /// <summary>The string the sandbox engine's hook is registered under; a build without it ends Inconclusive (InputInjectionUnavailable).</summary>
    public const string HookName = "__xraySetVirtualInput";

    public static PlaybackSetup Check(string root, string subjectFolder, AppSettings settings)
    {
        var issues = new List<string>();
        var notes = new List<string>();
        var chars = Path.Combine(root, "chars");

        // ---- engine
        var engine = Clean(settings.XRayEnginePath);
        bool? hasHook = null;
        if (engine is null)
            issues.Add("Playback needs an X-Ray engine build. The installed engine cannot receive scripted input, so set the engine path in Playback setup.");
        else if (!File.Exists(engine))
        {
            issues.Add($"The X-Ray engine was not found at {engine}.");
            engine = null;
        }
        else
        {
            hasHook = ContainsAscii(engine, HookName);
            if (hasHook == false)
                issues.Add("This engine build does not appear to contain the X-Ray virtual-input hook, so the combo would not play. Choose the X-Ray sandbox build.");
        }

        var dlls = Clean(settings.XRayEngineDlls);
        if (dlls is not null && !Directory.Exists(dlls))
        {
            issues.Add($"The engine runtime DLL folder was not found: {dlls}.");
            dlls = null;
        }

        // ---- dummy
        string? dummy = null, dummyDef = null;
        var wanted = Clean(settings.XRayDummy);
        if (wanted is not null)
        {
            dummyDef = FindDef(chars, wanted);
            if (dummyDef is null) issues.Add($"The dummy character '{wanted}' was not found under chars/ (or has no DEF).");
            else dummy = wanted;
        }
        else if (Directory.Exists(chars))
        {
            foreach (var dir in Directory.EnumerateDirectories(chars).OrderBy(d => Path.GetFileName(d).Equals("kfm", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                         .ThenBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (name.Equals(subjectFolder, StringComparison.OrdinalIgnoreCase)) continue;
                var def = FindDef(chars, name);
                if (def is null) continue;
                dummy = name;
                dummyDef = def;
                notes.Add($"Dummy defaults to '{name}'.");
                break;
            }

            if (dummy is null) issues.Add("No other character with a DEF was found to act as the dummy; install one or choose it in Playback setup.");
        }
        else
        {
            issues.Add("The install has no chars/ folder.");
        }

        // ---- stage
        string? stage = null;
        var wantedStage = Clean(settings.XRayStage)?.Replace('\\', '/');
        if (wantedStage is not null)
        {
            if (File.Exists(Path.Combine(root, wantedStage.Replace('/', Path.DirectorySeparatorChar)))) stage = wantedStage;
            else issues.Add($"The stage '{wantedStage}' was not found in the install.");
        }
        else
        {
            stage = FirstStage(root);
            if (stage is null) issues.Add("No stage was found in the install to play on.");
            else notes.Add($"Stage defaults to '{stage}'.");
        }

        return new PlaybackSetup(root, dummy, dummyDef, stage, engine, dlls, hasHook, issues, notes);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>The DEF of a character folder relative to chars/ ("kfm/kfm.def"), or null.</summary>
    private static string? FindDef(string chars, string folder)
    {
        var dir = Path.Combine(chars, folder);
        if (!Directory.Exists(dir)) return null;
        var named = Path.Combine(dir, folder + ".def");
        if (File.Exists(named)) return folder + "/" + folder + ".def";
        foreach (var def in Directory.EnumerateFiles(dir, "*.def").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (DefFileReader.ReadFileContent(def) is { } text && text.Contains("[Files]", StringComparison.OrdinalIgnoreCase))
                    return folder + "/" + Path.GetFileName(def);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* try the next */ }
        }

        return null;
    }

    private static string? FirstStage(string root)
    {
        var select = Path.Combine(root, "data", "select.def");
        if (File.Exists(select))
        {
            try
            {
                var inSection = false;
                foreach (var raw in File.ReadAllLines(select))
                {
                    var line = raw.Trim();
                    if (line.StartsWith('[')) { inSection = line.Equals("[ExtraStages]", StringComparison.OrdinalIgnoreCase); continue; }
                    if (!inSection || line.Length == 0 || line.StartsWith(';')) continue;
                    var path = line.Split(',')[0].Trim().Replace('\\', '/');
                    if (File.Exists(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)))) return path;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* fall through to a directory listing */ }
        }

        var stages = Path.Combine(root, "stages");
        if (!Directory.Exists(stages)) return null;
        var first = Directory.EnumerateFiles(stages, "*.def", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        return first is null ? null : "stages/" + Path.GetFileName(first);
    }

    /// <summary>Whether a file contains an ASCII string. A heuristic for "this engine registers that Lua function"; says nothing about whether it works.</summary>
    public static bool? ContainsAscii(string path, string needle)
    {
        try
        {
            var pattern = Encoding.ASCII.GetBytes(needle);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[1 << 20];
            var carry = pattern.Length - 1;
            var filled = 0;
            while (true)
            {
                var read = fs.Read(buffer, filled, buffer.Length - filled);
                if (read <= 0) return false;
                filled += read;
                if (buffer.AsSpan(0, filled).IndexOf(pattern) >= 0) return true;
                if (filled > carry)
                {
                    Buffer.BlockCopy(buffer, filled - carry, buffer, 0, carry);
                    filled = carry;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
