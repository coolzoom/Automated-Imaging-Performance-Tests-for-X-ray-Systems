using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WireInspect.Mcp;

public enum AgentKind { Cursor, VSCode, ClaudeCode, ClaudeDesktop, Codex, Trae, TraeCN, WorkBuddy }

public enum AgentConfigState { NotInstalled, NotConfigured, Configured, Outdated }

// Writes the two WireInspect MCP entries into each agent's user-level config: "wireinspect" drives the folder open in the GUI,
// "wireinspect-headless" starts a private headless inspector over stdio. Existing content is merged, never replaced,
// and the previous file is kept as <file>.wireinspect.bak before every change.
public sealed class AgentConfigurator
{
    public const string GuiName = "wireinspect";
    public const string HeadlessName = "wireinspect-headless";
    public const string BackupSuffix = ".wireinspect.bak";

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    readonly string _home, _appData;

    public AgentConfigurator(string command, IReadOnlyList<string> headlessArgs, IReadOnlyList<string> bridgeArgs, int port,
        string? home = null, string? appData = null)
    {
        Command = command;
        HeadlessArgs = headlessArgs;
        BridgeArgs = bridgeArgs;
        Port = port;
        _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _appData = appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    }

    // For an executable that understands `--mcp-stdio [--gui] [--port N]` (WireInspect.App).
    public static AgentConfigurator ForExecutable(string exe, int port, string? home = null, string? appData = null) =>
        new(exe, ["--mcp-stdio"], ["--mcp-stdio", "--gui", "--port", port.ToString()], port, home, appData);

    /// <summary>Entry points for the running process: under `dotnet run` agents must invoke
    /// `dotnet <app.dll> --mcp-stdio ...`; a published binary is invoked directly.</summary>
    public static AgentConfigurator ForCurrentProcess(int port, string? home = null, string? appData = null)
    {
        var arg0 = Environment.GetCommandLineArgs().FirstOrDefault() ?? "";
        if (arg0.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return new AgentConfigurator("dotnet", [arg0, "--mcp-stdio"],
                [arg0, "--mcp-stdio", "--gui", "--port", port.ToString()], port, home, appData);
        return ForExecutable(Environment.ProcessPath ?? arg0, port, home, appData);
    }

    public string Command { get; }
    public IReadOnlyList<string> HeadlessArgs { get; }
    public IReadOnlyList<string> BridgeArgs { get; }
    public int Port { get; }
    public string GuiUrl => $"http://127.0.0.1:{Port}/mcp";

    public static IReadOnlyList<AgentKind> All { get; } = Enum.GetValues<AgentKind>();

    public static string Label(AgentKind kind) => kind switch
    {
        AgentKind.Cursor => "Cursor",
        AgentKind.VSCode => "VS Code (Copilot)",
        AgentKind.ClaudeCode => "Claude Code",
        AgentKind.ClaudeDesktop => "Claude Desktop",
        AgentKind.Codex => "Codex",
        AgentKind.Trae => "Trae",
        AgentKind.TraeCN => "Trae CN",
        AgentKind.WorkBuddy => "WorkBuddy",
        _ => kind.ToString()
    };

    public string ConfigPath(AgentKind kind) => kind switch
    {
        AgentKind.Cursor => Path.Combine(_home, ".cursor", "mcp.json"),
        AgentKind.VSCode => Path.Combine(_appData, "Code", "User", "mcp.json"),
        AgentKind.ClaudeCode => Path.Combine(_home, ".claude.json"),
        AgentKind.ClaudeDesktop => Path.Combine(_appData, "Claude", "claude_desktop_config.json"),
        AgentKind.Codex => Path.Combine(_home, ".codex", "config.toml"),
        // Trae keeps user MCP servers next to its VS Code style settings (%APPDATA%\Trae\User, ~/Library/Application Support/Trae/User).
        AgentKind.Trae => Path.Combine(_appData, "Trae", "User", "mcp.json"),
        AgentKind.TraeCN => Path.Combine(_appData, "Trae CN", "User", "mcp.json"),
        // WorkBuddy user-level config (docs: ~/.workbuddy/mcp.json).
        AgentKind.WorkBuddy => Path.Combine(_home, ".workbuddy", "mcp.json"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    // Whether the agent appears installed for this user (its config folder exists).
    public bool IsInstalled(AgentKind kind) => File.Exists(ConfigPath(kind)) || kind switch
    {
        AgentKind.Cursor => Directory.Exists(Path.Combine(_home, ".cursor")),
        AgentKind.VSCode => Directory.Exists(Path.Combine(_appData, "Code")),
        AgentKind.ClaudeCode => Directory.Exists(Path.Combine(_home, ".claude")),
        AgentKind.ClaudeDesktop => Directory.Exists(Path.Combine(_appData, "Claude")),
        AgentKind.Codex => Directory.Exists(Path.Combine(_home, ".codex")),
        AgentKind.Trae => Directory.Exists(Path.Combine(_appData, "Trae")) || Directory.Exists(Path.Combine(_home, ".trae")),
        AgentKind.TraeCN => Directory.Exists(Path.Combine(_appData, "Trae CN")) || Directory.Exists(Path.Combine(_home, ".trae-cn")),
        AgentKind.WorkBuddy => Directory.Exists(Path.Combine(_home, ".workbuddy")),
        _ => false
    };

    public AgentConfigState Status(AgentKind kind)
    {
        string path = ConfigPath(kind);
        if (!File.Exists(path))
            return IsInstalled(kind) ? AgentConfigState.NotConfigured : AgentConfigState.NotInstalled;
        if (kind == AgentKind.Codex)
        {
            string text = File.ReadAllText(path);
            var gui = TomlTable(text, GuiName);
            var headless = TomlTable(text, HeadlessName);
            if (gui == null && headless == null)
                return AgentConfigState.NotConfigured;
            return gui == TomlBody(BridgeArgs) && headless == TomlBody(HeadlessArgs) ? AgentConfigState.Configured : AgentConfigState.Outdated;
        }
        var servers = ReadJson(path)[ServersKey(kind)] as JsonObject;
        var (guiEntry, headlessEntry) = Entries(kind);
        var g = servers?[GuiName];
        var h = servers?[HeadlessName];
        if (g == null && h == null)
            return AgentConfigState.NotConfigured;
        return JsonNode.DeepEquals(g, guiEntry) && JsonNode.DeepEquals(h, headlessEntry) ? AgentConfigState.Configured : AgentConfigState.Outdated;
    }

    // Adds or updates both entries; returns false when the file already had them exactly (nothing written).
    public bool Configure(AgentKind kind)
    {
        if (Status(kind) == AgentConfigState.Configured)
            return false;
        string path = ConfigPath(kind);
        if (kind == AgentKind.Codex)
        {
            string text = File.Exists(path) ? File.ReadAllText(path) : "";
            text = RemoveTomlTable(RemoveTomlTable(text, GuiName), HeadlessName).TrimEnd();
            var sb = new StringBuilder(text);
            if (text.Length > 0)
                sb.Append("\n\n");
            sb.Append($"[mcp_servers.{GuiName}]\n{TomlBody(BridgeArgs)}\n\n[mcp_servers.{HeadlessName}]\n{TomlBody(HeadlessArgs)}\n");
            Save(path, sb.ToString());
            return true;
        }
        var root = File.Exists(path) ? ReadJson(path) : new JsonObject();
        string key = ServersKey(kind);
        if (root[key] is not JsonObject servers)
            root[key] = servers = new JsonObject();
        var (gui, headless) = Entries(kind);
        servers[GuiName] = gui;
        servers[HeadlessName] = headless;
        Save(path, root.ToJsonString(Indented) + "\n");
        return true;
    }

    // Removes both entries; returns false when neither was present.
    public bool Remove(AgentKind kind)
    {
        string path = ConfigPath(kind);
        if (!File.Exists(path))
            return false;
        if (kind == AgentKind.Codex)
        {
            string text = File.ReadAllText(path);
            string stripped = RemoveTomlTable(RemoveTomlTable(text, GuiName), HeadlessName);
            if (stripped == text)
                return false;
            Save(path, stripped.TrimEnd() + (stripped.Trim().Length > 0 ? "\n" : ""));
            return true;
        }
        var root = ReadJson(path);
        if (root[ServersKey(kind)] is not JsonObject servers || !(servers.Remove(GuiName) | servers.Remove(HeadlessName)))
            return false;
        Save(path, root.ToJsonString(Indented) + "\n");
        return true;
    }

    static string ServersKey(AgentKind kind) => kind == AgentKind.VSCode ? "servers" : "mcpServers";

    JsonArray Args(IReadOnlyList<string> args) => new(args.Select(a => (JsonNode?)a).ToArray());

    (JsonObject Gui, JsonObject Headless) Entries(AgentKind kind) => kind switch
    {
        AgentKind.Cursor => (
            new JsonObject { ["url"] = GuiUrl },
            new JsonObject { ["command"] = Command, ["args"] = Args(HeadlessArgs) }),
        AgentKind.VSCode or AgentKind.ClaudeCode => (
            new JsonObject { ["type"] = "http", ["url"] = GuiUrl },
            new JsonObject { ["type"] = "stdio", ["command"] = Command, ["args"] = Args(HeadlessArgs) }),
        // Stdio only: the GUI entry goes through the stdio-to-HTTP bridge, which every client supports.
        AgentKind.ClaudeDesktop or AgentKind.Trae or AgentKind.TraeCN or AgentKind.WorkBuddy => (
            new JsonObject { ["command"] = Command, ["args"] = Args(BridgeArgs) },
            new JsonObject { ["command"] = Command, ["args"] = Args(HeadlessArgs) }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    static JsonObject ReadJson(string path)
    {
        string text = File.ReadAllText(path);
        if (text.Trim().Length == 0)
            return new JsonObject();
        try
        {
            return JsonNode.Parse(text, documentOptions: Lenient) as JsonObject ??
                   throw new InvalidDataException($"{path} 的顶层不是 JSON 对象");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} 不是有效的 JSON，未做修改：{ex.Message}", ex);
        }
    }

    static void Save(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // The first backup is kept: it is the user's config from before WireInspect ever touched it.
        if (File.Exists(path) && !File.Exists(path + BackupSuffix))
            File.Copy(path, path + BackupSuffix);
        string temp = path + ".wireinspect.tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    // ---------------- TOML (Codex) ----------------

    string TomlBody(IReadOnlyList<string> args) =>
        $"command = {TomlString(Command)}\nargs = [{string.Join(", ", args.Select(TomlString))}]";

    static string TomlString(string s) =>
        s.Contains('\'') || s.Contains('\n')
            ? "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\""
            : "'" + s + "'";

    static bool IsHeader(string line, string name)
    {
        string t = line.Trim();
        return t == $"[mcp_servers.{name}]" || t == $"[mcp_servers.\"{name}\"]" || t == $"[mcp_servers.'{name}']";
    }

    static bool IsAnyHeader(string line) => line.TrimStart().StartsWith('[');

    // Body of [mcp_servers.<name>] with blank lines and comments dropped, or null when absent.
    static string? TomlTable(string text, string name)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int start = Array.FindIndex(lines, l => IsHeader(l, name));
        if (start < 0)
            return null;
        var body = lines.Skip(start + 1).TakeWhile(l => !IsAnyHeader(l))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));
        return string.Join("\n", body);
    }

    static string RemoveTomlTable(string text, string name)
    {
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        int start = lines.FindIndex(l => IsHeader(l, name));
        if (start < 0)
            return text;
        int end = start + 1;
        while (end < lines.Count && !IsAnyHeader(lines[end]))
            end++;
        // Sub-tables such as [mcp_servers.wireinspect.env] belong to the entry too.
        while (end < lines.Count && lines[end].Trim().StartsWith($"[mcp_servers.{name}.", StringComparison.Ordinal))
        {
            end++;
            while (end < lines.Count && !IsAnyHeader(lines[end]))
                end++;
        }
        lines.RemoveRange(start, end - start);
        return string.Join(nl, lines);
    }
}
