using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using WireInspect.Core;

namespace WireInspect.Mcp;

public static class WireInspectTools
{
    public static readonly string Instructions =
        "WireInspect 是 X-ray 焊线检测软件：打开图片文件夹 → 选产品 → 批量检测单元/焊线/焊点 → 人工复核缺陷并判 PASS/FAIL。" +
        "典型流程：wireinspect_open_folder 打开文件夹 → wireinspect_list_products / wireinspect_select_product → " +
        "wireinspect_detect（all=true 重检全部，wait_seconds 等待）→ wireinspect_get_status 看进度 → " +
        "wireinspect_list_images / wireinspect_get_result / wireinspect_list_defects 看结果 → " +
        "wireinspect_render 出标注 PNG。" +
        "缺陷复核：wireinspect_set_defect_status（accepted/rejected/pending）、wireinspect_change_defect_type、" +
        "wireinspect_add_defect / wireinspect_delete_defect、wireinspect_set_verdict（PASS/FAIL/null 清除）。" +
        "参数：wireinspect_get_product 看当前产品参数，wireinspect_set_product_params 改 product.json（所有长度单位 px），" +
        "wireinspect_auto_wires 在框选区域内自动找线，wireinspect_create_product 从图片区域建新产品。" +
        "缺陷类型：" + string.Join("、", DefectTypes.All) + "。file 参数用 list_images 返回的相对路径，省略时用当前图。";

    public static McpServer CreateServer(WireInspectSession session, string version = "1.0.0") =>
        new(Create(session), "wireinspect", version, Instructions);

    public static IReadOnlyList<McpTool> Create(WireInspectSession s) =>
    [
        // ------------------------------------------------ folder / images
        new("wireinspect_open_folder",
            "打开图片文件夹（界面的「导入文件夹」）；返回文件数与跳过的 0 字节文件数。",
            Schema(("path", Str("文件夹绝对路径"), true)),
            a => Async(s.OpenFolderAsync(Required(a, "path")))),
        new("wireinspect_recent_folders",
            "最近打开过的文件夹列表。",
            Schema(),
            _ => new McpToolResult(Join(s.RecentFolders(), "path", "exists"), new JsonObject { ["folders"] = s.RecentFolders() })),
        new("wireinspect_list_images",
            "列出当前文件夹的图片：状态、缺陷数、判定。status 过滤：unchecked / ok / suspect / PASS / FAIL。",
            Schema(("status", Enum("按状态过滤", "unchecked", "ok", "suspect", "PASS", "FAIL"), false)),
            a =>
            {
                var rows = s.ListImages(Optional(a, "status"));
                return new McpToolResult(Join(rows, "file", "status_text", "defects"), new JsonObject { ["images"] = rows });
            }),
        new("wireinspect_select_image",
            "把当前图片切换到 file（list_images 里的相对路径）。",
            Schema(("file", Str("图片相对路径"), true)),
            a => new McpToolResult("当前图片 " + Required(a, "file"), s.SelectImage(Required(a, "file")))),
        new("wireinspect_get_status",
            "整体状态：文件夹、图片数、各状态计数、当前产品、检测进度。",
            Schema(),
            _ => new McpToolResult(Describe(s.Status()), s.Status())),
        // ------------------------------------------------ detection
        new("wireinspect_detect",
            "开始检测：all=false 只检未检测的图片，all=true 重检全部（保留手动/已复核标记）。后台运行，" +
            "最多等 wait_seconds 秒后返回进度；running=true 时继续调 wireinspect_get_status 等待。",
            Schema(("all", Bool("重检全部，默认 false"), false), ("wait_seconds", Num("最多等待秒数，默认 60"), false)),
            a => Async(s.DetectAsync(a["all"]?.GetValue<bool>() == true, Number(a, "wait_seconds", 60)))),
        new("wireinspect_stop",
            "停止运行中的检测。",
            Schema(),
            _ => new McpToolResult(s.Stop()["stopped"]?.GetValue<bool>() == true ? "已停止" : "没有运行中的检测", s.Stop())),
        new("wireinspect_get_result",
            "单张图片的完整检测结果：单元、每根线指标（z、sweep%、偏移、缺口）、缺陷、判定。",
            Schema(("file", Str("图片相对路径，省略=当前图"), false)),
            a => new McpToolResult(Describe(s.GetResult(Optional(a, "file"))), s.GetResult(Optional(a, "file")))),
        new("wireinspect_render",
            "渲染当前（或指定）图片的标注 PNG：单元框、导线、焊点、缺陷框。",
            Schema(("file", Str("图片相对路径，省略=当前图"), false)),
            a =>
            {
                var png = s.RenderPng(Optional(a, "file"));
                return new McpToolResult($"{png.Length / 1024} KB PNG", null, false, png);
            }),
        new("wireinspect_save_report",
            "导出整个文件夹的 CSV 报告（汇总 + 导线明细），返回文件路径。",
            Schema(("path", Str("输出 CSV 路径，省略时写到文件夹内"), false)),
            a => new McpToolResult("已保存 " + s.SaveReport(Optional(a, "path"))["summary_csv"], s.SaveReport(Optional(a, "path")))),
        new("wireinspect_clear_marks",
            "清除缺陷标记：scope=image 当前图 / subfolder 当前子文件夹 / folder 全部；verdicts=true 同时清除判定。",
            Schema(("scope", Enum("清除范围", "image", "subfolder", "folder"), true),
                   ("verdicts", Bool("同时清除 PASS/FAIL，默认 false"), false)),
            a =>
            {
                var r = s.ClearMarks(Required(a, "scope"), a["verdicts"]?.GetValue<bool>() == true);
                return new McpToolResult($"已清除 {r["cleared"]} 张图片的标记", r);
            }),
        // ------------------------------------------------ defects / review
        new("wireinspect_list_defects",
            "列缺陷：file 省略时列全部图片；status 过滤 pending / accepted / rejected。",
            Schema(("file", Str("图片相对路径，省略=全部"), false),
                   ("status", Enum("按状态过滤", "pending", "accepted", "rejected"), false)),
            a =>
            {
                var rows = s.ListDefects(Optional(a, "file"), Optional(a, "status"));
                return new McpToolResult(Join(rows, "file", "type", "status", "by"), new JsonObject { ["defects"] = rows });
            }),
        new("wireinspect_add_defect",
            "手动框缺陷：图片坐标框 + 类型（等同界面框选，source=manual、已确认）。",
            Schema(("x0", Num("左上 x"), true), ("y0", Num("左上 y"), true), ("x1", Num("右下 x"), true), ("y1", Num("右下 y"), true),
                   ("type", Enum("缺陷类型", [.. DefectTypes.All]), true), ("file", Str("图片相对路径，省略=当前图"), false)),
            a =>
            {
                var d = s.AddDefect(Optional(a, "file"), I(a, "x0"), I(a, "y0"), I(a, "x1"), I(a, "y1"), Required(a, "type"));
                return new McpToolResult($"已添加 {d["type"]} #{d["id"]}", d);
            }),
        new("wireinspect_set_defect_status",
            "改缺陷状态：accepted 确认 / rejected 误报 / pending 待复核。",
            Schema(("id", Str("缺陷 id"), true), ("status", Enum("状态", "accepted", "rejected", "pending"), true)),
            a => new McpToolResult("已更新", s.SetDefectStatus(Required(a, "id"), Required(a, "status")))),
        new("wireinspect_change_defect_type",
            "改缺陷类型（同时置为 accepted）。",
            Schema(("id", Str("缺陷 id"), true), ("type", Enum("缺陷类型", [.. DefectTypes.All]), true)),
            a => new McpToolResult("已更新", s.ChangeDefectType(Required(a, "id"), Required(a, "type")))),
        new("wireinspect_delete_defect",
            "删除缺陷。",
            Schema(("id", Str("缺陷 id"), true)),
            a => new McpToolResult("已删除 " + Required(a, "id"), s.DeleteDefect(Required(a, "id")))),
        new("wireinspect_set_verdict",
            "判定：verdict=PASS / FAIL / null 清除；省略 verdict 时只更新 comment、判定不变。",
            Schema(("verdict", Enum("判定", "PASS", "FAIL"), false),
                   ("file", Str("图片相对路径，省略=当前图"), false), ("comment", Str("备注"), false)),
            a =>
            {
                var r = s.SetVerdict(Optional(a, "file"), a.ContainsKey("verdict") ? Optional(a, "verdict") : null,
                    Optional(a, "comment"), a.ContainsKey("verdict"));
                return new McpToolResult($"已判定 {r["verdict"] ?? "清除"}", r);
            }),
        new("wireinspect_set_comment",
            "给图片写备注。",
            Schema(("comment", Str("备注文本"), true), ("file", Str("图片相对路径，省略=当前图"), false)),
            a => new McpToolResult("已保存备注", s.SetComment(Optional(a, "file"), Required(a, "comment")))),
        // ------------------------------------------------ product / config
        new("wireinspect_list_products",
            "产品库列表（products/<名>/tpl.png + product.json）。",
            Schema(),
            _ => new McpToolResult(Join(s.ListProducts(), "name", "selected"), new JsonObject { ["products"] = s.ListProducts() })),
        new("wireinspect_select_product",
            "切换当前产品（影响之后的检测）。",
            Schema(("name", Str("产品名"), true)),
            a => new McpToolResult("当前产品 " + Required(a, "name"), s.SelectProduct(Required(a, "name")))),
        new("wireinspect_get_product",
            "当前产品的全部参数（product.json 内容）。",
            Schema(),
            _ => new McpToolResult(Describe(s.GetProduct()), s.GetProduct())),
        new("wireinspect_set_product_params",
            "改当前产品参数并写回 product.json：match_thr、polarity(dark/bright)、wire_width_px、corridor_px、" +
            "end_tol_px、gap_max_px、shift_max_px、sweep_max_pct、z_missing、z_seg、z_ball、n_seg、" +
            "max_angle_deg（0=只正向）、local_align、align_search_px、allow_mirror、note、" +
            "wires（[{p1:[x,y],p2:[x,y]}]，模板坐标）。之后检测用新参数。",
            Schema(("patch", new JsonObject { ["type"] = "object", ["description"] = "要改的字段" }, true)),
            a => new McpToolResult("产品参数已保存", s.SetProductParams(a["patch"] as JsonObject ?? new JsonObject()))),
        new("wireinspect_auto_wires",
            "在图片的 rect 区域内自动找焊线（=新建产品对话框的自动找线）；返回相对 rect 左上角的线端点，" +
            "可直接用于 create_product 的 wires。",
            Schema(("rect", new JsonObject { ["type"] = "array", ["description"] = "[x,y,w,h] 单元区域" }, true),
                   ("file", Str("图片相对路径，省略=当前图"), false), ("product", Str("用该产品的线宽/极性，省略=当前产品"), false)),
            a => new McpToolResult(Describe(s.AutoWires(Arr(a, "rect", 0), Arr(a, "rect", 1), Arr(a, "rect", 2), Arr(a, "rect", 3),
                Optional(a, "file"), Optional(a, "product"))),
                s.AutoWires(Arr(a, "rect", 0), Arr(a, "rect", 1), Arr(a, "rect", 2), Arr(a, "rect", 3), Optional(a, "file"), Optional(a, "product")))),
        new("wireinspect_create_product",
            "建新产品：name + file + rect（单元区域，裁作 tpl.png）+ 可选 wires（相对 rect 左上角）与任意参数（同 set_product_params）。",
            Schema(("name", Str("产品名"), true), ("rect", new JsonObject { ["type"] = "array", ["description"] = "[x,y,w,h]" }, true),
                   ("file", Str("模板来源图片，省略=当前图"), false),
                   ("wires", new JsonObject { ["type"] = "array", ["description"] = "[{p1:[x,y],p2:[x,y]}]" }, false)),
            a =>
            {
                var r = s.CreateProduct(a);
                return new McpToolResult($"产品 {r["product"]} 已保存到 {r["dir"]}", r);
            }),
        new("wireinspect_get_config",
            "程序配置：操作员、默认产品、并行线程数、最近文件夹。",
            Schema(),
            _ => new McpToolResult(Describe(s.GetConfig()), s.GetConfig())),
        new("wireinspect_set_config",
            "改程序配置：operator、workers（0=自动）、product（同时切换当前产品）。",
            Schema(("patch", new JsonObject { ["type"] = "object", ["description"] = "要改的字段" }, true)),
            a => new McpToolResult("配置已保存", s.SetConfig(a["patch"] as JsonObject ?? new JsonObject()))),
    ];

    // ------------------------------------------------------------- helpers
    static McpToolResult Async<T>(Task<T> task) where T : JsonNode
    {
        var r = task.GetAwaiter().GetResult();
        return new McpToolResult(Describe(r), r as JsonObject ?? new JsonObject { ["result"] = r });
    }

    static string Describe(JsonNode? node)
    {
        if (node is JsonObject o && o["error"] is { } e) return "错误: " + e;
        return node?.ToJsonString(McpServer.Json) ?? "";
    }

    static string Join(JsonArray rows, params string[] cols)
    {
        var sb = new StringBuilder();
        foreach (var row in rows.OfType<JsonObject>())
            sb.AppendLine(string.Join("  ", cols.Select(c => row[c]?.ToString())));
        return sb.Length == 0 ? "（空）" : sb.ToString().TrimEnd();
    }

    static string Required(JsonObject a, string name) =>
        Optional(a, name) ?? throw new ArgumentException("缺少参数 " + name);

    static string? Optional(JsonObject a, string name) =>
        a[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    static int I(JsonObject a, string name) => (int)Number(a, name, 0);

    static double Number(JsonObject a, string name, double fallback)
    {
        if (a[name] is not JsonValue v) return fallback;
        if (v.TryGetValue<double>(out var d)) return d;
        return v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : fallback;
    }

    static double Arr(JsonObject a, string name, int i) =>
        (a[name] as JsonArray)?[i]?.GetValue<double>() ?? throw new ArgumentException($"{name}[{i}] 缺失");

    static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
    static JsonObject Num(string description) => new() { ["type"] = "number", ["description"] = description };
    static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };
    static JsonObject Enum(string description, params string[] values) => new()
    {
        ["type"] = "string",
        ["description"] = description,
        ["enum"] = new JsonArray(values.Select(v => (JsonNode?)v).ToArray())
    };
    static JsonObject Schema(params (string Name, JsonObject Type, bool Required)[] props)
    {
        var properties = new JsonObject();
        foreach (var p in props) properties[p.Name] = p.Type;
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        var required = props.Where(p => p.Required).Select(p => (JsonNode?)p.Name).ToArray();
        if (required.Length > 0) schema["required"] = new JsonArray(required);
        return schema;
    }
}
