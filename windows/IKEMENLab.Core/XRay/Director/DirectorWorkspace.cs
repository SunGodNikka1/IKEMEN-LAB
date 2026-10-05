using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IKEMENLab.Core.XRay.Model;

namespace IKEMENLab.Core.XRay.Director;

/// <summary>The working copy's bookkeeping: where it came from, what the base was, and exactly what was generated into it last.</summary>
public sealed record WorkspaceInfo(
    string CharacterFolder, string DefFile, string InstalledFolder, string BaseHash, DateTime CreatedUtc,
    string? GeneratedBuildHash, string? BehaviorId, string? ModelHash, string? CodeHash, DateTime? GeneratedUtc);

/// <summary>One changed file between the installed character and a build.</summary>
public sealed record FileChange(string Path, string Kind, int AddedLines, string? InstalledHash, string BuildHash);

/// <summary>What a build changes, file by file, with a unified-style diff (insertions only: a build is the base plus generated lines).</summary>
public sealed record BuildDiff(IReadOnlyList<FileChange> Files, string Text)
{
    public string Hash => DirectorHash.Of(Text);
}

/// <summary>
/// The AI Director's working copy for one character, under <c>%LOCALAPPDATA%\IKEMEN Lab\ai-director\&lt;folder&gt;\</c>:
/// <list type="bullet">
/// <item><c>base\&lt;folder&gt;</c> — a complete copy of the installed character folder taken when the workspace was created (never edited);</item>
/// <item><c>working\&lt;folder&gt;</c> — the working copy: base plus the generated code, rebuilt deterministically on every generation;</item>
/// <item><c>workspace.json</c> — the base hash and the exact build last generated.</item>
/// </list>
/// The installed character is only ever read here. A working copy that was changed outside IKEMEN Lab is never overwritten; an installed character that
/// changed after the base was taken blocks deployment until the workspace is re-based.
/// </summary>
public sealed class DirectorWorkspace
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public DirectorWorkspace(string root, string characterFolder)
    {
        Root = Path.GetFullPath(root);
        CharacterFolder = characterFolder;
    }

    public string Root { get; }
    public string CharacterFolder { get; }
    public string BaseFolder => Path.Combine(Root, "base", CharacterFolder);
    public string WorkingFolder => Path.Combine(Root, "working", CharacterFolder);
    private string InfoPath => Path.Combine(Root, "workspace.json");
    public bool Exists => File.Exists(InfoPath) && Directory.Exists(BaseFolder) && Directory.Exists(WorkingFolder);

    public WorkspaceInfo? Info => File.Exists(InfoPath) ? JsonSerializer.Deserialize<WorkspaceInfo>(File.ReadAllText(InfoPath), Json) : null;

    /// <summary>Creates the workspace from the installed character (a complete folder copy), or returns the existing one untouched.</summary>
    public WorkspaceInfo Ensure(string installedFolder, string defFile)
    {
        if (Exists) return Info!;
        if (!Directory.Exists(installedFolder)) throw new DirectoryNotFoundException(installedFolder);
        if (Directory.Exists(BaseFolder) || Directory.Exists(WorkingFolder))
            throw new InvalidOperationException($"{Root} holds a partial workspace (no workspace.json); it was left as it is. Remove it to start again.");
        CopyFolder(installedFolder, BaseFolder);
        CopyFolder(installedFolder, WorkingFolder);
        var info = new WorkspaceInfo(CharacterFolder, defFile, Path.GetFullPath(installedFolder), HashFolder(BaseFolder), DateTime.UtcNow, null, null, null, null, null);
        Save(info);
        return info;
    }

    /// <summary>Why the working copy must not be regenerated (it was changed outside IKEMEN Lab), or null.</summary>
    public string? Divergence()
    {
        var info = Info;
        if (info is null || !Directory.Exists(WorkingFolder)) return null;
        var expected = info.GeneratedBuildHash ?? info.BaseHash;
        return HashFolder(WorkingFolder) == expected ? null
            : "The working copy was changed outside IKEMEN Lab since it was last generated, so it is not overwritten. Re-base the workspace to discard those changes.";
    }

    /// <summary>Why the installed character no longer matches the base (it was edited or replaced since the workspace was made), or null.</summary>
    public string? InstalledDrift()
    {
        var info = Info;
        if (info is null) return null;
        if (!Directory.Exists(info.InstalledFolder)) return "The installed character folder is gone.";
        return HashFolder(info.InstalledFolder) == info.BaseHash ? null
            : "The installed character changed after the working copy was made; deploying would overwrite those changes. Re-base the workspace first.";
    }

    /// <summary>Rebuilds the working copy as base + <paramref name="code"/> (or base alone when null). Refused when the working copy diverged.</summary>
    public string Generate(GeneratedCode? code, CharacterFacts baseFacts, TaughtBehavior? behavior)
    {
        if (Divergence() is { } why) throw new InvalidOperationException(why);
        var info = Info ?? throw new InvalidOperationException("The workspace does not exist.");
        var hash = Build(BaseFolder, WorkingFolder, code, baseFacts);
        Save(info with
        {
            GeneratedBuildHash = hash, BehaviorId = behavior?.Id, ModelHash = behavior?.ModelHash, CodeHash = code?.Hash, GeneratedUtc = DateTime.UtcNow
        });
        return hash;
    }

    /// <summary>Discards the base and the working copy and takes a fresh base from the installed character.</summary>
    public WorkspaceInfo Rebase()
    {
        var info = Info ?? throw new InvalidOperationException("The workspace does not exist.");
        DeleteFolder(BaseFolder);
        DeleteFolder(WorkingFolder);
        File.Delete(InfoPath);
        return Ensure(info.InstalledFolder, info.DefFile);
    }

    private void Save(WorkspaceInfo info)
    {
        Directory.CreateDirectory(Root);
        var tmp = InfoPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(info, Json));
        File.Move(tmp, InfoPath, overwrite: true);
    }

    // ------------------------------------------------------------------ builds

    /// <summary>
    /// Writes <paramref name="output"/> = <paramref name="baseFolder"/> + the generated code, replacing whatever was there, and returns its hash. The edits:
    /// the generated states file; one DEF line in [Files]; the State -1 block (before the first or after the last existing rule); one suppression line
    /// after the header of each suppressed rule. Every other byte is the base's.
    /// </summary>
    public static string Build(string baseFolder, string output, GeneratedCode? code, CharacterFacts facts)
    {
        DeleteFolder(output);
        CopyFolder(baseFolder, output);
        if (code is null) return HashFolder(output);
        if (facts.MinusOneFile is not { } minusOne) throw new InvalidOperationException("There is no single State -1 to place the generated decision in.");

        // The State -1 file: suppression lines and the block, applied bottom-up so earlier line numbers stay valid.
        var m1Path = Path.Combine(output, minusOne);
        var (lines, newline) = ReadLines(m1Path);
        var header = lines.FindIndex(l => DirectorFacts.IsStatedef(l, -1));
        if (header < 0) throw new InvalidOperationException($"{minusOne} has no [Statedef -1].");
        var end = lines.FindIndex(header + 1, DirectorFacts.IsAnyStatedef);
        if (end < 0) end = lines.Count;
        var edits = new List<(int At, IReadOnlyList<string> Lines)>();
        foreach (var target in code.Suppress)
        {
            var at = ControllerHeaderLine(lines, header, end, target.ControllerId);
            if (at < 0 || !lines[at].Contains(target.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{target.Name} ({target.ControllerId}) was not found where the index puts it in {minusOne}'s State -1; nothing was generated.");
            edits.Add((at + 1, [code.SuppressLine]));
        }

        var block = Split(code.MinusOneBlock);
        var after = code.Placement == OwnershipPlacement.AfterExisting;
        var placement = after ? end : FirstController(lines, header, end);
        // Keep the block separated from what precedes and follows it.
        List<string> inserted = [.. placement > 0 && lines[placement - 1].Trim().Length > 0 ? new[] { "" } : [], .. block, ""];
        edits.Add((placement, inserted));
        foreach (var (at, add) in edits.OrderByDescending(e => e.At)) lines.InsertRange(at, add);
        WriteLines(m1Path, lines, newline);

        // The DEF: load the generated states file.
        var defPath = Path.Combine(output, facts.DefFile);
        var (defLines, defNewline) = ReadLines(defPath);
        var files = defLines.FindIndex(l => Regex.IsMatch(l, @"^\s*\[\s*files\s*\]", RegexOptions.IgnoreCase));
        if (files < 0) throw new InvalidOperationException("The DEF has no [Files] section.");
        var filesEnd = defLines.FindIndex(files + 1, l => l.TrimStart().StartsWith('['));
        if (filesEnd < 0) filesEnd = defLines.Count;
        var lastSt = Enumerable.Range(files + 1, filesEnd - files - 1).LastOrDefault(i => Regex.IsMatch(defLines[i], @"^\s*st\d*\s*=", RegexOptions.IgnoreCase), files);
        defLines.Insert(lastSt + 1, code.DefLine);
        WriteLines(defPath, defLines, defNewline);

        // The generated states, in the -1 file's newline style.
        DirectorFacts.WriteText(Path.Combine(output, CharacterFacts.GeneratedFile), string.Join(newline, Split(code.StatesFile)) + newline);
        return HashFolder(output);
    }

    /// <summary>The line of the first [State -1, …] controller (the block goes before it), or the line after the header's parameters.</summary>
    private static int FirstController(List<string> lines, int header, int end)
    {
        for (var i = header + 1; i < end; i++)
            if (lines[i].TrimStart().StartsWith('[')) return i;
        return end;
    }

    /// <summary>The header line of controller <c>state:-1/ctrl:N</c> (the N-th controller of the State -1 block, 0-based as the index numbers them).</summary>
    private static int ControllerHeaderLine(List<string> lines, int header, int end, string id)
    {
        var m = Regex.Match(id, @"/ctrl:(\d+)$");
        if (!m.Success) return -1;
        var n = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var count = 0;
        for (var i = header + 1; i < end; i++)
        {
            if (!lines[i].TrimStart().StartsWith('[')) continue;
            if (count == n) return i;
            count++;
        }

        return -1;
    }

    private static List<string> Split(string text) => text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();

    private static (List<string> Lines, string Newline) ReadLines(string path)
    {
        var text = DirectorFacts.ReadText(path);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return (text.Split(newline).ToList(), newline);
    }

    private static void WriteLines(string path, List<string> lines, string newline) => DirectorFacts.WriteText(path, string.Join(newline, lines));

    // ------------------------------------------------------------------ diff and hashes

    /// <summary>Every file that differs between <paramref name="installedFolder"/> and <paramref name="build"/>, with the inserted lines and a little context.</summary>
    public static BuildDiff Diff(string installedFolder, string build, string characterFolder)
    {
        var changes = new List<FileChange>();
        var sb = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(build, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(build, file).Replace('\\', '/');
            var installed = Path.Combine(installedFolder, rel);
            var buildHash = DirectorHash.OfBytes(File.ReadAllBytes(file));
            var display = $"chars/{characterFolder}/{rel}";
            if (!File.Exists(installed))
            {
                var added = DirectorFacts.ReadText(file).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
                changes.Add(new FileChange(display, "added", added.Length, null, buildHash));
                sb.Append($"--- /dev/null\n+++ {display} (new, generated)\n@@ new file, {added.Length} lines @@\n");
                foreach (var l in added) sb.Append('+').Append(l).Append('\n');
                continue;
            }

            var installedHash = DirectorHash.OfBytes(File.ReadAllBytes(installed));
            if (installedHash == buildHash) continue;
            var a = DirectorFacts.ReadText(installed).Replace("\r\n", "\n").Split('\n');
            var b = DirectorFacts.ReadText(file).Replace("\r\n", "\n").Split('\n');
            sb.Append($"--- {display} (installed)\n+++ {display} (working copy)\n");
            var inserted = Insertions(a, b, sb);
            changes.Add(new FileChange(display, "modified", inserted, installedHash, buildHash));
        }

        foreach (var file in Directory.EnumerateFiles(installedFolder, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(installedFolder, file).Replace('\\', '/');
            if (!File.Exists(Path.Combine(build, rel)))
            {
                changes.Add(new FileChange($"chars/{characterFolder}/{rel}", "removed", 0, DirectorHash.OfBytes(File.ReadAllBytes(file)), string.Empty));
                sb.Append($"--- chars/{characterFolder}/{rel} (installed)\n+++ /dev/null\n@@ removed @@\n");
            }
        }

        return new BuildDiff(changes, sb.ToString());
    }

    /// <summary>Hunks of lines that are in <paramref name="b"/> but not <paramref name="a"/> (a build only inserts); 2 lines of context each side.</summary>
    private static int Insertions(string[] a, string[] b, StringBuilder sb)
    {
        var inserted = 0;
        int i = 0, j = 0;
        while (j < b.Length)
        {
            if (i < a.Length && a[i] == b[j]) { i++; j++; continue; }
            var start = j;
            while (j < b.Length && (i >= a.Length || b[j] != a[i])) j++;
            sb.Append($"@@ after line {i} @@\n");
            for (var c = Math.Max(0, i - 2); c < i; c++) sb.Append(' ').Append(a[c]).Append('\n');
            for (var c = start; c < j; c++) sb.Append('+').Append(b[c]).Append('\n');
            for (var c = i; c < Math.Min(a.Length, i + 2); c++) sb.Append(' ').Append(a[c]).Append('\n');
            inserted += j - start;
        }

        return inserted;
    }

    /// <summary>The identity of a folder's exact contents: every file's relative path and sha256, sorted.</summary>
    public static string HashFolder(string folder)
    {
        if (!Directory.Exists(folder)) return string.Empty;
        var lines = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/').ToLowerInvariant() + ":" + DirectorHash.OfBytes(File.ReadAllBytes(f)))
            .Order(StringComparer.Ordinal);
        return DirectorHash.Of(string.Join("\n", lines));
    }

    public static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    public static void DeleteFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(folder, recursive: true);
    }
}
