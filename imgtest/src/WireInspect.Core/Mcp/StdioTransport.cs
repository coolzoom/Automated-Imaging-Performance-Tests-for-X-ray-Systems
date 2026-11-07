using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WireInspect.Mcp;

// MCP stdio transport: one JSON-RPC message per line on stdin/stdout. Requests are handled concurrently so
// wireinspect_stop and wireinspect_get_status still answer while another call waits on a detection job.
public static class StdioTransport
{
    public static async Task RunAsync(McpServer server, TextReader input, TextWriter output, CancellationToken ct = default) =>
        await Pump(input, output, line => Task.FromResult(server.Handle(line)), ct);

    internal static async Task Pump(TextReader input, TextWriter output, Func<string, Task<string?>> handle, CancellationToken ct)
    {
        var writeLock = new SemaphoreSlim(1, 1);
        var pending = new List<Task>();
        while (await input.ReadLineAsync(ct) is { } line)
        {
            if (line.Trim().Length == 0)
                continue;
            pending.RemoveAll(t => t.IsCompleted);
            pending.Add(Task.Run(async () =>
            {
                string? response;
                try { response = await handle(line); }
                catch (Exception ex) { response = McpServer.Serialize(McpServer.ErrorResponse(null, -32603, ex.Message)); }
                if (response == null)
                    return;
                await writeLock.WaitAsync(ct);
                try
                {
                    await output.WriteAsync(response + "\n");
                    await output.FlushAsync(ct);
                }
                finally { writeLock.Release(); }
            }, ct));
        }
        await Task.WhenAll(pending);
    }
}

// Bridges a stdio-only MCP client (Claude Desktop, Codex) to the GUI's HTTP endpoint.
public static class StdioHttpProxy
{
    public static string Unavailable(string url) =>
        $"无法连接 WireInspect 的 MCP 服务 {url}：请先启动 WireInspect.App，并在「工具 ▸ MCP 服务」中开启服务（或改用 wireinspect-headless）。";

    public static async Task RunAsync(string url, TextReader input, TextWriter output, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromHours(1) };
        string? session = null;
        await StdioTransport.Pump(input, output, async line =>
        {
            JsonNode? id = null;
            bool request = false;
            try
            {
                if (JsonNode.Parse(line) is JsonObject msg)
                {
                    request = msg["method"] != null && msg.ContainsKey("id");
                    id = msg["id"]?.DeepClone();
                }
            }
            catch (JsonException) { }
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(line, Encoding.UTF8, "application/json") };
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                if (session != null)
                    req.Headers.Add("Mcp-Session-Id", session);
                using var resp = await http.SendAsync(req, ct);
                if (resp.Headers.TryGetValues("Mcp-Session-Id", out var ids))
                    session = ids.FirstOrDefault() ?? session;
                string body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    return request ? Fail(id, $"HTTP {(int)resp.StatusCode}: {body}") : null;
                if (body.Trim().Length == 0)
                    return null;
                if (resp.Content.Headers.ContentType?.MediaType == "text/event-stream")
                    body = string.Join("\n", body.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l[5..].Trim()));
                return string.Join("\n", body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => JsonNode.Parse(l)!.ToJsonString(McpServer.Json)));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                return request ? Fail(id, Unavailable(url)) : null;
            }
        }, ct);
    }

    static string Fail(JsonNode? id, string message) => McpServer.Serialize(McpServer.ErrorResponse(id, -32000, message));
}
