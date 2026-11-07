using System.Text;
using WireInspect.Core;
using Xunit;

namespace WireInspect.Tests;

[Collection("serial")]   // License.PublicKey is global; keep these off parallel runners
public sealed class LicenseTests : IDisposable
{
    static readonly byte[] Secret = Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 3)).ToArray();
    static readonly byte[] Pub = Ed25519.PublicKey(Secret);
    const string Machine = "AAAA-BBBB-CCCC-DDDD";
    readonly byte[] _oldPub = License.PublicKey;
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wi_lic_" + Guid.NewGuid().ToString("N")[..8]);

    public LicenseTests() => License.PublicKey = Pub;
    public void Dispose()
    {
        License.PublicKey = _oldPub;
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    static Dictionary<string, string> LicData(string machine = Machine, string edition = "demo",
        string expires = "2099-12-31", string customer = "test")
        => new()
        {
            ["id"] = "t1", ["customer"] = customer, ["machine"] = machine,
            ["edition"] = edition, ["issued"] = "2026-01-01", ["expires"] = expires,
        };

    static double Epoch(string iso) => DateTimeOffset.Parse(iso + "T12:00:00+08:00").ToUnixTimeSeconds();

    [Fact]
    public void SignParse_Roundtrip()
    {
        var text = License.Sign(LicData(), Secret);
        var lic = License.Parse(text, Machine);
        Assert.Equal("demo", lic.Edition);
        Assert.Equal("test", lic.Customer);
        Assert.Equal(new DateOnly(2099, 12, 31), lic.Expires);
    }

    [Fact]
    public void Canonical_Matches_Python_JsonDumps()
    {
        // imgtestpy (Win7 build) verifies the same bytes: Python json.dumps(ensure_ascii=False,
        // sort_keys=True, separators=(',',':')) on {"id":..,"customer":..,"machine":..,"edition":..,
        // "issued":..,"expires":..}. Non-ASCII stays raw UTF-8.
        var lic = new Dictionary<string, string>
        {
            ["id"] = "abc", ["customer"] = "测试 A/B+\"x\"", ["machine"] = Machine,
            ["edition"] = "full", ["issued"] = "2026-01-01", ["expires"] = "2027-01-01",
        };
        Assert.Equal("{\"customer\":\"测试 A/B+\\\"x\\\"\",\"edition\":\"full\",\"expires\":\"2027-01-01\"," +
            "\"id\":\"abc\",\"issued\":\"2026-01-01\",\"machine\":\"AAAA-BBBB-CCCC-DDDD\"}",
            Encoding.UTF8.GetString(License.Canonical(lic)));
    }

    [Fact]
    public void Parse_PythonIssuedFile_Works()
    {
        // tests/fixtures/python_issued.lic was produced by imgtestpy tools/make_license.py with Secret:
        // proves the Win7 Python build and this app verify the same file.
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "python_issued.lic"));
        var l = License.Parse(text, Machine);
        Assert.Equal("Python签发", l.Customer);
    }

    [Fact]
    public void Parse_WrongMachine_Rejected()
        => Assert.Throws<LicenseError>(() => License.Parse(License.Sign(LicData(), Secret), "EEEE-0000-0000-0000"));

    [Fact]
    public void Parse_Tampered_Rejected()
    {
        var text = License.Sign(LicData(), Secret).Replace("demo", "full");
        Assert.Throws<LicenseError>(() => License.Parse(text, Machine));
    }

    [Fact]
    public void Check_Expired_Rejected()
    {
        var path = Path.Combine(_dir, "license.lic");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, License.Sign(LicData(expires: "2026-01-10"), Secret));
        Assert.NotNull(License.Check(path, machine: Machine, now: Epoch("2026-01-10")));   // expiry day itself still valid
        var e = Assert.Throws<LicenseError>(() => License.Check(path, machine: Machine, now: Epoch("2026-01-11")));
        Assert.Contains("到期", e.Message);
    }

    [Fact]
    public void Check_ClockRolledBack_Rejected()
    {
        var path = Path.Combine(_dir, "license.lic");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, License.Sign(LicData(), Secret));
        License.Check(path, machine: Machine, now: Epoch("2026-06-01"));                    // records clock.json
        Assert.Throws<LicenseError>(() => License.Check(path, machine: Machine, now: Epoch("2026-05-30")));
    }
}
