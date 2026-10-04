using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Names;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Sequences;
using IKEMENLab.Mcp.Protocol;

namespace IKEMENLab.Mcp.Lab;

/// <summary>
/// Observe tier: read-only questions answered from the same semantic index, names, candidate graph and stores the X-Ray window uses. Every answer is a
/// small slice (names first, ids secondary); source text only ever comes back for one object or an explicit, size-limited line range.
/// </summary>
internal sealed partial class ObserveTools(LabContext ctx)
{
    public const int DefaultSourceLines = 60, MaxSourceLines = 200, MaxSourceChars = 24_000;

    /// <summary>Category order for "useful moves" and lists: the moves a person usually means first.</summary>
    private static readonly string[] CategoryOrder = ["Super", "Special", "Projectile", "Throw", "Counter", "Normal", "Victim-state hit", "Mobility", "Defensive", "Summon", "Mode"];

    private static readonly Dictionary<string, string> CategoryArg = new(StringComparer.OrdinalIgnoreCase)
    {
        ["normal"] = "Normal", ["special"] = "Special", ["super"] = "Super", ["throw"] = "Throw", ["projectile"] = "Projectile", ["counter"] = "Counter",
        ["mobility"] = "Mobility", ["defensive"] = "Defensive", ["summon"] = "Summon", ["mode"] = "Mode"
    };

    public static readonly string[] Categories = ["all", "normal", "special", "super", "throw", "projectile", "counter", "mobility", "defensive", "summon", "mode"];

    // ------------------------------------------------------------------ inspect_character

    public JsonObject InspectCharacter(ToolArgs a)
    {
        var c = ctx.Load(a.Text("character"));
        var index = c.Index;
        var abilities = index.Of(ObjectKind.Ability).ToList();
        var byCategory = new JsonObject();
        foreach (var g in abilities.Select(x => PrimaryCategory(x)).GroupBy(x => x).OrderBy(g => Rank(g.Key)))
            byCategory[g.Key] = g.Count();
        var playable = abilities.Count(x => c.PathOf(x.Id).Path is not null);
        var useful = abilities.Where(x => c.PathOf(x.Id).Path is not null && PrimaryCategory(x) is not ("Other" or "Mode" or "Victim-state hit"))
            .OrderBy(x => Rank(PrimaryCategory(x))).ThenBy(x => EntryNumber(x)).Take(10)
            .Select(x => (JsonNode)AbilityRow(c, x)).ToArray();

        var names = c.Names.Names;
        var setup = ctx.Setup(c);
        var holder = ctx.Broker?.CurrentHolder();
        return new JsonObject
        {
            ["character"] = new JsonObject
            {
                ["name"] = c.DisplayName, ["folder"] = c.SubjectFolder, ["def"] = c.Located.Entry.DefPath,
                ["author"] = string.IsNullOrWhiteSpace(c.Located.Entry.Author) ? null : c.Located.Entry.Author
            },
            ["files"] = new JsonObject { ["count"] = index.Files.Count(f => !f.IsCommon), ["contentHash"] = c.ContentHash },
            ["abilities"] = new JsonObject { ["total"] = abilities.Count, ["playable"] = playable, ["byCategory"] = byCategory },
            ["usefulMoves"] = new JsonArray(useful),
            ["yourNames"] = new JsonObject
            {
                ["applied"] = names.Records.Keys.Count(id => names.StatusOf(id) == NameStatus.Current),
                ["waitingForReview"] = names.Review().Count
            },
            ["playback"] = SetupJson(setup, holder),
            ["sequenceLab"] = new JsonObject
            {
                ["savedVariants"] = ctx.Sequences.Load(c.Folder).Sequences.Count,
                ["experimentsOnTheseFiles"] = ctx.Experiments.List(c.ContentHash).Count
            },
            ["basis"] = Semantic.StaticBasis,
            ["next"] = "list_abilities lists every move; inspect_ability explains one; play_ability performs one in IKEMEN; test_sequence tries a follow-up."
        };
    }

    internal static JsonObject SetupJson(PlaybackSetup setup, RuntimeHolder? holder) => new()
    {
        ["ready"] = setup.Ready,
        ["dummy"] = setup.Dummy,
        ["stage"] = setup.Stage,
        ["approachDistance"] = setup.ApproachDistance,
        ["engine"] = setup.EnginePath,
        ["issues"] = Semantic.Strings(setup.Issues),
        ["engineBusyWith"] = holder?.Describe()
    };

    // ------------------------------------------------------------------ list_abilities

    public JsonObject ListAbilities(ToolArgs a)
    {
        var c = ctx.Load(a.Text("character"));
        var category = a.OptText("category") ?? "all";
        if (!category.Equals("all", StringComparison.OrdinalIgnoreCase) && !CategoryArg.ContainsKey(category))
            throw new ToolError($"Unknown category '{category}'.", "Use one of: " + string.Join(", ", Categories) + ".");
        var search = a.OptText("search");
        var playableOnly = a.Bool("playable_only");
        var limit = a.Int("limit", 40, 1, 100);
        var offset = a.Int("offset", 0, 0, 10_000);

        var all = c.Index.Of(ObjectKind.Ability)
            .Where(x => category.Equals("all", StringComparison.OrdinalIgnoreCase) || Labels(x).Contains(CategoryArg[category]))
            .Where(x => search is null || c.Index.NameOf(x.Id).Contains(search, StringComparison.OrdinalIgnoreCase) || x.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || x.Id.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(x => !playableOnly || c.PathOf(x.Id).Path is not null)
            .OrderBy(x => Rank(PrimaryCategory(x))).ThenBy(EntryNumber)
            .ToList();
        var page = all.Skip(offset).Take(limit).Select(x => (JsonNode)AbilityRow(c, x)).ToArray();
        var o = new JsonObject
        {
            ["character"] = c.DisplayName,
            ["total"] = all.Count,
            ["returned"] = page.Length,
            ["abilities"] = new JsonArray(page)
        };
        if (offset + page.Length < all.Count) o["nextOffset"] = offset + page.Length;
        o["basis"] = "Static: categories are inferred; damage and startup are literal values from the files (null when not literal). playable = Play Ability can press its command from neutral.";
        return o;
    }

    private static JsonObject AbilityRow(LoadedCharacter c, SemanticObject ability)
    {
        var index = c.Index;
        var row = new JsonObject { ["name"] = index.NameOf(ability.Id), ["id"] = ability.Id, ["category"] = PrimaryCategory(ability) };
        if (index.Names.IsRenamed(ability.Id)) row["xrayName"] = ability.Name;
        if (CommandText(index, ability) is { } cmd) row["command"] = cmd;
        if (ability.Prop("entryState") is { } entry && c.Graph.Move(entry) is { } move)
        {
            row["state"] = move.Number;
            if (move.HitDefCount > 0 && move.Damage is { } d) row["damage"] = Math.Round(d, 1);
            if (move.FirstHitTick is { } t) row["startupFrames"] = t;
            if (move.PowerCost > 0) row["meterCost"] = Math.Round(move.PowerCost);
        }

        row["playable"] = c.PathOf(ability.Id).Path is not null;
        if (ability.Prop("entry") == "ai") row["entry"] = "AI only";
        return row;
    }

    // ------------------------------------------------------------------ inspect_ability

    public JsonObject InspectAbility(ToolArgs a)
    {
        var c = ctx.Load(a.Text("character"));
        var index = c.Index;
        var id = Resolve.Ability(c, a.Text("ability"));
        var ability = index.Get(id)!;
        var entry = ability.Prop("entryState");
        var move = entry is null ? null : c.Graph.Move(entry);
        var path = c.PathOf(id);

        var o = new JsonObject
        {
            ["ability"] = Semantic.Ref(index, id),
            ["categories"] = new JsonArray(ability.Labels.Where(l => l.Category == LabelCategories.AbilityCategory)
                .Select(l => (JsonNode)new JsonObject { ["text"] = l.Text, ["confidence"] = Semantic.ConfidenceText(l.Confidence), ["rule"] = l.RuleId }).ToArray()),
            ["entry"] = ability.Prop("entry") switch { "ai" => "entered only by the AI (no command)", "command+ai" => "by command, and also by the AI", _ => "by command" }
        };
        if (entry is not null)
        {
            var es = Semantic.Ref(index, entry);
            if (index.Get(entry) is { } st)
            {
                if (st.Prop("p.type") is { } type) es["stateType"] = type;
                if (st.Prop("p.movetype") is { } mt) es["moveType"] = mt;
            }

            o["entryState"] = es;
        }

        o["commands"] = new JsonArray((ability.Prop("commands") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(cmd => (JsonNode)new JsonObject { ["name"] = cmd, ["notation"] = Notation(index, cmd) }).ToArray());
        if (move is not null)
            o["move"] = new JsonObject
            {
                ["damage"] = move.HitDefCount > 0 ? Semantic.Num(move.Damage) : null,
                ["damageExact"] = move.HitDefCount > 0 ? move.DamageExact : null,
                ["startupFrames"] = Semantic.Num(move.FirstHitTick),
                ["animationFrames"] = Semantic.Num(move.AnimTicks),
                ["hitDefs"] = move.HitDefCount,
                ["meterCost"] = move.PowerCost > 0 ? Math.Round(move.PowerCost) : null,
                ["hitFlag"] = move.HitFlag,
                ["guardFlag"] = move.GuardFlag,
                ["hitPause"] = Semantic.Num(move.PauseTime)
            };

        if (path.Path is { } p)
        {
            o["playAbility"] = new JsonObject
            {
                ["available"] = true,
                ["presses"] = p.Command is { } cmd ? string.Join("+", cmd.Split('+').Select(x => Notation(index, x) ?? x)) : null,
                ["command"] = p.Command,
                ["confidence"] = Semantic.ConfidenceText(p.Edge.Confidence),
                ["warnings"] = Semantic.Strings(p.Warnings),
                ["otherCommandPaths"] = p.Alternatives.Count
            };
        }
        else
        {
            o["playAbility"] = new JsonObject { ["available"] = false, ["why"] = path.Refused, ["canPreview"] = path.CanPreview };
        }

        if (entry is not null) o["followUps"] = FollowUps(c, entry, 12);
        var members = index.Incoming(id, RelationKind.PartOf).Select(r => index.Get(r.From)?.Kind).ToList();
        o["parts"] = new JsonObject
        {
            ["states"] = members.Count(k => k == ObjectKind.State), ["helpers"] = members.Count(k => k == ObjectKind.Helper),
            ["projectiles"] = members.Count(k => k == ObjectKind.Projectile), ["hitDefs"] = members.Count(k => k == ObjectKind.HitDef)
        };
        if (SourceOf(index, entry ?? id) is { } src) o["source"] = src;
        o["basis"] = Semantic.StaticBasis;
        return o;
    }

    /// <summary>Candidate follow-ups out of a state (cancels, chains, links, on-hit states): the best edge per target, strongest evidence first.</summary>
    private static JsonArray FollowUps(LoadedCharacter c, string stateId, int max)
    {
        var index = c.Index;
        var abilityByEntry = index.Of(ObjectKind.Ability).Where(x => x.Prop("entryState") is not null)
            .GroupBy(x => x.Prop("entryState")!).ToDictionary(g => g.Key, g => g.OrderBy(x => x.Id, StringComparer.Ordinal).First().Id, StringComparer.Ordinal);
        var edges = c.Graph.From(stateId).Where(e => e.To != stateId && e.Kind is not EdgeKind.Recovery and not EdgeKind.Start)
            .GroupBy(e => e.To)
            .Select(g => g.OrderBy(e => e.Confidence switch { Confidence.StaticProven => 0, Confidence.Inferred => 1, _ => 2 }).ThenBy(e => e.Unmodelled.Count).First())
            .OrderBy(e => e.Kind).ThenBy(e => e.To, StringComparer.Ordinal).ToList();
        var list = new JsonArray();
        foreach (var e in edges.Take(max))
        {
            var to = abilityByEntry.TryGetValue(e.To, out var ab) ? Semantic.Ref(index, ab) : Semantic.Ref(index, e.To);
            if (abilityByEntry.ContainsKey(e.To)) to["state"] = c.Graph.Move(e.To)?.Number;
            var item = new JsonObject
            {
                ["to"] = to,
                ["how"] = e.Kind switch
                {
                    EdgeKind.Cancel => "cancel", EdgeKind.Chain => "continues by itself", EdgeKind.Link => "link (after recovering control)",
                    EdgeKind.OnHit => "on hit (the move's own follow-up state)", _ => e.Kind.ToString().ToLowerInvariant()
                },
                ["confidence"] = Semantic.ConfidenceText(e.Confidence)
            };
            if (e.Commands.Count > 0) item["press"] = string.Join("+", e.Commands.Select(x => Notation(index, x) ?? x));
            if (e.Contact != ContactRequirement.None)
                item["needs"] = e.Contact switch { ContactRequirement.Hit => "the move to hit", ContactRequirement.Contact => "the move to touch", _ => "the move to be blocked" };
            if (e.EarliestTick is { } t) item["earliestFrame"] = t;
            if (e.Unmodelled.Count > 0) item["unmodelledConditions"] = e.Unmodelled.Count;
            list.Add(item);
        }

        if (edges.Count > max) list.Add(new JsonObject { ["more"] = edges.Count - max });
        return list;
    }

    // ------------------------------------------------------------------ explain_state

    public JsonObject ExplainState(ToolArgs a)
    {
        var c = ctx.Load(a.Text("character"));
        var index = c.Index;
        var id = Resolve.State(c, a.Text("state"));
        var state = index.Get(id)!;
        var o = new JsonObject { ["state"] = Semantic.Ref(index, id) };
        var props = new JsonObject();
        foreach (var (key, label) in new[] { ("p.type", "stateType"), ("p.movetype", "moveType"), ("p.physics", "physics"), ("p.anim", "animation"), ("p.ctrl", "control"),
                     ("p.poweradd", "meterGain"), ("p.juggle", "juggle"), ("p.velset", "velocity") })
            if (state.Prop(key) is { } v) props[label] = v;
        if (props.Count > 0) o["statedef"] = props;
        if (state.Prop("common") == "true") o["common"] = "An engine common state (common1.cns), shared by every character.";
        if (state.Labels.FirstOrDefault(l => l.Category == LabelCategories.Name) is { } authorName) o["authorComment"] = authorName.Text;

        var abilities = index.Outgoing(id, RelationKind.PartOf).Select(r => r.To).Where(x => index.Get(x)?.Kind == ObjectKind.Ability).Take(6).ToList();
        if (abilities.Count > 0) o["partOf"] = new JsonArray(abilities.Select(x => (JsonNode)Semantic.Ref(index, x)).ToArray());
        if (c.Graph.Move(id) is { } move)
            o["move"] = new JsonObject
            {
                ["damage"] = move.HitDefCount > 0 ? Semantic.Num(move.Damage) : null, ["startupFrames"] = Semantic.Num(move.FirstHitTick),
                ["animationFrames"] = Semantic.Num(move.AnimTicks), ["hitDefs"] = move.HitDefCount, ["neutral"] = move.IsNeutral
            };

        var into = index.TransitionsInto(id).ToList();
        o["enteredBy"] = Transitions(index, into, t => t.FromState, 10);
        var from = index.TransitionsFrom(id).ToList();
        o["leadsTo"] = Transitions(index, from, t => t.ToState, 15);

        var hitDefs = index.ControllersOf(id).SelectMany(ctrl => index.Outgoing(ctrl.Id, RelationKind.DefinesHitDef)).Select(r => index.Get(r.To)).OfType<SemanticObject>().Take(6).ToList();
        if (hitDefs.Count > 0)
            o["hitDefs"] = new JsonArray(hitDefs.Select(h =>
            {
                var hd = new JsonObject { ["id"] = h.Id };
                foreach (var (key, label) in new[] { ("p.attr", "attr"), ("p.damage", "damage"), ("p.hitflag", "hitFlag"), ("p.guardflag", "guardFlag"), ("p.pausetime", "pauseTime"),
                             ("p.ground.hittime", "groundHitTime"), ("p.animtype", "animType"), ("p.p2stateno", "sendsOpponentTo"), ("p.p1stateno", "thenGoesTo") })
                    if (h.Prop(key) is { } v) hd[label] = v;
                return (JsonNode)hd;
            }).ToArray());

        var spawns = index.ControllersOf(id).SelectMany(ctrl => index.Outgoing(ctrl.Id, RelationKind.SpawnsHelper, RelationKind.SpawnsProjectile)).Select(r => r.To).Distinct().Take(6).ToList();
        if (spawns.Count > 0) o["spawns"] = new JsonArray(spawns.Select(x => (JsonNode)Semantic.Ref(index, x)).ToArray());
        o["controllers"] = index.ControllersOf(id).Count;
        if (SourceOf(index, id) is { } src) o["source"] = src;
        o["basis"] = Semantic.StaticBasis;
        return o;
    }

    private static JsonArray Transitions(SemanticIndex index, IReadOnlyList<Transition> transitions, Func<Transition, string> other, int max)
    {
        var list = new JsonArray();
        foreach (var t in transitions.Take(max))
        {
            var item = new JsonObject { ["state"] = Semantic.Ref(index, other(t)), ["confidence"] = Semantic.ConfidenceText(t.Confidence) };
            if (t.Kind == RelationKind.SetsAttackerState) item["via"] = "the move's HitDef (when it hits)";
            if (t.Gate is { } g && !g.IsEmpty)
            {
                if (GateWords(index, g) is { Length: > 0 } words) item["when"] = words;
                var raw = SemanticIndex.GateText(g);
                item["triggers"] = raw.Length > 220 ? raw[..220] + " …" : raw;
            }

            list.Add(item);
        }

        if (transitions.Count > max) list.Add(new JsonObject { ["more"] = transitions.Count - max });
        return list;
    }

    /// <summary>The first trigger group's recognised conditions in plain words ("press D,DF,F+x · after a hit · needs control").</summary>
    private static string GateWords(SemanticIndex index, Gate g)
    {
        var f = g.Branches.FirstOrDefault()?.Facets;
        if (f is null) return string.Empty;
        var parts = new List<string>();
        if (f.Commands.Count > 0) parts.Add("press " + string.Join(" or ", f.Commands.Select(x => Notation(index, x) ?? x)));
        if (f.Contact.Count > 0) parts.Add("after " + string.Join("/", f.Contact.Select(x => x switch { "movehit" => "a hit", "moveguarded" => "a block", "movecontact" => "contact", _ => x })));
        if (f.CtrlRequired) parts.Add("needs control");
        foreach (var t in f.Time) parts.Add($"time {t.Op} {t.Value:0.##}");
        foreach (var t in f.AnimElem) parts.Add($"animation element {t.Op} {t.Value:0.##}");
        foreach (var p in f.Power) parts.Add($"meter {p.Op} {p.Value:0.##}");
        if (f.StateTypes.Count > 0) parts.Add("statetype " + string.Join("/", f.StateTypes));
        if (f.ReadsAiLevel) parts.Add("AI only (reads AILevel)");
        if (f.OtherConditions > 0) parts.Add($"{f.OtherConditions} other condition(s)");
        if (g.Branches.Count > 1) parts.Add($"or one of {g.Branches.Count - 1} other trigger group(s)");
        return string.Join(" · ", parts);
    }

    // ------------------------------------------------------------------ get_source

    public JsonObject GetSource(ToolArgs a)
    {
        var c = ctx.Load(a.Text("character"));
        var index = c.Index;
        var maxLines = a.Int("max_lines", DefaultSourceLines, 1, MaxSourceLines);
        var objectSpec = a.OptText("object");
        var fileSpec = a.OptText("file");
        JsonObject? subject = null;
        Core.XRay.Source.SourceFile file;
        int start, end;
        if (objectSpec is not null)
        {
            if (fileSpec is not null || a.Has("start_line")) throw new ToolError("Pass either 'object' or 'file' with 'start_line', not both.");
            var id = Resolve.Object(c, objectSpec);
            var obj = index.Get(id)!;
            if (obj.Source is not { } src || index.FileOf(src) is not { } f)
                throw new ToolError($"{index.NameOf(id)} ({id}) has no source location in the character files.");
            file = f;
            start = src.StartLine;
            end = src.EndLine;
            if (obj.Kind is ObjectKind.State or ObjectKind.Ability)
            {
                // A state is its Statedef block plus its [State] controllers; an ability's source is its entry state.
                var stateId = obj.Kind == ObjectKind.Ability ? obj.Prop("entryState") : id;
                if (stateId is not null)
                    foreach (var ctrl in index.ControllersOf(stateId))
                        if (ctrl.Source is { } cs && cs.FileId == src.FileId && cs.StartLine >= start) end = Math.Max(end, cs.EndLine);
            }

            subject = Semantic.Ref(index, id);
        }
        else if (fileSpec is not null)
        {
            var wanted = fileSpec.Replace('\\', '/');
            var matches = index.Files.Where(x => x.RelPath.Equals(wanted, StringComparison.OrdinalIgnoreCase) || x.RelPath.EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
                throw new ToolError(matches.Count == 0 ? $"'{fileSpec}' is not one of this character's files." : $"'{fileSpec}' matches several files: " + string.Join(", ", matches.Select(m => m.RelPath)),
                    "Files: " + string.Join(", ", index.Files.Select(x => x.RelPath)));
            file = matches[0];
            start = a.OptInt("start_line", 1, 10_000_000) ?? throw new ToolError("'start_line' is required with 'file' (whole files are never returned).");
            end = a.OptInt("end_line", start, 10_000_000) ?? start + maxLines - 1;
        }
        else
        {
            throw new ToolError("Pass 'object' (an ability, state or other object) or 'file' with 'start_line'.",
                "get_source returns one object's lines or an explicit range, never a whole file.");
        }

        var path = Path.IsPathRooted(file.RelPath) ? file.RelPath : Path.Combine(c.Root, file.RelPath.Replace('/', Path.DirectorySeparatorChar));
        var text = DefFileReader.ReadFileContent(path) ?? throw new ToolError($"{file.RelPath} could not be read.");
        var lines = LineBreaks().Split(text);
        if (start > lines.Length) throw new ToolError($"{file.RelPath} has only {lines.Length} lines.");
        end = Math.Min(end, lines.Length);
        var requested = end - start + 1;
        var sb = new StringBuilder();
        var last = start - 1;
        for (var n = start; n <= end && n - start < maxLines; n++)
        {
            var line = $"{n,5}| {lines[n - 1]}\n";
            if (sb.Length + line.Length > MaxSourceChars) break;
            sb.Append(line);
            last = n;
        }

        var o = new JsonObject();
        if (subject is not null) o["object"] = subject;
        o["file"] = file.RelPath;
        o["role"] = file.Role.ToString().ToLowerInvariant();
        o["lines"] = $"{start}-{last}";
        o["fileLines"] = lines.Length;
        o["truncated"] = last < end;
        if (last < end) o["nextStartLine"] = last + 1;
        if (requested > maxLines) o["note"] = $"The range has {requested} lines; at most {maxLines} are returned per call (max_lines ≤ {MaxSourceLines}).";
        o["text"] = sb.ToString();
        return o;
    }

    internal static JsonObject? SourceOf(SemanticIndex index, string id) =>
        index.Get(id)?.Source is { } s && index.FileOf(s) is { } f
            ? new JsonObject { ["file"] = f.RelPath, ["lines"] = $"{s.StartLine}-{s.EndLine}", ["getSource"] = $"get_source with object \"{id}\"" }
            : null;

    [GeneratedRegex("\r\n|\r|\n")]
    private static partial Regex LineBreaks();

    // ------------------------------------------------------------------ experiments

    public JsonObject ListExperiments(ToolArgs a)
    {
        var c = ctx.Load(a.Text("character"));
        var sequenceId = a.OptText("sequence_id");
        var limit = a.Int("limit", 10, 1, 30);
        var allVersions = a.Bool("all_file_versions");
        var variants = ctx.Sequences.Load(c.Folder).Sequences
            .Where(s => sequenceId is null || s.Id == sequenceId)
            .OrderByDescending(s => s.UpdatedUtc)
            .Select(s =>
            {
                string chain;
                try { chain = SequenceLabels.Chain(s.ToSequence().Actions, c.Index); }
                catch (FormatException) { chain = s.Spec; }
                return (JsonNode)new JsonObject { ["sequenceId"] = s.Id, ["name"] = s.Name, ["version"] = s.Version, ["steps"] = chain, ["spec"] = s.Spec };
            }).ToArray();
        var mine = ctx.Experiments.List(null, sequenceId).Where(e => e.Scope.Character == c.Index.CharacterId).ToList();
        var current = mine.Where(e => e.Scope.CharacterHash == c.ContentHash).ToList();
        var shown = (allVersions ? mine : current).Take(limit).Select(e => (JsonNode)Semantic.ExperimentRow(e)).ToArray();
        var o = new JsonObject
        {
            ["character"] = c.DisplayName,
            ["savedVariants"] = new JsonArray(variants),
            ["experiments"] = new JsonArray(shown),
            ["experimentsOnTheseFiles"] = current.Count
        };
        if (!allVersions && mine.Count > current.Count)
            o["olderFileVersions"] = $"{mine.Count - current.Count} experiment(s) ran on earlier versions of this character's files (all_file_versions: true lists them).";
        return o;
    }

    public JsonObject GetExperimentResults(ToolArgs a)
    {
        var e = FindExperiment(a.Text("experiment_id"));
        var includeTrials = a.Bool("include_trials");
        var lastTrial = e.Trials.LastOrDefault();
        var o = new JsonObject
        {
            ["experimentId"] = e.Id,
            ["headline"] = ExperimentText.Headline(e, null),
            ["scope"] = Semantic.Scope(e.Scope),
            ["steps"] = e.Steps,
            ["stats"] = Semantic.Stats(e),
            ["lines"] = Semantic.Strings(ExperimentText.Lines(e)),
            ["origin"] = e.Origin
        };
        if (lastTrial is not null && ReadSequenceReport(Path.Combine(e.Directory, "trials", lastTrial.RecordId)) is { } report)
        {
            report["trial"] = lastTrial.Number;
            o["lastTrial"] = report;
            if (e.Requested == 1 && report["summary"] is { } summary) o["headline"] = summary.DeepClone();
        }

        if (includeTrials)
            o["trials"] = new JsonArray(e.Trials.Take(50).Select(t => (JsonNode)new JsonObject
            {
                ["trial"] = t.Number, ["runId"] = t.RecordId, ["verdict"] = Semantic.VerdictText(t.Verdict), ["reason"] = t.Reason is null ? null : ExperimentText.Plain(t.Reason),
                ["failedStep"] = t.FailedStep, ["damage"] = Semantic.Num(t.Damage), ["endDistance"] = Semantic.Num(t.EndDistance), ["opponentAtEnd"] = t.EndOpponent,
                ["opponentOutOfHitstunFrames"] = Semantic.Num(t.OutOfHitstunFrames)
            }).ToArray());
        o["evidence"] = "Every trial replayed the whole sequence from neutral in a disposable match; verdicts come from the Sequence Lab's verifier. Results belong only to the scope above.";
        return o;
    }

    public JsonObject CompareExperiments(ToolArgs a)
    {
        var x = FindExperiment(a.Text("experiment_a"));
        var y = FindExperiment(a.Text("experiment_b"));
        var (rows, warning) = ExperimentText.Compare(x, y);
        return new JsonObject
        {
            ["a"] = new JsonObject { ["experimentId"] = x.Id, ["headline"] = ExperimentText.Headline(x, null) },
            ["b"] = new JsonObject { ["experimentId"] = y.Id, ["headline"] = ExperimentText.Headline(y, null) },
            ["rows"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject { ["metric"] = r.Metric, ["a"] = r.A, ["b"] = r.B }).ToArray()),
            ["sameSetup"] = x.Scope.SameSetup(y.Scope),
            ["warning"] = warning
        };
    }

    internal ExperimentSummary FindExperiment(string id)
    {
        if (!RunId().IsMatch(id)) throw new ToolError($"'{id}' is not an experiment id.", "Experiment ids look like 20261004-084321-30cf61 (list_experiments shows them).");
        return ctx.Experiments.List().FirstOrDefault(e => e.Id == id) ?? throw new ToolError($"No experiment '{id}'.", "list_experiments shows this character's experiments.");
    }

    /// <summary>The stored Sequence Lab report of one trial (sequence.json), reshaped like a fresh run's.</summary>
    internal static JsonObject? ReadSequenceReport(string recordDir)
    {
        var path = Path.Combine(recordDir, "sequence.json");
        if (!File.Exists(path)) return null;
        JsonObject r;
        try { r = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new JsonException(); }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
        string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var verdict = S(r["verdict"]) ?? "CouldNotTest";
        var o = new JsonObject
        {
            ["verdict"] = Semantic.VerdictText(verdict),
            ["verdictCode"] = verdict,
            ["summary"] = S(r["summary"]),
            ["steps"] = new JsonArray((r["steps"] as JsonArray ?? []).OfType<JsonObject>().Select(s =>
            {
                var outcome = S(s["outcome"]) ?? "NotReached";
                var connected = s["connected"] is JsonValue cv && cv.TryGetValue<bool>(out var cb) ? cb : (bool?)null;
                var reason = S(s["reason"]);
                var step = new JsonObject
                {
                    ["step"] = s["index"]?.DeepClone(), ["label"] = S(s["label"]), ["kind"] = S(s["kind"]), ["outcome"] = outcome,
                    ["result"] = S(s["why"]) ?? outcome switch
                    {
                        "Done" when connected == true => $"Done — connected at frame {s["contactFrame"]}",
                        "Done" when connected == false => "Done — but it did not touch the opponent",
                        "Done" => "Done",
                        "NotReached" => "Not reached",
                        _ => reason is null ? "Failed" : ExperimentText.Plain(reason)
                    }
                };
                if (reason is not null) step["reason"] = reason;
                if (connected is { } c) step["connected"] = c;
                return (JsonNode)step;
            }).ToArray()),
            ["attacks"] = $"{r["connectedAttacks"]} of {r["attacks"]} connected",
            ["damage"] = r["damage"]?.DeepClone()
        };
        foreach (var (from, to) in new[] { ("failedStep", "failedStep"), ("outOfHitstunFrames", "opponentOutOfHitstunFrames"), ("opponentCouldActFrames", "opponentCouldActFrames") })
            if (r[from] is JsonValue) o[to] = r[from]!.DeepClone();
        if (r["end"] is JsonObject end)
            o["end"] = new JsonObject
            {
                ["frame"] = end["frame"]?.DeepClone(), ["distance"] = end["distance"]?.DeepClone(), ["opponent"] = end["opponentPosture"]?.DeepClone(),
                ["youCanAct"] = end["youCanAct"]?.DeepClone()
            };
        o["runId"] = Path.GetFileName(recordDir);
        return o;
    }

    [GeneratedRegex(@"^\d{8}-\d{6}-[0-9a-f]{6}$")]
    internal static partial Regex RunId();

    // ------------------------------------------------------------------ get_runtime_trace

    public JsonObject GetRuntimeTrace(ToolArgs a)
    {
        var runId = a.Text("run_id");
        if (!RunId().IsMatch(runId)) throw new ToolError($"'{runId}' is not a run id.", "Run ids look like 20261004-084321-30cf61; play_ability, preview_state and experiment results give them.");
        var (dir, where) = FindRun(runId) ?? throw new ToolError($"No run '{runId}'.", "Runs from play_ability, preview_state, experiment trials and the app's own playback history can be read.");
        var tracePath = Path.Combine(dir, "trace.jsonl");
        if (!File.Exists(tracePath)) throw new ToolError($"Run {runId} has no recorded trace.");
        var log = TraceReader.ReadFile(tracePath);
        var from = a.OptInt("from_frame", 0, int.MaxValue);
        var to = a.OptInt("to_frame", 0, int.MaxValue);
        var maxEvents = a.Int("max_events", 60, 1, 200);
        var withSamples = a.Bool("samples") || from is not null || to is not null;
        var maxSamples = a.Int("max_samples", 30, 1, 120);

        // Names: P1 is the character the run was for; P2 is the dummy, whose own states are not in this index (only common states are named).
        SemanticIndex? index = null;
        var meta = ReadMeta(dir);
        if (a.OptText("character") is { } character) index = ctx.Load(character).Index;
        string P1(int? s) => s is null ? "?" : index is not null ? Semantic.StateLabel(index, s.Value) : $"State {s}";
        string P2(int? s) => s is null ? "?" : index?.Get($"state:{s}") is { } o && o.Prop("common") == "true" ? $"{index.NameOf(o.Id)} ({s})" : $"State {s}";

        bool InRange(long f) => (from is null || f >= from) && (to is null || f <= to);
        var events = new JsonArray();
        var total = 0;
        foreach (var ev in log.Events)
        {
            if (!InRange(ev.Frame)) continue;
            string? text = ev switch
            {
                StateChangeEvent sc => sc.Player == 1 ? $"You: {P1(sc.From)} → {P1(sc.To)}" : $"Opponent: {P2(sc.From)} → {P2(sc.To)}",
                LifeChangeEvent lc => $"{(lc.Player == 1 ? "Your" : "Opponent's")} life {lc.From:0.##} → {lc.To:0.##}",
                InputEvent ie when ie.Player == 1 => ie.Keys.Count == 0 ? "Released all keys" + StepText(ie.Step) : $"Holding {string.Join("+", ie.Keys)}" + StepText(ie.Step),
                DriverEvent de => $"Driver: {de.Kind}" + StepText(de.Step) + (de.Detail is { Length: > 0 } d ? $" — {d}" : string.Empty),
                EndEvent end => "Recording ended" + (end.Reason is null ? string.Empty : $" ({end.Reason})"),
                _ => null
            };
            if (text is null) continue;
            total++;
            if (events.Count < maxEvents) events.Add(new JsonObject { ["frame"] = ev.Frame, ["event"] = text });
        }

        var frames = log.Frames.ToList();
        var o = new JsonObject
        {
            ["runId"] = runId,
            ["store"] = where,
            ["run"] = meta,
            ["frames"] = frames.Count == 0 ? null : new JsonObject { ["first"] = frames[0].Frame, ["last"] = frames[^1].Frame, ["samples"] = frames.Count },
            ["engine"] = log.Meta is { } m ? new JsonObject { ["version"] = m.EngineVersion, ["sha256"] = m.EngineSha256, ["probe"] = m.ProbeVersion } : null,
            ["events"] = events
        };
        if (total > events.Count) o["moreEvents"] = $"{total - events.Count} more event(s) in this range; narrow from_frame/to_frame or raise max_events (≤ 200).";
        if (withSamples)
        {
            var picked = frames.Where(f => InRange(f.Frame)).ToList();
            var step = Math.Max(1, (int)Math.Ceiling(picked.Count / (double)maxSamples));
            o["samples"] = new JsonArray(picked.Where((_, i) => i % step == 0).Take(maxSamples).Select(f => (JsonNode)new JsonObject
            {
                ["frame"] = f.Frame,
                ["you"] = P1(f.P1.State) + (f.P1.Ctrl == true ? ", can act" : string.Empty),
                ["opponent"] = P2(f.P2.State) + " — " + Situation.Posture(f.P2),
                ["distance"] = Semantic.Num(f.Distance is { } d ? Math.Abs(d) : null),
                ["opponentLife"] = Semantic.Num(f.P2.Life)
            }).ToArray());
            if (step > 1) o["sampleEvery"] = step;
        }

        if (index is null) o["note"] = "Pass 'character' to name your character's states.";
        if (log.Issues.Count > 0) o["traceIssues"] = log.Issues.Count;
        return o;
    }

    private static string StepText(int? step) => step is { } s ? $" (step {s})" : string.Empty;

    private (string Dir, string Store)? FindRun(string runId)
    {
        foreach (var (root, label) in new[] { (ctx.Runs.StoreRoot, "MCP runs"), (ctx.AppPlaybackRoot, "your playback history (read-only)") })
        {
            var d = Path.Combine(root, runId);
            if (File.Exists(Path.Combine(d, "meta.json"))) return (d, label);
        }

        foreach (var e in ctx.Experiments.List())
        {
            var d = Path.Combine(e.Directory, "trials", runId);
            if (File.Exists(Path.Combine(d, "meta.json"))) return (d, $"experiment {e.Id}");
        }

        return null;
    }

    private static JsonObject? ReadMeta(string dir)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "meta.json"))) is not JsonObject m) return null;
            var o = new JsonObject { ["mode"] = m["mode"]?.DeepClone() ?? "combo", ["what"] = m["summary"]?.DeepClone(), ["status"] = m["status"]?.DeepClone() };
            if (m["reason"] is JsonValue) o["reason"] = m["reason"]!.DeepClone();
            if (m["mode"]?.GetValue<string>() == "preview") o["proof"] = false;
            o["dummy"] = m["dummy"]?.DeepClone();
            o["stage"] = m["stage"]?.DeepClone();
            return o;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { return null; }
    }

    // ------------------------------------------------------------------ helpers

    private static IReadOnlyList<string> Labels(SemanticObject ability) =>
        ability.Labels.Where(l => l.Category == LabelCategories.AbilityCategory).Select(l => l.Text).ToList();

    internal static string PrimaryCategory(SemanticObject ability) => Labels(ability).OrderBy(Rank).FirstOrDefault() ?? "Other";

    private static int Rank(string category) => Array.IndexOf(CategoryOrder, category) is var i && i >= 0 ? i : CategoryOrder.Length;

    private static int EntryNumber(SemanticObject ability) =>
        ability.Prop("entryState") is { } e && int.TryParse(e["state:".Length..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;

    internal static string? Notation(SemanticIndex index, string command) =>
        index.Get("cmd:" + command) is { } cmd ? cmd.Prop("notation") is { Length: > 0 } n ? n : cmd.Prop("raw") : null;

    internal static string? CommandText(SemanticIndex index, SemanticObject ability)
    {
        var commands = (ability.Prop("commands") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (commands.Length == 0) return null;
        var shown = commands.Take(2).Select(c => Notation(index, c) ?? c).Distinct().ToList();
        return string.Join(" / ", shown) + (commands.Length > 2 ? " / …" : string.Empty);
    }
}
