using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WireInspect.Core;

// WireInspect 授权签发工具（注册机，仅限内部使用，请勿发给客户）
//   dotnet run --project tools/WireInspect.LicenseTool            交互模式
//   ... --keygen [--write]                                       生成新密钥对（换公钥需重新编译程序）
//   ... --machine 3F2A-91C0-7B4D-E215 --customer 某某公司 --days 30 [--edition demo|full] [--out file.lic]
//   ... --machine ... --customer ... --expires 2027-06-30
// 私钥默认在 ../license_keys/vendor_ed25519.key（本仓库之外），丢失则无法再给已发布程序签发。

static class Program
{
    static readonly string KeyDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "license_keys"));
    public static string KeyPath => Path.Combine(KeyDir, "vendor_ed25519.key");
    public static string IssuedDir => Path.Combine(KeyDir, "issued");
    const string MachineRe = @"^[0-9A-F]{4}(-[0-9A-F]{4}){3}$";

    static int Main(string[] args)
    {
        var argsList = args.ToList();
        string? Opt(string name)
        {
            var i = argsList.IndexOf(name);
            return i >= 0 && i + 1 < argsList.Count ? argsList[i + 1] : null;
        }

        if (argsList.Contains("--mymachine"))
        {
            Console.WriteLine(License.MachineCode());
            return 0;
        }

        if (argsList.Contains("--keygen"))
        {
            var secret = RandomNumberGenerator.GetBytes(32);
            var pub = Ed25519.PublicKey(secret);
            Console.WriteLine("新密钥对已生成：");
            Console.WriteLine("  私钥（保密并备份）: " + Convert.ToHexString(secret).ToLower());
            Console.WriteLine("  公钥（粘贴到 src/WireInspect.Core/License.cs 的 PublicKey 后重新编译）: " + Convert.ToHexString(pub).ToLower());
            if (argsList.Contains("--write"))
            {
                Directory.CreateDirectory(KeyDir);
                File.WriteAllText(KeyPath, Convert.ToHexString(secret).ToLower() + "\n");
                Console.WriteLine("私钥已写入 " + KeyPath);
            }
            return 0;
        }

        var machine = Opt("--machine")?.Trim().ToUpper();
        var customer = Opt("--customer")?.Trim();
        var days = Opt("--days");
        var expiresArg = Opt("--expires");
        var edition = Opt("--edition") ?? "demo";
        var keyPath = Opt("--key") ?? KeyPath;
        var outFile = Opt("--out");

        if (argsList.Count == 0)
            return WireInspect.LicenseTool.GuiApp.Run(args);  // no args -> 注册机窗口
        if (argsList.Contains("--cli") && machine == null && customer == null && days == null && expiresArg == null)
            return Interactive(keyPath, outFile);           // --cli -> 控制台交互
        if (machine == null && customer == null && days == null && expiresArg == null)
            return Exit("无操作：不带参数打开注册机窗口；--cli 控制台交互；或 --machine/--customer/--days 直接签发");

        if (machine == null || customer == null || (days == null && expiresArg == null))
            return Exit("需要 --machine、--customer 以及 --days 或 --expires（或不带参数进入交互模式）");
        if (!Regex.IsMatch(machine, MachineRe)) return Exit("机器码格式应为 XXXX-XXXX-XXXX-XXXX");
        if (!License.Editions.ContainsKey(edition)) return Exit("--edition 只能是 demo 或 full");
        DateOnly exp;
        try { exp = ParseExpiry(days ?? expiresArg!, DateOnly.FromDateTime(DateTime.Today)); }
        catch (Exception ex) { return Exit(ex.Message); }
        var sec = LoadKey(keyPath);
        if (sec == null) return 1;
        Issue(sec, machine, customer, exp, edition, outFile);
        return 0;
    }

    static int Interactive(string keyPath, string? outFile)
    {
        var secret = LoadKey(keyPath);
        if (secret == null) return 1;
        Console.WriteLine("=========== 签发授权文件（注册机） ===========");
        Console.WriteLine("私钥: " + keyPath);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var m = Ask("客户机器码: ", v => Regex.IsMatch(v.ToUpper(), MachineRe) ? null : "机器码格式应为 XXXX-XXXX-XXXX-XXXX（客户授权窗口里显示的）").ToUpper();
        var c = Ask("客户名称（显示在程序和报告水印里）: ", v => v.Length > 0 ? null : "不能为空");
        var e = Ask("版本 1=演示版 2=正式版: ", v => v is "1" or "2" ? null : "请输入 1 或 2", "1");
        var ed = e == "2" ? "full" : "demo";
        var exp = ParseExpiry(Ask("有效天数或到期日 YYYY-MM-DD: ", v =>
        {
            try { ParseExpiry(v, today); return null; }
            catch (Exception ex) { return ex.Message.Contains('早') || ex.Message.Contains("至少") ? ex.Message : "请输入天数（如 30）或日期（如 2027-06-30）"; }
        }, "30"), today);
        Console.WriteLine($"\n即将签发: {c} · {License.Editions[ed]} · {m} · 有效期至 {exp:yyyy-MM-dd}");
        if (Ask("确认? (y/n): ", v => v.ToLower() is "y" or "n" ? null : "请输入 y 或 n", "y").ToLower() != "y")
        {
            Console.WriteLine("已取消");
            return 0;
        }
        Issue(secret, m, c, exp, ed, outFile);
        Console.WriteLine("把这个 .lic 文件发给客户，在授权窗口点“导入授权文件”即可。");
        return 0;
    }

    /// <summary>GUI-friendly variant of LoadKey: returns the key or null with an error message.</summary>
    public static byte[]? TryLoadKey(string path, out string error)
    {
        error = "";
        if (!File.Exists(path))
        {
            error = $"未找到签名私钥: {path}\n请把 vendor_ed25519.key 放到该位置（或用 --key 指定）；没有的话先运行 --keygen --write 并更新公钥。";
            return null;
        }
        byte[] secret;
        try { secret = Convert.FromHexString(File.ReadAllText(path).Trim()); }
        catch (Exception) { error = $"私钥文件无法读取: {path}"; return null; }
        if (secret.Length != 32 || !Ed25519.PublicKey(secret).SequenceEqual(License.PublicKey))
        {
            error = $"私钥与程序内置公钥不匹配: {path}";
            return null;
        }
        return secret;
    }

    /// <summary>Sign and write the license file; returns its path. GUI/console share this.</summary>
    public static string IssueLicense(byte[] secret, string machine, string customer, DateOnly expires, string edition,
        string? outFile = null, string? outDir = null)
    {
        var lic = License.Fields.ToDictionary(k => k, k => k switch
        {
            "id" => Guid.NewGuid().ToString("N")[..12],
            "customer" => customer,
            "machine" => machine,
            "edition" => edition,
            "issued" => DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd"),
            "expires" => expires.ToString("yyyy-MM-dd"),
            _ => "",
        });
        var text = License.Sign(lic, secret);
        License.Parse(text, machine);          // round-trip check before writing
        if (outFile == null)
        {
            outDir ??= IssuedDir;
            Directory.CreateDirectory(outDir);
            var safe = Regex.Replace(customer, @"[\\/:*?""<>|\s]+", "_");
            outFile = Path.Combine(outDir, $"license_{safe}_{machine[..4]}_{edition}_{expires:yyyyMMdd}.lic");
        }
        File.WriteAllText(outFile, text + "\n");
        LogIssued(Path.GetDirectoryName(Path.GetFullPath(outFile))!, lic, outFile);
        return outFile;
    }

    static byte[]? LoadKey(string path)
    {
        if (!File.Exists(path))
        {
            Exit($"未找到签名私钥: {path}\n请把 vendor_ed25519.key 放到该位置（或用 --key 指定）；没有的话先运行 --keygen --write 并更新公钥。");
            return null;
        }
        byte[] secret;
        try { secret = Convert.FromHexString(File.ReadAllText(path).Trim()); }
        catch (Exception) { Exit($"私钥文件无法读取: {path}"); return null; }
        if (secret.Length != 32 || !Ed25519.PublicKey(secret).SequenceEqual(License.PublicKey))
        {
            Exit($"私钥与程序内置公钥不匹配: {path}");
            return null;
        }
        return secret;
    }

    static int Exit(string msg) { Console.Error.WriteLine("!! " + msg); return 1; }

    static void LogIssued(string outDir, Dictionary<string, string> lic, string path)
    {
        var log = Path.Combine(outDir, "签发记录.csv");
        var first = !File.Exists(log);
        using var w = new StreamWriter(log, true, new UTF8Encoding(true));
        if (first) w.WriteLine("签发日期,客户,机器码,版本,到期日,授权编号,文件");
        w.WriteLine(string.Join(',', lic["issued"], lic["customer"], lic["machine"],
            License.Editions.GetValueOrDefault(lic["edition"], lic["edition"]), lic["expires"], lic["id"],
            Path.GetFileName(path)));
    }

    static void Issue(byte[] secret, string machine, string customer, DateOnly expires, string edition,
        string? outFile = null, string? outDir = null)
    {
        var outFile2 = IssueLicense(secret, machine, customer, expires, edition, outFile, outDir);
        Console.WriteLine($"\n已生成授权文件: {Path.GetFullPath(outFile2)}");
        Console.WriteLine($"  客户 {customer} · {License.Editions.GetValueOrDefault(edition, edition)} · 机器码 {machine} · 有效期至 {expires:yyyy-MM-dd}（含当天）");
    }

    static DateOnly ParseExpiry(string v, DateOnly today)
    {
        if (Regex.IsMatch(v, @"^\d+$"))
        {
            if (int.Parse(v) < 1) throw new ArgumentException("天数至少为 1");
            return today.AddDays(int.Parse(v) - 1);
        }
        var d = DateOnly.ParseExact(v, "yyyy-MM-dd");
        if (d < today) throw new ArgumentException("到期日早于今天");
        return d;
    }

    static string Ask(string prompt, Func<string, string?> check, string? def = null)
    {
        while (true)
        {
            Console.Write(prompt + (def != null ? $"[{def}] " : ""));
            var v = (Console.ReadLine() ?? "").Trim();
            if (v.Length == 0) v = def ?? "";
            var err = check(v);
            if (err == null) return v;
            Console.WriteLine("  !! " + err);
        }
    }
}
