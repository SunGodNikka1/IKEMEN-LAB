using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.SelectDef;
using IKEMENLab.Core.Settings;

namespace IKEMENLab.Core.Config;

public enum ConfigValueKind
{
    Motif,
    VSync,
    Fullscreen,
    MasterVolume
}

public sealed class ConfigMutationPreview
{
    public required string ConfigPath { get; init; }
    public required string Format { get; init; } // "ini" | "json"
    public required string KeyLabel { get; init; }
    public string? CurrentValue { get; init; }
    public required string NewValue { get; init; }
    public bool CanApply { get; init; }
    public string? Error { get; init; }
    public bool AlreadyMatches { get; init; }
}

public sealed class ConfigMutationResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public string? Description { get; init; }
    public string? ConfigPath { get; init; }
    public string? OperationId { get; init; }
    public bool Changed { get; init; }
    public IkemenConfig? ResultingConfig { get; init; }
    public OperationPlan? Plan { get; init; }
}

public interface IIkemenConfigMutationService
{
    ConfigMutationPreview Preview(string ikemenRoot, ConfigValueKind kind, string newValue);
    ConfigMutationResult Set(string ikemenRoot, ConfigValueKind kind, string newValue, bool dryRun = false);
    ConfigMutationResult SetMotif(string ikemenRoot, string rootRelativeSystemDef, bool dryRun = false);
    ConfigMutationResult SetBool(string ikemenRoot, ConfigValueKind kind, bool value, bool dryRun = false);
    ConfigMutationResult SetMasterVolume(string ikemenRoot, int volume, bool dryRun = false);
}

/// <summary>
/// Narrow SafeMutation-backed writes to Motif / VSync / Fullscreen / MasterVolume.
/// Preserves unrelated keys, sections, and comments. Never regenerates the whole config.
/// </summary>
public sealed class IkemenConfigMutationService : IIkemenConfigMutationService
{
    private readonly ISafeMutationService _mutations;
    private readonly string _stagingRoot;

    public IkemenConfigMutationService(ISafeMutationService? mutations = null, string? stagingRoot = null)
    {
        _mutations = mutations ?? new SafeMutationService();
        _stagingRoot = stagingRoot ?? Path.Combine(AppDataPaths.GetAppDataDirectory(), "config-staging");
        Directory.CreateDirectory(_stagingRoot);
    }

    public ConfigMutationResult SetMotif(string ikemenRoot, string rootRelativeSystemDef, bool dryRun = false)
    {
        var normalized = SelectDefReader.NormalizeSeparators(rootRelativeSystemDef.Trim()).TrimStart('/');
        return Set(ikemenRoot, ConfigValueKind.Motif, normalized, dryRun);
    }

    public ConfigMutationResult SetBool(string ikemenRoot, ConfigValueKind kind, bool value, bool dryRun = false)
    {
        if (kind is not (ConfigValueKind.VSync or ConfigValueKind.Fullscreen))
            return Fail("Boolean mutation only supports VSync and Fullscreen.");
        return Set(ikemenRoot, kind, value ? "1" : "0", dryRun);
    }

    public ConfigMutationResult SetMasterVolume(string ikemenRoot, int volume, bool dryRun = false)
    {
        if (volume < 0 || volume > 100)
            return Fail("MasterVolume must be between 0 and 100.");
        return Set(ikemenRoot, ConfigValueKind.MasterVolume, volume.ToString(System.Globalization.CultureInfo.InvariantCulture), dryRun);
    }

    public ConfigMutationPreview Preview(string ikemenRoot, ConfigValueKind kind, string newValue)
    {
        try
        {
            var root = IkemenPathGuard.NormalizeRoot(ikemenRoot);
            var current = IkemenConfigReader.Read(root);
            if (!current.Exists || current.SourcePath is null)
                return Blocked(kind, newValue, "No save/config.ini or save/config.json found.");

            var path = current.SourcePath;
            IkemenPathGuard.EnsureInsideRoot(root, path);
            var format = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "json" : "ini";
            var (section, key) = KeySpec(kind);
            var currentDisplay = DisplayCurrent(current, kind);

            if (string.IsNullOrWhiteSpace(newValue))
                return Blocked(kind, newValue, "New value is empty.", path, format, currentDisplay);

            if (ValuesMatch(current, kind, newValue))
            {
                return new ConfigMutationPreview
                {
                    ConfigPath = path,
                    Format = format,
                    KeyLabel = $"{section}.{key}",
                    CurrentValue = currentDisplay,
                    NewValue = newValue,
                    CanApply = true,
                    AlreadyMatches = true
                };
            }

            var text = DefFileReader.ReadFileContent(path);
            if (text is null)
                return Blocked(kind, newValue, "Config file could not be decoded.", path, format, currentDisplay);

            var edited = format == "json"
                ? EditJson(text, kind, newValue)
                : IniConfigEditor.SetValue(text, section, key, newValue);

            if (!edited.Success)
                return Blocked(kind, newValue, edited.Error ?? "Config edit rejected.", path, format, currentDisplay);

            return new ConfigMutationPreview
            {
                ConfigPath = path,
                Format = format,
                KeyLabel = $"{section}.{key}",
                CurrentValue = currentDisplay,
                NewValue = newValue,
                CanApply = true
            };
        }
        catch (Exception ex)
        {
            return Blocked(kind, newValue, ex.Message);
        }
    }

    public ConfigMutationResult Set(string ikemenRoot, ConfigValueKind kind, string newValue, bool dryRun = false)
    {
        try
        {
            var preview = Preview(ikemenRoot, kind, newValue);
            if (!preview.CanApply)
                return Fail(preview.Error ?? "Config mutation rejected.", preview.ConfigPath);

            var root = IkemenPathGuard.NormalizeRoot(ikemenRoot);
            var path = preview.ConfigPath;
            IkemenPathGuard.EnsureInsideRoot(root, path);

            if (preview.AlreadyMatches)
            {
                return new ConfigMutationResult
                {
                    Success = true,
                    Changed = false,
                    Description = "Already set.",
                    ConfigPath = path,
                    ResultingConfig = IkemenConfigReader.Read(root)
                };
            }

            using var gate = TargetWriteGate.Enter(path);
            var originalBytes = File.ReadAllBytes(path);
            var originalText = DefFileReader.Decode(originalBytes, out var originalEncoding);
            if (originalText is null)
                return Fail("Config file could not be decoded.", path);

            var (section, key) = KeySpec(kind);
            var edited = preview.Format == "json"
                ? EditJson(originalText, kind, newValue)
                : IniConfigEditor.SetValue(originalText, section, key, newValue);

            if (!edited.Success || edited.Content is null)
                return Fail(edited.Error ?? "Config edit rejected.", path);

            // Pre-write parse check for INI/JSON.
            if (preview.Format == "ini")
            {
                var tmp = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N") + "-check.ini");
                File.WriteAllText(tmp, edited.Content);
                try
                {
                    if (DefParser.ParseFile(tmp) is null)
                        return Fail("Proposed config.ini failed to parse.", path);
                }
                finally
                {
                    try { File.Delete(tmp); } catch { /* ignore */ }
                }
            }
            else
            {
                try { JsonDocument.Parse(edited.Content); }
                catch (JsonException ex) { return Fail("Proposed config.json failed to parse: " + ex.Message, path); }
            }

            var stagingFile = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
            byte[] proposed;
            try
            {
                proposed = DefFileReader.Encode(edited.Content, originalEncoding);
            }
            catch (EncoderFallbackException)
            {
                return Fail("The new value cannot be represented in the config file's text encoding.", path);
            }

            File.WriteAllBytes(stagingFile, proposed);

            try
            {
                var plan = _mutations.PlanReplaceFile(root, path, stagingFile);
                if (!plan.IsValid)
                    return Fail(plan.RejectionReason ?? "ReplaceFile plan rejected.", path, plan);

                if (dryRun)
                {
                    return new ConfigMutationResult
                    {
                        Success = true,
                        Changed = true,
                        Description = "Dry-run: config change planned.",
                        ConfigPath = path,
                        Plan = plan
                    };
                }

                var mutation = _mutations.ReplaceFile(root, path, stagingFile,
                    expectedCurrentHash: TargetWriteGate.Sha256Hex(originalBytes));
                if (!mutation.Success)
                    return Fail(mutation.Error ?? "SafeMutation ReplaceFile failed.", path, plan);

                var reRead = IkemenConfigReader.Read(root);
                if (!Verify(reRead, kind, newValue))
                {
                    _mutations.Rollback(mutation.OperationId);
                    return Fail("Post-write verification failed; rolled back.", path, plan, mutation.OperationId);
                }

                return new ConfigMutationResult
                {
                    Success = true,
                    Changed = true,
                    Description = $"Updated {preview.KeyLabel}.",
                    ConfigPath = path,
                    OperationId = mutation.OperationId,
                    ResultingConfig = reRead,
                    Plan = plan
                };
            }
            finally
            {
                try { if (File.Exists(stagingFile)) File.Delete(stagingFile); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static (string Section, string Key) KeySpec(ConfigValueKind kind) => kind switch
    {
        ConfigValueKind.Motif => ("Config", "Motif"),
        ConfigValueKind.VSync => ("Video", "VSync"),
        ConfigValueKind.Fullscreen => ("Video", "Fullscreen"),
        ConfigValueKind.MasterVolume => ("Sound", "MasterVolume"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string? DisplayCurrent(IkemenConfig config, ConfigValueKind kind) => kind switch
    {
        ConfigValueKind.Motif => config.Motif,
        ConfigValueKind.VSync => config.VSync is null ? null : (config.VSync.Value ? "1" : "0"),
        ConfigValueKind.Fullscreen => config.Fullscreen is null ? null : (config.Fullscreen.Value ? "1" : "0"),
        ConfigValueKind.MasterVolume => config.MasterVolume?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null
    };

    private static bool ValuesMatch(IkemenConfig config, ConfigValueKind kind, string newValue)
    {
        return kind switch
        {
            ConfigValueKind.Motif => string.Equals(
                SelectDefReader.NormalizeSeparators(config.Motif ?? ""),
                SelectDefReader.NormalizeSeparators(newValue),
                StringComparison.OrdinalIgnoreCase),
            ConfigValueKind.VSync => config.VSync == ParseBoolLiteral(newValue),
            ConfigValueKind.Fullscreen => config.Fullscreen == ParseBoolLiteral(newValue),
            ConfigValueKind.MasterVolume =>
                int.TryParse(newValue, out var n) && config.MasterVolume == n,
            _ => false
        };
    }

    private static bool Verify(IkemenConfig config, ConfigValueKind kind, string expected)
        => ValuesMatch(config, kind, expected);

    private static bool? ParseBoolLiteral(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        return v switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => null
        };
    }

    private static IniEditResult EditJson(string text, ConfigValueKind kind, string newValue)
    {
        try
        {
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (node is not JsonObject obj)
                return IniEditResult.Fail("config.json root must be an object.");

            switch (kind)
            {
                case ConfigValueKind.Motif:
                    obj["Motif"] = newValue;
                    break;
                case ConfigValueKind.VSync:
                    var b = ParseBoolLiteral(newValue);
                    if (b is null) return IniEditResult.Fail("Invalid boolean for VSync.");
                    if (obj.ContainsKey("VRetrace")) obj["VRetrace"] = b.Value;
                    else obj["VSync"] = b.Value;
                    break;
                case ConfigValueKind.Fullscreen:
                    var f = ParseBoolLiteral(newValue);
                    if (f is null) return IniEditResult.Fail("Invalid boolean for Fullscreen.");
                    obj["Fullscreen"] = f.Value;
                    break;
                case ConfigValueKind.MasterVolume:
                    if (!int.TryParse(newValue, out var vol))
                        return IniEditResult.Fail("Invalid MasterVolume.");
                    obj["MasterVolume"] = vol;
                    break;
            }

            return IniEditResult.Ok(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            return IniEditResult.Fail(ex.Message);
        }
    }

    private static ConfigMutationPreview Blocked(
        ConfigValueKind kind,
        string newValue,
        string error,
        string? path = null,
        string format = "ini",
        string? current = null)
    {
        var (section, key) = KeySpec(kind);
        return new ConfigMutationPreview
        {
            ConfigPath = path ?? "",
            Format = format,
            KeyLabel = $"{section}.{key}",
            CurrentValue = current,
            NewValue = newValue,
            CanApply = false,
            Error = error
        };
    }

    private static ConfigMutationResult Fail(
        string error,
        string? path = null,
        OperationPlan? plan = null,
        string? operationId = null)
        => new()
        {
            Success = false,
            Error = error,
            ConfigPath = path,
            Plan = plan,
            OperationId = operationId
        };
}

public sealed class IniEditResult
{
    public required bool Success { get; init; }
    public string? Content { get; init; }
    public string? Error { get; init; }
    public bool Changed { get; init; }

    public static IniEditResult Ok(string content, bool changed = true)
        => new() { Success = true, Content = content, Changed = changed };

    public static IniEditResult Fail(string error)
        => new() { Success = false, Error = error, Changed = false };
}

/// <summary>Line-level INI key mutation. Preserves comments, blank lines, and unrelated keys.</summary>
public static class IniConfigEditor
{
    public static IniEditResult SetValue(string content, string section, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(content))
            return IniEditResult.Fail("Config is empty.");

        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = content.Split(["\r\n", "\n", "\r"], StringSplitOptions.None).ToList();

        var sectionIndex = -1;
        var keyIndex = -1;
        string? currentSection = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            var trimmed = raw.Trim(' ', '\t', '\uFEFF');
            if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#'))
                continue;

            if (trimmed.StartsWith('[') && trimmed.Contains(']'))
            {
                var end = trimmed.IndexOf(']');
                currentSection = trimmed[1..end].Trim();
                if (currentSection.Equals(section, StringComparison.OrdinalIgnoreCase))
                    sectionIndex = i;
                continue;
            }

            if (currentSection is null ||
                !currentSection.Equals(section, StringComparison.OrdinalIgnoreCase))
                continue;

            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            var left = trimmed[..eq].Trim();
            if (!left.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

            keyIndex = i;
            break;
        }

        if (keyIndex >= 0)
        {
            lines[keyIndex] = ReplaceValueKeepingLayout(lines[keyIndex], value);
            return IniEditResult.Ok(string.Join(newline, lines));
        }

        if (sectionIndex < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add(string.Empty);
            lines.Add($"[{section}]");
            lines.Add($"{key} = {value}");
            return IniEditResult.Ok(string.Join(newline, lines));
        }

        // Insert after last non-empty line in section (before next section).
        var insertAt = sectionIndex + 1;
        for (var j = sectionIndex + 1; j < lines.Count; j++)
        {
            var t = lines[j].Trim(' ', '\t');
            if (t.StartsWith('[') && t.Contains(']'))
            {
                insertAt = j;
                break;
            }
            insertAt = j + 1;
        }

        lines.Insert(insertAt, $"{key} = {value}");
        return IniEditResult.Ok(string.Join(newline, lines));
    }

    private static string ReplaceValueKeepingLayout(string line, string newValue)
    {
        var eq = line.IndexOf('=');
        if (eq < 0) return line;

        // Keep everything through '=' and following spaces; drop trailing inline comment on value.
        var prefix = line[..(eq + 1)];
        var after = line[(eq + 1)..];
        var spaces = 0;
        while (spaces < after.Length && (after[spaces] == ' ' || after[spaces] == '\t')) spaces++;
        var spacing = after[..spaces];

        // Preserve trailing inline comment if present after the value.
        var rest = after[spaces..];
        string? trailingComment = null;
        var commentIdx = rest.IndexOf(';');
        if (commentIdx >= 0)
        {
            // Only treat as comment if it's after some value content or at start of remaining.
            trailingComment = rest[commentIdx..];
        }

        return prefix + spacing + newValue + (trailingComment is null ? "" : " " + trailingComment.TrimStart());
    }
}
