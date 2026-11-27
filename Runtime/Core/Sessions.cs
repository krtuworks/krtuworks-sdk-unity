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
        /// <summary>The launch's time in use when the current session began: the server counts only what follows.</summary>
        long sessionBaseMs;
        /// <summary>Told after each heartbeat (the minutes of play moved).</summary>
        internal Action? OnHeartbeat;
        /// <summary>Told the licence list of a session answer (only an open that sent a platform proof gets one).</summary>
        internal Func<Newtonsoft.Json.Linq.JToken?, Task>? OnEntitlements;
        readonly SingleFlight<SessionInfo?> opening = new SingleFlight<SessionInfo?>();
        /// <summary>Takes back a ticket that was got and never sent (set by the client).</summary>
        internal Action<string>? CancelTicket;
        /// <summary>Whether a session is wanted now (sessions on, the module on); a failed open is tried again only then.</summary>
        internal Func<bool> Wanted = () => true;
        /// <summary>The player the latest open ran for.</summary>
        string? openedFor;
        /// <summary>When a failed open is tried again (null: none failed).</summary>
        long? openRetryAt;
        long openBackoffMs;
        /// <summary>Moves on every <see cref="Close"/>: an answer that arrives after it belongs to a session nobody wants any more.</summary>
        int generation;

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

        long LaunchActiveMs => activeMs + (activeSince.HasValue ? clock.ElapsedMilliseconds - activeSince.Value : 0);

        /// <summary>Seconds in use in this launch (foreground only).</summary>
        internal long ActiveSeconds => LaunchActiveMs / 1000;

        /// <summary>
        /// Seconds in use since the current session began (foreground only): what the session reports. A
        /// session opened after another one (a new player, a timeout) starts from zero, not from the launch's total.
        /// </summary>
        internal long SessionActiveSeconds => Math.Max(0, LaunchActiveMs - sessionBaseMs) / 1000;

        internal void SetForeground(bool foreground)
        {
            if (foreground && !activeSince.HasValue) activeSince = clock.ElapsedMilliseconds;
            if (!foreground && activeSince.HasValue)
            {
                activeMs += clock.ElapsedMilliseconds - activeSince.Value;
                activeSince = null;
            }
        }

        /// <summary>
        /// Opens the player's session if there is none (one at a time). An open still running for another player is
        /// waited out and this player's own follows: it must not hand back that one's nothing.
        /// </summary>
        internal async Task<SessionInfo?> OpenAsync()
        {
            var asked = subject();
            var session = Session;
            if (session != null) return session;
            SessionInfo? result;
            try { result = await opening.Run(OpenOnce); }
            catch (Exception) when (asked != null && openedFor != asked && subject() == asked) { result = null; }
            if (result == null && asked != null && subject() == asked && openedFor != asked) return await opening.Run(OpenOnce);
            return result;
        }

        /// <summary>A failed open is tried again by the client's tick, with a growing wait (a server's own wait when it gave one).</summary>
        async Task<SessionInfo?> OpenOnce()
        {
            try
            {
                var opened = await DoOpen();
                if (opened != null)
                {
                    openRetryAt = null;
                    openBackoffMs = 0;
                }
                return opened;
            }
            catch (Exception err)
            {
                var wait = err is KrtuworksException known ? Retry.DelayMs(known, ref openBackoffMs) : (openBackoffMs = Math.Min(300_000, Math.Max(5_000, openBackoffMs * 2)));
                openRetryAt = clock.ElapsedMilliseconds + wait;
                throw;
            }
        }

        /// <summary>The last open failed: the client's tick asks again once its wait is over.</summary>
        internal bool OpenFailed => openRetryAt.HasValue;

        /// <summary>Lets a failed open be tried again at the next tick (back online).</summary>
        internal void RetryOpenSoon()
        {
            if (openRetryAt.HasValue) openRetryAt = 0;
            openBackoffMs = 0;
        }

        async Task<SessionInfo?> DoOpen()
        {
            var owner = subject();
            openedFor = owner;
            if (owner == null) return null;
            if (current != null && current.Owner != owner) Close();
            var openedIn = generation;
            if (clientSessionId == null)
            {
                // A new session (not an open sent again after a lost answer): its time in use starts now.
                clientSessionId = Ids.UuidV4();
                sessionBaseMs = LaunchActiveMs;
            }
            var body = new JObject { ["clientSessionId"] = clientSessionId, ["platform"] = opts.Platform, ["build"] = opts.Build };
            if (opts.Device != null) body["device"] = JObject.FromObject(opts.Device, Newtonsoft.Json.JsonSerializer.Create(Json.Settings));
            if (opts.Locale != null) body["locale"] = opts.Locale;
            string? usedTicket = null;
            if (platformTicket != null)
            {
                usedTicket = await platformTicket();
                if (usedTicket != null) body["platformProof"] = new JObject { ["steamTicket"] = usedTicket };
            }
            Answer answer;
            try
            {
                answer = await api.PlayerAsync("/v1/sessions", new PlayerCall { Method = "POST", Json = body, Owner = owner });
            }
            catch (KrtuworksException err) when (err.AuthLayer && usedTicket != null)
            {
                // The credentials failed before the call went out: the ticket was never used.
                CancelTicket?.Invoke(usedTicket);
                throw;
            }
            var session = answer.Json?["session"] as JObject;
            if (session != null && generation != openedIn)
            {
                // Closed while the open was on its way (the SDK shut down, the player left): the server's session ends now.
                var lateId = (string?)session["id"];
                if (!string.IsNullOrEmpty(lateId)) _ = EndQuietly(lateId!, owner);
                return null;
            }
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
            if (openRetryAt.HasValue && clock.ElapsedMilliseconds >= openRetryAt.Value && Session == null && subject() != null && Wanted())
            {
                try { await OpenAsync(); }
                catch (Exception) { /* the failure set the next try */ }
                return;
            }
            if (clock.ElapsedMilliseconds < nextHeartbeatAt) return;
            await HeartbeatAsync();
        }

        async Task EndQuietly(string id, string owner)
        {
            try { await api.PlayerAsync($"/v1/sessions/{id}/end", new PlayerCall { Method = "POST", Json = new JObject { ["activeSeconds"] = 0 }, Owner = owner }); }
            catch (KrtuworksException) { /* the server times it out */ }
        }

        internal async Task HeartbeatAsync()
        {
            var session = Session;
            if (session == null) return;
            var beatIn = generation;
            nextHeartbeatAt = clock.ElapsedMilliseconds + heartbeatSeconds * 1000L;
            try
            {
                var answer = await api.PlayerAsync($"/v1/sessions/{session.Id}/heartbeat", new PlayerCall
                {
                    Method = "POST",
                    Json = new JObject { ["activeSeconds"] = SessionActiveSeconds, ["foreground"] = Foreground },
                    Owner = session.Owner,
                });
                if (answer.Json?["nextHeartbeatSeconds"]?.Type == JTokenType.Integer && (int)answer.Json["nextHeartbeatSeconds"]! > 0)
                {
                    heartbeatSeconds = (int)answer.Json["nextHeartbeatSeconds"]!;
                    nextHeartbeatAt = clock.ElapsedMilliseconds + heartbeatSeconds * 1000L;
                }
            }
            catch (KrtuworksException err) when ((err.Code == "session_ended" || err.Code == "session_not_found") && generation == beatIn)
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
                    Json = new JObject { ["activeSeconds"] = SessionActiveSeconds, ["foreground"] = Foreground },
                    Owner = session.Owner,
                });
            }
            catch (KrtuworksException err)
            {
                failure = err;
            }
            var active = SessionActiveSeconds;
            Close();
            // The session is ended even when its heartbeat failed, so the round leaves nothing open.
            try
            {
                await api.PlayerAsync($"/v1/sessions/{session.Id}/end", new PlayerCall { Method = "POST", Json = new JObject { ["activeSeconds"] = active }, Owner = session.Owner });
            }
            catch (KrtuworksException) when (failure != null) { /* the heartbeat's failure is the one told */ }
            if (failure != null) throw failure;
        }

        /// <summary>Ends the session.</summary>
        internal async Task EndAsync()
        {
            var session = Session;
            var active = SessionActiveSeconds;
            Close();
            if (session == null) return;
            try
            {
                await api.PlayerAsync($"/v1/sessions/{session.Id}/end", new PlayerCall { Method = "POST", Json = new JObject { ["activeSeconds"] = active }, Owner = session.Owner });
            }
            catch (KrtuworksException) { /* the server times it out */ }
        }

        /// <summary>Forgets the session without telling the server (the player changed: it times out there).</summary>
        internal void Close()
        {
            generation++;
            current = null;
            clientSessionId = null;
            nextHeartbeatAt = long.MaxValue;
            openRetryAt = null;
            openBackoffMs = 0;
        }
    }
}
