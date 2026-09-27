using System.Text.RegularExpressions;

namespace IKEMENLab.Core.Parsing;

public static partial class DefContentClassifier
{
    public static bool IsStoryboardDefFile(string path)
    {
        var content = DefFileReader.ReadFileContent(path);
        if (content is null) return false;
        return content.Contains("[scenedef]", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsValidCharacterDefFile(string path)
    {
        var content = DefFileReader.ReadFileContent(path);
        if (content is null) return false;
        return IsValidCharacterContent(content);
    }

    public static bool IsValidCharacterContent(string content)
    {
        var lowercased = content.ToLowerInvariant();

        if (lowercased.Contains("[scenedef]")) return false;
        if (lowercased.Contains("[fnt]") || lowercased.Contains("[fnt v2]")) return false;

        if (lowercased.Contains("[stageinfo]") || lowercased.Contains("[bgdef]"))
        {
            if (!(lowercased.Contains("[files]") &&
                  (lowercased.Contains(".cmd") || lowercased.Contains(".cns") || lowercased.Contains(".air"))))
            {
                return false;
            }
        }

        return lowercased.Contains("[files]") &&
               (lowercased.Contains(".cmd") || lowercased.Contains(".cns") || lowercased.Contains(".air"));
    }

    public static bool IsValidStageDefFile(string path)
    {
        var content = DefFileReader.ReadFileContent(path);
        if (content is null) return false;
        return IsValidStageContent(content);
    }

    public static bool IsValidStageContent(string content)
    {
        var lowercased = content.ToLowerInvariant();

        if (lowercased.Contains("[scenedef]")) return false;

        if (lowercased.Contains("[files]") &&
            (lowercased.Contains(".cmd") || lowercased.Contains(".cns") || lowercased.Contains(".air")))
        {
            return false;
        }

        if (lowercased.Contains("[fnt]") || lowercased.Contains("[fnt v2]")) return false;

        var hasStageInfo = lowercased.Contains("[stageinfo]");
        var hasBgDef = lowercased.Contains("[bgdef]");
        var hasBgElements = BgElementRegex().IsMatch(lowercased);

        return hasStageInfo || hasBgDef || hasBgElements;
    }

    [GeneratedRegex(@"\[bg\s", RegexOptions.CultureInvariant)]
    private static partial Regex BgElementRegex();
}
