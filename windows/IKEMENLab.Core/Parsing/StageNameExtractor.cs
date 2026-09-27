namespace IKEMENLab.Core.Parsing;

public static class StageNameExtractor
{
    public static string Extract(string defPath)
    {
        var content = DefFileReader.ReadFileContent(defPath);
        var extracted = content is null ? null : ExtractFromContent(content);
        var fallback = Path.GetFileNameWithoutExtension(defPath)
            .Replace('_', ' ')
            .Replace('-', ' ');

        if (!string.IsNullOrEmpty(extracted) && extracted.Length > 2)
        {
            return extracted;
        }

        return fallback;
    }

    public static string? ExtractFromContent(string content)
    {
        var lines = content.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim(' ', '\t');
            if (trimmed.Length == 0 || trimmed.StartsWith(';')) continue;

            var equals = trimmed.IndexOf('=');
            if (equals < 0) continue;

            var key = trimmed[..equals].Trim(' ', '\t').ToLowerInvariant();
            if (key is not ("name" or "displayname")) continue;

            var valueAndComment = trimmed[(equals + 1)..].Trim(' ', '\t');
            var semicolon = valueAndComment.IndexOf(';');
            if (semicolon >= 0)
            {
                var before = valueAndComment[..semicolon]
                    .Trim(' ', '\t')
                    .Replace("\"", string.Empty);
                var after = valueAndComment[(semicolon + 1)..]
                    .Trim(' ', '\t')
                    .Replace("\"", string.Empty);

                if (before.Length <= 2 && after.Length > before.Length)
                {
                    return after;
                }

                return string.IsNullOrEmpty(before) ? null : before;
            }

            var clean = valueAndComment.Replace("\"", string.Empty);
            return string.IsNullOrEmpty(clean) ? null : clean;
        }

        return null;
    }
}
