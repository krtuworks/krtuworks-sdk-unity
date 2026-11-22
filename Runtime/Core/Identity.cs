#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Krtuworks
{
    public enum PlayerKind { Guest, Account }

    public sealed class Player
    {
        public PlayerKind Kind;
        /// <summary>The id this app knows the player by: the same on every device, never the Krtuworks ID itself.</summary>
        public string Subject = "";
        public string? Username;
        public string? Picture;
    }

    /// <summary>signed_in · guest · signed_out · merged · deletion_pending · sanctioned</summary>
    public sealed class IdentityChange
    {
        public string Type = "";
        public Player? Player;
        public bool Promoted;
        public bool Created;
        public string? Reason;
        public string? PreviousSubject;
        public string? MergedInto;
    }

    /// <summary>A console's sign-in: show <see cref="UserCode"/> and <see cref="VerificationUri"/> (or a QR of the complete one).</summary>
    public sealed class DeviceLink
    {
        public string UserCode = "";
        public string VerificationUri = "";
        public string VerificationUriComplete = "";
        public int ExpiresIn;
        public int Interval = 5;
        internal string DeviceCode = "";
    }
}

namespace Krtuworks.Internal
{
    internal sealed class StoredTokens
    {
        [JsonProperty("kind")] public string Kind = "guest";
        [JsonProperty("subject")] public string Subject = "";
        [JsonProperty("username")] public string? Username;
        [JsonProperty("picture")] public string? Picture;
        [JsonProperty("accessToken")] public string AccessToken = "";
        /// <summary>Milliseconds on the server's clock.</summary>
        [JsonProperty("accessExpiresAt")] public long AccessExpiresAt;
        [JsonProperty("refreshToken")] public string? RefreshToken;
        /// <summary>`oidc` (the token endpoint) or `device` (a console: refreshed through the device broker).</summary>
        [JsonProperty("via")] public string Via = "oidc";
    }

    internal sealed class GuestRequest
    {
        [JsonProperty("birthDate")] public string BirthDate = "";
        [JsonProperty("acceptedTerms")] public string AcceptedTerms = "";
    }

    internal sealed class IdentityMeta
    {
        [JsonProperty("epoch")] public long Epoch;
        [JsonProperty("waitingOwners")] public List<string> WaitingOwners = new List<string>();
        [JsonProperty("guestRequest")] public GuestRequest? GuestRequest;
    }

    internal sealed class PendingSignIn
    {
        [JsonProperty("verifier")] public string Verifier = "";
        [JsonProperty("nonce")] public string Nonce = "";
        [JsonProperty("redirectUri")] public string RedirectUri = "";
        [JsonProperty("epoch")] public long Epoch;
        [JsonProperty("startedAt")] public long StartedAt;
    }

    /// <summary>
    /// Who plays: the tokens, the device's guest key, and every way a player arrives or leaves. One token
    /// chain per sign-in client; every write of it under one lock, and a refresh writes its answer only
    /// over the refresh token it spent. Same rules as the JS SDK (krtuworks-sdk-js/src/identity.ts).
    /// </summary>
    internal sealed class Identity
    {
        internal const string DeviceKeyGrant = "urn:krtuworks:params:oauth:grant-type:device-key";
        internal const string SteamTicketGrant = "urn:krtuworks:params:oauth:grant-type:steam-ticket";
        internal const string DeviceCodeGrant = "urn:ietf:params:oauth:grant-type:device_code";
        internal const string CurrentTerms = "TERMS:v1 PRIVACY:v1";
        const long RefreshEarlyMs = 60_000;
        const long PendingTtlMs = 10 * 60_000;

        readonly Resolved opts;
        readonly IKrtuStore store;
        readonly ISecretStore secrets;
        readonly IHttpTransport http;
        readonly Clock clock;
        readonly IPlatformServices? platform;
        StoredTokens? tokens;
        readonly SingleFlight<StoredTokens> refreshing = new SingleFlight<StoredTokens>();

        /// <summary>Bumped whenever the signed-in player changes; an answer made for another one is dropped.</summary>
        internal int Generation;
        internal event Action<IdentityChange>? Changed;

        internal Identity(Resolved opts, IKrtuStore store, ISecretStore secrets, IHttpTransport http, Clock clock, IPlatformServices? platform)
        {
            this.opts = opts;
            this.store = store;
            this.secrets = secrets;
            this.http = http;
            this.clock = clock;
            this.platform = platform;
        }

        string Key(string name) => $"krtu:{opts.ClientId}:{name}";
        string LockName => $"krtu:{opts.ClientId}:identity";

        static KrtuworksException SignedOut() => new KrtuworksException("signed_out", "No player is signed in.");

        internal async Task<Player?> LoadAsync()
        {
            tokens = await Json.Get<StoredTokens>(secrets, Key("tokens"));
            return Player;
        }

        internal Player? Player => tokens == null
            ? null
            : new Player { Kind = tokens.Kind == "account" ? PlayerKind.Account : PlayerKind.Guest, Subject = tokens.Subject, Username = tokens.Username, Picture = tokens.Picture };

        void Emit(IdentityChange change)
        {
            try { Changed?.Invoke(change); }
            catch (Exception) { /* a listener's failure is the app's */ }
        }

        internal async Task<IdentityMeta> MetaAsync() => await Json.Get<IdentityMeta>(store, Key("meta")) ?? new IdentityMeta();

        Task SaveMeta(IdentityMeta meta) => Json.Put(store, Key("meta"), meta);

        internal async Task<List<string>> WaitingOwnersAsync() => (await MetaAsync()).WaitingOwners;

        /// <summary>The merge of `subject` is settled: it no longer waits, and its guest key (retired by the server) goes.</summary>
        internal async Task ForgetWaitingOwnerAsync(string subject)
        {
            using (await Locks.Take(LockName))
            {
                var meta = await MetaAsync();
                meta.WaitingOwners.Remove(subject);
                await SaveMeta(meta);
                var key = await Json.Get<DeviceKey>(secrets, Key("deviceKey"));
                if (key?.Subject == subject) await secrets.DeleteAsync(Key("deviceKey"));
            }
        }

        async Task Adopt(StoredTokens? next)
        {
            if (next != null) await Json.Put(secrets, Key("tokens"), next);
            else await secrets.DeleteAsync(Key("tokens"));
            if (tokens?.Subject != next?.Subject) Generation++;
            tokens = next;
        }

        // ── Tokens ──────────────────────────────────────────────────────────

        /// <summary>A valid access token, refreshed first when it is about to expire (or `force`). One refresh at a time.</summary>
        internal async Task<string> AccessTokenAsync(bool force = false) => (await CredentialsAsync(force)).AccessToken;

        /// <summary>
        /// The access token together with the player it belongs to, read from the same stored tokens object: a
        /// refresh may hand back another player's tokens (another flow signed in meanwhile), and only the pair says whose.
        /// </summary>
        internal async Task<(string AccessToken, string Subject)> CredentialsAsync(bool force = false)
        {
            var t = tokens ?? throw SignedOut();
            if (!force && t.AccessExpiresAt - RefreshEarlyMs > clock.ServerNowMs) return (t.AccessToken, t.Subject);
            var next = await refreshing.Run(() => Refresh(t.AccessToken));
            return (next.AccessToken, next.Subject);
        }

        Task<Answer> Token(Dictionary<string, string?> fields, string? path = null, string? clientId = null)
        {
            fields["client_id"] = clientId ?? opts.ClientId;
            return Wire.Send(http, clock, Wire.Form($"{opts.IdOrigin}{path ?? "/oidc/token"}", fields));
        }

        StoredTokens TokensFrom(JObject? json, string kind, StoredTokens? previous, string via = "oidc")
        {
            var access = json?["access_token"]?.ToString();
            if (string.IsNullOrEmpty(access)) throw new KrtuworksException("invalid_response", "The token answer carries no access token.");
            var claims = Jwt.Claims(access!);
            var id = json?["id_token"]?.Type == JTokenType.String ? Jwt.Claims((string)json["id_token"]!) : null;
            var subject = (string?)claims["sub"] ?? (string?)id?["sub"] ?? "";
            if (subject == "") throw new KrtuworksException("invalid_response", "The access token names no player.");
            var same = previous?.Subject == subject;
            var exp = claims["exp"]?.Type == JTokenType.Integer ? (long)claims["exp"]! * 1000 : clock.ServerNowMs + 600_000;
            return new StoredTokens
            {
                Kind = kind,
                Subject = subject,
                Username = (string?)id?["preferred_username"] ?? (same ? previous!.Username : null),
                Picture = (string?)id?["picture"] ?? (same ? previous!.Picture : null),
                AccessToken = access!,
                AccessExpiresAt = exp,
                RefreshToken = json?["refresh_token"]?.Type == JTokenType.String ? (string)json["refresh_token"]! : same ? previous!.RefreshToken : null,
                Via = via,
            };
        }

        async Task<StoredTokens> Refresh(string usedAccess)
        {
            using (await Locks.Take(LockName))
            {
                var stored = await Json.Get<StoredTokens>(secrets, Key("tokens"));
                if (stored == null)
                {
                    await Adopt(null);
                    throw SignedOut();
                }
                if (stored.AccessToken != usedAccess && stored.AccessExpiresAt - RefreshEarlyMs > clock.ServerNowMs)
                {
                    await Adopt(stored);
                    return stored;
                }
                if (stored.RefreshToken == null)
                {
                    if (stored.Kind == "guest") return await GuestAgain(stored);
                    await Adopt(null);
                    Emit(new IdentityChange { Type = "signed_out", Reason = "expired" });
                    throw SignedOut();
                }
                var fields = new Dictionary<string, string?> { ["grant_type"] = "refresh_token", ["refresh_token"] = stored.RefreshToken };
                Answer answer;
                if (stored.Via == "device") answer = await Token(fields, "/v1/device/token", opts.DeviceClientId);
                else
                {
                    fields["resource"] = Resolved.DevResource;
                    answer = await Token(fields);
                }
                if (answer.Status == 200)
                {
                    var next = TokensFrom(answer.Json, stored.Kind, stored, stored.Via);
                    await Adopt(next);
                    return next;
                }
                return await Refused(stored, Wire.ErrorOf(answer));
            }
        }

        /// <summary>A refresh the server refused, read by what it says; under the identity lock.</summary>
        async Task<StoredTokens> Refused(StoredTokens stored, KrtuworksException err)
        {
            if (err.Code != "invalid_grant") throw err;
            var why = (string?)err.Details["description"] ?? err.Message;
            if (why.Contains("account_merged")) return await MarkMergedLocked(stored.Subject, null);
            // A frozen app keeps its grants: the same refresh token works again once it is resumed.
            if (why.Contains("app_suspended")) throw err;
            await Adopt(null);
            if (why.Contains("sanctioned"))
            {
                Emit(new IdentityChange { Type = "sanctioned" });
                throw new KrtuworksException("sanctioned", "The player is banned from this app.", 403);
            }
            if (why.Contains("account_deletion_pending"))
            {
                Emit(new IdentityChange { Type = "deletion_pending" });
                throw new KrtuworksException("account_deletion_pending", "This account is waiting to be deleted.", 403);
            }
            if (stored.Kind == "guest") return await GuestAgain(stored);
            Emit(new IdentityChange { Type = "signed_out", Reason = why });
            throw SignedOut();
        }

        async Task<StoredTokens> MarkMergedLocked(string previousSubject, string? mergedInto)
        {
            var meta = await MetaAsync();
            if (!meta.WaitingOwners.Contains(previousSubject)) meta.WaitingOwners.Add(previousSubject);
            await SaveMeta(meta);
            await Adopt(null);
            Emit(new IdentityChange { Type = "merged", PreviousSubject = previousSubject, MergedInto = mergedInto });
            throw new KrtuworksException("account_merged", "This account was merged into another one; sign in with it.", 401, null, new JObject { ["mergedInto"] = mergedInto });
        }

        /// <summary>The server said the player's account went into another one (401 account_merged).</summary>
        internal async Task MarkMergedAsync(string previousSubject, string? mergedInto)
        {
            using (await Locks.Take(LockName)) await MarkMergedLocked(previousSubject, mergedInto);
        }

        // ── Guests (device key, Steam ticket) ───────────────────────────────

        async Task<string> Challenge(string purpose)
        {
            var answer = await Wire.Send(http, clock, Wire.Form($"{opts.IdOrigin}/oidc/device-key/challenge", new Dictionary<string, string?> { ["client_id"] = opts.ClientId, ["purpose"] = purpose }));
            if (answer.Status != 200 || answer.Json?["nonce"]?.Type != JTokenType.String) throw Wire.ErrorOf(answer);
            return (string)answer.Json!["nonce"]!;
        }

        async Task<string> ProofWith(DeviceKey key, string purpose)
        {
            var nonce = await Challenge(purpose);
            return key.SignProof(nonce, opts.ClientId, purpose, clock.ServerNowMs / 1000);
        }

        /// <summary>A proof by this device's guest key for one of the guest's own calls (deletion, data copy).</summary>
        internal async Task<string> GuestProofAsync(string purpose)
        {
            var key = await Json.Get<DeviceKey>(secrets, Key("deviceKey"));
            if (key == null || !key.Known) throw new KrtuworksException("no_guest_key", "This device holds no guest key.");
            return await ProofWith(key, purpose);
        }

        /// <summary>The device-key (or Steam ticket) grant; the caller holds the identity lock.</summary>
        async Task<(StoredTokens tokens, bool created)> GuestGrant(GuestRequest? request, string? steamTicket)
        {
            var key = await Json.Get<DeviceKey>(secrets, Key("deviceKey"));
            if (key == null)
            {
                // Stored before the server hears of it: a lost answer must not leave a guest no key can reach.
                key = DeviceKey.Create();
                await Json.Put(secrets, Key("deviceKey"), key);
            }
            if (!key.Known && request == null) throw new KrtuworksException("birth_date_required", "A new guest needs the age screen: pass the birth date and the accepted terms.", 400);
            var assertion = await ProofWith(key, "login");
            var fields = new Dictionary<string, string?>
            {
                ["grant_type"] = steamTicket != null ? SteamTicketGrant : DeviceKeyGrant,
                ["assertion"] = assertion,
                ["scope"] = Resolved.GuestScope,
                ["resource"] = Resolved.DevResource,
                ["ticket"] = steamTicket,
                ["birth_date"] = key.Known ? null : request!.BirthDate,
                ["accepted_terms"] = key.Known ? null : request!.AcceptedTerms,
            };
            var answer = await Token(fields);
            if (answer.Status != 200)
            {
                var err = Wire.ErrorOf(answer);
                if (err.Code == "terms_required") err.Details["terms"] = TermsIn(err.Message);
                throw err;
            }
            // A Steam ticket of a Steam-linked Krtuworks ID signs in as that account (it has a username).
            var id = answer.Json?["id_token"]?.Type == JTokenType.String ? Jwt.Claims((string)answer.Json["id_token"]!) : null;
            var kind = steamTicket != null && id?["preferred_username"]?.Type == JTokenType.String ? "account" : "guest";
            var created = !key.Known;
            var signedIn = TokensFrom(answer.Json, kind, null);
            if (created || (kind == "guest" && key.Subject != signedIn.Subject))
            {
                key.Known = true;
                if (kind == "guest") key.Subject = signedIn.Subject;
                await Json.Put(secrets, Key("deviceKey"), key);
            }
            return (signedIn, created);
        }

        /// <summary>`terms_required` names the documents in its text: `Accept the current terms: TERMS:v2 PRIVACY:v1.`</summary>
        internal static string? TermsIn(string text)
        {
            var named = Regex.Matches(text, @"\b[A-Z][A-Z_]*:v\d+\b").Cast<Match>().Select(m => m.Value).Distinct().ToList();
            return named.Count > 0 ? string.Join(" ", named) : null;
        }

        /// <summary>A guest whose refresh token is gone signs in again with its key; under the identity lock.</summary>
        async Task<StoredTokens> GuestAgain(StoredTokens stored)
        {
            try
            {
                var (signedIn, _) = await GuestGrant(null, null);
                await Adopt(signedIn);
                return signedIn;
            }
            catch (KrtuworksException e) when (e.Code == "invalid_grant" || e.Code == "birth_date_required")
            {
                var why = (string?)e.Details["description"] ?? e.Message;
                if (why.Contains("account_merged")) return await MarkMergedLocked(stored.Subject, null);
                await Adopt(null);
                if (why.Contains("account_deletion_pending"))
                {
                    Emit(new IdentityChange { Type = "deletion_pending" });
                    throw new KrtuworksException("account_deletion_pending", "This account is waiting to be deleted.", 403);
                }
                if (why.Contains("sanctioned"))
                {
                    Emit(new IdentityChange { Type = "sanctioned" });
                    throw new KrtuworksException("sanctioned", "The player is banned from this app.", 403);
                }
                Emit(new IdentityChange { Type = "signed_out", Reason = why });
                throw SignedOut();
            }
        }

        /// <summary>
        /// Plays as a guest (or, on Steam, as the Steam account's player). A new guest needs the age screen's
        /// answers (13 or older, the current terms); they are kept and tried again on later launches when the
        /// server could not make the guest now. A sign-in the player starts meanwhile wins.
        /// </summary>
        internal async Task<Player> SignInAsGuestAsync(string? birthDate, string? acceptedTerms, bool viaSteam)
        {
            var meta = await MetaAsync();
            var epoch = meta.Epoch;
            if (birthDate != null)
            {
                meta.GuestRequest = new GuestRequest { BirthDate = birthDate, AcceptedTerms = acceptedTerms ?? CurrentTerms };
                await SaveMeta(meta);
            }
            Player player;
            bool created;
            using (await Locks.Take(LockName))
            {
                var current = await Json.Get<StoredTokens>(secrets, Key("tokens"));
                if (current != null)
                {
                    await Adopt(current);
                    return Player!;
                }
                var request = (await MetaAsync()).GuestRequest;
                string? ticket = null;
                if (viaSteam)
                {
                    ticket = platform == null ? null : await platform.GetWebApiTicketAsync();
                    if (ticket == null) throw new KrtuworksException("steam_unavailable", "Steam gave no ticket.");
                }
                (StoredTokens, bool) result;
                try { result = await GuestGrant(request, ticket); }
                catch (KrtuworksException e) when (ticket != null && e.Code == "steam_not_configured") { result = await GuestGrant(request, null); }
                var now = await MetaAsync();
                if (now.Epoch != epoch) throw new KrtuworksException("context_changed", "A sign-in started while the guest was being made; the guest was not used.");
                now.GuestRequest = null;
                await SaveMeta(now);
                await Adopt(result.Item1);
                player = Player!;
                created = result.Item2;
            }
            Emit(new IdentityChange { Type = player.Kind == PlayerKind.Guest ? "guest" : "signed_in", Player = player, Created = created });
            return player;
        }

        // ── Sign-in with Krtuworks ID: authorization code + PKCE in the system browser ──

        /// <summary>
        /// The address to send the player to (RFC 8252: the system browser, a loopback redirect). A guest
        /// playing here is carried along (`krtu_guest`). Starting a sign-in bumps the epoch.
        /// </summary>
        internal async Task<string> BeginSignInAsync(string redirectUri, bool carryGuest = true)
        {
            long epoch;
            using (await Locks.Take(LockName))
            {
                var meta = await MetaAsync();
                meta.Epoch++;
                await SaveMeta(meta);
                epoch = meta.Epoch;
            }
            var verifier = Ids.RandomToken(32);
            var state = Ids.RandomToken(16);
            var nonce = Ids.RandomToken(16);
            var query = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("client_id", opts.ClientId),
                new KeyValuePair<string, string>("redirect_uri", redirectUri),
                new KeyValuePair<string, string>("response_type", "code"),
                new KeyValuePair<string, string>("scope", Resolved.PlayerScope),
                // offline_access only counts with prompt=consent (OIDC Core 11); no screen for the app's own players.
                new KeyValuePair<string, string>("prompt", "consent"),
                new KeyValuePair<string, string>("resource", Resolved.DevResource),
                new KeyValuePair<string, string>("code_challenge", Ids.Base64Url(Ids.Sha256(verifier))),
                new KeyValuePair<string, string>("code_challenge_method", "S256"),
                new KeyValuePair<string, string>("state", state),
                new KeyValuePair<string, string>("nonce", nonce),
            };
            if (tokens?.Kind == "guest" && carryGuest)
            {
                var key = await Json.Get<DeviceKey>(secrets, Key("deviceKey"));
                if (key != null && key.Known)
                {
                    try { query.Add(new KeyValuePair<string, string>("krtu_guest", await ProofWith(key, "promote"))); }
                    catch (KrtuworksException) { /* offline or refused: the sign-in goes on without the guest */ }
                }
            }
            await Json.Put(secrets, Key($"signin:{state}"), new PendingSignIn { Verifier = verifier, Nonce = nonce, RedirectUri = redirectUri, Epoch = epoch, StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            return $"{opts.IdOrigin}/oidc/auth?{string.Join("&", query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"))}";
        }

        /// <summary>The query of a return address, read from the text (a phone's return has the app's own scheme, which <see cref="Uri"/> parses differently per runtime).</summary>
        static Dictionary<string, string> QueryOf(string url)
        {
            var q = new Dictionary<string, string>();
            var start = url.IndexOf('?');
            var end = url.IndexOf('#');
            var query = start < 0 ? "" : end > start ? url.Substring(start + 1, end - start - 1) : url.Substring(start + 1);
            foreach (var part in query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                var k = Uri.UnescapeDataString((eq < 0 ? part : part.Substring(0, eq)).Replace('+', ' '));
                var v = eq < 0 ? "" : Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' '));
                q[k] = v;
            }
            return q;
        }

        /// <summary>Finishes a sign-in from the address the browser came back to.</summary>
        internal async Task<Player> CompleteSignInAsync(string callbackUrl)
        {
            var q = QueryOf(callbackUrl);
            if (!q.TryGetValue("state", out var state)) throw new KrtuworksException("invalid_callback", "The address carries no sign-in.");
            var pending = await Json.Get<PendingSignIn>(secrets, Key($"signin:{state}"));
            await secrets.DeleteAsync(Key($"signin:{state}"));
            if (pending == null || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pending.StartedAt > PendingTtlMs)
                throw new KrtuworksException("invalid_callback", "This sign-in is unknown or too old: start it again.");
            if (q.TryGetValue("error", out var error)) throw new KrtuworksException(error, q.TryGetValue("error_description", out var d) ? d : "The sign-in did not finish.", 400);
            if (q.TryGetValue("iss", out var iss) && iss != opts.Issuer) throw new KrtuworksException("invalid_callback", "The answer came from another issuer.");
            if (!q.TryGetValue("code", out var code)) throw new KrtuworksException("invalid_callback", "The address carries no code.");
            var answer = await Token(new Dictionary<string, string?>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = pending.RedirectUri,
                ["code_verifier"] = pending.Verifier,
                ["resource"] = Resolved.DevResource,
            });
            if (answer.Status != 200) throw Wire.ErrorOf(answer);
            CheckIdToken(answer.Json?["id_token"], pending.Nonce);
            Player player;
            bool promoted = false;
            using (await Locks.Take(LockName))
            {
                var meta = await MetaAsync();
                if (meta.Epoch != pending.Epoch) throw new KrtuworksException("context_changed", "Another sign-in started after this one; this one was not used.");
                var before = await Json.Get<StoredTokens>(secrets, Key("tokens"));
                var next = TokensFrom(answer.Json, "account", before);
                if (before?.Kind == "guest")
                {
                    if (before.Subject == next.Subject)
                    {
                        // The guest became this Krtuworks ID: same player, its key retired by the server.
                        promoted = true;
                        await secrets.DeleteAsync(Key("deviceKey"));
                    }
                    else if (!meta.WaitingOwners.Contains(before.Subject)) meta.WaitingOwners.Add(before.Subject);
                }
                meta.GuestRequest = null;
                await SaveMeta(meta);
                await Adopt(next);
                player = Player!;
            }
            Emit(new IdentityChange { Type = "signed_in", Player = player, Promoted = promoted });
            return player;
        }

        /// <summary>OIDC Core 3.1.3.7 for a token straight from the token endpoint over TLS: issuer, audience, nonce, time.</summary>
        void CheckIdToken(JToken? idToken, string nonce)
        {
            if (idToken?.Type != JTokenType.String) throw new KrtuworksException("invalid_response", "The sign-in answer carries no ID token.");
            var claims = Jwt.Claims((string)idToken!);
            var aud = claims["aud"] is JArray a ? a.Select(x => (string?)x).ToList() : new List<string?> { (string?)claims["aud"] };
            var fresh = claims["exp"]?.Type == JTokenType.Integer && (long)claims["exp"]! * 1000 > clock.ServerNowMs - 60_000;
            if ((string?)claims["iss"] != opts.Issuer || !aud.Contains(opts.ClientId) || (string?)claims["nonce"] != nonce || !fresh)
                throw new KrtuworksException("invalid_response", "The ID token is not for this sign-in.");
        }

        /// <summary>
        /// The whole desktop sign-in: a loopback listener on 127.0.0.1 (any port, RFC 8252 §7.3), the system
        /// browser opened on the sign-in page, the code exchanged when the browser comes back.
        /// </summary>
        internal async Task<Player> SignInWithBrowserAsync(Action<string> openBrowser, CancellationToken cancel, bool carryGuest = true)
        {
            using var receiver = new LoopbackReceiver();
            var url = await BeginSignInAsync($"http://127.0.0.1:{receiver.Port}/callback", carryGuest);
            openBrowser(url);
            var callback = await receiver.WaitAsync(cancel);
            return await CompleteSignInAsync(callback);
        }

        /// <summary>A phone's return address: the app's own scheme (RFC 8252 §7.1), registered for every app's native client.</summary>
        internal string AppScheme => $"com.krtuworks.{opts.Slug}";

        /// <summary>
        /// The phone sign-in: the platform's authentication session (iOS ASWebAuthenticationSession, Android
        /// Custom Tabs + the app's scheme) opens the sign-in page and hands back the address it returned to.
        /// </summary>
        internal async Task<Player> SignInWithAppAsync(Func<string, string, CancellationToken, Task<string>> authenticate, CancellationToken cancel, bool carryGuest = true)
        {
            var url = await BeginSignInAsync($"{AppScheme}:/callback", carryGuest);
            var callback = await authenticate(url, AppScheme, cancel);
            return await CompleteSignInAsync(callback);
        }

        // ── A console's sign-in (device flow through the broker) ──────────────

        internal async Task<DeviceLink> StartDeviceLinkAsync(string? deviceName)
        {
            if (opts.DeviceClientId == null) throw new InvalidOperationException("krtuworks-config.json names no device client for this environment");
            var answer = await Token(new Dictionary<string, string?> { ["scope"] = Resolved.PlayerScope, ["device_name"] = deviceName }, "/v1/device/authorize", opts.DeviceClientId);
            if (answer.Status != 200) throw Wire.ErrorOf(answer);
            var j = answer.Json!;
            return new DeviceLink
            {
                DeviceCode = (string)j["device_code"]!,
                UserCode = (string)j["user_code"]!,
                VerificationUri = (string)j["verification_uri"]!,
                VerificationUriComplete = (string?)j["verification_uri_complete"] ?? (string)j["verification_uri"]!,
                ExpiresIn = (int?)j["expires_in"] ?? 600,
                Interval = (int?)j["interval"] ?? 5,
            };
        }

        /// <summary>Waits for the player to approve the code on another device (RFC 8628 polling, slow_down respected).</summary>
        internal async Task<Player> FinishDeviceLinkAsync(DeviceLink link, CancellationToken cancel)
        {
            var interval = Math.Max(1, link.Interval);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(link.ExpiresIn);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await opts.Delay(TimeSpan.FromSeconds(interval), cancel);
                var answer = await Token(new Dictionary<string, string?> { ["grant_type"] = DeviceCodeGrant, ["device_code"] = link.DeviceCode }, "/v1/device/token", opts.DeviceClientId);
                if (answer.Status == 200)
                {
                    Player player;
                    using (await Locks.Take(LockName))
                    {
                        var meta = await MetaAsync();
                        meta.Epoch++;
                        meta.GuestRequest = null;
                        await SaveMeta(meta);
                        var before = await Json.Get<StoredTokens>(secrets, Key("tokens"));
                        var next = TokensFrom(answer.Json, "account", before, "device");
                        if (before?.Kind == "guest" && before.Subject != next.Subject && !meta.WaitingOwners.Contains(before.Subject))
                        {
                            meta.WaitingOwners.Add(before.Subject);
                            await SaveMeta(meta);
                        }
                        await Adopt(next);
                        player = Player!;
                    }
                    Emit(new IdentityChange { Type = "signed_in", Player = player });
                    return player;
                }
                var err = Wire.ErrorOf(answer);
                if (err.Code == "authorization_pending") continue;
                if (err.Code == "slow_down")
                {
                    interval += 5;
                    continue;
                }
                throw err;
            }
            throw new KrtuworksException("expired_token", "The code expired before it was approved.", 400);
        }

        /// <summary>Forgets the player on this device (a guest's key stays: signing in as the guest again reaches it).</summary>
        internal async Task SignOutAsync()
        {
            StoredTokens? stored;
            using (await Locks.Take(LockName))
            {
                var meta = await MetaAsync();
                meta.Epoch++;
                meta.GuestRequest = null;
                await SaveMeta(meta);
                stored = await Json.Get<StoredTokens>(secrets, Key("tokens"));
                await Adopt(null);
            }
            if (stored?.RefreshToken != null && stored.Via == "oidc")
            {
                try
                {
                    await Token(new Dictionary<string, string?> { ["token"] = stored.RefreshToken, ["token_type_hint"] = "refresh_token" }, "/oidc/token/revocation");
                }
                catch (KrtuworksException) { /* offline: the token expires on its own */ }
            }
            Emit(new IdentityChange { Type = "signed_out", Reason = "signed_out" });
        }
    }

    /// <summary>
    /// The one request the system browser sends back to the app (RFC 8252 §7.3): a listener on 127.0.0.1
    /// at a port the system picks, answering a small page and handing the address over.
    /// </summary>
    internal sealed class LoopbackReceiver : IDisposable
    {
        readonly TcpListener listener;
        internal int Port { get; }

        internal LoopbackReceiver()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        internal async Task<string> WaitAsync(CancellationToken cancel)
        {
            using (cancel.Register(() => listener.Stop()))
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(); }
                    catch (Exception) when (cancel.IsCancellationRequested) { throw new OperationCanceledException(cancel); }
                    using (client)
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[8192];
                        var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancel);
                        var line = Encoding.ASCII.GetString(buffer, 0, read).Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                        var parts = line.Split(' ');
                        var path = parts.Length > 1 ? parts[1] : "/";
                        var ours = path.StartsWith("/callback", StringComparison.Ordinal);
                        var page = ours
                            ? "<!doctype html><meta charset=utf-8><title>Krtuworks ID</title><body style=\"font-family:sans-serif;padding:3em\">You are signed in. You can close this window and go back to the game.</body>"
                            : "";
                        var head = ours ? "HTTP/1.1 200 OK" : "HTTP/1.1 404 Not Found";
                        var bytes = Encoding.UTF8.GetBytes($"{head}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(page)}\r\nConnection: close\r\n\r\n{page}");
                        await stream.WriteAsync(bytes, 0, bytes.Length, cancel);
                        if (ours) return $"http://127.0.0.1:{Port}{path}";
                    }
                }
            }
        }

        public void Dispose() => listener.Stop();
    }
}
