using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Model;

namespace IKEMENLab.Core.XRay.Behavior;

/// <summary>
/// The three behavior evidence levels, kept strictly apart:
/// <list type="bullet">
/// <item><see cref="Possible"/>: static AI logic suggests the behavior. Never runtime proof, whatever else is known.</item>
/// <item><see cref="Observed"/>: a watched run on the current character files and engine recorded at least one complete episode.</item>
/// <item><see cref="ConfirmedPattern"/>: complete episodes repeated under the explicit <see cref="ConfirmedRule"/>.</item>
/// </list>
/// <see cref="NotSeen"/>: neither (which is not evidence that the character never does it).
/// </summary>
public enum BehaviorLevel { NotSeen, Possible, Observed, ConfirmedPattern }

/// <summary>
/// The Confirmed Pattern threshold. A simple, explicit rule — not statistics: at least <see cref="MinEpisodes"/> complete episodes, in at least
/// <see cref="MinRuns"/> separate watched runs, against at least <see cref="MinSetups"/> different setups (opponent, stage and opponent AI level), all on the
/// current character files and engine.
/// </summary>
public sealed record ConfirmedRule(int MinEpisodes, int MinRuns, int MinSetups)
{
    public static ConfirmedRule Default { get; } = new(3, 2, 2);

    public string Text => $"at least {MinEpisodes} complete episodes in at least {MinRuns} separate watched runs against at least {MinSetups} different setups " +
                          "(opponent, stage, opponent AI level), all on the current character files and engine";
}

public static class BehaviorLevels
{
    public static string Name(BehaviorLevel level) => level switch
    {
        BehaviorLevel.Possible => "Possible",
        BehaviorLevel.Observed => "Observed",
        BehaviorLevel.ConfirmedPattern => "Confirmed Pattern",
        _ => "Not seen"
    };

    public static string Meaning(BehaviorLevel level, ConfirmedRule rule) => level switch
    {
        BehaviorLevel.Possible => "Static AI logic contains rules consistent with this behavior. It has not been recorded at runtime on the current files and engine; static code is never runtime proof.",
        BehaviorLevel.Observed => "At least one complete episode was recorded in a watched run on the current character files and engine.",
        BehaviorLevel.ConfirmedPattern => "Recorded repeatedly: " + rule.Text + ".",
        _ => "No static rule was recognised and no complete episode was recorded on the current files and engine. That is not evidence that the character never does it."
    };
}

/// <summary>A watched run as stored: who and what ran (files hash, engine, opponent, stage, AI levels), and what the detector found in its trace.</summary>
public sealed record BehaviorRun(
    string Id, DateTime CreatedUtc, string Origin, string Character, string CharacterHash, string? EngineSha256, string? Dummy, string? Stage,
    int SubjectAiLevel, int OpponentAiLevel, int Seconds, Detection Detection, IReadOnlyList<int> SuperStates)
{
    public const string SchemaVersion = "ikemenlab.xray.behavior-run/1";
    public int DetectorVersion { get; init; } = BehaviorDetector.Version;
    public string Directory { get; init; } = string.Empty;

    /// <summary>What makes two runs "different setups" for the Confirmed Pattern rule.</summary>
    public string SetupKey => $"{Dummy}|{Stage}|ai{OpponentAiLevel}";
    public string SetupText => $"vs {Dummy ?? "?"} on {StageName} (opponent AI {OpponentAiLevel})";
    public string StageName => Stage is null ? "?" : Path.GetFileNameWithoutExtension(Stage.Replace('\\', '/'));
}

public sealed record EpisodeRef(BehaviorRun Run, BehaviorEpisode Episode);

/// <summary>A recognised-behavior card: the template, its evidence level with the static and runtime evidence shown separately, and what limits it.</summary>
public sealed record BehaviorCard(
    BehaviorTemplate Template, BehaviorLevel Level, IReadOnlyList<StaticRule> StaticRules, bool StaticSupports,
    IReadOnlyList<EpisodeRef> Episodes, int Runs, IReadOnlyList<string> Setups, int StaleEpisodes, IReadOnlyList<string> StaleReasons,
    IReadOnlyDictionary<string, int> Outcomes, IReadOnlyList<int> ActionStates, int Incomplete, IReadOnlyList<string> Unknowns, ConfirmedRule Rule)
{
    public string LevelName => BehaviorLevels.Name(Level);
    public string LevelMeaning => BehaviorLevels.Meaning(Level, Rule);
}

/// <summary>Builds the cards for one character from its static AI rules and its stored watched runs.</summary>
public static class BehaviorCatalog
{
    /// <summary>Why a run's episodes do not count for the current files and engine (null when they do).</summary>
    public static string? StaleReason(BehaviorRun run, string characterHash, string? engineSha)
    {
        if (!string.Equals(run.CharacterHash, characterHash, StringComparison.OrdinalIgnoreCase)) return "the character's files changed since this run";
        if (engineSha is null) return "the current playback engine is not known, so runtime evidence cannot be matched to it";
        if (run.EngineSha256 is null) return "this run did not record which engine it used";
        if (!string.Equals(run.EngineSha256, engineSha, StringComparison.OrdinalIgnoreCase)) return "the playback engine changed since this run";
        return null;
    }

    public static IReadOnlyList<BehaviorCard> Build(SemanticIndex index, CandidateGraph graph, IEnumerable<BehaviorRun> runs, string characterHash, string? engineSha,
        ConfirmedRule? rule = null)
    {
        rule ??= ConfirmedRule.Default;
        var statics = StaticBehavior.Find(index, graph);
        var all = runs.Where(r => r.Character == index.CharacterId).ToList();
        var current = all.Where(r => r.Detection.AiControlled && StaleReason(r, characterHash, engineSha) is null).ToList();
        var stale = all.Where(r => r.Detection.AiControlled && StaleReason(r, characterHash, engineSha) is not null).ToList();
        var cards = new List<BehaviorCard>();
        foreach (var t in BehaviorTemplates.All)
        {
            var mineStatic = statics.Where(s => s.Template == t.Id).ToList();
            var supports = StaticBehavior.Supports(t.Id, statics);
            var episodes = current.SelectMany(r => r.Detection.Episodes.Where(e => e.Template == t.Id).Select(e => new EpisodeRef(r, e))).ToList();
            var runsWith = episodes.Select(e => e.Run.Id).Distinct().Count();
            var setups = episodes.Select(e => e.Run).DistinctBy(r => r.SetupKey).Select(r => r.SetupText).ToList();
            var staleCount = stale.Sum(r => r.Detection.Episodes.Count(e => e.Template == t.Id));
            var staleReasons = stale.Where(r => r.Detection.Episodes.Any(e => e.Template == t.Id))
                .Select(r => StaleReason(r, characterHash, engineSha)!).Distinct().ToList();
            var level = episodes.Count >= rule.MinEpisodes && runsWith >= rule.MinRuns && episodes.Select(e => e.Run.SetupKey).Distinct().Count() >= rule.MinSetups
                ? BehaviorLevel.ConfirmedPattern
                : episodes.Count > 0 ? BehaviorLevel.Observed
                : supports ? BehaviorLevel.Possible
                : BehaviorLevel.NotSeen;
            var outcomes = episodes.GroupBy(e => e.Episode.Outcome).ToDictionary(g => g.Key, g => g.Count());
            // The moves seen doing it when there are episodes; otherwise the targets of the consistent static rules.
            var states = (episodes.Count > 0 ? episodes.SelectMany(e => e.Episode.ActionStates) : mineStatic.Select(s => StateNumber(s.TargetStateId)).OfType<int>())
                .Distinct().Take(12).ToList();
            var unknowns = current.SelectMany(r => r.Detection.Missing).Distinct()
                .Where(m => t.Id == BehaviorTemplates.ProjectileResponse ? m.StartsWith("p2.numProj", StringComparison.Ordinal) : !m.StartsWith("p2.numProj", StringComparison.Ordinal) && !m.StartsWith("backEdge", StringComparison.Ordinal) && !m.StartsWith("p1.aiLevel", StringComparison.Ordinal))
                .ToList();
            if (current.Count == 0) unknowns.Insert(0, "No watched run on the current character files and engine yet.");
            cards.Add(new BehaviorCard(t, level, mineStatic, supports, episodes, runsWith, setups, staleCount, staleReasons, outcomes, states,
                current.Sum(r => r.Detection.Incomplete.TryGetValue(t.Id, out var n) ? n : 0), unknowns, rule));
        }

        return cards;
    }

    internal static int? StateNumber(string id) =>
        id.StartsWith("state:", StringComparison.Ordinal) && int.TryParse(id["state:".Length..], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
}
