using System.Globalization;
using System.Text;

namespace WireInspect.Mcp;

// Shared entry for `WireInspect --mcp-stdio` and `wireinspect mcp`: [--gui] [--port N] [--folder DIR].
public static class McpHost
{
    public const string Usage =
        "--mcp-stdio [--gui] [--port N] [--folder DIR]\n" +
        "  无 --gui：独立（headless）检测会话，通过 stdio 提供 MCP 服务\n" +
        "  --gui   ：把 stdio 转发到正在运行的 WireInspect 窗口的 MCP 服务（http://127.0.0.1:N/mcp，默认端口 3918）";

    public static int RunStdio(IReadOnlyList<string> args, string version)
    {
        bool gui = args.Contains("--gui", StringComparer.OrdinalIgnoreCase);
        int port = int.TryParse(Value(args, "--port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : HttpTransport.DefaultPort;
        string? folder = Value(args, "--folder");
        var utf8 = new UTF8Encoding(false);
        var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
        var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };
        var stderr = TextWriter.Synchronized(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        // stdout carries protocol frames only; stray Console output goes to stderr (the client's server log).
        Console.SetOut(stderr);
        try
        {
            if (gui)
            {
                stderr.WriteLine($"wireinspect mcp: bridging stdio to http://127.0.0.1:{port}/mcp");
                StdioHttpProxy.RunAsync($"http://127.0.0.1:{port}/mcp", stdin, stdout).GetAwaiter().GetResult();
                return 0;
            }
            var session = new HeadlessSession { Echo = stderr.WriteLine };
            if (folder != null)
            {
                var info = session.OpenFolderAsync(folder).GetAwaiter().GetResult();
                stderr.WriteLine($"wireinspect mcp: {info["files"]} images in {folder}");
            }
            var server = WireInspectTools.CreateServer(session, version);
            stderr.WriteLine("wireinspect mcp: headless server on stdio");
            StdioTransport.RunAsync(server, stdin, stdout).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine("wireinspect mcp: " + ex.Message);
            return 1;
        }
    }

    static string? Value(IReadOnlyList<string> args, string name)
    {
        for (int i = 0; i + 1 < args.Count; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
