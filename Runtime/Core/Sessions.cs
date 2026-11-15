#nullable enable
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Krtuworks
{
    public sealed class SessionInfo
    {
        public string Id = "";
        /// <summary>The player the session is for.</summary>
        public string Owner = "";
        public string StartedAt = "";
        /// <summary>The platform account (Steam) the session proved and whether this player mirrors with it; null when none was proved.</summary>
        public PlatformLink? PlatformLink;
    }

    /// <summary>
    /// Whether the player mirrors (stats, achievements) with the platform account the session proved: ACTIVE
    /// (mirrored), PENDING_CONSENT (a second Steam account: ask the player, then
    /// <see cref="KrtuworksClient.AnswerPlatformConsentAsync"/>), REFUSED (the player said no), LIMIT (a third
    /// account; a game mirrors with two at most), MISMATCH (another Krtuworks ID mirrors with it here). The game
    /// hears the ones to tell the player through <see cref="KrtuworksClient.PlatformLinkNotice"/>.
    /// </summary>
    public sealed class PlatformLink
    {
        public string Platform = "";
        /// <summary>The player's own platform account (SteamID) the session proved.</summary>
        public string PlatformAccountId = "";
        public string State = "";
    }
}

namespace Krtuworks.Internal
{
    /// <summary>
    /// The play session, the rules of krtuworks-sdk-js/src/sessions.ts: opened once per launch and
    /// player, kept alive every `heartbeatSeconds` with the time actually in use (foreground only), ended when
    /// the app goes away. An open whose answer was lost is sent again with the same `clientSessionId` and
    /// gets the same session. No timer of its own: the client's tick asks <see cref="DueAsync"/>.
    /// </summary>
    internal sealed class SessionKeeper
    {
        readonly Resolved opts;
        readonly Api api;
        readonly Func<string?> subject;
        /// <summary>A Steam ticket for the session's ownership check, when the platform gives one.</summary>
        readonly Func<Task<string?>>? platformTicket;
        readonly Stopwatch clock = Stopwatch.StartNew();
        SessionInfo? current;
        string? clientSessionId;
        int heartbeatSeconds = 60;
        long nextHeartbeatAt = long.MaxValue;
        long activeMs;
        long? activeSince;
        /// <summary>Told after each heartbeat (the minutes of play moved).</summary>
        internal Action? OnHeartbeat;
        /// <summary>Told the licence list of a session answer (only an open that sent a platform proof gets one).</summary>
        internal Func<Newtonsoft.Json.Linq.JToken?, Task>? OnEntitlements;
        readonly SingleFlight<SessionInfo?> opening = new SingleFlight<SessionInfo?>();

        internal SessionKeeper(Resolved opts, Api api, Func<string?> subject, Func<Task<string?>>? platformTicket)
        {
            this.opts = opts;
            this.api = api;
            this.subject = subject;
            this.platformTicket = platformTicket;
            activeSince = clock.ElapsedMilliseconds;
        }

        internal SessionInfo? Session
        {
            get
            {
                var owner = subject();
                return current != null && current.Owner == owner ? current : null;
            }
        }

        internal bool Foreground => activeSince.HasValue;

        /// <summary>Seconds in use in this launch (foreground only).</summary>
        internal long ActiveSeconds => (activeMs + (activeSince.HasValue ? clock.ElapsedMilliseconds - activeSince.Value : 0)) / 1000;

        internal void SetForeground(bool foreground)
        {
            if (foreground && !activeSince.HasValue) activeSince = clock.ElapsedMilliseconds;
            if (!foreground && activeSince.HasValue)
            {
                activeMs += clock.ElapsedMilliseconds - activeSince.Value;
                activeSince = null;
            }
        }

        /// <summary>Opens the player's session if there is none (one at a time).</summary>
        internal Task<SessionInfo?> OpenAsync()
        {
            var session = Session;
            if (session != null) return Task.FromResult<SessionInfo?>(session);
            return opening.Run(DoOpen);
        }

        async Task<SessionInfo?> DoOpen()
        {
            var owner = subject();
            if (owner == null) return null;
            if (current != null && current.Owner != owner) Close();
            clientSessionId ??= Ids.UuidV4();
            var body = new JObject { ["clientSessionId"] = clientSessionId, ["platform"] = opts.Platform, ["build"] = opts.Build };
            if (opts.Device != null) body["device"] = JObject.FromObject(opts.Device, Newtonsoft.Json.JsonSerializer.Create(Json.Settings));
            if (opts.Locale != null) body["locale"] = opts.Locale;
            if (platformTicket != null)
            {
                var ticket = await platformTicket();
                if (ticket != null) body["platformProof"] = new JObject { ["steamTicket"] = ticket };
            }
            var answer = await api.PlayerAsync("/v1/sessions", new PlayerCall { Method = "POST", Json = body });
            var session = answer.Json?["session"] as JObject;
            if (session == null || subject() != owner) return null;
            current = new SessionInfo { Id = (string?)session["id"] ?? "", Owner = owner, StartedAt = (string?)session["startedAt"] ?? "", PlatformLink = LinkOf(answer.Json?["platformLink"]) };
            if (OnEntitlements != null)
            {
                try { await OnEntitlements(answer.Json?["entitlements"]); }
                catch (Exception) { /* what the licences kept is not worth the session */ }
            }
            if (answer.Json?["heartbeatSeconds"]?.Type == JTokenType.Integer) heartbeatSeconds = (int)answer.Json["heartbeatSeconds"]!;
            nextHeartbeatAt = clock.ElapsedMilliseconds + heartbeatSeconds * 1000L;
            return current;
        }

        /// <summary>The server's `platformLink` (the session open's, the consent answer's); null when it is not one.</summary>
        internal static PlatformLink? LinkOf(JToken? token)
        {
            if (!(token is JObject o) || o["platformAccountId"]?.Type != JTokenType.String || o["state"]?.Type != JTokenType.String) return null;
            return new PlatformLink { Platform = (string?)o["platform"] ?? "", PlatformAccountId = (string)o["platformAccountId"]!, State = (string)o["state"]! };
        }

        /// <summary>Sends the heartbeat when it is due.</summary>
        internal async Task DueAsync()
        {
            if (clock.ElapsedMilliseconds < nextHeartbeatAt) return;
            await HeartbeatAsync();
        }

        internal async Task HeartbeatAsync()
        {
            var session = Session;
            if (session == null) return;
            nextHeartbeatAt = clock.ElapsedMilliseconds + heartbeatSeconds * 1000L;
            try
            {
                var answer = await api.PlayerAsync($"/v1/sessions/{session.Id}/heartbeat", new PlayerCall
                {
                    Method = "POST",
                    Json = new JObject { ["activeSeconds"] = ActiveSeconds, ["foreground"] = Foreground },
                });
                if (answer.Json?["nextHeartbeatSeconds"]?.Type == JTokenType.Integer && (int)answer.Json["nextHeartbeatSeconds"]! > 0)
                {
                    heartbeatSeconds = (int)answer.Json["nextHeartbeatSeconds"]!;
                    nextHeartbeatAt = clock.ElapsedMilliseconds + heartbeatSeconds * 1000L;
                }
            }
            catch (KrtuworksException err) when (err.Code == "session_ended" || err.Code == "session_not_found")
            {
                // The server closed it (30 minutes without a heartbeat): a new one, with a new id.
                current = null;
                clientSessionId = null;
                try { await OpenAsync(); }
                catch (KrtuworksException) { /* the next tick tries again */ }
            }
            catch (KrtuworksException) { /* offline: the next heartbeat carries the time */ }
            OnHeartbeat?.Invoke();
        }

        /// <summary>One whole session where every step must answer: open, a heartbeat, end (the validator's round).</summary>
        internal async Task RoundAsync()
        {
            var session = await OpenAsync();
            if (session == null) throw new KrtuworksException("signed_out", "No player is signed in.");
            KrtuworksException? failure = null;
            try
            {
                await api.PlayerAsync($"/v1/sessions/{session.Id}/heartbeat", new PlayerCall
                {
                    Method = "POST",
                    Json = new JObject { ["activeSeconds"] = ActiveSeconds, ["foreground"] = Foreground },
                });
            }
            catch (KrtuworksException err)
            {
                failure = err;
            }
            var active = ActiveSeconds;
            Close();
            // The session is ended even when its heartbeat failed, so the round leaves nothing open.
            try
            {
                await api.PlayerAsync($"/v1/sessions/{session.Id}/end", new PlayerCall { Method = "POST", Json = new JObject { ["activeSeconds"] = active } });
            }
            catch (KrtuworksException) when (failure != null) { /* the heartbeat's failure is the one told */ }
            if (failure != null) throw failure;
        }

        /// <summary>Ends the session.</summary>
        internal async Task EndAsync()
        {
            var session = Session;
            var active = ActiveSeconds;
            Close();
            if (session == null) return;
            try
            {
                await api.PlayerAsync($"/v1/sessions/{session.Id}/end", new PlayerCall { Method = "POST", Json = new JObject { ["activeSeconds"] = active } });
            }
            catch (KrtuworksException) { /* the server times it out */ }
        }

        /// <summary>Forgets the session without telling the server (the player changed: it times out there).</summary>
        internal void Close()
        {
            current = null;
            clientSessionId = null;
            nextHeartbeatAt = long.MaxValue;
        }
    }
}
