namespace IKEMENLab.Core.Parsing;

public static class DefParser
{
    public static DefParseResult? ParseFile(string path)
    {
        var content = DefFileReader.ReadFileContent(path);
        return content is null ? null : Parse(content);
    }

    public static DefParseResult Parse(string content)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var sectionValues = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        string? currentSection = null;

        var lines = content.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim(' ', '\t');
            if (trimmed.Length == 0 || trimmed.StartsWith(';'))
            {
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.Contains(']'))
            {
                var end = trimmed.IndexOf(']');
                if (end > 0)
                {
                    currentSection = trimmed[1..end].ToLowerInvariant();
                    if (!sectionValues.ContainsKey(currentSection))
                    {
                        sectionValues[currentSection] = new Dictionary<string, string>(StringComparer.Ordinal);
                    }
                }

                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (equals < 0) continue;

            var key = trimmed[..equals].Trim(' ', '\t').ToLowerInvariant();
            var value = trimmed[(equals + 1)..].Trim(' ', '\t');

            var comment = value.IndexOf(';');
            if (comment >= 0)
            {
                value = value[..comment].Trim(' ', '\t');
            }

            value = value.Replace("\"", string.Empty);

            if (currentSection is not null)
            {
                sectionValues[currentSection][key] = value;
            }

            values[key] = value;
        }

        var readonlySections = sectionValues.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyDictionary<string, string>)kvp.Value,
            StringComparer.Ordinal);

        return new DefParseResult(values, readonlySections);
    }
}
