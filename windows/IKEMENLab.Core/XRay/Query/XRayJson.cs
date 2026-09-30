using System.Text.Encodings.Web;
using System.Text.Json;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Source;

namespace IKEMENLab.Core.XRay.Query;

/// <summary>
/// Deterministic, versioned JSON (<see cref="SemanticIndex.SchemaVersion"/>) for agents and tools: objects and relationships are
/// in id order, property bags are sorted, and the same index always serialises to the same bytes.
/// </summary>
public static class XRayJson
{
    private static readonly JsonWriterOptions Options = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Index(SemanticIndex index) => Write(w => WriteIndex(w, index));

    public static string ExplanationJson(SemanticIndex index, Explanation e) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("schema", SemanticIndex.SchemaVersion);
        w.WriteString("character", index.CharacterId);
        w.WritePropertyName("subject");
        WriteObject(w, index, e.Subject);
        w.WritePropertyName("sections");
        w.WriteStartArray();
        foreach (var s in e.Sections)
        {
            w.WriteStartObject();
            w.WriteString("title", s.Title);
            w.WritePropertyName("items");
            w.WriteStartArray();
            foreach (var it in s.Items) WriteItem(w, index, it);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    });

    public static string ObjectList(SemanticIndex index, string title, IEnumerable<SemanticObject> objects) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("schema", SemanticIndex.SchemaVersion);
        w.WriteString("character", index.CharacterId);
        w.WriteString("query", title);
        w.WritePropertyName("results");
        w.WriteStartArray();
        foreach (var o in objects) WriteObject(w, index, o);
        w.WriteEndArray();
        w.WriteEndObject();
    });

    public static string Transitions(SemanticIndex index, string stateId, IReadOnlyList<Transition> transitions) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("schema", SemanticIndex.SchemaVersion);
        w.WriteString("character", index.CharacterId);
        w.WriteString("state", stateId);
        w.WritePropertyName("transitions");
        w.WriteStartArray();
        foreach (var t in transitions)
        {
            w.WriteStartObject();
            w.WriteString("to", t.ToState);
            w.WriteString("controller", t.ControllerId);
            w.WriteString("kind", t.Kind.ToString());
            w.WriteString("confidence", t.Confidence.ToString());
            w.WriteString("relationship", t.RelationshipId);
            if (t.Gate is not null) { w.WritePropertyName("gate"); WriteGate(w, t.Gate); }
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    });

    public static string VarUsage(SemanticIndex index, string variableId, IReadOnlyList<VarUse> uses) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("schema", SemanticIndex.SchemaVersion);
        w.WriteString("character", index.CharacterId);
        w.WriteString("variable", variableId);
        w.WritePropertyName("labels");
        WriteLabels(w, index.Get(variableId)?.Labels ?? []);
        w.WritePropertyName("uses");
        w.WriteStartArray();
        foreach (var u in uses)
        {
            w.WriteStartObject();
            w.WriteString("access", u.Relationship.Kind.ToString());
            w.WriteString("by", u.Controller.Id);
            if (u.State is not null) w.WriteString("state", u.State.Id);
            w.WriteString("confidence", u.Relationship.Confidence.ToString());
            WriteProps(w, u.Relationship.Props);
            WriteSource(w, index, u.Relationship.Evidence.FirstOrDefault()?.Source);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    });

    public static string Rules() => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("schema", SemanticIndex.SchemaVersion);
        w.WritePropertyName("rules");
        w.WriteStartArray();
        foreach (var r in EvidenceRules.Rules.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            w.WriteStartObject();
            w.WriteString("id", r.Id);
            w.WriteString("confidence", r.Confidence.ToString());
            w.WriteString("description", r.Description);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    });

    // ------------------------------------------------------------------ writers

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Options)) body(w);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteIndex(Utf8JsonWriter w, SemanticIndex index)
    {
        w.WriteStartObject();
        w.WriteString("schema", SemanticIndex.SchemaVersion);
        w.WriteString("character", index.CharacterId);

        w.WritePropertyName("files");
        w.WriteStartArray();
        foreach (var f in index.Files)
        {
            w.WriteStartObject();
            w.WriteNumber("id", f.Id);
            w.WriteString("path", f.RelPath);
            w.WriteString("role", f.Role.ToString().ToLowerInvariant());
            w.WriteBoolean("common", f.IsCommon);
            w.WriteString("hash", f.Hash);
            w.WriteEndObject();
        }

        w.WriteEndArray();

        w.WritePropertyName("objects");
        w.WriteStartArray();
        foreach (var o in index.Objects) WriteObject(w, index, o);
        w.WriteEndArray();

        w.WritePropertyName("relationships");
        w.WriteStartArray();
        foreach (var r in index.Relationships.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            w.WriteStartObject();
            w.WriteString("id", r.Id);
            w.WriteString("kind", r.Kind.ToString());
            w.WriteString("from", r.From);
            w.WriteString("to", r.To);
            w.WriteString("confidence", r.Confidence.ToString());
            w.WritePropertyName("evidence");
            w.WriteStartArray();
            foreach (var e in r.Evidence)
            {
                w.WriteStartObject();
                w.WriteString("rule", e.RuleId);
                WriteSource(w, index, e.Source);
                if (e.Note is not null) w.WriteString("note", e.Note);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            WriteProps(w, r.Props);
            var gateOwner = r.Gate is null ? null : r.From.EndsWith("/hitdef", StringComparison.Ordinal) ? r.From[..^7] : r.From;
            if (gateOwner is not null) w.WriteString("gateOf", gateOwner);
            w.WriteEndObject();
        }

        w.WriteEndArray();

        w.WritePropertyName("diagnostics");
        w.WriteStartArray();
        foreach (var d in index.Diagnostics)
        {
            w.WriteStartObject();
            w.WriteString("severity", d.Severity.ToString());
            w.WriteString("code", d.Code);
            w.WriteString("message", d.Message);
            WriteSource(w, index, d.Source);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static void WriteObject(Utf8JsonWriter w, SemanticIndex index, SemanticObject o)
    {
        w.WriteStartObject();
        w.WriteString("id", o.Id);
        w.WriteString("kind", o.Kind.ToString());
        w.WriteString("name", o.Name);
        if (o.ParentId is not null) w.WriteString("parent", o.ParentId);
        if (o.IsStub) w.WriteBoolean("unresolved", true);
        WriteSource(w, index, o.Source);
        WriteProps(w, o.Props);
        if (o.Labels.Count > 0)
        {
            w.WritePropertyName("labels");
            WriteLabels(w, o.Labels);
        }

        if (o.Gate is not null)
        {
            w.WritePropertyName("gate");
            WriteGate(w, o.Gate);
        }

        w.WriteEndObject();
    }

    private static void WriteLabels(Utf8JsonWriter w, IEnumerable<Label> labels)
    {
        w.WriteStartArray();
        foreach (var l in labels)
        {
            w.WriteStartObject();
            w.WriteString("text", l.Text);
            w.WriteString("category", l.Category);
            w.WriteString("confidence", l.Confidence.ToString());
            w.WriteString("rule", l.RuleId);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteGate(Utf8JsonWriter w, Gate gate)
    {
        w.WriteStartObject();
        w.WritePropertyName("triggerAll");
        w.WriteStartArray();
        foreach (var l in gate.TriggerAll) WriteLine(w, l);
        w.WriteEndArray();
        w.WritePropertyName("branches");
        w.WriteStartArray();
        foreach (var b in gate.Branches)
        {
            w.WriteStartObject();
            w.WriteNumber("trigger", b.Number);
            w.WritePropertyName("lines");
            w.WriteStartArray();
            foreach (var l in b.Lines) WriteLine(w, l);
            w.WriteEndArray();
            var f = b.Facets;
            w.WritePropertyName("facets");
            w.WriteStartObject();
            WriteStrings(w, "commands", f.Commands);
            WriteStrings(w, "negatedCommands", f.NegatedCommands);
            WriteStrings(w, "contact", f.Contact);
            WriteCompares(w, "power", f.Power);
            WriteCompares(w, "time", f.Time);
            WriteCompares(w, "animElem", f.AnimElem);
            w.WriteBoolean("ctrlRequired", f.CtrlRequired);
            WriteStrings(w, "stateTypes", f.StateTypes);
            WriteStrings(w, "moveTypes", f.MoveTypes);
            w.WriteBoolean("readsAILevel", f.ReadsAiLevel);
            w.WriteNumber("otherConditions", f.OtherConditions);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteLine(Utf8JsonWriter w, TriggerLine l)
    {
        w.WriteStartObject();
        w.WriteString("text", l.Text);
        w.WriteString("expr", Expressions.ExprPrinter.ToSExpr(l.Expression));
        if (l.Source is { } s) { w.WriteNumber("line", s.StartLine); }
        w.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        w.WritePropertyName(name);
        w.WriteStartArray();
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    }

    private static void WriteCompares(Utf8JsonWriter w, string name, IReadOnlyList<Expressions.NumCompare> values)
    {
        w.WritePropertyName(name);
        w.WriteStartArray();
        foreach (var v in values)
        {
            w.WriteStartObject();
            w.WriteString("op", v.Op);
            w.WriteNumber("value", v.Value);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteItem(Utf8JsonWriter w, SemanticIndex index, ExplainItem it)
    {
        w.WriteStartObject();
        w.WriteString("id", it.Id);
        w.WriteString("name", it.Name);
        w.WriteString("kind", it.Kind.ToString());
        if (it.Confidence is { } c) w.WriteString("confidence", c.ToString());
        if (it.Via is not null) w.WriteString("via", it.Via);
        if (it.Note is not null) w.WriteString("note", it.Note);
        WriteSource(w, index, it.Source);
        w.WriteEndObject();
    }

    private static void WriteProps(Utf8JsonWriter w, IEnumerable<KeyValuePair<string, string>> props)
    {
        var list = props.ToList();
        if (list.Count == 0) return;
        w.WritePropertyName("props");
        w.WriteStartObject();
        foreach (var (k, v) in list.OrderBy(p => p.Key, StringComparer.Ordinal)) w.WriteString(k, v);
        w.WriteEndObject();
    }

    private static void WriteSource(Utf8JsonWriter w, SemanticIndex index, SourceRef? source)
    {
        if (source is not { } s || index.FileOf(s) is not { } file) return;
        w.WritePropertyName("source");
        w.WriteStartObject();
        w.WriteString("file", file.RelPath);
        w.WriteNumber("startLine", s.StartLine);
        w.WriteNumber("endLine", s.EndLine);
        w.WriteEndObject();
    }
}
