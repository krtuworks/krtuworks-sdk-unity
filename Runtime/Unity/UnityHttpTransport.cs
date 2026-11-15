#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Krtuworks.Unity
{
    /// <summary>
    /// The SDK's HTTP through UnityWebRequest (every platform, WebGL included). Runs on the main thread: the
    /// SDK's awaits come back to Unity's synchronization context. No answer at all is `network_error`; an
    /// HTTP error status is an answer like any other.
    /// </summary>
    public sealed class UnityHttpTransport : IHttpTransport
    {
        /// <summary>Headers UnityWebRequest sets itself and refuses from the caller.</summary>
        static readonly HashSet<string> Managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "content-length", "host", "accept-encoding" };

        public int TimeoutSeconds = 30;

        public Task<HttpAnswer> SendAsync(HttpCall call)
        {
            var request = new UnityWebRequest(call.Url, call.Method) { downloadHandler = new DownloadHandlerBuffer(), timeout = TimeoutSeconds };
            if (call.Body != null)
            {
                request.uploadHandler = new UploadHandlerRaw(call.Body);
                if (call.Headers.TryGetValue("content-type", out var type)) request.uploadHandler.contentType = type;
            }
            foreach (var h in call.Headers)
            {
                if (!Managed.Contains(h.Key)) request.SetRequestHeader(h.Key, h.Value);
            }
            var done = new TaskCompletionSource<HttpAnswer>();
            var operation = request.SendWebRequest();
            operation.completed += _ =>
            {
                try
                {
                    if (request.result == UnityWebRequest.Result.ConnectionError || request.responseCode == 0)
                    {
                        done.SetException(new KrtuworksException("network_error", $"No answer from {new Uri(call.Url).Host}: {request.error}"));
                        return;
                    }
                    var answer = new HttpAnswer { Status = (int)request.responseCode, Body = request.downloadHandler?.data ?? Array.Empty<byte>() };
                    var headers = request.GetResponseHeaders();
                    if (headers != null) foreach (var h in headers) answer.Headers[h.Key] = h.Value;
                    done.SetResult(answer);
                }
                finally { request.Dispose(); }
            };
            return done.Task;
        }
    }
}
