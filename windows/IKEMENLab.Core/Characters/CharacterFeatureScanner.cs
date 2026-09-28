using System.Text;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.Validation;

namespace IKEMENLab.Core.Characters;

/// <summary>
/// Detects the list-row feature badges (INTRO · SFX · AI) from real files:
/// INTRO = an [Arcade] intro.storyboard that exists (or an intro.def beside the DEF);
/// SFX = the [Files] sound file exists;
/// AI = the command/state files reference AILevel, or define many AI/CPU-named commands.
/// (The macOS check for "[state -1" matched every character, so it is not reused.)
/// </summary>
public static class CharacterFeatureScanner
{
    private const long MaxBytesScanned = 24L * 1024 * 1024;

    public static CharacterFeatures Scan(string root, CharacterEntry character)
    {
        var def = Path.GetFullPath(Path.Combine(root, character.DefPath));
        var parsed = DefParser.ParseFile(def);
        if (parsed is null) return new CharacterFeatures(false, false, false);

        var intro = Resolve(root, def, parsed.Value("intro.storyboard", "arcade")) is not null ||
                    File.Exists(Path.Combine(Path.GetDirectoryName(def)!, "intro.def"));
        var sound = Resolve(root, def, parsed.Value("sound", "files")) is not null;

        var scripts = new List<string>();
        void Add(string? key)
        {
            var path = Resolve(root, def, key is null ? null : parsed.Value(key, "files"));
            if (path is not null && !scripts.Contains(path, StringComparer.OrdinalIgnoreCase)) scripts.Add(path);
        }

        Add("cmd");
        Add("cns");
        Add("ai");
        Add("st");
        for (var i = 0; i <= 9; i++) Add("st" + i);

        return new CharacterFeatures(intro, sound, HasAi(scripts));
    }

    public static bool HasAi(IEnumerable<string> scriptFiles)
    {
        long budget = MaxBytesScanned;
        var aiCommands = 0;
        foreach (var file in scriptFiles)
        {
            if (budget <= 0) break;
            string? text;
            try
            {
                var length = new FileInfo(file).Length;
                if (length > budget) continue;
                budget -= length;
                text = DefFileReader.ReadFileContent(file);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if (text is null) continue;
            if (text.Contains("ailevel", StringComparison.OrdinalIgnoreCase)) return true;
            aiCommands += CountAiCommandNames(text);
            if (aiCommands >= 10) return true;
        }

        return false;
    }

    private static int CountAiCommandNames(string text)
    {
        var count = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimStart();
            if (!line.StartsWith("name", StringComparison.OrdinalIgnoreCase)) continue;
            var q = line.IndexOf('"');
            if (q < 0) continue;
            var name = line[(q + 1)..].TrimStart();
            if (name.StartsWith("ai", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("cpu", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    private static string? Resolve(string root, string def, string? reference)
        => string.IsNullOrWhiteSpace(reference) ? null : ContentValidator.ResolveResource(root, def, reference);
}
