#nullable enable
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Krtuworks.Internal
{
    /// <summary>
    /// The device's own key: a P-256 pair made here, kept in the platform's secret store
    ///. A guest's account is bound to it; it never leaves the device. `known` once the
    /// server has taken it; `subject` the guest it signs in.
    /// </summary>
    internal sealed class DeviceKey
    {
        [JsonProperty("d")] public string D = "";
        [JsonProperty("x")] public string X = "";
        [JsonProperty("y")] public string Y = "";
        [JsonProperty("thumbprint")] public string Thumbprint = "";
        [JsonProperty("known")] public bool Known;
        [JsonProperty("subject")] public string? Subject;

        internal static DeviceKey Create()
        {
            var d = P256.NewPrivateKey();
            var (x, y) = P256.PublicKey(d);
            var key = new DeviceKey { D = Ids.Base64Url(d), X = Ids.Base64Url(x), Y = Ids.Base64Url(y) };
            key.Thumbprint = ThumbprintOf(key.X, key.Y);
            return key;
        }

        /// <summary>RFC 7638: the required members only, in lexicographic order, no whitespace.</summary>
        internal static string ThumbprintOf(string x, string y) => Ids.Base64Url(Ids.Sha256($"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{x}\",\"y\":\"{y}\"}}"));

        /// <summary>
        /// The compact JWS a device-key call carries: header `{ alg: ES256, typ: krtu-device-key+jwt, jwk }`,
        /// claims `{ nonce, client_id, purpose, key_thumbprint, iat }`. `iat` is the server's clock as far as
        /// it is known: a device whose clock is off still signs inside the server's minute.
        /// </summary>
        internal string SignProof(string nonce, string clientId, string purpose, long iat)
        {
            var header = new JObject
            {
                ["alg"] = "ES256",
                ["typ"] = "krtu-device-key+jwt",
                ["jwk"] = new JObject { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = X, ["y"] = Y },
            };
            var claims = new JObject { ["nonce"] = nonce, ["client_id"] = clientId, ["purpose"] = purpose, ["key_thumbprint"] = Thumbprint, ["iat"] = iat };
            var input = $"{Ids.Base64Url(header.ToString(Formatting.None))}.{Ids.Base64Url(claims.ToString(Formatting.None))}";
            var signature = P256.Sign(Ids.FromBase64Url(D), Encoding.ASCII.GetBytes(input));
            return $"{input}.{Ids.Base64Url(signature)}";
        }
    }

    internal static class Jwt
    {
        /// <summary>A JWT's claims, unverified: the SDK reads its own tokens, the servers judge them.</summary>
        internal static JObject Claims(string token)
        {
            var parts = token.Split('.');
            if (parts.Length != 3) throw new KrtuworksException("invalid_response", "Not a JWT.");
            return JObject.Parse(Encoding.UTF8.GetString(Ids.FromBase64Url(parts[1])));
        }
    }
}
