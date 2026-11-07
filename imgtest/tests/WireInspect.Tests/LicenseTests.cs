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
