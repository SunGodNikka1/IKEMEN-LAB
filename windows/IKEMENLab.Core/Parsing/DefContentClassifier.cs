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

    /// <summary>
    /// Screenpack/motif system.def: requires enough UI evidence, not merely any system.def.
    /// </summary>
    public static bool IsValidScreenpackDefFile(string path)
    {
        if (!Path.GetFileName(path).Equals("system.def", StringComparison.OrdinalIgnoreCase))
            return false;
        var content = DefFileReader.ReadFileContent(path);
        if (content is null) return false;
        return IsValidScreenpackContent(content);
    }

    public static bool IsValidScreenpackContent(string content)
    {
        var lowercased = content.ToLowerInvariant();
        if (lowercased.Contains("[scenedef]") && !lowercased.Contains("[title info]") && !lowercased.Contains("[select info]"))
            return false;
        if (lowercased.Contains("[stageinfo]") || lowercased.Contains("[bgdef]"))
            return false;
        if (lowercased.Contains("[files]") &&
            (lowercased.Contains(".cmd") || lowercased.Contains(".cns")) &&
            !lowercased.Contains("[title info]") &&
            !lowercased.Contains("[select info]"))
        {
            // Character-like DEF named system.def — reject.
            return false;
        }

        var hasFilesBlock = lowercased.Contains("[files]");
        var hasFilesEvidence = hasFilesBlock &&
            (HasIniKey(lowercased, "select") || HasIniKey(lowercased, "fight") || HasIniKey(lowercased, "title") ||
             HasIniKey(lowercased, "spr"));
        var hasSectionEvidence =
            lowercased.Contains("[title info]") ||
            lowercased.Contains("[select info]") ||
            lowercased.Contains("[vs screen]") ||
            lowercased.Contains("[option info]") ||
            lowercased.Contains("[victory screen]") ||
            lowercased.Contains("[continue screen]");

        return hasFilesEvidence || hasSectionEvidence;
    }

    private static bool HasIniKey(string lowercasedContent, string key)
    {
        // Crude but effective: "key =" or "key=" on a line (after lowercasing).
        var needle = "\n" + key;
        var idx = 0;
        while ((idx = lowercasedContent.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            var after = idx + needle.Length;
            while (after < lowercasedContent.Length && (lowercasedContent[after] == ' ' || lowercasedContent[after] == '\t'))
                after++;
            if (after < lowercasedContent.Length && lowercasedContent[after] == '=')
                return true;
            idx++;
        }

        // Also check start of file
        if (lowercasedContent.StartsWith(key, StringComparison.Ordinal))
        {
            var after = key.Length;
            while (after < lowercasedContent.Length && (lowercasedContent[after] == ' ' || lowercasedContent[after] == '\t'))
                after++;
            if (after < lowercasedContent.Length && lowercasedContent[after] == '=')
                return true;
        }

        return false;
    }

    [GeneratedRegex(@"\[bg\s", RegexOptions.CultureInvariant)]
    private static partial Regex BgElementRegex();
}
