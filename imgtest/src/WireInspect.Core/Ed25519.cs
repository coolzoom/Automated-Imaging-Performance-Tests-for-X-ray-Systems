using System.Numerics;
using System.Security.Cryptography;

namespace WireInspect.Core;

/// <summary>Pure-managed Ed25519 (RFC 8032 reference algorithm), used to verify license files offline.
/// Slow but dependency-free; only a few signatures are checked per launch. Not constant-time, so
/// signing should only happen on the vendor's own computer.</summary>
public static class Ed25519
{
    static readonly BigInteger P = (BigInteger.One << 255) - 19;
    static readonly BigInteger L = (BigInteger.One << 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    static readonly BigInteger D = Mod(-121665 * BigInteger.ModPow(121666, P - 2, P));
    static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
    static readonly BigInteger[] G = MakeG();

    static BigInteger Mod(BigInteger x) { x %= P; return x.Sign < 0 ? x + P : x; }

    static BigInteger[] MakeG()
    {
        var gy = Mod(4 * BigInteger.ModPow(5, P - 2, P));
        var gx = RecoverX(gy, 0)!.Value;
        return [gx, gy, BigInteger.One, Mod(gx * gy)];
    }

    static BigInteger[] Add(BigInteger[] p, BigInteger[] q)
    {
        var a = Mod((p[1] - p[0]) * (q[1] - q[0]));
        var b = Mod((p[1] + p[0]) * (q[1] + q[0]));
        var c = Mod(2 * p[3] * q[3] * D);
        var d2 = Mod(2 * p[2] * q[2]);
        var e = Mod(b - a); var f = Mod(d2 - c); var g = Mod(d2 + c); var h = Mod(b + a);
        return [Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h)];
    }

    static BigInteger[] Mul(BigInteger s, BigInteger[] p)
    {
        var q = new BigInteger[] { 0, 1, 1, 0 };
        while (s > 0)
        {
            if (!s.IsEven) q = Add(q, p);
            p = Add(p, p);
            s >>= 1;
        }
        return q;
    }

    static bool Equal(BigInteger[] p, BigInteger[] q)
        => Mod(p[0] * q[2] - q[0] * p[2]) == 0 && Mod(p[1] * q[2] - q[1] * p[2]) == 0;

    static BigInteger? RecoverX(BigInteger y, int sign)
    {
        if (y >= P) return null;
        var x2 = Mod((y * y - 1) * BigInteger.ModPow(Mod(D * y * y + 1), P - 2, P));
        if (x2 == 0) return sign == 0 ? BigInteger.Zero : null;
        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (Mod(x * x - x2) != 0) x = Mod(x * SqrtM1);
        if (Mod(x * x - x2) != 0) return null;
        if ((int)(x & 1) != sign) x = P - x;
        return x;
    }

    static byte[] Compress(BigInteger[] p)
    {
        var zinv = BigInteger.ModPow(p[2], P - 2, P);
        var x = Mod(p[0] * zinv); var y = Mod(p[1] * zinv);
        return ToLe(y | ((x & 1) << 255), 32);
    }

    static BigInteger[]? Decompress(byte[] s)
    {
        if (s.Length != 32) return null;
        var y = FromLe(s);
        int sign = (int)(y >> 255);
        y &= (BigInteger.One << 255) - 1;
        var x = RecoverX(y, sign);
        return x == null ? null : [x.Value, y, BigInteger.One, Mod(x.Value * y)];
    }

    static (BigInteger a, byte[] prefix) SecretExpand(byte[] secret)
    {
        if (secret.Length != 32) throw new ArgumentException("bad secret key length");
        var h = SHA512.HashData(secret);
        var a = FromLe(h[..32]);
        a &= (BigInteger.One << 254) - 8;
        a |= BigInteger.One << 254;
        return (a, h[32..]);
    }

    public static byte[] PublicKey(byte[] secret)
    {
        var (a, _) = SecretExpand(secret);
        return Compress(Mul(a, G));
    }

    public static byte[] Sign(byte[] secret, byte[] msg)
    {
        var (a, prefix) = SecretExpand(secret);
        var A = Compress(Mul(a, G));
        var r = FromLe(SHA512.HashData([.. prefix, .. msg])) % L;
        var R = Compress(Mul(r, G));
        var h = FromLe(SHA512.HashData([.. R, .. A, .. msg])) % L;
        return [.. R, .. ToLe((r + h * a) % L, 32)];
    }

    public static bool Verify(byte[] pub, byte[] msg, byte[] sig)
    {
        if (pub.Length != 32 || sig.Length != 64) return false;
        var A = Decompress(pub);
        var R = Decompress(sig[..32]);
        if (A == null || R == null) return false;
        var s = FromLe(sig[32..]);
        if (s >= L) return false;
        var h = FromLe(SHA512.HashData([.. sig[..32], .. pub, .. msg])) % L;
        return Equal(Mul(s, G), Add(R, Mul(h, A)));
    }

    static BigInteger FromLe(byte[] b) => new(b, isUnsigned: true, isBigEndian: false);

    static byte[] ToLe(BigInteger x, int len)
    {
        var b = x.ToByteArray(isUnsigned: true, isBigEndian: false);
        if (b.Length >= len) return b[..len];
        var o = new byte[len];
        b.CopyTo(o, 0);
        return o;
    }
}
