using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WireInspect.Core;

/// <summary>Offline license: a vendor-signed JSON file bound to one computer's machine code and an
/// expiry date. The license file is {"license": {...}, "sig": base64}. The signature covers the
/// canonical JSON of "license" and is checked against PublicKey; only the holder of the private key
/// can issue licenses (tools/WireInspect.LicenseTool).</summary>
public sealed class LicenseError(string message) : Exception(message);

public sealed class License
{
    public static byte[] PublicKey = Convert.FromHexString("228c2e83575ded29b10bb68ba8412efa5593cfca598bbd025d47b29f1f7d4d09");

    public static readonly Dictionary<string, string> Editions = new() { ["demo"] = "演示版", ["full"] = "正式版" };
    public const int ClockSlack = 3600, TrialDays = 7;
    static readonly byte[] TrialKey = SHA256.HashData([.. "wireinspect|trial|"u8, .. Convert.FromHexString("9c41e7d20b5a86f3")]);
    public static readonly string[] Fields = ["id", "customer", "machine", "edition", "issued", "expires"];

    public readonly string Id, Customer, Machine, Edition, Issued;
    public readonly DateOnly Expires;
    public bool Trial => _expiryEpoch.HasValue;
    readonly double? _expiryEpoch;
    public Dictionary<string, string> Data { get; }

    public License(Dictionary<string, string> data, double? expiryEpoch = null)
    {
        Data = data;
        Id = data["id"]; Customer = data["customer"]; Machine = data["machine"];
        Edition = data["edition"]; Issued = data["issued"];
        Expires = DateOnly.ParseExact(data["expires"], "yyyy-MM-dd");
        _expiryEpoch = expiryEpoch;
    }

    public bool Demo => Edition != "full";
    public string EditionText => Editions.GetValueOrDefault(Edition, Edition);

    /// <summary>Valid through the whole expiry day, local time (trials: exactly TrialDays after the start).</summary>
    public double ExpiryEpoch()
        => _expiryEpoch ?? ((DateTimeOffset)(Expires.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local))).ToUnixTimeSeconds();

    public int DaysLeft(double? now = null)
    {
        var t = now ?? Now();
        return Math.Max(0, (int)Math.Ceiling((ExpiryEpoch() - t) / 86400));
    }

    public string Summary(double? now = null)
        => Trial
            ? $"{EditionText} · {TrialDays} 天试用，至 {DateTimeOffset.FromUnixTimeSeconds((long)ExpiryEpoch()).LocalDateTime:yyyy-MM-dd HH:mm}（剩 {DaysLeft(now)} 天）"
            : $"{Customer} · {EditionText} · 有效期至 {Expires:yyyy-MM-dd}（剩 {DaysLeft(now)} 天）";

    static double Now() => DateTimeOffset.Now.ToUnixTimeMilliseconds() / 1000.0;

    // ------------------------------------------------------------------ paths
    /// <summary>Licenses are enforced in packaged builds (single-file publish); WIREINSPECT_FORCE_LICENSE=1
    /// enforces them when run from source.</summary>
    public static bool Required()
        => Environment.GetEnvironmentVariable("WIREINSPECT_FORCE_LICENSE") != null
           || string.IsNullOrEmpty(System.Reflection.Assembly.GetEntryAssembly()?.Location);  // single-file bundle

    public static string StateDir()
    {
        string b;
        if (OperatingSystem.IsMacOS()) b = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
        else if (OperatingSystem.IsWindows()) b = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        else b = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(b, "WireInspect");
    }

    public static string LicensePath() => Path.Combine(StateDir(), "license.lic");

    /// <summary>Trial start is recorded in two places; the earliest valid record wins.</summary>
    public static string[] TrialPaths()
    {
        string second;
        if (OperatingSystem.IsMacOS()) second = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Preferences", ".wireinspect_t");
        else if (OperatingSystem.IsWindows()) second = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WireInspect", ".t");
        else second = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", ".wireinspect_t");
        return [Path.Combine(StateDir(), "trial.dat"), second];
    }

    // ------------------------------------------------------------------ machine code
    static string PlatformId()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var ps = Process.Start(new ProcessStartInfo("ioreg", "-rd1 -c IOPlatformExpertDevice")
                { RedirectStandardOutput = true, UseShellExecute = false })!;
                ps.WaitForExit(10_000);
                foreach (var line in ps.StandardOutput.ReadToEnd().Split('\n'))
                    if (line.Contains("IOPlatformUUID"))
                        return line.Split('=', 2)[1].Trim().Trim('"');
            }
            else if (OperatingSystem.IsWindows())
            {
                var v = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null);
                if (v is string s && s.Length > 0) return s;
            }
            else
            {
                foreach (var p in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
                    if (File.Exists(p)) return File.ReadAllText(p).Trim();
            }
        }
        catch (Exception) { }
        // last resort: primary NIC MAC address
        return "mac-" + (System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            ?.GetPhysicalAddress().ToString() ?? "000000000000");
    }

    static string? _machine;

    /// <summary>16 hex digits in 4 groups, e.g. 3F2A-91C0-7B4D-E215.</summary>
    public static string MachineCode()
    {
        _machine ??= string.Join('-', Enumerable.Range(0, 4).Select(i =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("wireinspect|" + PlatformId().Trim().ToUpper()))).Substring(i * 4, 4)));
        return _machine;
    }

    // ------------------------------------------------------------------ sign / parse
    /// <summary>Canonical JSON like Python json.dumps(sort_keys=True, separators=(',',':'), ensure_ascii=False):
    /// the Win7 Python build (imgtestpy) signs/verifies the same bytes, so a .lic works in both apps.</summary>
    public static byte[] Canonical(Dictionary<string, string> lic)
        => Encoding.UTF8.GetBytes("{" + string.Join(',', Fields.OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => JsonEscape(k) + ":" + JsonEscape(lic[k]))) + "}");

    static string JsonEscape(string s)
    {
        var b = new StringBuilder().Append('"');
        foreach (var c in s)
            b.Append(c switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                '\b' => "\\b", '\f' => "\\f",
                _ when c < ' ' => "\\u" + ((int)c).ToString("x4"),
                _ => c.ToString(),
            });
        return b.Append('"').ToString();
    }

    public static string Sign(Dictionary<string, string> lic, byte[] secret)
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["license"] = Fields.ToDictionary(k => k, k => lic[k]),
            ["sig"] = Convert.ToBase64String(Ed25519.Sign(secret, Canonical(lic))),
        }, Json.Options);

    /// <summary>Verify signature, machine binding and fields; does not look at the clock.</summary>
    public static License Parse(string text, string? machine = null)
    {
        Dictionary<string, string> lic;
        byte[] sig;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var l = doc.RootElement.GetProperty("license");
            lic = Fields.ToDictionary(k => k, k => l.GetProperty(k).GetString() ?? throw new FormatException());
            sig = Convert.FromBase64String(doc.RootElement.GetProperty("sig").GetString() ?? "");
        }
        catch (Exception) { throw new LicenseError("授权文件格式不正确。"); }
        if (!Ed25519.Verify(PublicKey, Canonical(lic), sig))
            throw new LicenseError("授权文件无效（签名校验失败）。");
        License outp;
        try { outp = new License(lic); }
        catch (Exception) { throw new LicenseError("授权文件格式不正确。"); }
        if (!lic["machine"].Equals(machine ?? MachineCode(), StringComparison.OrdinalIgnoreCase))
            throw new LicenseError("授权文件不属于这台电脑（机器码不符）。");
        return outp;
    }

    // ------------------------------------------------------------------ clock
    /// <summary>Reject a clock set back to before the last seen time; remember the latest time seen.</summary>
    static double CheckClock(double now, string directory)
    {
        var path = Path.Combine(directory, "clock.json");
        double last = 0;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var v = doc.RootElement.GetProperty("last").GetDouble();
            if (double.IsFinite(v)) last = v;
        }
        catch (Exception) { }
        if (now < last - ClockSlack)
            throw new LicenseError("系统时间异常（早于上次使用时间），请恢复正确时间后重试。");
        if (now > last)
            try
            {
                Directory.CreateDirectory(directory);
                Json.Save(path, new Dictionary<string, double> { ["last"] = now });
            }
            catch (IOException) { }
        return Math.Max(now, last);
    }

    /// <summary>Return the valid License or raise LicenseError with a message for the user.</summary>
    public static License Check(string? path = null, double? now = null, string? machine = null)
    {
        path ??= LicensePath();
        var t = now ?? Now();
        if (!File.Exists(path)) throw new LicenseError("本机尚未授权。");
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception) { throw new LicenseError("无法读取授权文件。"); }
        var lic = Parse(text, machine);
        var seen = CheckClock(t, Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (seen >= lic.ExpiryEpoch()) throw new LicenseError($"授权已于 {lic.Expires:yyyy-MM-dd} 到期。");
        return lic;
    }

    /// <summary>Validate a license file chosen by the user and copy it into place; returns the License.</summary>
    public static License Install(string src, double? now = null, string? machine = null)
    {
        string text;
        try { text = File.ReadAllText(src); }
        catch (Exception) { throw new LicenseError("无法读取所选文件。"); }
        var lic = Parse(text, machine);
        var t = now ?? Now();
        if (t >= lic.ExpiryEpoch()) throw new LicenseError($"该授权文件已于 {lic.Expires:yyyy-MM-dd} 到期。");
        var dst = LicensePath();
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.WriteAllText(dst + ".tmp", text);
        File.Move(dst + ".tmp", dst, true);
        return Check(dst, t, machine);
    }

    // ------------------------------------------------------------------ trial
    static string TrialMac(double first, string machine)
        => Convert.ToHexString(HMACSHA256.HashData(TrialKey, Encoding.UTF8.GetBytes($"{first:0.000}|{machine}"))).ToLower();

    /// <summary>Earliest recorded trial start; null if never started. Throws if a record was tampered with.</summary>
    static double? TrialFirst(string machine)
    {
        var firsts = new List<double>();
        foreach (var p in TrialPaths())
        {
            if (!File.Exists(p)) continue;
            bool ok;
            double first;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(p));
                first = doc.RootElement.GetProperty("first").GetDouble();
                ok = double.IsFinite(first) && doc.RootElement.GetProperty("mac").GetString() == TrialMac(first, machine);
            }
            catch (Exception) { ok = false; first = 0; }
            if (!ok) throw new LicenseError("演示期记录无效，请导入授权文件。");
            firsts.Add(first);
        }
        return firsts.Count > 0 ? firsts.Min() : null;
    }

    static License TrialLicense(double first, string machine)
    {
        var exp = first + TrialDays * 86400;
        var data = new Dictionary<string, string>
        {
            ["id"] = "trial", ["customer"] = "试用用户", ["machine"] = machine, ["edition"] = "demo",
            ["issued"] = DateTimeOffset.FromUnixTimeSeconds((long)first).LocalDateTime.ToString("yyyy-MM-dd"),
            ["expires"] = DateTimeOffset.FromUnixTimeSeconds((long)exp).LocalDateTime.ToString("yyyy-MM-dd"),
        };
        return new License(data, exp);
    }

    /// <summary>("available", null) | ("active", License) | ("ended", message).</summary>
    public static (string state, object? value) TrialStatus(double? now = null, string? machine = null)
    {
        machine ??= MachineCode();
        var t = now ?? Now();
        double? first;
        double seen;
        try
        {
            first = TrialFirst(machine);
            if (first == null) return ("available", null);
            seen = CheckClock(t, StateDir());
        }
        catch (LicenseError e) { return ("ended", e.Message); }
        var lic = TrialLicense(first.Value, machine);
        if (seen >= lic.ExpiryEpoch()) return ("ended", $"{TrialDays} 天演示期已结束，请导入授权文件。");
        return ("active", lic);
    }

    public static License StartTrial(double? now = null, string? machine = null)
    {
        machine ??= MachineCode();
        var t = now ?? Now();
        var (state, value) = TrialStatus(t, machine);
        if (state == "ended") throw new LicenseError((string)value!);
        if (state == "active") return (License)value!;
        var r = Math.Round(t, 3);
        var rec = JsonSerializer.Serialize(new { first = r, mac = TrialMac(r, machine) });
        int written = 0;
        foreach (var p in TrialPaths())
            try { Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, rec); written++; }
            catch (IOException) { }
        if (written == 0) throw new LicenseError("无法写入演示期记录。");
        CheckClock(t, StateDir());
        return TrialLicense(r, machine);
    }

    /// <summary>Re-validate a running license or trial; raises LicenseError when it is no longer valid.</summary>
    public static License Refresh(License lic, double? now = null)
    {
        if (!lic.Trial) return Check(now: now);
        var (state, value) = TrialStatus(now);
        return state == "active" ? (License)value! : throw new LicenseError(state == "ended" ? (string)value! : "演示期记录丢失。");
    }
}
