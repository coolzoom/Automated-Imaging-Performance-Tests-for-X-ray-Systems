using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WireInspect.Core;
using WireInspect.Mcp;

namespace WireInspect.Tests;

public class McpProtocolTests
{
    static JsonObject Call(McpServer server, string method, JsonObject? p = null, int id = 1)
    {
        var req = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (p != null) req["params"] = p;
        return JsonNode.Parse(server.Handle(req.ToJsonString())!)!.AsObject();
    }

    [Fact]
    public void Initialize_Ping_ToolsList_ToolCall()
    {
        var server = WireInspectTools.CreateServer(new HeadlessSession());
        var init = Call(server, "initialize", new JsonObject
        {
            ["protocolVersion"] = McpServer.ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "0" },
        });
        Assert.Equal("wireinspect", init["result"]?["serverInfo"]?["name"]?.GetValue<string>());
        Assert.Equal(McpServer.ProtocolVersion, init["result"]?["protocolVersion"]?.GetValue<string>());
        Assert.NotNull(init["result"]?["instructions"]?.GetValue<string>());

        Assert.NotNull(Call(server, "ping")["result"]);

        var tools = Call(server, "tools/list")["result"]!["tools"]!.AsArray();
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("wireinspect_detect", names);
        Assert.Contains("wireinspect_set_product_params", names);
        Assert.Contains("wireinspect_add_defect", names);
        Assert.True(names.Count >= 20);
        var detect = tools.Single(t => t!["name"]!.GetValue<string>() == "wireinspect_detect")!;
        Assert.Equal("object", detect["inputSchema"]?["type"]?.GetValue<string>());

        // calling a tool without a folder must fail cleanly, not crash the server
        var bad = Call(server, "tools/call", new JsonObject { ["name"] = "wireinspect_get_status" });
        Assert.NotNull(bad["result"]);   // get_status works even with no folder
        var err = Call(server, "tools/call", new JsonObject
        {
            ["name"] = "wireinspect_get_result",
            ["arguments"] = new JsonObject(),
        });
        Assert.True(err["result"]?["isError"]?.GetValue<bool>() == true);

        var unknown = Call(server, "tools/call", new JsonObject { ["name"] = "nope" });
        Assert.Equal(-32602, unknown["error"]?["code"]?.GetValue<int>());

        var noMethod = Call(server, "bogus/method");
        Assert.Equal(-32601, noMethod["error"]?["code"]?.GetValue<int>());

        // notifications get no response at all; malformed JSON gets a -32700 parse error
        Assert.Null(server.Handle("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));
        var parse = JsonNode.Parse(server.Handle("not json")!)!;
        Assert.Equal(-32700, parse["error"]?["code"]?.GetValue<int>());
    }

    [Fact]
    public async Task Stdio_RoundTrip()
    {
        var server = WireInspectTools.CreateServer(new HeadlessSession());
        var input = new StringReader(
            "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"0\"}}}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/call\",\"params\":{\"name\":\"wireinspect_get_status\",\"arguments\":{}}}\n");
        var output = new StringWriter();
        await StdioTransport.RunAsync(server, input, output);
        var msgs = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        Assert.Equal(2, msgs.Count);   // requests are handled concurrently, so match by id not by order
        var init = msgs.Single(m => m["id"]?.GetValue<int>() == 7);
        Assert.Equal("wireinspect", init["result"]?["serverInfo"]?["name"]?.GetValue<string>());
        var call = msgs.Single(m => m["id"]?.GetValue<int>() == 8);
        Assert.Equal("headless", call["result"]?["structuredContent"]?["mode"]?.GetValue<string>());
    }

    [Fact]
    public async Task Http_Initialize_And_ToolCall()
    {
        var session = new HeadlessSession();
        using var transport = new HttpTransport(WireInspectTools.CreateServer(session), 0);
        transport.Start();
        Assert.True(transport.Running);
        Assert.True(transport.Port > 0);

        using var http = new HttpClient();
        var resp = await http.PostAsJsonAsync(transport.Url, new
        {
            jsonrpc = "2.0", id = 1, method = "initialize",
            @params = new { protocolVersion = McpServer.ProtocolVersion, capabilities = new { }, clientInfo = new { name = "t", version = "0" } },
        });
        Assert.True(resp.IsSuccessStatusCode);
        var sessionId = resp.Headers.GetValues("Mcp-Session-Id").Single();
        var init = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        Assert.Equal("wireinspect", init["result"]?["serverInfo"]?["name"]?.GetValue<string>());

        var req = new HttpRequestMessage(HttpMethod.Post, transport.Url)
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "wireinspect_get_status", arguments = new { } } }),
        };
        req.Headers.Add("Mcp-Session-Id", sessionId);
        var call = await http.SendAsync(req);
        var body = JsonNode.Parse(await call.Content.ReadAsStringAsync())!;
        Assert.Equal("headless", body["result"]?["structuredContent"]?["mode"]?.GetValue<string>());

        // notifications: 202, no body
        var notif = await http.PostAsJsonAsync(transport.Url, new { jsonrpc = "2.0", method = "notifications/initialized" });
        Assert.Equal(System.Net.HttpStatusCode.Accepted, notif.StatusCode);
        Assert.Equal("", await notif.Content.ReadAsStringAsync());
    }
}

public class McpHeadlessTests : IDisposable
{
    readonly TempDir _home = new(), _folder = new();
    readonly string _prevHome;

    public McpHeadlessTests()
    {
        _prevHome = Environment.GetEnvironmentVariable("WIREINSPECT_HOME") ?? "";
        Environment.SetEnvironmentVariable("WIREINSPECT_HOME", _home.Path);
        using var good = Synthetic.Make();
        var product = Synthetic.MakeProduct(good);
        product.Name = "mcp_test";
        product.Save(Path.Combine(_home.Path, "products"));
        using (var g = Synthetic.Make()) ImageIO.WritePng(Path.Combine(_folder.Path, "ok.png"), g);
        using (var g = Synthetic.Make(new Synthetic.Opts { Missing = (0, 0) })) ImageIO.WritePng(Path.Combine(_folder.Path, "bad.png"), g);
    }

    public void Dispose() => Environment.SetEnvironmentVariable("WIREINSPECT_HOME", _prevHome);

    [Fact]
    public void Full_Workflow_Over_Tools()
    {
        var s = new HeadlessSession();
        var server = WireInspectTools.CreateServer(s);

        JsonObject Tool(string name, JsonObject? args = null)
        {
            var req = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = name, ["arguments"] = args ?? new JsonObject() },
            };
            var resp = JsonNode.Parse(server.Handle(req.ToJsonString())!)!.AsObject();
            var result = resp["result"]!.AsObject();
            Assert.True(result["isError"]?.GetValue<bool>() != true, result.ToJsonString());
            return result["structuredContent"]!.AsObject();
        }

        var open = Tool("wireinspect_open_folder", new JsonObject { ["path"] = _folder.Path });
        Assert.Equal(2, open["files"]?.GetValue<int>());

        Assert.Equal("mcp_test", Tool("wireinspect_select_product", new JsonObject { ["name"] = "mcp_test" })["product"]?.GetValue<string>());

        var det = Tool("wireinspect_detect", new JsonObject { ["all"] = false, ["wait_seconds"] = 30 });
        Assert.Equal(false, det["running"]?.GetValue<bool>());

        var bad = Tool("wireinspect_get_result", new JsonObject { ["file"] = "bad.png" });
        Assert.Equal(9, bad["units"]!.AsArray().Count);
        Assert.Contains(bad["wires"]!.AsArray().OfType<JsonObject>(), w => w["flags"]!.AsArray().Any(f => f!.GetValue<string>() == "MISSING"));
        Assert.True(bad["defects"]!.AsArray().Count >= 1);
        var defectId = bad["defects"]![0]!["id"]!.GetValue<string>();

        var ok = Tool("wireinspect_get_result", new JsonObject { ["file"] = "ok.png" });
        Assert.Empty(ok["defects"]!.AsArray());

        // reject the auto defect → image becomes ok
        Tool("wireinspect_set_defect_status", new JsonObject { ["id"] = defectId, ["status"] = "rejected" });
        // manual defect box → suspect again
        var manual = Tool("wireinspect_add_defect", new JsonObject
        {
            ["file"] = "bad.png", ["x0"] = 10, ["y0"] = 10, ["x1"] = 40, ["y1"] = 40, ["type"] = "断线",
        });
        Tool("wireinspect_change_defect_type", new JsonObject { ["id"] = manual["id"]!.GetValue<string>(), ["type"] = "缺线" });
        Assert.Equal(1, Tool("wireinspect_get_status")["counts"]?["suspect"]?.GetValue<int>());

        var verdict = Tool("wireinspect_set_verdict", new JsonObject { ["file"] = "bad.png", ["verdict"] = "FAIL", ["comment"] = "断线复检" });
        Assert.Equal("FAIL", verdict["verdict"]?.GetValue<string>());

        var commentOnly = Tool("wireinspect_set_verdict", new JsonObject { ["file"] = "bad.png", ["comment"] = "备注2" });
        Assert.Equal("FAIL", commentOnly["verdict"]?.GetValue<string>());
        Assert.Equal("备注2", commentOnly["comment"]?.GetValue<string>());

        var files = Tool("wireinspect_list_images")["images"]!.AsArray().Select(i => i!["file"]!.GetValue<string>()).ToList();
        Assert.Equal(["bad.png", "ok.png"], files);

        var report = Tool("wireinspect_save_report", new JsonObject { ["path"] = Path.Combine(_folder.Path, "r.csv") });
        Assert.True(File.Exists(report["summary_csv"]!.GetValue<string>()));
        Assert.True(File.Exists(report["wires_csv"]!.GetValue<string>()));

        // session.json persisted: a fresh session sees the same verdict
        var s2 = new Session(_folder.Path);
        Assert.Equal(Session.Fail, s2.Status("bad.png"));
        Assert.Equal("缺线", s2.Rec("bad.png").Defects.Single(d => d.Source == "manual").Type);
    }

    [Fact]
    public async Task Product_Params_And_AutoWires()
    {
        var s = new HeadlessSession();
        await s.OpenFolderAsync(_folder.Path);
        s.SelectProduct("mcp_test");

        var p = s.GetProduct();
        Assert.Equal("mcp_test", p["name"]?.GetValue<string>());
        var patched = s.SetProductParams(new JsonObject { ["shift_max_px"] = 2.0, ["sweep_max_pct"] = 4 });
        Assert.Equal(2.0, patched["shift_max_px"]?.GetValue<double>());
        // persisted to product.json
        var onDisk = Product.Load(Path.Combine(_home.Path, "products", "mcp_test"));
        Assert.Equal(2.0, onDisk.ShiftMaxPx);

        var wires = s.AutoWires(0, 0, Synthetic.UnitW, Synthetic.UnitH, "ok.png", null);
        Assert.True(wires["count"]!.GetValue<int>() >= 1);
    }
}

public class AgentConfiguratorTests
{
    [Fact]
    public void Json_Merge_Backup_And_Status()
    {
        using var home = new TempDir();
        using var appData = new TempDir();
        var cfg = AgentConfigurator.ForExecutable("/opt/wireinspect/WireInspect.App", 3918, home.Path, appData.Path);
        var cursorDir = Path.Combine(home.Path, ".cursor");
        Directory.CreateDirectory(cursorDir);
        var path = cfg.ConfigPath(AgentKind.Cursor);
        File.WriteAllText(path, """
            {
              "mcpServers": {
                "other-tool": { "command": "/usr/bin/other" },
                "wireinspect": { "url": "http://127.0.0.1:9999/mcp" }
              },
              "unrelated": { "keep": "me" }
            }
            """);

        Assert.Equal(AgentConfigState.Outdated, cfg.Status(AgentKind.Cursor));
        Assert.True(cfg.Configure(AgentKind.Cursor));
        Assert.Equal(AgentConfigState.Configured, cfg.Status(AgentKind.Cursor));
        Assert.False(cfg.Configure(AgentKind.Cursor));   // already exact → no write

        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal("/usr/bin/other", root["mcpServers"]?["other-tool"]?["command"]?.GetValue<string>());
        Assert.Equal("http://127.0.0.1:3918/mcp", root["mcpServers"]?["wireinspect"]?["url"]?.GetValue<string>());
        Assert.Equal("/opt/wireinspect/WireInspect.App", root["mcpServers"]?["wireinspect-headless"]?["command"]?.GetValue<string>());
        Assert.Equal("me", root["unrelated"]?["keep"]?.GetValue<string>());
        Assert.True(File.Exists(path + AgentConfigurator.BackupSuffix));
        Assert.Contains("9999", File.ReadAllText(path + AgentConfigurator.BackupSuffix));

        Assert.True(cfg.Remove(AgentKind.Cursor));
        root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Null(root["mcpServers"]?["wireinspect"]);
        Assert.NotNull(root["mcpServers"]?["other-tool"]);
        Assert.False(cfg.Remove(AgentKind.Cursor));
    }

    [Fact]
    public void Codex_Toml_RoundTrip()
    {
        using var home = new TempDir();
        using var appData = new TempDir();
        var cfg = AgentConfigurator.ForExecutable("/opt/wi/WireInspect App", 3918, home.Path, appData.Path);
        var path = cfg.ConfigPath(AgentKind.Codex);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "model = 'gpt-5'\n\n[mcp_servers.other]\ncommand = 'x'\n");

        Assert.Equal(AgentConfigState.NotConfigured, cfg.Status(AgentKind.Codex));
        cfg.Configure(AgentKind.Codex);
        Assert.Equal(AgentConfigState.Configured, cfg.Status(AgentKind.Codex));

        var text = File.ReadAllText(path);
        Assert.Contains("model = 'gpt-5'", text);
        Assert.Contains("[mcp_servers.other]", text);
        Assert.Contains("[mcp_servers.wireinspect]", text);
        Assert.Contains("[mcp_servers.wireinspect-headless]", text);
        Assert.Contains("'--gui'", text);

        cfg.Remove(AgentKind.Codex);
        text = File.ReadAllText(path);
        Assert.DoesNotContain("wireinspect", text);
        Assert.Contains("[mcp_servers.other]", text);
    }

    [Fact]
    public void Invalid_Json_Is_Refused_Untouched()
    {
        using var home = new TempDir();
        using var appData = new TempDir();
        var cfg = AgentConfigurator.ForExecutable("/opt/wi/x", 3918, home.Path, appData.Path);
        var path = cfg.ConfigPath(AgentKind.ClaudeCode);
        File.WriteAllText(path, "{ broken json !!!");
        Assert.Throws<InvalidDataException>(() => cfg.Configure(AgentKind.ClaudeCode));
        Assert.Equal("{ broken json !!!", File.ReadAllText(path));
        Assert.False(File.Exists(path + AgentConfigurator.BackupSuffix));
    }

    [Fact]
    public void Devin_McpConfig_Uses_Url_And_Command()
    {
        using var home = new TempDir();
        using var appData = new TempDir();
        var cfg = AgentConfigurator.ForExecutable("/opt/wi/WireInspect", 3918, home.Path, appData.Path);
        var path = cfg.ConfigPath(AgentKind.Devin);
        if (OperatingSystem.IsWindows())
            Assert.Equal(Path.Combine(appData.Path, "devin", "mcp_config.json"), path);
        else
            Assert.Equal(Path.Combine(home.Path, ".config", "devin", "mcp_config.json"), path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        cfg.Configure(AgentKind.Devin);
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal("http://127.0.0.1:3918/mcp", root["mcpServers"]?["wireinspect"]?["url"]?.GetValue<string>());
        var args = root["mcpServers"]!["wireinspect-headless"]!["args"]!.AsArray().Select(a => a!.GetValue<string>());
        Assert.Contains("--mcp-stdio", args);
    }

    [Fact]
    public void Stdio_Only_Agents_Bridge_Through_Dotnet()
    {
        using var home = new TempDir();
        using var appData = new TempDir();
        var cfg = new AgentConfigurator("dotnet", ["/x/app.dll", "--mcp-stdio"],
            ["/x/app.dll", "--mcp-stdio", "--gui", "--port", "3918"], 3918, home.Path, appData.Path);
        cfg.Configure(AgentKind.ClaudeDesktop);
        var root = JsonNode.Parse(File.ReadAllText(cfg.ConfigPath(AgentKind.ClaudeDesktop)))!.AsObject();
        var gui = root["mcpServers"]?["wireinspect"];
        Assert.Equal("dotnet", gui?["command"]?.GetValue<string>());
        Assert.Contains("--gui", gui!["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        var headless = root["mcpServers"]?["wireinspect-headless"];
        Assert.Equal("dotnet", headless?["command"]?.GetValue<string>());
    }
}
