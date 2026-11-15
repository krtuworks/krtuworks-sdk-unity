#nullable enable
using System;
using System.Numerics;
using System.Security.Cryptography;

namespace Krtuworks.Internal
{
    /// <summary>
    /// ECDSA on P-256 with SHA-256 in plain C#: Unity's runtimes do not offer it on every platform, and a
    /// device key must sign the same everywhere. Nonces are deterministic (RFC 6979), so no platform random
    /// source is trusted for them; the signature is the raw r||s JWS wants. Not constant-time: the key only
    /// guards a guest account on the player's own device.
    /// </summary>
    internal static class P256
    {
        static readonly BigInteger P = Hex("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff");
        static readonly BigInteger N = Hex("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551");
        static readonly BigInteger B = Hex("5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b");
        static readonly BigInteger Gx = Hex("6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296");
        static readonly BigInteger Gy = Hex("4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5");

        static BigInteger Hex(string hex) => ToInt(FromHex(hex));

        internal static byte[] FromHex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        static BigInteger Mod(BigInteger a, BigInteger m)
        {
            var r = BigInteger.Remainder(a, m);
            return r.Sign < 0 ? r + m : r;
        }

        static BigInteger Inverse(BigInteger a, BigInteger m) => BigInteger.ModPow(Mod(a, m), m - 2, m);

        internal static BigInteger ToInt(byte[] bigEndian) => new BigInteger(bigEndian, isUnsigned: true, isBigEndian: true);

        internal static byte[] ToBytes32(BigInteger value)
        {
            var raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (raw.Length == 32) return raw;
            var out32 = new byte[32];
            Buffer.BlockCopy(raw, 0, out32, 32 - raw.Length, raw.Length);
            return out32;
        }

        // Points in Jacobian coordinates (X, Y, Z): x = X/Z^2, y = Y/Z^3; Z = 0 is the point at infinity.
        struct Jacobian
        {
            public BigInteger X, Y, Z;
            public Jacobian(BigInteger x, BigInteger y, BigInteger z) { X = x; Y = y; Z = z; }
            public bool Infinity => Z.IsZero;
        }

        static Jacobian Double(Jacobian p)
        {
            if (p.Infinity || p.Y.IsZero) return new Jacobian(BigInteger.One, BigInteger.One, BigInteger.Zero);
            // a = -3: M = 3 (X - Z^2)(X + Z^2)
            var zz = Mod(p.Z * p.Z, P);
            var m = Mod(3 * Mod(p.X - zz, P) * Mod(p.X + zz, P), P);
            var yy = Mod(p.Y * p.Y, P);
            var s = Mod(4 * p.X * yy, P);
            var x = Mod(m * m - 2 * s, P);
            var y = Mod(m * (s - x) - 8 * Mod(yy * yy, P), P);
            var z = Mod(2 * p.Y * p.Z, P);
            return new Jacobian(x, y, z);
        }

        static Jacobian Add(Jacobian p, Jacobian q)
        {
            if (p.Infinity) return q;
            if (q.Infinity) return p;
            var z1z1 = Mod(p.Z * p.Z, P);
            var z2z2 = Mod(q.Z * q.Z, P);
            var u1 = Mod(p.X * z2z2, P);
            var u2 = Mod(q.X * z1z1, P);
            var s1 = Mod(p.Y * q.Z * z2z2, P);
            var s2 = Mod(q.Y * p.Z * z1z1, P);
            if (u1 == u2) return s1 == s2 ? Double(p) : new Jacobian(BigInteger.One, BigInteger.One, BigInteger.Zero);
            var h = Mod(u2 - u1, P);
            var r = Mod(s2 - s1, P);
            var hh = Mod(h * h, P);
            var hhh = Mod(h * hh, P);
            var v = Mod(u1 * hh, P);
            var x = Mod(r * r - hhh - 2 * v, P);
            var y = Mod(r * (v - x) - s1 * hhh, P);
            var z = Mod(p.Z * q.Z * h, P);
            return new Jacobian(x, y, z);
        }

        static (BigInteger x, BigInteger y) Affine(Jacobian p)
        {
            var zi = Inverse(p.Z, P);
            var zi2 = Mod(zi * zi, P);
            return (Mod(p.X * zi2, P), Mod(p.Y * zi2 * zi, P));
        }

        static Jacobian Multiply(BigInteger k, BigInteger x, BigInteger y)
        {
            var result = new Jacobian(BigInteger.One, BigInteger.One, BigInteger.Zero);
            var addend = new Jacobian(x, y, BigInteger.One);
            var bits = k.ToByteArray(isUnsigned: true, isBigEndian: true);
            foreach (var b in bits)
            {
                for (var bit = 7; bit >= 0; bit--)
                {
                    result = Double(result);
                    if (((b >> bit) & 1) == 1) result = Add(result, addend);
                }
            }
            return result;
        }

        internal static bool OnCurve(BigInteger x, BigInteger y)
        {
            if (x.Sign < 0 || x >= P || y.Sign < 0 || y >= P) return false;
            return Mod(y * y, P) == Mod(x * x * x - 3 * x + B, P);
        }

        /// <summary>The public point of private scalar d, as 32-byte big-endian x and y.</summary>
        internal static (byte[] x, byte[] y) PublicKey(byte[] d)
        {
            var (x, y) = Affine(Multiply(ToInt(d), Gx, Gy));
            return (ToBytes32(x), ToBytes32(y));
        }

        /// <summary>A new private scalar in [1, n-1].</summary>
        internal static byte[] NewPrivateKey()
        {
            using var rng = RandomNumberGenerator.Create();
            var d = new byte[32];
            while (true)
            {
                rng.GetBytes(d);
                var v = ToInt(d);
                if (v.Sign > 0 && v < N) return d;
            }
        }

        static byte[] Hmac(byte[] key, params byte[][] parts)
        {
            using var h = new HMACSHA256(key);
            var len = 0;
            foreach (var p in parts) len += p.Length;
            var all = new byte[len];
            var at = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, all, at, p.Length); at += p.Length; }
            return h.ComputeHash(all);
        }

        /// <summary>Signs SHA-256(message) with d: r||s, 64 bytes (RFC 6979 nonce).</summary>
        internal static byte[] Sign(byte[] d, byte[] message)
        {
            byte[] h1;
            using (var sha = SHA256.Create()) h1 = sha.ComputeHash(message);
            var z = Mod(ToInt(h1), N);
            var x = ToBytes32(ToInt(d));
            var h = ToBytes32(z);
            var v = new byte[32];
            var k = new byte[32];
            for (var i = 0; i < 32; i++) v[i] = 0x01;
            k = Hmac(k, v, new byte[] { 0x00 }, x, h);
            v = Hmac(k, v);
            k = Hmac(k, v, new byte[] { 0x01 }, x, h);
            v = Hmac(k, v);
            var priv = ToInt(d);
            while (true)
            {
                v = Hmac(k, v);
                var kk = ToInt(v);
                if (kk.Sign > 0 && kk < N)
                {
                    var (rx, _) = Affine(Multiply(kk, Gx, Gy));
                    var r = Mod(rx, N);
                    if (!r.IsZero)
                    {
                        var s = Mod(Inverse(kk, N) * (z + r * priv), N);
                        if (!s.IsZero)
                        {
                            var sig = new byte[64];
                            Buffer.BlockCopy(ToBytes32(r), 0, sig, 0, 32);
                            Buffer.BlockCopy(ToBytes32(s), 0, sig, 32, 32);
                            return sig;
                        }
                    }
                }
                k = Hmac(k, v, new byte[] { 0x00 });
                v = Hmac(k, v);
            }
        }

        /// <summary>Checks an r||s signature of SHA-256(message) by the point (x, y).</summary>
        internal static bool Verify(byte[] x, byte[] y, byte[] message, byte[] signature)
        {
            if (signature.Length != 64) return false;
            var qx = ToInt(x);
            var qy = ToInt(y);
            if (!OnCurve(qx, qy)) return false;
            var r = ToInt(signature.AsSpan(0, 32).ToArray());
            var s = ToInt(signature.AsSpan(32, 32).ToArray());
            if (r.Sign <= 0 || r >= N || s.Sign <= 0 || s >= N) return false;
            byte[] digest;
            using (var sha = SHA256.Create()) digest = sha.ComputeHash(message);
            var z = Mod(ToInt(digest), N);
            var w = Inverse(s, N);
            var u1 = Mod(z * w, N);
            var u2 = Mod(r * w, N);
            var point = Add(Multiply(u1, Gx, Gy), Multiply(u2, qx, qy));
            if (point.Infinity) return false;
            var (px, _) = Affine(point);
            return Mod(px, N) == r;
        }
    }
}
