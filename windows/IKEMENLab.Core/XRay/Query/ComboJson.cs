using System.Text.Encodings.Web;
using System.Text.Json;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Model;

namespace IKEMENLab.Core.XRay.Query;

/// <summary>
/// Deterministic JSON for combo candidates (<see cref="SchemaVersion"/>). Routes are always reported with <c>status: "candidate"</c>;
/// nothing here is, or can be, runtime-verified.
/// </summary>
public static class ComboJson
{
    public const string SchemaVersion = "ikemenlab.xray.combo/1";
    private static readonly JsonWriterOptions Options = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Options)) body(w);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void Header(Utf8JsonWriter w, CandidateGraph g)
    {
        w.WriteString("schema", SchemaVersion);
        w.WriteString("indexSchema", SemanticIndex.SchemaVersion);
        w.WriteString("character", g.Index.CharacterId);
    }

    public static string Readiness(CandidateGraph g) => Write(w =>
    {
        var r = g.Readiness();
        w.WriteStartObject();
        Header(w, g);
        w.WriteNumber("states", r.States);
        w.WriteNumber("moveStates", r.MoveStates);
        w.WriteNumber("neutralStates", r.NeutralStates);
        w.WriteNumber("edges", r.Edges);
        w.WriteNumber("globalEdges", r.GlobalEdges);
        w.WriteNumber("unconstrainedGlobalEdges", r.UnconstrainedGlobalEdges);
        w.WriteNumber("edgesWithUnmodelledConditions", r.EdgesWithUnmodelled);
        w.WriteNumber("dynamicTargets", r.DynamicTargets);
        w.WriteNumber("movesWithHitDef", r.MovesWithHitDef);
        w.WriteNumber("movesWithLiteralDamage", r.MovesWithLiteralDamage);
        w.WriteNumber("movesWithKnownTicks", r.MovesWithKnownTicks);
        WriteCounts(w, "edgesByKind", r.EdgesByKind);
        WriteCounts(w, "edgesByConfidence", r.EdgesByConfidence);
        XRayJson.WriteStrings(w, "findings", r.Findings);
        w.WriteEndObject();
    });

    public static string Edges(CandidateGraph g, string? from, IEnumerable<CandidateEdge> edges) => Write(w =>
    {
        w.WriteStartObject();
        Header(w, g);
        if (from is not null) w.WriteString("from", from);
        w.WritePropertyName("edges");
        w.WriteStartArray();
        foreach (var e in edges) WriteEdge(w, g, e);
        w.WriteEndArray();
        w.WriteEndObject();
    });

    public static string Search(CandidateGraph g, ComboSearchResult result) => Write(w =>
    {
        var o = result.Options;
        w.WriteStartObject();
        Header(w, g);
        w.WritePropertyName("options");
        w.WriteStartObject();
        w.WriteString("from", o.From ?? "neutral");
        w.WriteNumber("maxMoves", o.MaxMoves);
        w.WriteNumber("startMeter", o.StartMeter);
        if (o.MaxFrames is { } f) w.WriteNumber("maxFrames", f);
        w.WriteBoolean("hitConfirmOnly", o.HitConfirmOnly);
        w.WriteBoolean("allowChains", o.AllowChains);
        w.WriteBoolean("allowLinks", o.AllowLinks);
        w.WriteNumber("maxRepeats", o.MaxRepeats);
        w.WriteNumber("top", o.Top);
        w.WriteString("strategy", o.Strategy.ToString());
        if (o.Strategy == ComboStrategy.Beam) w.WriteNumber("beamWidth", o.BeamWidth);
        w.WriteNumber("maxExpansions", o.MaxExpansions);
        if (o.MaxUnmodelledPerEdge is { } m) w.WriteNumber("maxUnmodelledPerEdge", m);
        w.WriteString("weakestAllowed", o.WeakestAllowed.ToString());
        w.WriteEndObject();

        w.WriteNumber("expansions", result.Expansions);
        w.WriteBoolean("truncated", result.Truncated);
        XRayJson.WriteStrings(w, "warnings", result.Warnings);

        w.WritePropertyName("routes");
        w.WriteStartArray();
        foreach (var r in result.Routes)
        {
            w.WriteStartObject();
            w.WriteString("status", "candidate");
            w.WriteString("start", r.StartState);
            w.WriteString("confidence", r.Confidence.ToString());
            w.WriteNumber("damageKnown", r.DamageKnown);
            w.WriteBoolean("damageComplete", r.DamageComplete);
            w.WriteNumber("meterSpent", r.MeterSpent);
            w.WriteNumber("endMeter", r.EndMeter);
            if (r.MinFrames is { } frames) w.WriteNumber("minFrames", frames);
            w.WriteBoolean("framesComplete", r.FramesComplete);
            w.WriteNumber("unmodelledConditions", r.UnmodelledCount);
            XRayJson.WriteStrings(w, "notes", r.Notes);
            w.WritePropertyName("steps");
            w.WriteStartArray();
            foreach (var s in r.Steps)
            {
                w.WriteStartObject();
                w.WriteString("edge", s.Edge.Id);
                w.WriteString("kind", s.Edge.Kind.ToString());
                w.WriteString("from", s.Edge.From);
                w.WriteString("to", s.Edge.To);
                w.WriteString("name", g.Index.Get(s.Move.StateId) is null ? s.Move.Name : g.Index.NameOf(s.Move.StateId));
                if (g.Index.Names.IsRenamed(s.Move.StateId)) w.WriteString("defaultName", s.Move.Name);
                XRayJson.WriteStrings(w, "commands", s.Edge.Commands);
                w.WriteString("contact", s.Edge.Contact.ToString());
                w.WriteString("confidence", s.Edge.Confidence.ToString());
                if (s.Damage is { } d) w.WriteNumber("damage", d);
                w.WriteNumber("meterAfter", s.MeterAfter);
                XRayJson.WriteStrings(w, "unmodelled", s.Edge.Unmodelled);
                w.WriteString("controller", s.Edge.ControllerId);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    });

    private static void WriteEdge(Utf8JsonWriter w, CandidateGraph g, CandidateEdge e)
    {
        w.WriteStartObject();
        w.WriteString("id", e.Id);
        w.WriteString("from", e.From);
        w.WriteString("to", e.To);
        w.WriteString("kind", e.Kind.ToString());
        w.WriteString("contact", e.Contact.ToString());
        w.WriteString("confidence", e.Confidence.ToString());
        w.WriteString("controller", e.ControllerId);
        w.WriteString("relationship", e.RelationshipId);
        w.WriteNumber("trigger", e.BranchNumber);
        w.WriteBoolean("global", e.IsGlobal);
        w.WriteBoolean("sourceHasHitDef", e.SourceHasHitDef);
        if (e.EarliestTick is { } t) w.WriteNumber("earliestTick", t);
        XRayJson.WriteStrings(w, "commands", e.Commands);
        w.WritePropertyName("facets");
        XRayJson.WriteFacets(w, e.Facets);
        w.WritePropertyName("evidence");
        w.WriteStartArray();
        foreach (var x in e.Evidence)
        {
            w.WriteStartObject();
            w.WriteString("rule", x.RuleId);
            if (x.RelationshipId is not null) w.WriteString("relationship", x.RelationshipId);
            XRayJson.WriteSource(w, g.Index, x.Source);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        XRayJson.WriteStrings(w, "unmodelled", e.Unmodelled);
        XRayJson.WriteStrings(w, "notes", e.Notes);
        w.WriteEndObject();
    }

    private static void WriteCounts(Utf8JsonWriter w, string name, IReadOnlyDictionary<string, int> counts)
    {
        w.WritePropertyName(name);
        w.WriteStartObject();
        foreach (var (k, v) in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal)) w.WriteNumber(k, v);
        w.WriteEndObject();
    }
}
