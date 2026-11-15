#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Krtuworks
{
    /// <summary>One HTTP request as the SDK makes it.</summary>
    public sealed class HttpCall
    {
        public string Method = "GET";
        public string Url = "";
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[]? Body;
    }

    /// <summary>One HTTP answer: status, headers (any case) and the raw body.</summary>
    public sealed class HttpAnswer
    {
        public int Status;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = Array.Empty<byte>();

        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    }

    /// <summary>
    /// How the SDK talks HTTP. Unity uses UnityWebRequest (Runtime/Unity); anything else can use
    /// <see cref="HttpClientTransport"/>. A failure to get any answer throws `network_error`.
    /// </summary>
    public interface IHttpTransport
    {
        Task<HttpAnswer> SendAsync(HttpCall call);
    }

    /// <summary>HttpClient, for desktop builds and tools.</summary>
    public sealed class HttpClientTransport : IHttpTransport
    {
        static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        public async Task<HttpAnswer> SendAsync(HttpCall call)
        {
            using var request = new HttpRequestMessage(new HttpMethod(call.Method), call.Url);
            if (call.Body != null) request.Content = new ByteArrayContent(call.Body);
            foreach (var h in call.Headers)
            {
                if (h.Key.StartsWith("content-", StringComparison.OrdinalIgnoreCase))
                {
                    request.Content ??= new ByteArrayContent(Array.Empty<byte>());
                    request.Content.Headers.Remove(h.Key);
                    request.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
                else request.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            HttpResponseMessage response;
            try
            {
                response = await Client.SendAsync(request);
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException)
            {
                throw new KrtuworksException("network_error", $"No answer from {new Uri(call.Url).Host}: {e.Message}");
            }
            using (response)
            {
                var answer = new HttpAnswer { Status = (int)response.StatusCode, Body = await response.Content.ReadAsByteArrayAsync() };
                foreach (var h in response.Headers.Concat(response.Content.Headers)) answer.Headers[h.Key] = string.Join(", ", h.Value);
                return answer;
            }
        }
    }

    /// <summary>
    /// Where the SDK keeps what must outlive the app: queues, the remote config, meta (an
    /// <see cref="IKrtuStore"/>), and the secrets (tokens, the device key) in an <see cref="ISecretStore"/>.
    /// Values are JSON text.
    /// </summary>
    public interface IKrtuStore
    {
        Task<string?> GetAsync(string key);
        Task SetAsync(string key, string value);
        Task DeleteAsync(string key);
        Task<IReadOnlyList<string>> KeysAsync(string prefix);
    }

    /// <summary>The platform's secret storage (Keychain, DPAPI, Keystore, Secret Service).</summary>
    public interface ISecretStore : IKrtuStore
    {
    }

    /// <summary>Kept in this process only (tests, tools).</summary>
    public sealed class MemoryStore : ISecretStore
    {
        readonly Dictionary<string, string> data = new Dictionary<string, string>();

        public Task<string?> GetAsync(string key)
        {
            lock (data) return Task.FromResult(data.TryGetValue(key, out var v) ? v : null);
        }

        public Task SetAsync(string key, string value)
        {
            lock (data) data[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key)
        {
            lock (data) data.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> KeysAsync(string prefix)
        {
            lock (data) return Task.FromResult<IReadOnlyList<string>>(data.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList());
        }
    }
}

namespace Krtuworks.Internal
{
    internal static class Json
    {
        internal static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            DateParseHandling = DateParseHandling.None,
        };

        internal static string Write(object value) => JsonConvert.SerializeObject(value, Formatting.None, Settings);

        internal static T? Read<T>(string? text) where T : class => text == null ? null : JsonConvert.DeserializeObject<T>(text, Settings);

        internal static async Task<T?> Get<T>(IKrtuStore store, string key) where T : class => Read<T>(await store.GetAsync(key));

        internal static Task Put(IKrtuStore store, string key, object value) => store.SetAsync(key, Write(value));

        internal static JObject? Parse(byte[] body)
        {
            if (body.Length == 0) return null;
            try
            {
                using var reader = new JsonTextReader(new System.IO.StringReader(Encoding.UTF8.GetString(body))) { DateParseHandling = DateParseHandling.None };
                return JToken.ReadFrom(reader) as JObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>One-at-a-time sections by name, in this process.</summary>
    internal static class Locks
    {
        static readonly Dictionary<string, SemaphoreSlim> Gates = new Dictionary<string, SemaphoreSlim>();

        sealed class Releaser : IDisposable
        {
            readonly SemaphoreSlim gate;
            public Releaser(SemaphoreSlim gate) => this.gate = gate;
            public void Dispose() => gate.Release();
        }

        internal static async Task<IDisposable> Take(string name)
        {
            SemaphoreSlim gate;
            lock (Gates)
            {
                if (!Gates.TryGetValue(name, out gate!)) Gates[name] = gate = new SemaphoreSlim(1, 1);
            }
            await gate.WaitAsync();
            return new Releaser(gate);
        }
    }

    /// <summary>
    /// One run at a time: callers that come while a run is going get the same task. A run that finished
    /// before it was handed back (everything it awaited was already done) is never kept, so the next caller
    /// starts a fresh one instead of reading an old result.
    /// </summary>
    internal sealed class SingleFlight<T>
    {
        readonly object gate = new object();
        Task<T>? current;

        internal Task<T> Run(Func<Task<T>> start)
        {
            Task<T> task;
            lock (gate)
            {
                if (current != null) return current;
                task = start();
                if (task.IsCompleted) return task;
                current = task;
            }
            _ = Clear(task);
            return task;
        }

        async Task Clear(Task<T> task)
        {
            try { await task; }
            catch (Exception) { /* the caller sees it */ }
            lock (gate)
            {
                if (current == task) current = null;
            }
        }
    }

    /// <summary>
    /// Time as the server reads it: the device's wall clock, this launch's id, the time
    /// since the launch (monotonic) and, once known, how far the device clock is from the server's.
    /// </summary>
    internal sealed class Clock
    {
        internal readonly string LaunchId = Ids.UuidV7();
        readonly Stopwatch started = Stopwatch.StartNew();
        internal double? ServerOffsetMs { get; private set; }

        internal long MonotonicMs => started.ElapsedMilliseconds;

        internal long ServerNowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)(ServerOffsetMs ?? 0);

        internal void NoteServerTime(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                ServerOffsetMs = at.ToUnixTimeMilliseconds() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        internal static string IsoNow() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A parsed answer: the status, the headers and the JSON body (null when there is none).</summary>
    internal sealed class Answer
    {
        internal int Status;
        internal HttpAnswer Raw = new HttpAnswer();
        internal JObject? Json;
    }

    internal static class Wire
    {
        internal static async Task<Answer> Send(IHttpTransport http, Clock clock, HttpCall call)
        {
            var raw = await http.SendAsync(call);
            clock.NoteServerTime(raw.Header("date"));
            var type = raw.Header("content-type") ?? "";
            var json = type.Contains("json") ? Json.Parse(raw.Body) : null;
            var serverTime = json?["serverTime"]?.Type == JTokenType.String ? (string?)json["serverTime"] : null;
            if (serverTime != null) clock.NoteServerTime(serverTime);
            return new Answer { Status = raw.Status, Raw = raw, Json = json };
        }

        /// <summary>
        /// The error an answer carries: the server and the /v1/guest calls answer `{ error: { code, message } }`,
        /// the token endpoint and the device-key challenge OAuth's `{ error, error_description }`.
        /// </summary>
        internal static KrtuworksException ErrorOf(Answer answer)
        {
            var json = (JObject?)answer.Json?.DeepClone() ?? new JObject();
            var code = $"http_{answer.Status}";
            var message = $"The server answered {answer.Status}.";
            var error = json["error"];
            if (error is JObject e)
            {
                if (e["code"]?.Type == JTokenType.String) code = (string)e["code"]!;
                if (e["message"]?.Type == JTokenType.String) message = (string)e["message"]!;
            }
            else if (error?.Type == JTokenType.String)
            {
                code = (string)error!;
                if (json["error_description"]?.Type == JTokenType.String)
                {
                    message = (string)json["error_description"]!;
                    json["description"] = message;
                }
            }
            json.Remove("error");
            json.Remove("error_description");
            double? retryAfter = null;
            if (json["retryAfter"]?.Type == JTokenType.Integer || json["retryAfter"]?.Type == JTokenType.Float) retryAfter = (double)json["retryAfter"]!;
            else if (double.TryParse(answer.Raw.Header("retry-after"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ra)) retryAfter = ra;
            return new KrtuworksException(code, message, answer.Status, retryAfter, json);
        }

        /// <summary>A form body (the token endpoint wants one).</summary>
        internal static HttpCall Form(string url, IEnumerable<KeyValuePair<string, string?>> fields)
        {
            var parts = fields.Where(f => f.Value != null).Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value!)}");
            var call = new HttpCall { Method = "POST", Url = url, Body = Encoding.UTF8.GetBytes(string.Join("&", parts)) };
            call.Headers["content-type"] = "application/x-www-form-urlencoded";
            return call;
        }
    }
}
