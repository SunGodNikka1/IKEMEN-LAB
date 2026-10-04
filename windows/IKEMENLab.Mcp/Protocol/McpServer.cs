using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IKEMENLab.Mcp.Protocol;

/// <summary>
/// A Model Context Protocol server over stdio: newline-delimited JSON-RPC 2.0 (initialize, ping, tools/list, tools/call). Requests are answered
/// concurrently (a get_job that waits never blocks another call); responses are written one per line under a lock. Nothing but protocol messages
/// ever reaches the output: diagnostics go to the log (stderr).
/// </summary>
public sealed class McpServer
{
    /// <summary>Protocol revisions this server speaks, newest first. A client asking for one of these gets it; any other gets the newest.</summary>
    public static readonly string[] SupportedVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    public const string ServerName = "ikemenlab-xray";

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IReadOnlyList<McpTool> _tools;
    private readonly string _instructions;
    private readonly TextWriter _log;
    private readonly string _version;

    public McpServer(IReadOnlyList<McpTool> tools, string instructions, TextWriter log, string version)
    {
        _tools = tools;
        _instructions = instructions;
        _log = log;
        _version = version;
    }

    /// <summary>The negotiated protocol revision (the newest until initialize).</summary>
    public string Protocol { get; private set; } = SupportedVersions[0];

    /// <summary>The client's name from initialize (e.g. "claude-code"), shown to other clients while this server holds the engine.</summary>
    public string? ClientName { get; private set; }

    public event Action<string>? ClientIdentified;

    public IReadOnlyList<McpTool> Tools => _tools;

    /// <summary>Reads requests until the input ends (the client closed the pipe) or <paramref name="cancel"/> fires, then waits for in-flight calls.</summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancel)
    {
        var writeLock = new SemaphoreSlim(1, 1);
        var inFlight = new List<Task>();
        while (!cancel.IsCancellationRequested)
        {
            string? line;
            try { line = await input.ReadLineAsync(cancel).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var task = Task.Run(async () =>
            {
                string? response;
                try { response = await HandleLineAsync(line, cancel).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    // A request is always answered: an unexpected failure becomes an internal error for that id, never silence.
                    _log.WriteLine($"[mcp] request failed: {ex}");
                    response = Serialize(Error(IdOf(line), McpProtocolException.InternalError, "Internal error: " + ex.Message));
                }

                if (response is null) return;
                await writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await output.WriteAsync(response + "\n").ConfigureAwait(false);
                    await output.FlushAsync().ConfigureAwait(false);
                }
                finally { writeLock.Release(); }
            }, CancellationToken.None);
            lock (inFlight)
            {
                inFlight.RemoveAll(t => t.IsCompleted);
                inFlight.Add(task);
            }
        }

        Task[] remaining;
        lock (inFlight) remaining = inFlight.ToArray();
        await Task.WhenAny(Task.WhenAll(remaining), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
    }

    /// <summary>Answers one line (a message or a batch). Null when nothing is to be sent back (notifications, responses).</summary>
    public async Task<string?> HandleLineAsync(string line, CancellationToken cancel)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException ex)
        {
            return Serialize(Error(null, McpProtocolException.ParseError, "Parse error: " + ex.Message));
        }

        if (node is JsonArray batch)
        {
            var responses = new JsonArray();
            foreach (var item in batch)
                if (item is JsonObject o && await HandleMessageAsync(o, cancel).ConfigureAwait(false) is { } r) responses.Add(r);
            return responses.Count == 0 ? null : Serialize(responses);
        }

        if (node is not JsonObject message) return Serialize(Error(null, McpProtocolException.InvalidRequest, "A JSON-RPC message must be an object."));
        return await HandleMessageAsync(message, cancel).ConfigureAwait(false) is { } response ? Serialize(response) : null;
    }

    public async Task<JsonObject?> HandleMessageAsync(JsonObject message, CancellationToken cancel)
    {
        var hasId = message.TryGetPropertyValue("id", out var idNode);
        var id = idNode?.DeepClone();
        var method = message["method"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m : null;
        if (method is null)
        {
            // A response to a request of ours (this server sends none), or garbage.
            return hasId && !message.ContainsKey("result") && !message.ContainsKey("error")
                ? Error(id, McpProtocolException.InvalidRequest, "Missing 'method'.")
                : null;
        }

        var parameters = message["params"] as JsonObject ?? new JsonObject();
        try
        {
            JsonNode? result;
            switch (method)
            {
                case "initialize": result = Initialize(parameters); break;
                case "ping": result = new JsonObject(); break;
                case "tools/list": result = new JsonObject { ["tools"] = new JsonArray(_tools.Select(t => (JsonNode)t.Describe()).ToArray()) }; break;
                case "tools/call": result = await CallToolAsync(parameters, cancel).ConfigureAwait(false); break;
                default:
                    if (!hasId || method.StartsWith("notifications/", StringComparison.Ordinal)) return null;
                    throw new McpProtocolException(McpProtocolException.MethodNotFound, $"Method not found: {method}");
            }

            return hasId ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result } : null;
        }
        catch (McpProtocolException ex)
        {
            return hasId ? Error(id, ex.Code, ex.Message) : null;
        }
    }

    private JsonObject Initialize(JsonObject p)
    {
        var requested = p["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        Protocol = requested is not null && SupportedVersions.Contains(requested) ? requested : SupportedVersions[0];
        if (p["clientInfo"]?["name"] is JsonValue cn && cn.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name))
        {
            ClientName = name.Trim();
            ClientIdentified?.Invoke(ClientName);
        }

        _log.WriteLine($"[mcp] initialize: client={ClientName ?? "?"} protocol={Protocol}");
        return new JsonObject
        {
            ["protocolVersion"] = Protocol,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["title"] = "IKEMEN Lab · Character X-Ray", ["version"] = _version },
            ["instructions"] = _instructions
        };
    }

    private async Task<JsonObject> CallToolAsync(JsonObject p, CancellationToken cancel)
    {
        var name = p["name"] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n : throw new McpProtocolException(McpProtocolException.InvalidParams, "tools/call needs 'name'.");
        var tool = _tools.FirstOrDefault(t => t.Name == name) ?? throw new McpProtocolException(McpProtocolException.InvalidParams, $"Unknown tool: {name}");
        var args = p["arguments"] switch
        {
            null => new JsonObject(),
            JsonObject o => o,
            _ => throw new McpProtocolException(McpProtocolException.InvalidParams, "'arguments' must be an object.")
        };

        JsonObject body;
        var isError = false;
        var started = DateTime.UtcNow;
        try
        {
            body = await tool.Handler(new ToolArgs(args), cancel).ConfigureAwait(false);
        }
        catch (ToolError ex)
        {
            isError = true;
            body = new JsonObject { ["error"] = ex.Message };
            if (ex.Hint is not null) body["hint"] = ex.Hint;
        }
        catch (Exception ex)
        {
            isError = true;
            _log.WriteLine($"[mcp] {name} failed: {ex}");
            body = new JsonObject { ["error"] = ex.Message };
        }

        _log.WriteLine($"[mcp] {name} {(DateTime.UtcNow - started).TotalMilliseconds:0} ms{(isError ? " (error)" : string.Empty)}");

        var result = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = body.ToJsonString(Indented) }),
            ["isError"] = isError
        };
        // Structured results exist from 2025-06-18 on; older clients get the same JSON as text only.
        if (string.CompareOrdinal(Protocol, "2025-06-18") >= 0) result["structuredContent"] = body.DeepClone();
        return result;
    }

    private static JsonNode? IdOf(string line)
    {
        try { return (JsonNode.Parse(line) as JsonObject)?["id"]?.DeepClone(); }
        catch (JsonException) { return null; }
    }

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static string Serialize(JsonNode node) => node.ToJsonString(Compact);
}
