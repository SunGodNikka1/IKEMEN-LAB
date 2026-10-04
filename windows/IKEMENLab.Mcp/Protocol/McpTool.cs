using System.Globalization;
using System.Text.Json.Nodes;

namespace IKEMENLab.Mcp.Protocol;

/// <summary>One MCP tool: its metadata (name, schema, hints) and the handler that answers a call with a small JSON object.</summary>
public sealed record McpTool(
    string Name, string Title, string Description, JsonObject InputSchema, bool ReadOnly, Func<ToolArgs, CancellationToken, Task<JsonObject>> Handler)
{
    public JsonObject Describe() => new()
    {
        ["name"] = Name,
        ["title"] = Title,
        ["description"] = Description,
        ["inputSchema"] = InputSchema.DeepClone(),
        ["annotations"] = new JsonObject
        {
            ["title"] = Title,
            ["readOnlyHint"] = ReadOnly,
            ["destructiveHint"] = false,
            ["idempotentHint"] = ReadOnly,
            ["openWorldHint"] = false
        }
    };
}

/// <summary>A tool call that cannot be answered (bad arguments, unknown character or ability…): returned to the agent as an error result, not a protocol error.</summary>
public sealed class ToolError(string message, string? hint = null) : Exception(message)
{
    public string? Hint { get; } = hint;
}

/// <summary>A JSON-RPC level error (unknown method, unknown tool, malformed request).</summary>
public sealed class McpProtocolException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
    public const int ParseError = -32700, InvalidRequest = -32600, MethodNotFound = -32601, InvalidParams = -32602, InternalError = -32603;
}

/// <summary>Typed, validated access to a tool call's arguments. Every problem is a <see cref="ToolError"/> naming the argument.</summary>
public sealed class ToolArgs(JsonObject args)
{
    public JsonObject Raw { get; } = args;

    public bool Has(string name) => Raw.TryGetPropertyValue(name, out var v) && v is not null;

    public string Text(string name) => OptText(name) ?? throw new ToolError($"'{name}' is required.");

    public string? OptText(string name)
    {
        if (!Raw.TryGetPropertyValue(name, out var v) || v is null) return null;
        if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        if (v is JsonValue num && (num.TryGetValue<int>(out _) || num.TryGetValue<long>(out _) || num.TryGetValue<double>(out _))) return v.ToJsonString();
        throw new ToolError($"'{name}' must be a string.");
    }

    public int Int(string name, int fallback, int min, int max)
    {
        if (!Raw.TryGetPropertyValue(name, out var v) || v is null) return fallback;
        int n;
        if (v is JsonValue jv && jv.TryGetValue<int>(out var i)) n = i;
        else if (v is JsonValue jd && jd.TryGetValue<double>(out var d) && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue) n = (int)d;
        else if (v is JsonValue js && js.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) n = p;
        else throw new ToolError($"'{name}' must be a whole number.");
        if (n < min || n > max) throw new ToolError($"'{name}' must be from {min} to {max} (it is {n}).");
        return n;
    }

    public int? OptInt(string name, int min, int max) => Has(name) ? Int(name, 0, min, max) : null;

    public bool Bool(string name, bool fallback = false)
    {
        if (!Raw.TryGetPropertyValue(name, out var v) || v is null) return fallback;
        if (v is JsonValue jv && jv.TryGetValue<bool>(out var b)) return b;
        if (v is JsonValue js && js.TryGetValue<string>(out var s) && bool.TryParse(s, out var p)) return p;
        throw new ToolError($"'{name}' must be true or false.");
    }

    public JsonArray? OptArray(string name)
    {
        if (!Raw.TryGetPropertyValue(name, out var v) || v is null) return null;
        return v as JsonArray ?? throw new ToolError($"'{name}' must be an array.");
    }

    public ToolArgs? OptObject(string name)
    {
        if (!Raw.TryGetPropertyValue(name, out var v) || v is null) return null;
        return v is JsonObject o ? new ToolArgs(o) : throw new ToolError($"'{name}' must be an object.");
    }
}

/// <summary>Small helpers for writing the tools' JSON Schemas.</summary>
public static class Schema
{
    public static JsonObject Object(JsonObject properties, params string[] required)
    {
        var o = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        if (required.Length > 0) o["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        return o;
    }

    public static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    public static JsonObject Enum(string description, params string[] values) => new()
    {
        ["type"] = "string", ["description"] = description, ["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray())
    };

    public static JsonObject Int(string description, int min, int max, int? fallback = null)
    {
        var o = new JsonObject { ["type"] = "integer", ["description"] = description, ["minimum"] = min, ["maximum"] = max };
        if (fallback is { } f) o["default"] = f;
        return o;
    }

    public static JsonObject Bool(string description, bool? fallback = null)
    {
        var o = new JsonObject { ["type"] = "boolean", ["description"] = description };
        if (fallback is { } f) o["default"] = f;
        return o;
    }
}
