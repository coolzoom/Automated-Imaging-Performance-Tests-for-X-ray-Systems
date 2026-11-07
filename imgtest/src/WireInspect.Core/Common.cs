using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace WireInspect.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static T Load<T>(string path) where T : new()
        => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T() : new T();

    /// <summary>Write through a temp file so a crash never leaves half a file.</summary>
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, true);
    }
}

public static class AppPaths
{
    /// <summary>imgtest/ (the folder holding products/ and config.json): WIREINSPECT_HOME, else the first
    /// ancestor of the executable that has a products/ folder, else the executable folder. Re-resolved when
    /// the environment variable changes so tests and the MCP host can redirect it.</summary>
    static string? _root, _rootEnv;
    public static string Root
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("WIREINSPECT_HOME");
            if (env != _rootEnv) { _rootEnv = env; _root = null; }
            return _root ??= FindRoot();
        }
    }
    public static string Products => Path.Combine(Root, "products");
    public static string Config => Path.Combine(Root, "config.json");

    static string FindRoot()
    {
        var env = Environment.GetEnvironmentVariable("WIREINSPECT_HOME");
        if (!string.IsNullOrEmpty(env)) return Path.GetFullPath(env);
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "products"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
}

public sealed class AppConfig
{
    public List<string> RecentFolders { get; set; } = [];
    public string Operator { get; set; } = "";
    public string Product { get; set; } = "";
    public int Workers { get; set; }
    public int McpPort { get; set; } = 3918;
    public bool McpAutoStart { get; set; }

    public const int RecentMax = 10;

    public static AppConfig Load() => Json.Load<AppConfig>(AppPaths.Config);
    public void Save() => Json.Save(AppPaths.Config, this);

    /// <summary>Change one setting on the file as it is now: config.json is shared with the Python version (and
    /// other windows), so saving a copy loaded at start-up would wipe what they recorded meanwhile.</summary>
    public static AppConfig Update(Action<AppConfig> change)
    {
        var c = Load();
        change(c);
        c.Save();
        return c;
    }

    /// <summary>Absolute path without a trailing separator: "a/b/" and "a/b" are the same folder.</summary>
    public static string NormPath(string p)
    {
        var full = Path.GetFullPath(p);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? full : trimmed;
    }

    /// <summary>Recent folders, normalised, newest first, each once.</summary>
    public List<string> Recent() => RecentFolders.Where(p => !string.IsNullOrWhiteSpace(p)).Select(NormPath)
        .Distinct(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase).Take(RecentMax).ToList();

    public static void AddRecent(string path) => Update(c =>
    {
        c.RecentFolders = [path, .. c.RecentFolders];
        c.RecentFolders = c.Recent();
    });
}

public static class DefectTypes
{
    public const string Missing = "缺线", Broken = "断线", Sweep = "线弧异常", Bond1 = "第一焊点异常",
        Bond2 = "第二焊点异常", Shift = "焊点偏移", Other = "其他";

    public static readonly string[] All = [Missing, Broken, Sweep, Bond1, Bond2, Shift, Other];

    public static readonly Dictionary<string, string> Colors = new()
    {
        [Missing] = "#ff3030", [Broken] = "#ff6a00", [Sweep] = "#ffa000", [Bond1] = "#e8336d",
        [Bond2] = "#ff00aa", [Shift] = "#5e7bff", [Other] = "#30d0ff",
    };

    public static readonly Dictionary<string, string> FromFlag = new()
    {
        ["MISSING"] = Missing, ["BROKEN"] = Broken, ["BOND1"] = Bond1, ["BOND2"] = Bond2,
        ["SWEEP"] = Sweep, ["SHIFT"] = Shift,
    };

    public static string Color(string type) => Colors.GetValueOrDefault(type, "#30ff60");
}

public sealed class Defect
{
    public int X0 { get; set; }
    public int Y0 { get; set; }
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public string Type { get; set; } = DefectTypes.Other;
    public double Score { get; set; }
    public string Source { get; set; } = "auto";      // auto | manual
    public string Status { get; set; } = "pending";   // pending | accepted | rejected
    public string By { get; set; } = "";              // e.g. 单元3·线1
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public Defect Clone() => (Defect)MemberwiseClone();

    public static double Iou(Defect a, Defect b)
    {
        double ix = Math.Max(0, Math.Min(a.X1, b.X1) - Math.Max(a.X0, b.X0));
        double iy = Math.Max(0, Math.Min(a.Y1, b.Y1) - Math.Max(a.Y0, b.Y0));
        double inter = ix * iy;
        double union = (double)(a.X1 - a.X0) * (a.Y1 - a.Y0) + (double)(b.X1 - b.X0) * (b.Y1 - b.Y0) - inter;
        return inter / Math.Max(union, 1);
    }

    public static bool Overlaps(Defect a, Defect b)
    {
        double cx = (a.X0 + a.X1) / 2.0, cy = (a.Y0 + a.Y1) / 2.0;
        return Iou(a, b) >= 0.3 || (b.X0 <= cx && cx <= b.X1 && b.Y0 <= cy && cy <= b.Y1);
    }
}

public static class Scanner
{
    /// <summary>Image paths under root, relative and '/'-separated, in folder-tree order; 0-byte files go to
    /// <paramref name="empty"/>.</summary>
    public static List<string> Scan(string root, List<string>? empty = null)
    {
        var outList = new List<string>();
        Walk(root, "");
        return outList;

        void Walk(string dir, string rel)
        {
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception) { return; }
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                if (name.StartsWith('.') || !ImageIO.Exts.Contains(Path.GetExtension(name).ToLowerInvariant())) continue;
                var r = rel.Length == 0 ? name : rel + "/" + name;
                long size;
                try { size = new FileInfo(f).Length; } catch (Exception) { continue; }
                if (size == 0) empty?.Add(r);
                else outList.Add(r);
            }
            Array.Sort(dirs, StringComparer.Ordinal);
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith('.') || name is "report" or "report_wire") continue;
                Walk(d, rel.Length == 0 ? name : rel + "/" + name);
            }
        }
    }
}
