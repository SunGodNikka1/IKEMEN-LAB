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

        foreach (var encoding in _fallbackEncodings!)
        {
            try
            {
                return encoding.GetString(span);
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
}
