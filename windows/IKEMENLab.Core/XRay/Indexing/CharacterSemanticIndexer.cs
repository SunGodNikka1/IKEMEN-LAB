using System.Globalization;
using System.Security.Cryptography;
using IKEMENLab.Core.Characters;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.Sprites;
using IKEMENLab.Core.Validation;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Source;

namespace IKEMENLab.Core.XRay.Indexing;

public sealed class XRayOptions
{
    /// <summary>Also index the engine's common states (common1.cns) so edges to states 0, 20, 5000… resolve exactly.</summary>
    public bool IncludeCommonStates { get; init; } = true;
    /// <summary>Look sprites up in the SFF so AIR frames can be checked and sized.</summary>
    public bool ReadSprites { get; init; } = true;
    public int MaxFileBytes { get; init; } = 32 * 1024 * 1024;
}

/// <summary>
/// Builds the Character Semantic Index from a character's own files: DEF [Files] → CMD/CNS/ST/AIR (+ common states, SFF).
/// Read-only, deterministic, and tolerant of malformed input (problems become diagnostics, not exceptions).
/// </summary>
public static class CharacterSemanticIndexer
{
    private static readonly string[] StateFileKeys = ["cmd", "cns", "st", "st0", "st1", "st2", "st3", "st4", "st5", "st6", "st7", "st8", "st9"];

    public static SemanticIndex Build(string ikemenRoot, CharacterEntry character, XRayOptions? options = null)
    {
        options ??= new XRayOptions();
        DefFileReader.EnsureEncodingsRegistered();
        var root = Path.GetFullPath(ikemenRoot);
        var defPath = Path.GetFullPath(Path.Combine(root, character.DefPath));
        var charId = "char:" + character.Id;
        var b = new IndexBuilder();

        var charObj = b.Add(ObjectKind.Character, charId, string.IsNullOrWhiteSpace(character.DisplayName) ? character.Id : character.DisplayName);
        charObj.Props["folder"] = character.Id;
        charObj.Props["def"] = character.DefPath.Replace('\\', '/');

        var defText = File.Exists(defPath) ? DefFileReader.ReadFileContent(defPath) : null;
        if (defText is null)
        {
            b.Warn("def.unreadable", $"Character DEF '{character.DefPath}' could not be read.", null, DiagnosticSeverity.Error);
            return b.Build(charId);
        }

        var parsed = DefParser.Parse(defText);
        charObj.Props["engine"] = CharacterDetailsReader.EngineLabel(parsed);
        var defFile = AddFile(b, root, defPath, defText, SourceRole.Def, false);
        b.Relate(RelationKind.Contains, charId, "file:" + defFile.RelPath, "structure.contains", null);

        // ---- files in load order: character files first, common states last (lowest priority)
        var plan = new List<(string Path, SourceRole Role, bool Common)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { defPath };
        foreach (var key in StateFileKeys)
        {
            var reference = parsed.Value(key, "files");
            if (string.IsNullOrWhiteSpace(reference)) continue;
            if (reference.EndsWith(".zss", StringComparison.OrdinalIgnoreCase))
            {
                b.Warn("zss.not-indexed", $"'{reference}' is a ZSS script; ZSS is not indexed in this version.", null);
                continue;
            }

            var path = ContentValidator.ResolveResource(root, defPath, reference);
            if (path is null) { b.Warn("file.missing", $"[Files] {key} = {reference} was not found.", null); continue; }
            if (seen.Add(path)) plan.Add((path, key == "cmd" ? SourceRole.Cmd : key == "cns" ? SourceRole.Cns : SourceRole.St, false));
        }

        if (options.IncludeCommonStates)
        {
            var reference = parsed.Value("stcommon", "files");
            if (string.IsNullOrWhiteSpace(reference)) reference = "common1.cns";
            var path = ContentValidator.ResolveResource(root, defPath, reference);
            if (path is not null && seen.Add(path)) plan.Add((path, SourceRole.Common, true));
        }

        var airPath = parsed.Value("anim", "files") is { Length: > 0 } airRef ? ContentValidator.ResolveResource(root, defPath, airRef) : null;
        if (airPath is null && !string.IsNullOrWhiteSpace(parsed.Value("anim", "files")))
            b.Warn("file.missing", $"[Files] anim = {parsed.Value("anim", "files")} was not found.", null);

        // ---- phase 1: objects
        var commands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var states = new StateIndexResult();
        var definitionCounts = new Dictionary<int, int>();
        var loadedCommon = false;
        foreach (var (path, role, common) in plan)
        {
            var text = Read(path, options.MaxFileBytes, b);
            if (text is null) continue;
            var file = AddFile(b, root, path, text, role, common);
            b.Relate(RelationKind.Contains, charId, "file:" + file.RelPath, "structure.contains", null);
            var lex = SourceLexer.Lex(text, file.Id);
            b.Diagnostics.AddRange(lex.Diagnostics);
            foreach (var block in lex.Blocks.Where(x => x.Header.StartsWith("Include", StringComparison.OrdinalIgnoreCase)))
                b.Warn("include.not-indexed", $"[{block.Header}] pulls in another file; includes are not followed in this version.", block.Span(file.Id), DiagnosticSeverity.Info);

            foreach (var kv in CommandIndexer.Index(b, file, lex.Blocks)) commands.TryAdd(kv.Key, kv.Value);
            StateIndexer.Index(b, file, lex.Blocks, states, definitionCounts);
            if (common) loadedCommon = true;
        }

        if (airPath is not null)
        {
            var text = Read(airPath, options.MaxFileBytes, b);
            if (text is not null)
            {
                var file = AddFile(b, root, airPath, text, SourceRole.Air, false);
                b.Relate(RelationKind.Contains, charId, "file:" + file.RelPath, "structure.contains", null);
                var lex = SourceLexer.Lex(text, file.Id);
                b.Diagnostics.AddRange(lex.Diagnostics);
                AirIndexer.Index(b, file, lex.Blocks, options.ReadSprites ? ReadSprites(root, character) : null);
            }
        }

        // ---- phase 2: relationships
        var animIds = new Dictionary<int, string>();
        var animFrames = new Dictionary<string, List<AnimFrameInfo>>(StringComparer.Ordinal);
        foreach (var o in b.Objects.Values.Where(o => o.Kind == ObjectKind.Animation && !o.IsStub))
        {
            if (o.Id.Contains('#')) continue;
            var number = int.Parse(o.Id["anim:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture);
            animIds[number] = o.Id;
        }

        foreach (var o in b.Objects.Values.Where(o => o.Kind == ObjectKind.AnimFrame))
        {
            var list = animFrames.TryGetValue(o.ParentId!, out var l) ? l : animFrames[o.ParentId!] = [];
            list.Add(new AnimFrameInfo(o.Id, (int)Num(o.Prop("startTick")), (int)Num(o.Prop("ticks"))));
        }

        var ctx = new LinkContext
        {
            B = b, States = states, Commands = commands, AnimIds = animIds, AnimFrames = animFrames,
            CommonLoaded = loadedCommon, CharacterId = charId
        };
        StateLinker.Link(ctx);
        VariableLinker.Link(ctx);
        AbilityGrouper.Group(ctx);
        LabelInferrer.Run(ctx);

        charObj.Props["states"] = states.Effective.Count.ToString(CultureInfo.InvariantCulture);
        return b.Build(charId);
    }

    private static SourceFile AddFile(IndexBuilder b, string root, string path, string text, SourceRole role, bool common)
    {
        var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (rel.StartsWith("..", StringComparison.Ordinal)) rel = path.Replace('\\', '/');
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
        var file = b.AddFile(rel, role, common, hash);
        b.Add(ObjectKind.File, "file:" + rel, System.IO.Path.GetFileName(rel)).Props["role"] = role.ToString().ToLowerInvariant();
        return file;
    }

    private static string? Read(string path, int maxBytes, IndexBuilder b)
    {
        try
        {
            if (new FileInfo(path).Length > maxBytes)
            {
                b.Warn("file.too-large", $"'{System.IO.Path.GetFileName(path)}' is larger than {maxBytes / 1024 / 1024} MB and was skipped.", null);
                return null;
            }

            var text = DefFileReader.ReadFileContent(path);
            if (text is null) b.Warn("file.unreadable", $"'{System.IO.Path.GetFileName(path)}' could not be decoded.", null);
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            b.Warn("file.unreadable", $"'{System.IO.Path.GetFileName(path)}': {ex.Message}", null);
            return null;
        }
    }

    private static Dictionary<(int, int), SpriteFacts>? ReadSprites(string root, CharacterEntry character)
    {
        var sffPath = CharacterDetailsReader.ResolveSprite(root, character);
        if (sffPath is null) return null;
        using var sff = SffFile.Open(sffPath);
        if (sff is null) return null;
        var map = new Dictionary<(int, int), SpriteFacts>();
        foreach (var s in sff.Sprites)
        {
            var (w, h) = sff.Dimensions(s);
            map.TryAdd((s.Group, s.Number), new SpriteFacts(w, h, s.AxisX, s.AxisY));
        }

        return map;
    }

    private static double Num(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
}
