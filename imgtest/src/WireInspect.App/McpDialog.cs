using System.Net.Http;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using WireInspect.Core;
using WireInspect.Mcp;

namespace WireInspect.App;

/// <summary>MCP server controls plus one-click agent configuration: adds "wireinspect" (this GUI, via HTTP or
/// the stdio bridge) and "wireinspect-headless" (private stdio session) to every installed agent's config.</summary>
public sealed class McpDialog : Window
{
    static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#8fa8c8"));
    static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#3fd68f"));
    static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#ffb020"));

    readonly MainWindow _w;
    readonly NumericUpDown _port;
    readonly CheckBox _auto;
    readonly Button _btnStart;
    readonly TextBlock _status, _url, _testResult;
    readonly StackPanel _agentRows = new() { Spacing = 4 };
    readonly TextBlock _agentHint = new() { Foreground = Dim, FontSize = 12 };

    public McpDialog(MainWindow w)
    {
        _w = w;
        Title = "MCP 服务与 AI Agent 配置";
        Width = 560; SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0e2138"));

        _port = new NumericUpDown { Minimum = 1, Maximum = 65535, Increment = 1, Value = _w.McpPort, Width = 130, FormatString = "0" };
        _auto = new CheckBox { Content = "启动程序时自动开启", IsChecked = w.McpAutoStart };
        _btnStart = new Button { MinWidth = 80 };
        _btnStart.Click += (_, _) => ToggleServer();
        _status = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _url = new TextBlock { Foreground = Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        _testResult = new TextBlock { Foreground = Dim, FontSize = 12 };

        var btnTest = new Button { Content = "测试连接" };
        btnTest.Click += async (_, _) => await TestConnection();
        var btnCopy = new Button { Content = "复制地址" };
        btnCopy.Click += async (_, _) =>
        {
            if (Clipboard is { } cb) await cb.SetTextAsync(_w.McpEndpoint);
            _testResult.Text = "已复制";
        };

        var btnAll = new Button { Content = "一键配置全部已安装 Agent" };
        btnAll.Click += (_, _) => ConfigureAll();
        var btnRefresh = new Button { Content = "刷新" };
        btnRefresh.Click += (_, _) => FillAgents();

        var scroll = new ScrollViewer { MaxHeight = 310, Content = _agentRows };

        Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 10,
            Children =
            {
                new TextBlock { Text = "MCP 服务（只监听 127.0.0.1）", FontWeight = FontWeight.SemiBold },
                Row(6, new TextBlock { Text = "端口", VerticalAlignment = VerticalAlignment.Center }, _port, _btnStart, _auto),
                Row(6, _status, _url),
                Row(6, btnTest, btnCopy, _testResult),
                new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#274a75")), Margin = new Thickness(0, 6) },
                new TextBlock { Text = "AI Agent 一键配置", FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "写入两个 MCP 条目：wireinspect（控制本窗口，走 stdio 桥接或 HTTP）和 wireinspect-headless（独立检测进程）。已有配置会合并并自动备份。",
                    Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                },
                Row(6, btnAll, btnRefresh, _agentHint),
                scroll,
            },
        };

        RefreshServerUi();
        FillAgents();
    }

    static StackPanel Row(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    AgentConfigurator Configurator() => AgentConfigurator.ForCurrentProcess((int)(_port.Value ?? HttpTransport.DefaultPort));

    void ToggleServer()
    {
        int port = (int)(_port.Value ?? HttpTransport.DefaultPort);
        bool running = _w.McpRunning, portChanged = port != _w.McpPort;
        _w.SaveMcpSettings(port, _auto.IsChecked == true);
        if (running) _w.StopMcp();
        if (!running || portChanged)
            try { _w.StartMcp(); }
            catch (Exception e) { _testResult.Text = "启动失败: " + e.Message; }
        RefreshServerUi();
    }

    void RefreshServerUi()
    {
        _btnStart.Content = _w.McpRunning ? "停止" : "启动";
        _port.IsEnabled = !_w.McpRunning;
        _status.Text = _w.McpRunning ? "● 运行中" : "○ 已停止";
        _status.Foreground = _w.McpRunning ? Ok : Dim;
        _url.Text = _w.McpRunning ? _w.McpEndpoint : "";
    }

    async Task TestConnection()
    {
        _testResult.Text = "连接中…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var res = await http.PostAsync(_w.McpEndpoint, new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"mcp-dialog","version":"1.0"}}}""",
                System.Text.Encoding.UTF8, "application/json"));
            var body = await res.Content.ReadAsStringAsync();
            var ok = JsonNode.Parse(body)?["result"]?["serverInfo"]?["name"]?.GetValue<string>() == "wireinspect";
            _testResult.Text = res.IsSuccessStatusCode && ok ? "✓ 服务正常" : $"✗ HTTP {(int)res.StatusCode}";
            _testResult.Foreground = res.IsSuccessStatusCode && ok ? Ok : Warn;
        }
        catch (Exception e) { _testResult.Text = "✗ " + e.Message; _testResult.Foreground = Warn; }
    }

    // ------------------------------------------------------------------ agents
    void FillAgents()
    {
        _agentRows.Children.Clear();
        var cfg = Configurator();
        int installed = 0;
        foreach (var kind in AgentConfigurator.All)
        {
            var state = cfg.Status(kind);
            if (state != AgentConfigState.NotInstalled) installed++;
            var (text, brush) = state switch
            {
                AgentConfigState.Configured => ("已配置", Ok),
                AgentConfigState.Outdated => ("需更新", Warn),
                AgentConfigState.NotConfigured => ("未配置", Dim),
                _ => ("未安装", Dim),
            };
            var label = new TextBlock { Text = AgentConfigurator.Label(kind), Width = 130, VerticalAlignment = VerticalAlignment.Center };
            var st = new TextBlock { Text = text, Foreground = brush, Width = 60, VerticalAlignment = VerticalAlignment.Center };
            var btn = new Button { Content = state == AgentConfigState.NotInstalled ? "配置" : "配置", IsEnabled = state != AgentConfigState.NotInstalled };
            var k = kind;
            btn.Click += (_, _) => RunAgentOp(() => cfg.Configure(k), $"{AgentConfigurator.Label(k)} 已配置");
            var remove = new Button { Content = "移除", IsEnabled = state is AgentConfigState.Configured or AgentConfigState.Outdated };
            remove.Click += (_, _) => RunAgentOp(() => cfg.Remove(k), $"{AgentConfigurator.Label(k)} 已移除");
            _agentRows.Children.Add(Row(8, label, st, btn, remove));
        }
        _agentHint.Text = $"{installed} 个已安装";
    }

    void RunAgentOp(Func<bool> op, string okText)
    {
        try { _agentHint.Text = op() ? okText : "没有变化"; }
        catch (Exception e) { _agentHint.Text = e.Message; }
        FillAgents();
    }

    void ConfigureAll()
    {
        var cfg = Configurator();
        int done = 0;
        foreach (var kind in AgentConfigurator.All)
            if (cfg.Status(kind) != AgentConfigState.NotInstalled)
                try { if (cfg.Configure(kind)) done++; }
                catch (Exception e) { _agentHint.Text = $"{AgentConfigurator.Label(kind)}: {e.Message}"; FillAgents(); return; }
        _agentHint.Text = done > 0 ? $"已配置 {done} 个 Agent，重启 Agent 后生效" : "全部已是最新";
        FillAgents();
    }
}
