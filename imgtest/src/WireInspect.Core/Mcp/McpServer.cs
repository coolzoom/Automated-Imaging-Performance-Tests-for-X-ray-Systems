using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WireInspect.Mcp;

public sealed record McpToolResult(string Text, JsonNode? Structured = null, bool IsError = false, byte[]? Png = null)
{
    public static McpToolResult Error(string text) => new(text, null, true);
}

public sealed record McpTool(string Name, string Description, JsonObject InputSchema, Func<JsonObject, McpToolResult> Run);

// Model Context Protocol server core: JSON-RPC 2.0 over any transport, tools capability only.
public sealed class McpServer
{
    public const string ProtocolVersion = "2025-06-18";
    static readonly string[] SupportedVersions = [ProtocolVersion, "2025-03-26", "2024-11-05"];

    public static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    readonly Dictionary<string, McpTool> _tools;
    readonly string _name, _version, _instructions;

    public McpServer(IEnumerable<McpTool> tools, string name = "wireinspect", string version = "1.0.0", string instructions = "")
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _name = name;
        _version = version;
        _instructions = instructions;
    }

    public IReadOnlyCollection<McpTool> Tools => _tools.Values;

    // Raised on the calling thread after each tools/call with the tool name and whether it failed.
    public event Action<string, bool>? ToolCalled;

    // Returns the serialized response, or null for notifications and responses sent by the client.
    public string? Handle(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException ex) { return Serialize(ErrorResponse(null, -32700, "Parse error: " + ex.Message)); }
        if (node is not JsonObject msg)
            return Serialize(ErrorResponse(null, -32600, "Invalid Request: expected a JSON-RPC object"));
        var response = HandleMessage(msg);
        return response == null ? null : Serialize(response);
    }

    public static string Serialize(JsonNode node) => node.ToJsonString(Json);

    JsonObject? HandleMessage(JsonObject msg)
    {
        string? method = msg["method"]?.GetValue<string>();
        bool hasId = msg.ContainsKey("id");
        var id = msg["id"]?.DeepClone();
        if (method == null)
            return hasId && msg["result"] == null && msg["error"] == null ? ErrorResponse(id, -32600, "Invalid Request: missing method") : null;
        if (!hasId)
            return null;
        var p = msg["params"] as JsonObject ?? new JsonObject();
        try
        {
            return method switch
            {
                "initialize" => Result(id, Initialize(p)),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject { ["tools"] = new JsonArray(_tools.Values.Select(Describe).ToArray<JsonNode?>()) }),
                "tools/call" => CallTool(id, p),
                "resources/list" => Result(id, new JsonObject { ["resources"] = new JsonArray() }),
                "prompts/list" => Result(id, new JsonObject { ["prompts"] = new JsonArray() }),
                _ => ErrorResponse(id, -32601, "Method not found: " + method)
            };
        }
        catch (Exception ex)
        {
            return ErrorResponse(id, -32603, "Internal error: " + ex.Message);
        }
    }

    JsonObject Initialize(JsonObject p)
    {
        string requested = p["protocolVersion"]?.GetValue<string>() ?? ProtocolVersion;
        var result = new JsonObject
        {
            ["protocolVersion"] = SupportedVersions.Contains(requested) ? requested : ProtocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = _name, ["title"] = "WireInspect X-ray 焊线检测", ["version"] = _version }
        };
        if (_instructions.Length > 0)
            result["instructions"] = _instructions;
        return result;
    }

    static JsonObject Describe(McpTool t) => new()
    {
        ["name"] = t.Name,
        ["description"] = t.Description,
        ["inputSchema"] = t.InputSchema.DeepClone()
    };

    JsonObject CallTool(JsonNode? id, JsonObject p)
    {
        string? name = p["name"]?.GetValue<string>();
        if (name == null || !_tools.TryGetValue(name, out var tool))
            return ErrorResponse(id, -32602, "Unknown tool: " + name);
        var args = p["arguments"] as JsonObject ?? new JsonObject();
        McpToolResult r;
        try { r = tool.Run(args); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { r = McpToolResult.Error(ex.Message); }
        ToolCalled?.Invoke(name, r.IsError);
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = r.Text });
        if (r.Png != null)
            content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(r.Png), ["mimeType"] = "image/png" });
        var result = new JsonObject
        {
            ["content"] = content,
            ["isError"] = r.IsError
        };
        if (r.Structured is JsonObject structured)
            result["structuredContent"] = structured.DeepClone();
        return Result(id, result);
    }

    static JsonObject Result(JsonNode? id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    public static JsonObject ErrorResponse(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };
}
