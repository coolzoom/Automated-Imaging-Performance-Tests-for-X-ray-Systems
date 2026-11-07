using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using WireInspect.Core;
using WireInspect.Mcp;

namespace WireInspect.App;

// MCP server lifecycle and the bridge that lets agent tools drive this window like a user would.
public partial class MainWindow
{
    HttpTransport? _mcp;
    GuiSession? _mcpSession;

    internal int McpPort => _cfg.McpPort;
    internal bool McpAutoStart => _cfg.McpAutoStart;
    internal bool McpRunning => _mcp != null;
    internal string McpEndpoint => $"http://127.0.0.1:{_cfg.McpPort}/mcp";

    internal void SaveMcpSettings(int port, bool autoStart)
    {
        _cfg.McpPort = port;
        _cfg.McpAutoStart = autoStart;
        AppConfig.Update(c => { c.McpPort = port; c.McpAutoStart = autoStart; });
    }

    void InitMcp()
    {
        _mcpSession = new GuiSession(this);
        if (_cfg.McpAutoStart)
            try { StartMcp(); }
            catch (Exception e) { Status("MCP 服务启动失败: " + e.Message); }
    }

    internal void StartMcp()
    {
        StopMcp();
        var server = WireInspectTools.CreateServer(_mcpSession ??= new GuiSession(this), "1.0.0");
        server.ToolCalled += (name, err) => Dispatcher.UIThread.Post(() => Status("MCP: " + name + (err ? "（错误）" : "")));
        _mcp = new HttpTransport(server, _cfg.McpPort);
        _mcp.Start();
        Status($"MCP 服务已启动: {McpEndpoint}");
    }

    internal void StopMcp()
    {
        _mcp?.Dispose();
        _mcp = null;
    }

    /// <summary>Session data changed through MCP: refresh everything that shows it.</summary>
    void AgentRefresh(string? rel)
    {
        ApplyFilter();
        UpdateCounts();
        if (rel != null) UpdateItem(rel);
        if (rel == null || rel == _current) { RefreshDefects(); RefreshInfo(); }
    }

    sealed class GuiSession : WireInspectSession
    {
        readonly MainWindow _w;
        public GuiSession(MainWindow w) => _w = w;
        public override string Mode => "gui";
        public override bool Busy => _w._running;
        protected override (int Done, int Total) DetectProgress => ((int)_w._progress.Value, (int)_w._progress.Maximum);

        void Sync() { _session = _w._session; _product = _w._product; _current = _w._current; }

        protected override T OnUi<T>(Func<T> f)
        {
            var r = Dispatcher.UIThread.CheckAccess() ? f() : Dispatcher.UIThread.InvokeAsync(f).GetAwaiter().GetResult();
            Sync();
            return r;
        }

        protected override async Task<T> OnUiAsync<T>(Func<Task<T>> f)
        {
            var r = await (Dispatcher.UIThread.CheckAccess() ? f() : Dispatcher.UIThread.InvokeAsync(f));
            Sync();
            return r;
        }

        protected override void AfterChange(string? rel = null)
        {
            _w.AgentRefresh(rel);
            Sync();
        }

        protected override async Task<JsonObject> DoOpenFolderAsync(string path)
        {
            await _w.OpenFolderAsync(path);
            Sync();
            return FolderInfo();
        }

        protected override void DoSelectImage(string rel) { _w.SelectRel(rel); _current = rel; }

        protected override Task DoStartDetectAsync(bool all)
        {
            var s = _w._session!;
            var todo = all ? s.Files.ToList() : s.Files.Where(r => !s.HasDetection(r)).ToList();
            _detectTotal = todo.Count;
            return todo.Count == 0 ? Task.CompletedTask : _w.RunBatch(todo);
        }

        protected override void DoStop() => _w._detectCts?.Cancel();

        protected override void DoSelectProduct(string? name)
        {
            _w._loadingProducts = true;
            try { _w._productCombo.SelectedItem = name; }
            finally { _w._loadingProducts = false; }
            _w.SelectProduct(name);
            _product = _w._product;
        }

        protected override void AfterProductSaved(Product p)
        {
            // The mutated product IS the window's product; just refresh the tooltip.
            if (_w._product == p)
                ToolTip.SetTip(_w._productCombo, $"{p.Name}：{p.Wires.Count} 根线/单元，模板 {p.TplSize[0]}×{p.TplSize[1]}");
            _product = p;
        }
    }
}
