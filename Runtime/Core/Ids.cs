#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;

namespace Krtuworks.Internal
{
    internal static class Ids
    {
        static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        internal static byte[] RandomBytes(int count)
        {
            var b = new byte[count];
            lock (Rng) Rng.GetBytes(b);
            return b;
        }

        static string Uuid(byte[] b)
        {
            var hex = new StringBuilder(32);
            foreach (var x in b) hex.Append(x.ToString("x2"));
            var h = hex.ToString();
            return $"{h.Substring(0, 8)}-{h.Substring(8, 4)}-{h.Substring(12, 4)}-{h.Substring(16, 4)}-{h.Substring(20)}";
        }

        /// <summary>RFC 9562 UUIDv7: 48 bits of Unix milliseconds, then random. Made once per event, op and batch.</summary>
        internal static string UuidV7(long? nowMs = null)
        {
            var b = RandomBytes(16);
            var ts = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (var i = 5; i >= 0; i--) { b[i] = (byte)(ts & 0xff); ts >>= 8; }
            b[6] = (byte)((b[6] & 0x0f) | 0x70);
            b[8] = (byte)((b[8] & 0x3f) | 0x80);
            return Uuid(b);
        }

        internal static string UuidV4()
        {
            var b = RandomBytes(16);
            b[6] = (byte)((b[6] & 0x0f) | 0x40);
            b[8] = (byte)((b[8] & 0x3f) | 0x80);
            return Uuid(b);
        }

        internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        internal static string Base64Url(string text) => Base64Url(Encoding.UTF8.GetBytes(text));

        internal static byte[] FromBase64Url(string text)
        {
            var b64 = text.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(b64 + new string('=', (4 - b64.Length % 4) % 4));
        }

        internal static byte[] Sha256(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(bytes);
        }

        internal static byte[] Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));

        internal static string Sha256Hex(byte[] bytes)
        {
            var sb = new StringBuilder(64);
            foreach (var x in Sha256(bytes)) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>A URL-safe random string (PKCE verifier, state, nonce).</summary>
        internal static string RandomToken(int bytes = 32) => Base64Url(RandomBytes(bytes));

        /// <summary>Equal bytes, compared in a time that does not depend on where they differ.</summary>
        internal static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
