#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Krtuworks.Internal;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Krtuworks.Tests
{
    internal sealed class Reply
    {
        internal int Status = 200;
        internal JToken? Json;
        internal byte[]? Body;
        internal Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class FakeCall
    {
        internal string Method = "";
        internal string Path = "";
        internal Dictionary<string, string> Query = new Dictionary<string, string>();
        internal Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, string>? Form;
        internal JObject? Json;
        internal byte[] Raw = Array.Empty<byte>();

        internal string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        internal string? Field(string name) => Form != null && Form.TryGetValue(name, out var v) ? v : null;
    }

    /// <summary>A stand-in for id. and dev.: routes by method and path, records every call (krtuworks-sdk-js/test/fakeServer.ts).</summary>
    internal sealed class FakeServer : IHttpTransport
    {
        readonly List<FakeCall> calls = new List<FakeCall>();
        readonly Dictionary<string, Func<FakeCall, Task<Reply>>> routes = new Dictionary<string, Func<FakeCall, Task<Reply>>>();
        /// <summary>Calls to these paths fail with no answer (a dropped connection).</summary>
        internal readonly HashSet<string> Offline = new HashSet<string>();
        /// <summary>How far the server's clock is from this machine's.</summary>
        internal TimeSpan ClockOffset = TimeSpan.Zero;

        internal List<FakeCall> Calls
        {
            get { lock (calls) return calls.ToList(); }
        }

        internal FakeServer On(string method, string path, Func<FakeCall, Reply> handler)
        {
            lock (routes) routes[$"{method} {path}"] = call => Task.FromResult(handler(call));
            return this;
        }

        /// <summary>The handler a route has now (to wrap it).</summary>
        internal Func<FakeCall, Task<Reply>> RouteFor(string method, string path)
        {
            lock (routes) return routes[$"{method} {path}"];
        }

        internal FakeServer OnAsync(string method, string path, Func<FakeCall, Task<Reply>> handler)
        {
            lock (routes) routes[$"{method} {path}"] = handler;
            return this;
        }

        readonly List<(string Method, string Prefix, Func<FakeCall, Task<Reply>> Handler)> prefixes = new List<(string, string, Func<FakeCall, Task<Reply>>)>();

        /// <summary>A route for every path under `prefix` (ids a test cannot know in advance); exact routes win.</summary>
        internal FakeServer OnPrefix(string method, string prefix, Func<FakeCall, Reply> handler)
        {
            lock (routes) prefixes.Add((method, prefix, call => Task.FromResult(handler(call))));
            return this;
        }

        internal int Count(string method, string path) => Calls.Count(c => c.Method == method && c.Path == path);

        static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split(new[] { '=' }, 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");

        static byte[] Gunzip(byte[] raw)
        {
            using var input = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
        }

        public async Task<HttpAnswer> SendAsync(HttpCall request)
        {
            await Task.Yield();
            var url = new Uri(request.Url);
            var raw = request.Body ?? Array.Empty<byte>();
            if (request.Headers.TryGetValue("content-encoding", out var encoding) && encoding == "gzip") raw = Gunzip(raw);
            var text = Encoding.UTF8.GetString(raw);
            var type = request.Headers.TryGetValue("content-type", out var t) ? t : "";
            var call = new FakeCall
            {
                Method = request.Method,
                Path = url.AbsolutePath,
                Query = ParseQuery(url.Query),
                Headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase),
                Raw = raw,
                Form = type.Contains("x-www-form-urlencoded") ? ParseQuery(text) : null,
                Json = type.Contains("json") && text.Length > 0 ? JObject.Parse(text) : null,
            };
            lock (calls) calls.Add(call);
            if (Offline.Contains(call.Path)) throw new KrtuworksException("network_error", $"No answer from {url.Host}: offline");
            Func<FakeCall, Task<Reply>>? handler;
            lock (routes)
            {
                if (!routes.TryGetValue($"{call.Method} {call.Path}", out handler))
                {
                    var hit = prefixes.FirstOrDefault(p => p.Method == call.Method && call.Path.StartsWith(p.Prefix, StringComparison.Ordinal));
                    handler = hit.Handler;
                }
            }
            var reply = handler != null
                ? await handler(call)
                : new Reply { Status = 404, Json = new JObject { ["error"] = new JObject { ["code"] = "not_found", ["message"] = call.Path } } };
            var answer = new HttpAnswer { Status = reply.Status };
            answer.Headers["date"] = (DateTimeOffset.UtcNow + ClockOffset).ToString("r");
            foreach (var h in reply.Headers) answer.Headers[h.Key] = h.Value;
            if (reply.Json != null)
            {
                answer.Headers["content-type"] = "application/json";
                answer.Body = Encoding.UTF8.GetBytes(reply.Json.ToString(Formatting.None));
            }
            else answer.Body = reply.Body ?? Array.Empty<byte>();
            return answer;
        }

        // ── Helpers the tests share ─────────────────────────────────────────

        static string B64(JObject o) => Ids.Base64Url(o.ToString(Formatting.None));

        /// <summary>An unsigned JWT the SDK reads (it never checks signatures: the servers do).</summary>
        internal static string FakeJwt(JObject claims) => $"{B64(new JObject { ["alg"] = "ES256", ["typ"] = "at+jwt" })}.{B64(claims)}.sig";

        /// <summary>Checks a device-key proof the way Krtuworks ID does (header key, signature, claims).</summary>
        internal static (JObject header, JObject payload, bool ok) VerifyProof(string assertion)
        {
            var parts = assertion.Split('.');
            var header = JObject.Parse(Encoding.UTF8.GetString(Ids.FromBase64Url(parts[0])));
            var payload = JObject.Parse(Encoding.UTF8.GetString(Ids.FromBase64Url(parts[1])));
            var jwk = (JObject)header["jwk"]!;
            var ok = P256.Verify(Ids.FromBase64Url((string)jwk["x"]!), Ids.FromBase64Url((string)jwk["y"]!), Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Ids.FromBase64Url(parts[2]));
            return (header, payload, ok);
        }

        internal static KrtuworksAppConfig App => KrtuworksAppConfig.FromJson(
            "{\"appId\":\"app_aaaaaaaaaaaaaaaaaaaa\",\"slug\":\"buried\",\"environments\":{\"TEST\":{\"clientId\":\"buried.test.native\",\"deviceClientId\":\"buried.test.device\"}}}");

        internal KrtuworksOptions Options(MemoryStore? store = null, MemoryStore? secrets = null, Action<KrtuworksOptions>? extra = null)
        {
            var shared = store ?? new MemoryStore();
            var options = new KrtuworksOptions
            {
                Config = App,
                Environment = "TEST",
                Platform = "WINDOWS",
                Build = "1.0.0",
                Http = this,
                Store = shared,
                Secrets = secrets ?? shared,
                Sessions = false,
            };
            extra?.Invoke(options);
            return options;
        }

        static int tokenSeq;

        /// <summary>A token answer for `sub`, with a fresh refresh token.</summary>
        internal static JObject TokenAnswer(string sub, JObject? idExtra = null, int ttlSeconds = 600)
        {
            var n = System.Threading.Interlocked.Increment(ref tokenSeq);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var id = new JObject { ["iss"] = "https://id.krtuworks.com/oidc", ["sub"] = sub, ["aud"] = "buried.test.native", ["exp"] = now + 600 };
            if (idExtra != null) id.Merge(idExtra);
            return new JObject
            {
                ["access_token"] = FakeJwt(new JObject { ["sub"] = sub, ["aud"] = "devapi", ["exp"] = now + ttlSeconds, ["scope"] = "app:player", ["n"] = n }),
                ["refresh_token"] = $"rt-{n}",
                ["id_token"] = FakeJwt(id),
                ["token_type"] = "Bearer",
                ["expires_in"] = ttlSeconds,
            };
        }

        internal static Reply Ok(JToken json) => new Reply { Json = json };

        internal static Reply OAuthError(string error, string description) => new Reply { Status = 400, Json = new JObject { ["error"] = error, ["error_description"] = description } };

        /// <summary>Routes a device-key guest through: challenge and grant (one guest per key).</summary>
        internal void GuestRoutes(string sub = "pw_guest_aaaaaaaaaaaa")
        {
            var nonce = 0;
            var known = new HashSet<string>();
            On("POST", "/oidc/device-key/challenge", _ => Ok(new JObject { ["nonce"] = $"n{System.Threading.Interlocked.Increment(ref nonce)}".PadRight(43, 'x'), ["expires_in"] = 60 }));
            On("POST", "/oidc/token", call =>
            {
                var grant = call.Field("grant_type");
                if (grant == Identity.DeviceKeyGrant)
                {
                    var (_, payload, ok) = VerifyProof(call.Field("assertion")!);
                    if (!ok) return OAuthError("invalid_grant", "The device key proof was refused: signature.");
                    var thumbprint = (string)payload["key_thumbprint"]!;
                    lock (known)
                    {
                        if (!known.Contains(thumbprint))
                        {
                            if (call.Field("birth_date") == null) return OAuthError("invalid_birth_date", "Enter a valid date of birth.");
                            if (call.Field("accepted_terms") != "TERMS:v1 PRIVACY:v1") return OAuthError("terms_required", "Accept the current terms: TERMS:v2 PRIVACY:v1.");
                            known.Add(thumbprint);
                        }
                    }
                    return Ok(TokenAnswer(sub));
                }
                if (grant == "refresh_token") return Ok(TokenAnswer(sub));
                return OAuthError("unsupported_grant_type", "");
            });
        }

        /// <summary>The remote config route; `Bump` makes a new version (a new ETag).</summary>
        internal Action ConfigRoute(JObject config, JArray? switches = null, int startVersion = 1)
        {
            var version = startVersion;
            On("GET", "/v1/apps/buried/config", call =>
            {
                var etag = $"\"v{version}\"";
                if (call.Header("if-none-match") == etag) return new Reply { Status = 304 };
                var reply = Ok(new JObject
                {
                    ["appId"] = "app_x", ["environment"] = "TEST", ["platform"] = "WINDOWS", ["version"] = $"{version}.0",
                    ["config"] = config, ["switches"] = switches ?? new JArray(), ["serverTime"] = Clock.IsoNow(),
                });
                reply.Headers["etag"] = etag;
                return reply;
            });
            return () => version++;
        }

        /// <summary>Takes every event batch (all accepted) into `into`; statistics are on.</summary>
        internal void AcceptEvents(List<JObject> into)
        {
            On("POST", "/v1/events", call =>
            {
                var body = call.Json!;
                lock (into) into.AddRange(((JArray)body["events"]!).Cast<JObject>());
                return Ok(new JObject
                {
                    ["batchId"] = body["batchId"], ["accepted"] = new JArray(((JArray)body["events"]!).Select(e => e["id"])),
                    ["duplicate"] = new JArray(), ["rejected"] = new JArray(), ["retry"] = new JArray(), ["telemetry"] = true,
                });
            });
            On("GET", "/v1/me/telemetry-opt-out", _ => Ok(new JObject { ["optedOut"] = false, ["updatedAt"] = null }));
        }

        /// <summary>Applies every progress op (`value` echoed).</summary>
        internal void ApplyProgress(List<JObject>? into = null)
        {
            On("POST", "/v1/me/progress", call =>
            {
                var ops = ((JArray)call.Json!["ops"]!).Cast<JObject>().ToList();
                if (into != null) lock (into) into.AddRange(ops);
                return Ok(new JObject
                {
                    ["results"] = new JArray(ops.Select(o => new JObject { ["opId"] = o["opId"], ["status"] = "applied", ["type"] = o["type"], ["key"] = o["key"], ["value"] = o["value"] })),
                });
            });
        }
    }
}
