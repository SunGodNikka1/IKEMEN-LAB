using System.Text;

namespace IKEMENLab.Core.Parsing;

/// <summary>
/// Reads DEF files with the Mac DEFParser encoding fallback order.
/// Strict decoding — no replacement characters.
/// </summary>
public static class DefFileReader
{
    private static readonly object Gate = new();
    private static bool _providerRegistered;
    private static Encoding[]? _fallbackEncodings;

    public static void EnsureEncodingsRegistered()
    {
        if (_providerRegistered) return;
        lock (Gate)
        {
            if (_providerRegistered) return;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _fallbackEncodings =
            [
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                Encoding.GetEncoding(1252),   // Windows-1252
                Encoding.GetEncoding(932),    // Shift-JIS
                Encoding.GetEncoding(28591),  // Latin-1 / ISO-8859-1
                Encoding.ASCII
            ];
            _providerRegistered = true;
        }
    }

    public static string? ReadFileContent(string path)
    {
        EnsureEncodingsRegistered();

        if (!File.Exists(path)) return null;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch
        {
            return null;
        }

        // Strip UTF-8 BOM before strict decode attempts.
        ReadOnlySpan<byte> span = bytes;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        // UTF-8 first (strict). Legacy content is then decided by byte statistics rather than by
        // "first encoding that does not throw", because Windows-1252 accepts every byte and would
        // otherwise turn Japanese/Korean names into mojibake.
        try
        {
            return _fallbackEncodings![0].GetString(span);
        }
        catch (DecoderFallbackException)
        {
        }

        foreach (var codePage in RankLegacyCodePages(span))
        {
            try
            {
                return Strict(codePage).GetString(span);
            }
            catch (DecoderFallbackException)
            {
                // try next
            }
            catch (ArgumentException)
            {
                // try next
            }
        }

        return null;
    }

    private static Encoding Strict(int codePage)
        => Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    /// <summary>
    /// Orders Shift-JIS (932), Korean (949) and Windows-1252 by how plausible the bytes are in each.
    /// Accented Latin letters sit inside ASCII words; CJK text forms double-byte runs; Korean uses the
    /// KS X 1001 Hangul block. 1252 wins ties, matching the previous behaviour.
    /// </summary>
    internal static IReadOnlyList<int> RankLegacyCodePages(ReadOnlySpan<byte> bytes)
    {
        static bool AsciiLetter(byte b) => b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';
        static bool SjisLead(byte b) => b is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xEF;
        static bool SjisTrail(byte b) => b is >= 0x40 and <= 0x7E or >= 0x80 and <= 0xFC;

        double latin = 0, sjis = 0, korean = 0;

        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b < 0xC0 || b is 0xD7 or 0xF7) continue;
            var prev = i > 0 && AsciiLetter(bytes[i - 1]);
            var next = i + 1 < bytes.Length && AsciiLetter(bytes[i + 1]);
            latin += prev || next ? 3 : 0.5;
        }

        var run = 0;
        for (var i = 0; i < bytes.Length;)
        {
            var b = bytes[i];
            if (b < 0x80)
            {
                sjis += ScoreRun(run, bytes, i);
                run = 0;
                i++;
            }
            else if (b is >= 0xA1 and <= 0xDF)
            {
                sjis += ScoreRun(run, bytes, i) - 1; // half-width katakana: rare in real names
                run = 0;
                i++;
            }
            else if (SjisLead(b) && i + 1 < bytes.Length && SjisTrail(bytes[i + 1]))
            {
                run++;
                i += 2;
            }
            else
            {
                sjis += ScoreRun(run, bytes, i) - 2;
                run = 0;
                i++;
            }
        }

        sjis += ScoreRun(run, bytes, bytes.Length);

        for (var i = 0; i + 1 < bytes.Length; i++)
        {
            if (bytes[i] is >= 0xB0 and <= 0xC8 && bytes[i + 1] is >= 0xA1 and <= 0xFE)
            {
                korean += 2;
                i++;
            }
        }

        var ranked = new List<(int CodePage, double Score, int Tie)>
        {
            (1252, latin, 0),
            (932, sjis, 1),
            (949, korean, 2)
        };

        return ranked
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Tie)
            .Select(r => r.CodePage)
            .Append(28591)
            .ToList();

        // A run of ≥2 double-byte characters is strong evidence; a lone pair counts unless it is
        // wedged between ASCII letters (the signature of an accented Latin letter).
        static double ScoreRun(int length, ReadOnlySpan<byte> all, int endIndex)
        {
            if (length == 0) return 0;
            if (length >= 2) return 2 * length;
            var start = endIndex - 2;
            var before = start > 0 && AsciiLetter(all[start - 1]);
            var after = endIndex < all.Length && AsciiLetter(all[endIndex]);
            return before && after ? 0 : 2;
        }
    }
}
