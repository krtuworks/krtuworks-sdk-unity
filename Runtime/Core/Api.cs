#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Krtuworks.Internal
{
    internal sealed class PlayerCall
    {
        internal string Method = "GET";
        internal Dictionary<string, string?>? Query;
        /// <summary>A JSON body (sent as application/json).</summary>
        internal JToken? Json;
        /// <summary>A raw body with its own content type in <see cref="Headers"/>.</summary>
        internal byte[]? Body;
        internal Dictionary<string, string>? Headers;
        /// <summary>Statuses that are answers, not failures (304, 202 …); 2xx always are.</summary>
        internal int[]? Accept;
        /// <summary>
        /// The player the call is made for (a queue's owner, a stored save's). Default: whoever plays when the
        /// call starts. The call goes only with that player's token; with any other it is not sent.
        /// </summary>
        internal string? Owner;
    }

    /// <summary>The player's calls to the server (`/v1`, Bearer + X-Krtu-Build). Same rules as krtuworks-sdk-js/src/api.ts.</summary>
    internal sealed class Api
    {
        readonly Resolved opts;
        readonly Identity identity;
        readonly IHttpTransport http;
        readonly Clock clock;

        internal Api(Resolved opts, Identity identity, IHttpTransport http, Clock clock)
        {
            this.opts = opts;
            this.identity = identity;
            this.http = http;
            this.clock = clock;
        }

        internal string Url(string path, Dictionary<string, string?>? query)
        {
            var url = $"{opts.DevOrigin}{path}";
            var parts = (query ?? new Dictionary<string, string?>()).Where(q => q.Value != null).Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value!)}").ToList();
            return parts.Count == 0 ? url : $"{url}?{string.Join("&", parts)}";
        }

        static bool Accepted(PlayerCall call, int status) => call.Accept != null && call.Accept.Contains(status);

        /// <summary>A call without a player (the remote config).</summary>
        internal async Task<Answer> OpenAsync(string path, PlayerCall call)
        {
            var request = new HttpCall { Method = call.Method, Url = Url(path, call.Query) };
            foreach (var h in call.Headers ?? new Dictionary<string, string>()) request.Headers[h.Key] = h.Value;
            var answer = await Wire.Send(http, clock, request);
            if (answer.Status >= 400 && !Accepted(call, answer.Status)) throw Wire.ErrorOf(answer);
            return answer;
        }

        /// <summary>
        /// A player's call, for `call.Owner` (or whoever plays now). Every attempt sends only with a token of that
        /// player: when the token in hand is another one's (the player changed while it was fetched, or before
        /// the retry after a 401), nothing is sent (`context_changed`). An expired or revoked token is refreshed
        /// once and the call made again; a merged account is reported to the identity; an answer that arrives
        /// after the player changed is dropped too, so it can never land in another player's state.
        /// </summary>
        internal async Task<Answer> PlayerAsync(string path, PlayerCall call)
        {
            var generation = identity.Generation;
            var owner = call.Owner ?? identity.Player?.Subject ?? throw new KrtuworksException("signed_out", "No player is signed in.");
            var answer = await Attempt(path, call, false, owner);
            if (answer.Status == 401)
            {
                var err = Wire.ErrorOf(answer);
                if (err.Code == "account_merged") await identity.MarkMergedAsync(owner, (string?)err.Details["mergedInto"]);
                if (err.Code == "account_deleted")
                {
                    await identity.MarkDeletedAsync(owner);
                    throw err;
                }
                if (err.Code == "token_expired" || err.Code == "token_revoked" || err.Code == "invalid_token") answer = await Attempt(path, call, true, owner);
            }
            if (identity.Generation != generation) throw new KrtuworksException("context_changed", "The player changed while the call ran; it was dropped.");
            if ((answer.Status >= 300 || answer.Status < 200) && !Accepted(call, answer.Status))
            {
                var refused = Wire.ErrorOf(answer);
                if (refused.Code == "account_merging") identity.NoteMerging(owner, true);
                throw refused;
            }
            identity.NoteMerging(owner, false);
            return answer;
        }

        async Task<Answer> Attempt(string path, PlayerCall call, bool forceRefresh, string owner)
        {
            (string Token, string Subject) credentials;
            try
            {
                credentials = await identity.CredentialsAsync(forceRefresh);
            }
            catch (KrtuworksException err)
            {
                // A failure of the token, not of what the call carries: no queue may drop its item for it.
                err.AuthLayer = true;
                throw;
            }
            if (credentials.Subject != owner) throw new KrtuworksException("context_changed", "The player changed while the call ran; it was dropped.") { AuthLayer = true };
            var request = new HttpCall { Method = call.Method, Url = Url(path, call.Query), Body = call.Body };
            request.Headers["authorization"] = $"Bearer {credentials.Token}";
            request.Headers["x-krtu-build"] = opts.Build;
            foreach (var h in call.Headers ?? new Dictionary<string, string>()) request.Headers[h.Key] = h.Value;
            if (call.Json != null)
            {
                request.Headers["content-type"] = "application/json";
                request.Body = Encoding.UTF8.GetBytes(call.Json.ToString(Newtonsoft.Json.Formatting.None));
            }
            return await Wire.Send(http, clock, request);
        }
    }
}
