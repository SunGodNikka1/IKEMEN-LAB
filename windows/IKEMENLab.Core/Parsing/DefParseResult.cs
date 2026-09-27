namespace IKEMENLab.Core.Parsing;

public sealed class DefParseResult
{
    public DefParseResult(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> sectionValues)
    {
        Values = values;
        SectionValues = sectionValues;
    }

    public IReadOnlyDictionary<string, string> Values { get; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> SectionValues { get; }

    public string? Value(string key, string? section = null)
    {
        var loweredKey = key.ToLowerInvariant();
        if (section is not null)
        {
            var sectionKey = section.ToLowerInvariant();
            if (SectionValues.TryGetValue(sectionKey, out var map) &&
                map.TryGetValue(loweredKey, out var sectionValue))
            {
                return sectionValue;
            }

            return null;
        }

        return Values.TryGetValue(loweredKey, out var value) ? value : null;
    }

    public int IntValue(string key, string? section = null, int defaultValue = 0)
    {
        var stringValue = Value(key, section);
        if (stringValue is null) return defaultValue;
        return int.TryParse(stringValue, out var parsed) ? parsed : defaultValue;
    }

    public string? Name => Value("name");
    public string? DisplayName => Value("displayname");
    public string? Author => Value("author");
    public string? VersionDate => Value("versiondate");

    public string? SpriteFile
    {
        get
        {
            var sprite = Value("sprite");
            if (!string.IsNullOrEmpty(sprite)) return sprite;
            return Value("spr", "bgdef") ?? Value("spr");
        }
    }

    public string? EffectiveName => Name ?? DisplayName;
}
