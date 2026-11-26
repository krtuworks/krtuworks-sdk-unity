#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Krtuworks.Internal;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Krtuworks
{
    public sealed class Gate
    {
        public bool Maintenance;
        public string? MaintenanceMessage;
        /// <summary>The owner's minimum version is newer than this build: the player must update from the store.</summary>
        public bool UpdateRequired;
        public BuildSwitch? SwitchedOff;
    }

    public sealed class StatView
    {
        [JsonProperty("key")] public string Key = "";
        [JsonProperty("name")] public string Name = "";
        [JsonProperty("description")] public string? Description;
        /// <summary>SUM, MAX, MIN or LATEST.</summary>
        [JsonProperty("kind")] public string Kind = "";
        [JsonProperty("valueType")] public string ValueType = "";
        [JsonProperty("value")] public double? Value;
        [JsonProperty("updatedAt")] public string? UpdatedAt;
        /// <summary>The stat on each platform (a Steam stat's API name); its Steam type follows <see cref="ValueType"/>.</summary>
        [JsonProperty("platformIds")] public Dictionary<string, string> PlatformIds = new Dictionary<string, string>();
    }

    public sealed class AchievementView
    {
        [JsonProperty("key")] public string Key = "";
        [JsonProperty("id")] public string Id = "";
        [JsonProperty("name")] public string? Name;
        [JsonProperty("description")] public string? Description;
        [JsonProperty("hidden")] public bool Hidden;
        [JsonProperty("retired")] public bool Retired;
        [JsonProperty("unlocked")] public bool Unlocked;
        [JsonProperty("unlockedAt")] public string? UnlockedAt;
        [JsonProperty("platformIds")] public Dictionary<string, string> PlatformIds = new Dictionary<string, string>();
        [JsonProperty("iconUrl")] public string? IconUrl;
        [JsonProperty("lockedIconUrl")] public string? LockedIconUrl;
    }

    public sealed class ProgressView
    {
        [JsonProperty("locale")] public string? Locale;
        [JsonProperty("stats")] public List<StatView> Stats = new List<StatView>();
        [JsonProperty("achievements")] public List<AchievementView> Achievements = new List<AchievementView>();
    }

    public sealed class Experiment
    {
        [JsonProperty("key")] public string Key = "";
        [JsonProperty("configKey")] public string ConfigKey = "";
        [JsonProperty("group")] public string Group = "";
        [JsonProperty("value")] public JToken? Value;
    }

    public sealed class PlayerProfile
    {
        public Dictionary<string, JToken> Fields = new Dictionary<string, JToken>();
        public string? UpdatedAt;
    }

    public sealed class MergeSettlement
    {
        public string PreviousSubject = "";
        /// <summary>Saves the merged account left waiting on this device: the player chooses (AdoptWaitingSaveAsync / DiscardWaitingSaveAsync).</summary>
        public List<WaitingSave> WaitingSaves = new List<WaitingSave>();
    }

    public sealed class SendResult
    {
        public ProgressFlushResult Progress = new ProgressFlushResult();
        public BugFlushResult Bugs = new BugFlushResult();
        public FlushResult Events = new FlushResult();
    }

    /// <summary>Why the SDK suggests a guest links a Krtuworks ID now: first_achievement, first_save or play_time.</summary>
    public delegate void LinkReminderHandler(string reason);

    /// <summary>
    /// The Krtuworks SDK's core: one instance per app and environment, the rules of the JS package
    /// (krtuworks-sdk-js/src/client.ts) with the native sign-ins (system browser + loopback, a console's code, Steam).
    /// In Unity, <c>KrtuworksUnity.InitializeAsync</c> builds it with the engine's transport and stores.
    /// </summary>
    public sealed class KrtuworksClient
    {
        /// <summary>The terms a new guest accepts on the age screen (Krtuworks ID Terms + Privacy, the versions this SDK shows).</summary>
        public const string CurrentTerms = Identity.CurrentTerms;

        /// <summary><see cref="State"/> when the SDK is ready.</summary>
        public const string StateReady = "ready";

        /// <summary>
        /// <see cref="State"/> when the app asks for the server on the very first launch (`firstLaunch: "online"` in krtuworks-config.json),
        /// there is no player yet, and the server could not be reached: the game shows "connect to play" and tries again with
        /// <see cref="RefreshConfigAsync"/> (or leaves it to the SDK's own refresh).
        /// </summary>
        public const string StateFirstLaunchOffline = "first_launch_offline";

        /// <summary>`ready`, or `first_launch_offline` (see <see cref="StateFirstLaunchOffline"/>). Without `firstLaunch: "online"` it is always `ready`: the game starts offline-first.</summary>
        public string State { get; private set; } = StateReady;

        static readonly Regex EventType = new Regex("^[a-z][a-z0-9_]{0,63}$");
        static readonly Regex Pairwise = new Regex("^pw_[A-Za-z0-9_-]{8,120}$");
        const long FlushMs = 30_000;
        const long ConfigMs = 5 * 60_000;
        const long FlushSoonMs = 2_000;

        readonly Resolved options;
        readonly IKrtuStore store;
        readonly IHttpTransport http;
        readonly IPlatformServices? platform;
        readonly StatMirror? statMirror;
        readonly CloudMirror? cloudMirror;
        readonly LeaderboardImport? leaderboardImport;
        readonly Clock clock = new Clock();
        readonly Identity identity;
        readonly Api api;
        readonly EventQueue events;
        readonly ProgressQueue progress;
        readonly SessionKeeper sessions;
        readonly RemoteConfig config;
        readonly Files files;
        readonly ProgressionTracker levels;
        readonly PositionSampler positions;
        readonly BugReports bugs;
        readonly PresenceKeeper presence;
        readonly EntitlementKeeper licences;
        readonly Stopwatch uptime = Stopwatch.StartNew();
        readonly CancellationTokenSource stopping = new CancellationTokenSource();
        bool optedOut;
        Dictionary<string, JToken?> experiments = new Dictionary<string, JToken?>();
        string? sessionOwner;
        long countedActive;
        long flushSoonAt = long.MaxValue;
        long lastFlushAt;
        long lastConfigAt;
        bool platformSynced;
        Task settling = Task.CompletedTask;
        readonly SingleFlight<SendResult> flushing = new SingleFlight<SendResult>();

        /// <summary>The player changed: signed_in · guest · signed_out · merged · deletion_pending · sanctioned; and while an account is being merged, merging (once) · merging_ended (once).</summary>
        public event Action<IdentityChange>? IdentityChanged;
        /// <summary>A merged account's waiting queues moved to the signed-in player (after the server confirmed the merge).</summary>
        public event Action<MergeSettlement>? MergeSettled;
        /// <summary>
        /// A guest has something to lose (the first achievement, the first save, a while of play) and the app's
        /// `linkReminder` setting says it is time: the game shows its own prompt and, if the player agrees, starts
        /// a sign-in. Remote config `linkReminder`: `{ enabled, afterMinutes, everyDays }` (default on, 30, 3).
        /// </summary>
        public event LinkReminderHandler? LinkReminder;
        /// <summary>
        /// One of the player's bug reports was fixed since the app last looked (at a player's arrival, and on
        /// <see cref="MyBugReportsAsync"/>): the game may tell the player "fixed in <c>FixedIn</c>".
        /// </summary>
        public event Action<BugReportView>? BugFixed;
        /// <summary>
        /// The session proved a Steam account this player does not mirror with, for a reason the player should
        /// hear (told at a player's arrival; <see cref="PlatformLink"/> says the same at any time):
        /// PENDING_CONSENT, a second Steam account: ask whether the progress may also show on it, then
        /// <see cref="AnswerPlatformConsentAsync"/> · LIMIT, a third one: a game mirrors with two at most, one can
        /// be removed at id.krtuworks.com/apps · MISMATCH: this Steam account belongs to another Krtuworks ID.
        /// A refused one is the player's own answer and is not told.
        /// </summary>
        public event Action<PlatformLink>? PlatformLinkNotice;
        /// <summary>
        /// Steam Cloud holds a copy of a mirrored SAVE or PROFILE slot that differs from the server's, and nothing tells
        /// which is newer (the first time this device compares them, or both moved since). The copy waits on the server;
        /// the game asks the player which to keep and calls <see cref="ResolveFileAsync"/> (`keep`: the chosen version's
        /// id, `current`: <c>Server.Id</c>). Told once per waiting copy and launch; nothing in that slot is mirrored
        /// until the player chose.
        /// </summary>
        public event Action<PlatformSaveConflict>? PlatformSaveConflict;

        KrtuworksClient(KrtuworksOptions o)
        {
            options = Resolved.From(o);
            positions = new PositionSampler(options.PositionSampleSeconds);
            store = o.Store ?? new MemoryStore();
            var secrets = o.Secrets ?? throw new ArgumentException("Secrets is the platform's secret store (tokens and the device key)");
            http = o.Http ?? new HttpClientTransport();
            // The platform's services (Steam) are a game's only.
            platform = options.Game ? o.PlatformServices : null;
            if (platform is IPlatformStats platformStats) statMirror = new StatMirror(platformStats, () => uptime.ElapsedMilliseconds);
            if (platform is IPlatformLeaderboards platformBoards) leaderboardImport = new LeaderboardImport(platformBoards, store, options.ClientId);
            identity = new Identity(options, store, secrets, http, clock, platform);
            api = new Api(options, identity, http, clock);
            string? Subject() => identity.Player?.Subject;
            events = new EventQueue(options, store, clock, api, Subject);
            progress = new ProgressQueue(options, store, api, Subject);
            var steamSessions = options.Platform == "STEAM" && platform != null;
            sessions = new SessionKeeper(options, api, Subject, steamSessions ? platform!.GetWebApiTicketAsync : (Func<Task<string?>>?)null);
            config = new RemoteConfig(options, api, store);
            sessions.CancelTicket = identity.CancelTicket;
            sessions.Wanted = () => options.Sessions && config.ModuleOn("sessions") && config.SwitchForBuild()?.Mode != "ALL";
            files = new Files(options, api, store, Subject, () => identity.Generation, () => _ = Quietly(RemindLinking("first_save"))) { Stop = stopping.Token };
            if (platform is IPlatformCloud platformCloud)
                cloudMirror = new CloudMirror(platformCloud, files, store, options.ClientId, conflict =>
                {
                    try { PlatformSaveConflict?.Invoke(conflict); }
                    catch (Exception) { /* the app's listener */ }
                }, (code, slot) => Error($"{options.Platform} cloud: {code} ({slot})", "other", $"sdk.{code}"));
            levels = new ProgressionTracker(store, options.ClientId, () => Subject() ?? EventQueue.Anon, () => sessions.ActiveSeconds, data => Track("progression", data));
            bugs = new BugReports(options, store, api, () => Subject() ?? EventQueue.Anon, Subject, report =>
            {
                var listeners = BugFixed;
                if (listeners == null) return false;
                listeners(report);
                return true;
            });
            presence = new PresenceKeeper(options, platform as IPlatformPresence, api, store, () => config.ModuleOn("presence"), () => levels.InPlay?.Level, () => sessions.SessionActiveSeconds, line => bugs.Log(line));
            sessions.OnHeartbeat = () => presence.ReapplyAuto(false);
            licences = new EntitlementKeeper(options, platform as IPlatformOwnership, api, store, Subject);
            sessions.OnEntitlements = licences.FromSessionAsync;
        }

        static async Task Quietly(Task work)
        {
            try { await work; }
            catch (Exception) { /* background work: the next tick tries again */ }
        }

        /// <summary>Makes the SDK ready: the player and the remote config this device kept, then the server in the background.</summary>
        public static async Task<KrtuworksClient> InitializeAsync(KrtuworksOptions options)
        {
            var krtu = new KrtuworksClient(options);
            krtu.sessionOwner = (await krtu.identity.LoadAsync())?.Subject;
            await krtu.config.LoadAsync();
            await krtu.presence.LoadAsync(krtu.sessionOwner);
            await krtu.licences.EnsureLoadedAsync();
            krtu.identity.Changed += krtu.OnIdentityChanged;
            krtu.lastFlushAt = krtu.lastConfigAt = krtu.uptime.ElapsedMilliseconds;
            if (krtu.options.FirstLaunchOnline && krtu.identity.Player == null && krtu.config.Value == null)
            {
                // The very first launch of an app that needs the server: it has to answer now, or the game says so.
                try { await krtu.config.RefreshAsync(); }
                catch (KrtuworksException err) when (err.Status == 0) { krtu.State = StateFirstLaunchOffline; }
                catch (KrtuworksException) { /* the server answered, just not with a config: the SDK's own refresh asks again */ }
            }
            else _ = Quietly(krtu.RefreshConfigAsync());
            krtu.settling = krtu.Settle();
            _ = krtu.Loop(krtu.stopping.Token);
            return krtu;
        }

        /// <summary>The background work: heartbeats, sending every 30 s (2 s after progress), the remote config every 5 min.</summary>
        async Task Loop(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try { await options.Delay(TimeSpan.FromSeconds(1), stop); }
                catch (OperationCanceledException) { return; }
                var now = uptime.ElapsedMilliseconds;
                await Quietly(sessions.DueAsync());
                if (now >= flushSoonAt || now - lastFlushAt >= FlushMs) await Quietly(FlushAsync());
                if (statMirror != null) await Quietly(statMirror.StoreAsync(false));
                if (now - lastConfigAt >= ConfigMs)
                {
                    lastConfigAt = now;
                    await Quietly(RefreshConfigAsync());
                    await Quietly(presence.RefreshAsync());
                }
            }
        }

        // ── Who plays ─────────────────────────────────────────────────────────

        public Player? Player => identity.Player;

        /// <summary>The SDK's own store, for the small values the ready-made screens keep between launches.</summary>
        internal IKrtuStore Store => store;

        /// <summary>The SDK's wait: the ready-made screens wait through it too (no <c>Task.Delay</c> in WebGL).</summary>
        internal Func<TimeSpan, CancellationToken, Task> Delay => options.Delay;

        internal string IdOrigin => options.IdOrigin;

        /// <summary>The server's time now (the device clock moved by the offset the SDK learned from the server).</summary>
        internal DateTimeOffset ServerNow => DateTimeOffset.FromUnixTimeMilliseconds(clock.ServerNowMs);

        public string Environment => options.Environment;

        /// <summary>GAME, MOBILE_APP, WEB_APP or DESKTOP_APP, from krtuworks-config.json.</summary>
        public string AppType => options.Type;

        /// <summary>Plays as a guest on this device; a new guest needs the age screen's answers (birth date `yyyy-MM-dd`, <see cref="CurrentTerms"/>). On Steam the Steam account's player.</summary>
        public Task<Player> SignInAsGuestAsync(string? birthDate = null, string? acceptedTerms = null) =>
            identity.SignInAsGuestAsync(birthDate, acceptedTerms, options.Platform == "STEAM" && platform != null);

        /// <summary>
        /// Desktop sign-in with Krtuworks ID (RFC 8252): opens the system browser on the sign-in page and waits
        /// for it to come back to this app. A guest playing here is carried along unless `carryGuest` is false.
        /// </summary>
        public async Task<Player> SignInWithBrowserAsync(Action<string> openBrowser, CancellationToken cancel = default, bool carryGuest = true)
        {
            await events.PersistAsync();
            return await identity.SignInWithBrowserAsync(openBrowser, cancel, carryGuest);
        }

        /// <summary>The scheme a phone app registers so the sign-in page can come back to it (`com.krtuworks.<slug>`).</summary>
        public string AppScheme => identity.AppScheme;

        /// <summary>
        /// Phone sign-in with Krtuworks ID: `authenticate(url, scheme, cancel)` is the platform's
        /// authentication session; it opens `url` and completes with the address the browser came back to on
        /// `scheme`. The Unity layer supplies it for iOS and Android.
        /// </summary>
        public async Task<Player> SignInWithAppAsync(Func<string, string, CancellationToken, Task<string>> authenticate, CancellationToken cancel = default, bool carryGuest = true)
        {
            await events.PersistAsync();
            return await identity.SignInWithAppAsync(authenticate, cancel, carryGuest);
        }

        /// <summary>Finishes a sign-in from the address the browser came back to (a phone app the system closed while the browser was open, started again by that address).</summary>
        public Task<Player> CompleteSignInAsync(string callbackUrl) => identity.CompleteSignInAsync(callbackUrl);

        /// <summary>A console's or TV's sign-in: show <see cref="DeviceLink.UserCode"/> and the address, then <see cref="FinishDeviceLinkAsync"/>.</summary>
        public Task<DeviceLink> StartDeviceLinkAsync(string? deviceName = null) => identity.StartDeviceLinkAsync(deviceName);

        /// <summary>Waits until the player approved the code on another device.</summary>
        public Task<Player> FinishDeviceLinkAsync(DeviceLink link, CancellationToken cancel = default) => identity.FinishDeviceLinkAsync(link, cancel);

        public async Task SignOutAsync()
        {
            await Quietly(FlushAsync());
            await sessions.EndAsync();
            await identity.SignOutAsync();
        }

        void OnIdentityChanged(IdentityChange change)
        {
            if (stopping.IsCancellationRequested) return;
            if (change.Type == "merging" || change.Type == "merging_ended")
            {
                // Only the game is told: the player and the queues are as they were.
                try { IdentityChanged?.Invoke(change); }
                catch (Exception) { /* the app's listener */ }
                return;
            }
            if (change.Type == "signed_out" && change.Reason == "account_deleted" && change.PreviousSubject != null) _ = Quietly(DropOwnerAsync(change.PreviousSubject));
            if (change.Type == "merged" || change.Type == "signed_out" || change.Type == "deletion_pending" || change.Type == "sanctioned")
            {
                sessions.Close();
                sessionOwner = null;
                presence.PlayerChanged(null);
                ForgetPlayer();
            }
            else
            {
                // A guest that became a Krtuworks ID is the same player: its session goes on.
                if (sessionOwner != change.Player?.Subject)
                {
                    sessions.Close();
                    ForgetPlayer();
                }
                sessionOwner = change.Player?.Subject;
                presence.PlayerChanged(sessionOwner);
                events.ResetBackoff();
                progress.ResetBackoff();
                bugs.ResetBackoff();
                settling = After(settling);
            }
            try { IdentityChanged?.Invoke(change); }
            catch (Exception) { /* the app's listener */ }
        }

        /// <summary>What the last player's answers left in memory: the next player starts from the app's own values.</summary>
        void ForgetPlayer()
        {
            licences.Forget();
            positions.Reset();
            experiments = new Dictionary<string, JToken?>();
            optedOut = false;
        }

        /// <summary>The player's account is gone: what waited on this device for it can never be taken.</summary>
        async Task DropOwnerAsync(string owner)
        {
            await events.DropAsync(owner);
            await progress.DropAsync(owner);
            await bugs.DropAsync(owner);
            await files.DropOwnerAsync(owner);
        }

        async Task After(Task previous)
        {
            await Quietly(previous);
            await Quietly(Settle());
        }

        /// <summary>
        /// What follows a player's arrival: the anonymous queues go to the first player; a merged account's
        /// waiting queues move here once the server confirms the merge; the
        /// session opens; unfinished saves are finished; the queues are sent. Without a player, a guest the
        /// server could not make on an earlier launch is tried again.
        /// </summary>
        async Task Settle()
        {
            if (stopping.IsCancellationRequested) return;
            var player = identity.Player;
            if (player == null)
            {
                if ((await identity.MetaAsync()).GuestRequest != null) await Quietly(SignInAsGuestAsync());
                return;
            }
            await events.MoveAsync(EventQueue.Anon, player.Subject);
            await progress.MoveAsync(EventQueue.Anon, player.Subject);
            await bugs.MoveAsync(EventQueue.Anon, player.Subject);
            await Quietly(SettleMerges(player.Subject));
            await Quietly(ReadOptOut());
            await Quietly(licences.EnsureLoadedAsync());
            if (stopping.IsCancellationRequested) return;
            if (options.Sessions && config.ModuleOn("sessions")) await Quietly(sessions.OpenAsync());
            if (options.Game && options.Platform == "STEAM") await Quietly(licences.RefreshAsync());
            await Quietly(files.ResumeAsync());
            await Quietly(FlushAsync());
            if (config.ModuleOn("bugReports")) await Quietly(bugs.CheckFixedAsync());
            if (platform != null) await Quietly(SettlePlatformLink());
            await Quietly(presence.RefreshAsync());
        }

        /// <summary>
        /// The session's platform account: mirrored, the achievements line up once per launch; waiting for the
        /// player's answer, a third account or another Krtuworks ID's, the game is told. None of those, a refused
        /// one or none proved mirrors nothing, so nothing is sent.
        /// </summary>
        async Task SettlePlatformLink()
        {
            var link = sessions.Session?.PlatformLink;
            if (link != null && (link.State == "PENDING_CONSENT" || link.State == "LIMIT" || link.State == "MISMATCH"))
            {
                PlatformLinkNotice?.Invoke(link);
                return;
            }
            if (link?.State != "ACTIVE" || platformSynced) return;
            platformSynced = true;
            await Quietly(SyncPlatformAchievementsAsync());
            await MirrorFiles(null);
            await Quietly(ImportPlatformScoresAsync());
        }

        /// <summary>
        /// Brings the player's own row of each platform leaderboard that takes it in (a Steam name, import on) in as a score,
        /// once per board and platform account. Only with the session's platform account mirrored; the platform is only read.
        /// </summary>
        async Task ImportPlatformScoresAsync()
        {
            var link = sessions.Session?.PlatformLink;
            var owner = identity.Player?.Subject;
            if (leaderboardImport == null || link?.State != "ACTIVE" || owner == null || !config.ModuleOn("leaderboards")) return;
            var boards = await GetLeaderboardsAsync();
            if (await leaderboardImport.RunAsync(owner, link.Platform, link.PlatformAccountId, boards, (key, score) => progress.ReportScoreAsync(key, score, null, true)) > 0) ScheduleFlush();
        }

        /// <summary>
        /// Lines the platform's cloud (Steam Cloud) up with the mirrored slots now. It runs on its own when a mirrored
        /// session starts, after each save of a mirrored slot and after the player's choice; a copy that waits for the
        /// player is told through <see cref="PlatformSaveConflict"/>. Never throws: a platform failure leaves the slot alone.
        /// </summary>
        public Task SyncPlatformFilesAsync() => MirrorFiles(null);

        /// <summary>
        /// The platform's cloud lines up with the mirrored slots (all of them, or one after a save): only while the
        /// session's platform account is mirrored for this player and the cloudSave module is on.
        /// </summary>
        async Task MirrorFiles(string? slot)
        {
            var session = sessions.Session;
            var owner = identity.Player?.Subject;
            if (cloudMirror == null || session?.PlatformLink?.State != "ACTIVE" || owner == null || !config.ModuleOn("cloudSave")) return;
            await Quietly(cloudMirror.SyncAsync(owner, session.PlatformLink.PlatformAccountId, session.Id, slot));
        }

        async Task SettleMerges(string subject)
        {
            foreach (var previous in (await identity.WaitingOwnersAsync()).ToList())
            {
                if (previous == subject || !Pairwise.IsMatch(previous))
                {
                    await identity.ForgetWaitingOwnerAsync(previous);
                    continue;
                }
                var answer = await api.PlayerAsync("/v1/me/merge-check", new PlayerCall { Method = "POST", Json = new JObject { ["previousSubject"] = previous } });
                if (answer.Json?["confirmed"]?.Type != JTokenType.Boolean || !(bool)answer.Json["confirmed"]!) continue;
                await events.MoveAsync(previous, subject);
                await progress.MoveAsync(previous, subject);
                await bugs.MoveAsync(previous, subject);
                await identity.ForgetWaitingOwnerAsync(previous);
                var settled = new MergeSettlement { PreviousSubject = previous, WaitingSaves = await files.WaitingForAsync(previous) };
                try { MergeSettled?.Invoke(settled); }
                catch (Exception) { /* the app's listener */ }
            }
        }

        /// <summary>Waits for the work that follows a player's arrival (tests, or a game that wants to know it is done).</summary>
        public Task ReadyAsync() => settling;

        // ── Telemetry ─────────────────────────────────────────────────────────

        static JObject? DataOf(object? data) => data switch
        {
            null => null,
            JObject j => j,
            _ => JObject.FromObject(data, JsonSerializer.Create(Json.Settings)),
        };

        /// <summary>
        /// Records an event (a ready-made family like `screen_view` or `economy`, or the app's own type).
        /// Nothing is made when its module is off, the owner switched this build's telemetry off, or the
        /// player turned statistics off. Returns whether the event was kept.
        /// </summary>
        public bool Track(string type, object? data = null, EventPosition? position = null, int? schemaVersion = null)
        {
            if (!EventType.IsMatch(type)) throw new ArgumentException($"\"{type}\" is not an event type (a lowercase letter, then lowercase letters, digits and _; at most 64)");
            if (!TelemetryAllowed(Modules.Of(type))) return false;
            var json = DataOf(data);
            bugs.NoteEvent(events.Add(type, json != null && json.Count > 0 ? json : null, position, sessions.Session?.Id, schemaVersion));
            return true;
        }

        bool TelemetryAllowed(string module) => !optedOut && config.SwitchForBuild() == null && config.ModuleOn(module);

        /// <summary>A level starts: the SDK counts the attempt and its times and deaths. Levels and deaths are for games only (nothing happens in an app).</summary>
        public async Task LevelStartAsync(LevelRef level)
        {
            if (!options.Game) return;
            await levels.StartAsync(level);
            presence.ReapplyAuto(true);
        }

        public void LevelComplete(double? score = null)
        {
            if (options.Game) levels.Finish("complete", score);
        }

        public void LevelFail(double? score = null)
        {
            if (options.Game) levels.Finish("fail", score);
        }

        public void LevelQuit()
        {
            if (options.Game) levels.Finish("quit", null);
        }

        public void LevelCheckpoint(string name)
        {
            if (options.Game) levels.Checkpoint(name);
        }

        public Task LevelRestartAsync() => options.Game ? levels.RestartAsync() : Task.CompletedTask;

        /// <summary>A death, counted toward the level in play and told with its place. Games only: false in an app.</summary>
        public bool Death(string cause, string? enemy = null, EventPosition? position = null)
        {
            if (!options.Game) return false;
            var place = levels.NoteDeath();
            var data = new JObject { ["cause"] = cause };
            if (enemy != null) data["enemy"] = enemy;
            if (!string.IsNullOrEmpty(place?.World)) data["world"] = place!.World;
            if (!string.IsNullOrEmpty(place?.Level)) data["level"] = place!.Level;
            return Track("death", data, position);
        }

        /// <summary>
        /// Where the player is now; call it as often as you like (every frame is fine). At most one `position_sample`
        /// goes out per <see cref="KrtuworksOptions.PositionSampleSeconds"/> of active play, carrying the seconds since
        /// the one before, with the level in play. Games only, with the positions module on; returns whether a sample was kept.
        /// </summary>
        public bool SamplePosition(EventPosition position)
        {
            if (!options.Game) return false;
            if (position == null || (position.Space != "world" && position.Space != "screen") || string.IsNullOrEmpty(position.Area))
                throw new ArgumentException("position is {Space: world|screen, Area, X, Y, Z?}");
            if (!IsFinite(position.X) || !IsFinite(position.Y) || !IsFinite(position.Z ?? 0)) throw new ArgumentException("position X, Y and Z are finite numbers");
            var seconds = positions.Due(sessions.ActiveSeconds);
            if (seconds == null) return false;
            var place = levels.InPlay;
            var data = new JObject { ["seconds"] = seconds.Value };
            if (!string.IsNullOrEmpty(place?.World)) data["world"] = place!.World;
            if (!string.IsNullOrEmpty(place?.Level)) data["level"] = place!.Level;
            return Track("position_sample", data, position);
        }

        static bool IsFinite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);

        /// <summary>An error the game caught, as the errors family tells it (kind: exception, crash, assert, other).</summary>
        public bool Error(Exception err, string kind = "exception", string? fingerprint = null, bool handled = true) =>
            Error($"{err.GetType().Name}: {err.Message}", kind, fingerprint, handled);

        public bool Error(string message, string kind = "exception", string? fingerprint = null, bool handled = true)
        {
            bugs.Log($"error {message}");
            var place = levels.InPlay;
            var data = new JObject { ["kind"] = kind, ["message"] = message.Length > 500 ? message.Substring(0, 500) : message, ["handled"] = handled };
            if (fingerprint != null) data["fingerprint"] = fingerprint.Length > 100 ? fingerprint.Substring(0, 100) : fingerprint;
            if (!string.IsNullOrEmpty(place?.World)) data["world"] = place!.World;
            if (!string.IsNullOrEmpty(place?.Level)) data["level"] = place!.Level;
            return Track("error", data);
        }

        // ── Bug reports ───────────────────────────────────────────────────────

        /// <summary>The categories of a bug report, the same menu in every app.</summary>
        public static IReadOnlyList<string> BugCategories => BugReports.Categories;

        /// <summary>
        /// A bug report from the player. The SDK adds the platform, build, device, language, level in play,
        /// frame rate, the last 50 events and the log lines. The report is kept on this device first and sent
        /// when it can be, its screenshot right after it; a player sends at most 5 an hour and 20 a day in an
        /// app. Refused at once when the owner turned bug reports off (`module_disabled`) or the input can never
        /// be taken (`invalid_request`). Returns the report's id on this device.
        /// </summary>
        public async Task<string> ReportBugAsync(BugReportInput input)
        {
            if (!config.ModuleOn("bugReports")) throw new KrtuworksException("module_disabled", "Bug reports are off in this app.", 403);
            var place = levels.InPlay;
            var level = place == null ? null : string.Join("/", new[] { place.World, place.Level, place.Part }.Where(x => !string.IsNullOrEmpty(x)));
            var reportId = await bugs.ReportAsync(input, level);
            ScheduleFlush();
            return reportId;
        }

        /// <summary>A line for the bug reports' log (kept in memory, the last 32 KB; sent only inside a report the player makes).</summary>
        public void Log(string message) => bugs.Log(message);

        /// <summary>The player's bug reports in this app, newest first, with their status and the build a fix shipped in.</summary>
        public Task<List<BugReportView>> MyBugReportsAsync() => bugs.MineAsync();

        /// <summary>
        /// Reports another player's profile picture: <paramref name="subject"/> is the id your app knows them
        /// by (another player of this app, e.g. from your game server), the reason one of
        /// <see cref="AvatarReportReasons"/>, an optional note of at most 300 characters. Krtuworks reviews it;
        /// the reporter is never told an outcome. Once per player and picture (again answers true:
        /// already reported), at most 10 a day (<c>too_many_reports</c>). Needs a connection: nothing waits on
        /// this device.
        /// </summary>
        public async Task<bool> ReportAvatarAsync(string subject, string reason, string? note = null)
        {
            var (path, json) = AvatarReportReasons.Body(subject, reason, note);
            var answer = await api.PlayerAsync(path, new PlayerCall { Method = "POST", Json = json });
            return answer.Json?["already"]?.Type == JTokenType.Boolean && (bool)answer.Json["already"]!;
        }

        /// <summary>The player's statistics choice in this app (on by default). Off: no event is made or kept.</summary>
        public async Task<bool> GetTelemetryOptOutAsync()
        {
            await ReadOptOut();
            return optedOut;
        }

        public async Task SetTelemetryOptOutAsync(bool optOut)
        {
            await api.PlayerAsync("/v1/me/telemetry-opt-out", new PlayerCall { Method = "PUT", Json = new JObject { ["optedOut"] = optOut } });
            optedOut = optOut;
            await store.SetAsync(OptOutKey, optOut ? "true" : "false");
        }

        string OptOutKey => $"krtu:{options.ClientId}:optout:{identity.Player?.Subject ?? EventQueue.Anon}";

        async Task ReadOptOut()
        {
            var key = OptOutKey;
            // A player with nothing kept on this device is on until the server says otherwise.
            optedOut = await store.GetAsync(key) == "true";
            if (identity.Player == null) return;
            var answer = await api.PlayerAsync("/v1/me/telemetry-opt-out", new PlayerCall());
            // Another player came in while the answer was on its way: it is not theirs.
            if (OptOutKey != key) return;
            optedOut = answer.Json?["optedOut"]?.Type == JTokenType.Boolean && (bool)answer.Json["optedOut"]!;
            await store.SetAsync(key, optedOut ? "true" : "false");
        }

        // ── Progress ──────────────────────────────────────────────────────────

        void ScheduleFlush()
        {
            var at = uptime.ElapsedMilliseconds + FlushSoonMs;
            if (at < flushSoonAt) flushSoonAt = at;
        }

        /// <summary>Adds to a SUM stat. Kept on the device until the server has it; never lost, never counted twice.</summary>
        public async Task IncrementStatAsync(string key, double by = 1)
        {
            await progress.ReportAsync("add", key, by);
            ScheduleFlush();
        }

        /// <summary>Reports a value to a MAX, MIN or LATEST stat.</summary>
        public async Task SetStatAsync(string key, double value)
        {
            await progress.ReportAsync("set", key, value);
            ScheduleFlush();
        }

        /// <summary>
        /// Reports a score for a leaderboard. Kept on the device until the server has it, like a stat: never lost, never
        /// counted twice. The board decides what it does (KEEP_BEST keeps the better score, FORCE_UPDATE takes it; a
        /// score outside the board's range is refused for good). <paramref name="details"/> are 1 to 64 bytes the game keeps with the score.
        /// </summary>
        public async Task SubmitScoreAsync(string key, long value, byte[]? details = null)
        {
            await progress.ReportScoreAsync(key, value, details, false);
            ScheduleFlush();
        }

        /// <summary>The app's leaderboards with the player's score and rank (names in <paramref name="locale"/>, else the SDK's locale).</summary>
        public async Task<List<LeaderboardView>> GetLeaderboardsAsync(string? locale = null)
        {
            RequireLeaderboards();
            var answer = await api.PlayerAsync("/v1/me/leaderboards", new PlayerCall { Query = new Dictionary<string, string?> { ["locale"] = locale ?? options.Locale } });
            var boards = (answer.Json?["leaderboards"] as JArray ?? new JArray()).Select(b => b.ToObject<LeaderboardView>() ?? new LeaderboardView()).ToList();
            await progress.LearnBoardsAsync(boards.Select(b => (b.Key, b.SortOrder, b.UploadMethod)));
            return boards;
        }

        /// <summary>
        /// A page of a leaderboard in its order. <paramref name="scope"/> `global` reads from <paramref name="offset"/>; `around`
        /// centres the page on the player (a player with no score gets the top; the offset stays 0). 1 to 100 rows.
        /// </summary>
        public async Task<LeaderboardEntries> GetLeaderboardEntriesAsync(string key, string scope = "global", int limit = 25, int offset = 0)
        {
            if (scope != "global" && scope != "around") throw new ArgumentException("scope is global or around");
            if (limit < 1 || limit > 100) throw new ArgumentException("limit is 1 to 100");
            if (offset < 0 || (scope == "around" && offset != 0)) throw new ArgumentException("offset is 0 or more, and 0 for around");
            RequireLeaderboards();
            var answer = await api.PlayerAsync($"/v1/leaderboards/{Uri.EscapeDataString(key)}/entries", new PlayerCall
            {
                Query = new Dictionary<string, string?> { ["scope"] = scope, ["limit"] = limit.ToString(), ["offset"] = offset.ToString() },
            });
            return answer.Json?.ToObject<LeaderboardEntries>() ?? new LeaderboardEntries();
        }

        void RequireLeaderboards()
        {
            if (!config.ModuleOn("leaderboards")) throw new KrtuworksException("module_disabled", "Leaderboards are off in this app.", 403);
        }

        /// <summary>
        /// Unlocks an achievement here (kept until the server has it) and on the platform when its id there is known
        /// and the session's platform account is mirrored for this player (else the next sync does it).
        /// </summary>
        public async Task UnlockAchievementAsync(string key)
        {
            await progress.UnlockAsync(key);
            ScheduleFlush();
            if (platform != null && Mirroring)
            {
                var ids = await Json.Get<Dictionary<string, string>>(store, PlatformIdsKey);
                if (ids != null && ids.TryGetValue(key, out var platformId)) await Quietly(platform.UnlockAchievementAsync(platformId));
            }
            await Quietly(RemindLinking("first_achievement"));
        }

        string PlatformIdsKey => $"krtu:{options.ClientId}:platformIds:{options.Platform}";

        string StatIdsKey => $"krtu:{options.ClientId}:statIds:{options.Platform}";

        /// <summary>The session proved a platform account this player mirrors with: the platform may be written to.</summary>
        bool Mirroring => sessions.Session?.PlatformLink?.State == "ACTIVE";

        /// <summary>The stats the server just gave values to, set on the platform (the server's value, never an increase).</summary>
        async Task MirrorStats(Dictionary<string, double> values)
        {
            if (statMirror == null || values.Count == 0 || !Mirroring) return;
            var ids = await Json.Get<Dictionary<string, StatPlatformId>>(store, StatIdsKey);
            if (ids == null) return;
            foreach (var pair in values)
                if (ids.TryGetValue(pair.Key, out var stat)) await statMirror.SetAsync(stat, pair.Value);
            await statMirror.StoreAsync(false);
        }

        /// <summary>The app's stats and achievements with the player's values (names in the SDK's locale where the app has them).</summary>
        public async Task<ProgressView> GetProgressAsync()
        {
            var answer = await api.PlayerAsync("/v1/me/progress", new PlayerCall { Query = new Dictionary<string, string?> { ["locale"] = options.Locale } });
            var view = answer.Json?.ToObject<ProgressView>() ?? new ProgressView();
            await progress.LearnKindsAsync(view.Stats.Select(s => (s.Key, s.Kind)));
            var ids = view.Achievements.Where(a => a.PlatformIds.ContainsKey(options.Platform)).ToDictionary(a => a.Key, a => a.PlatformIds[options.Platform]);
            await Json.Put(store, PlatformIdsKey, ids);
            var statIds = view.Stats.Where(st => st.PlatformIds.ContainsKey(options.Platform))
                .ToDictionary(st => st.Key, st => new StatPlatformId { Id = st.PlatformIds[options.Platform], Type = st.ValueType == "integer" ? "INT" : "FLOAT" });
            await Json.Put(store, StatIdsKey, statIds);
            return view;
        }

        /// <summary>
        /// Lines achievements and stats up with the platform's (Steam): what the platform has is unlocked here, what
        /// this player has here is unlocked there, and what support took back here is cleared there too; the stats
        /// the platform holds are read and sent, the server takes them in by each stat's kind (`StatsImported`) and
        /// names the values the platform should hold (`StatsToPlatform`, set and stored at once). Runs once per
        /// launch on its own when the session's Steam account is mirrored; call it after a platform sign-in. The open
        /// session goes with it: the server mirrors only with the Steam account the session proved
        /// (`409 platform_link_mismatch`, <c>Details["state"]</c>, when that account is not mirrored for this player).
        /// </summary>
        public async Task<(List<string> UnlockedHere, List<string> ToPlatform, List<string> ToClear, List<string> StatsImported, List<string> StatsToPlatform)> SyncPlatformAchievementsAsync()
        {
            if (platform == null) throw new InvalidOperationException("No platform services were given (or this is not a game)");
            var session = sessions.Session ?? (options.Sessions && config.ModuleOn("sessions") ? await sessions.OpenAsync() : null);
            var unlocked = await platform.UnlockedAchievementsAsync();
            var body = new JObject { ["platform"] = options.Platform, ["build"] = options.Build, ["unlocked"] = new JArray(unlocked.ToArray()) };
            var statIds = new Dictionary<string, StatPlatformId>();
            if (statMirror != null)
            {
                await GetProgressAsync();
                statIds = await Json.Get<Dictionary<string, StatPlatformId>>(store, StatIdsKey) ?? statIds;
                var held = new JArray();
                foreach (var stat in statIds.Values)
                {
                    var value = await statMirror.ReadAsync(stat);
                    if (value.HasValue) held.Add(new JObject { ["id"] = stat.Id, ["value"] = value.Value });
                }
                body["stats"] = held;
            }
            if (session != null) body["sessionId"] = session.Id;
            var answer = await api.PlayerAsync("/v1/me/progress/platform-sync", new PlayerCall { Method = "POST", Json = body });
            List<string> Ids(string name) => (answer.Json?[name] as JArray ?? new JArray()).Select(v => (string?)v ?? "").ToList();
            var here = Ids("unlockedHere");
            var there = Ids("toPlatform");
            var clear = Ids("toClear");
            var imported = Ids("statsImported");
            var statsThere = new List<string>();
            foreach (var id in there) await Quietly(platform.UnlockAchievementAsync(id));
            foreach (var id in clear) await Quietly(platform.ClearAchievementAsync(id));
            if (statMirror != null)
            {
                var byId = statIds.Values.ToDictionary(st => st.Id);
                foreach (var item in answer.Json?["statsToPlatform"] as JArray ?? new JArray())
                {
                    var id = (string?)item["id"] ?? "";
                    if (!byId.TryGetValue(id, out var stat) || (item["value"]?.Type != JTokenType.Integer && item["value"]?.Type != JTokenType.Float)) continue;
                    await Quietly(statMirror.SetAsync(stat, (double)item["value"]!));
                    statsThere.Add(id);
                }
                await Quietly(statMirror.StoreAsync(true));
            }
            return (here, there, clear, imported, statsThere);
        }

        /// <summary>The platform account (Steam) the open session proved and whether this player mirrors with it; null when none was proved.</summary>
        public PlatformLink? PlatformLink => sessions.Session?.PlatformLink;

        /// <summary>
        /// The player's answer, from the game's own dialog, for the Steam account the session proved while it waits
        /// (<see cref="PlatformLinkNotice"/> with PENDING_CONSENT): allowed, the progress mirrors with it in every game (the
        /// achievements line up at once); refused, it is never asked again (the account site at id.krtuworks.com/apps
        /// changes either). Nothing waiting: <c>consent_not_pending</c>, nothing sent.
        /// </summary>
        public async Task<PlatformLink> AnswerPlatformConsentAsync(bool allow)
        {
            var session = sessions.Session;
            var link = session?.PlatformLink;
            if (session == null || link == null || link.State != "PENDING_CONSENT")
                throw new KrtuworksException("consent_not_pending", "No Steam account is waiting for the player's answer in this session.");
            var answer = await api.PlayerAsync("/v1/me/platform-links/consent", new PlayerCall
            {
                Method = "POST",
                Json = new JObject { ["platform"] = link.Platform, ["platformAccountId"] = link.PlatformAccountId, ["allow"] = allow, ["sessionId"] = session.Id },
            });
            var after = SessionKeeper.LinkOf(answer.Json?["platformLink"]) ?? throw new KrtuworksException("unknown", "The server's answer carried no platform link.");
            session.PlatformLink = after;
            if (after.State == "ACTIVE" && platform != null)
            {
                platformSynced = true;
                await Quietly(SyncPlatformAchievementsAsync());
                await MirrorFiles(null);
                await Quietly(ImportPlatformScoresAsync());
            }
            return after;
        }

        /// <summary>Progress the server refused for good (a retired stat, a wrong value): for the game's own diagnostics.</summary>
        public IReadOnlyList<ProgressRefusal> ProgressRefusals => progress.Refusals.ToList();

        // ── What the player owns (the game and its DLCs) ──────────────────────

        /// <summary>
        /// The player's licences as the server last said them (kept on this device, so an earlier launch's answer is here when the server
        /// cannot be reached): the game (`Sku` base) first, then the DLCs. <see cref="Entitlement.Checked"/> marks what the server confirmed
        /// with the platform in this launch. Empty for an app that is not a game.
        /// </summary>
        public IReadOnlyList<Entitlement> Entitlements => options.Game ? licences.List() : new List<Entitlement>();

        /// <summary>Asks the server for the stored licences now (`GET /v1/me/entitlements`) and keeps the answer. Throws when the server cannot be reached.</summary>
        public Task<IReadOnlyList<Entitlement>> RefreshEntitlementsAsync() =>
            options.Game ? licences.RefreshAsync() : Task.FromResult<IReadOnlyList<Entitlement>>(new List<Entitlement>());

        /// <summary>
        /// Whether the player owns the DLC with this product key. The server's answer for this launch first (its check of the session's Steam
        /// ticket); when the server could not check, the platform client's own answer (Steam took the money, so Steam is authoritative);
        /// without that, the last answer the server gave on an earlier launch. False for an unknown key and for an app that is not a game.
        /// </summary>
        public bool OwnsDlc(string key) => options.Game && licences.OwnsDlc(key);

        // ── Rich presence ─────────────────────────────────────────────────────

        /// <summary>
        /// Shows the player's state (`in_level`) on the platform's rich presence (Steam), with the values of the parameters the state
        /// declares. LEVEL (the level in play) and SESSION_MINUTES (minutes of play this session) fill themselves when the game gives
        /// none and follow a level start and each heartbeat; any other parameter without a value is <c>param_missing</c>. A value is
        /// text (numbers in the invariant culture) cut to 256 UTF-8 bytes; a parameter the state does not declare is dropped. Never
        /// throws for the reasons <see cref="PresenceResult"/> names; a player who hides their presence keeps the state waiting for
        /// <see cref="SetPresenceVisibleAsync"/>.
        /// </summary>
        public Task<PresenceResult> SetPresenceAsync(string key, IDictionary<string, object>? parameters = null) => Task.FromResult(presence.Set(key, parameters));

        /// <summary>Clears the platform's presence and forgets the state the game wanted shown.</summary>
        public void ClearPresence() => presence.Clear();

        /// <summary>
        /// The party the player is in, for the platform's "join" and group display (Steam's player group): written with every state and
        /// at once. Null clears it; <paramref name="size"/> is the party's current size.
        /// </summary>
        public void SetPresenceGroup(string? groupId, int? size = null) => presence.SetGroup(groupId, size);

        /// <summary>Whether the player shows their presence, as this device last knew it; null before the first read. The player's own choice, kept on the server.</summary>
        public bool? PresenceVisible => presence.Visible;

        /// <summary>The player's choice to show their presence. Off clears the platform's presence at once and keeps the state the game wanted; on writes it again. <c>module_disabled</c> while presence is off in this app.</summary>
        public Task SetPresenceVisibleAsync(bool visible) => presence.SetVisibleAsync(visible);

        // ── The app's settings, A/B tests, status ─────────────────────────────

        public async Task RefreshConfigAsync()
        {
            await config.RefreshAsync();
            State = StateReady;
        }

        /// <summary>The owner's gates for this build: maintenance, a newer minimum version, a switch.</summary>
        public Gate GetGate()
        {
            var maintenance = config.Get("maintenance") as JObject;
            var minVersion = config.Get("minVersion");
            return new Gate
            {
                Maintenance = maintenance?["enabled"]?.Type == JTokenType.Boolean && (bool)maintenance["enabled"]!,
                MaintenanceMessage = maintenance?["message"]?.Type == JTokenType.String ? (string?)maintenance["message"] : null,
                UpdateRequired = minVersion?.Type == JTokenType.String && RemoteConfig.OlderThan(options.Build, (string)minVersion!),
                SwitchedOff = config.SwitchForBuild(),
            };
        }

        /// <summary>The modules the owner left on (all of them until the config says otherwise).</summary>
        public Dictionary<string, bool> ModuleStates() => config.ModuleStates();

        /// <summary>The player's A/B groups; their values take over the config's (<see cref="Value"/>).</summary>
        public async Task<List<Experiment>> RefreshExperimentsAsync()
        {
            var subject = identity.Player?.Subject;
            var answer = await api.PlayerAsync("/v1/me/experiments", new PlayerCall());
            var list = (answer.Json?["experiments"] as JArray)?.ToObject<List<Experiment>>() ?? new List<Experiment>();
            // Another player came in while the answer was on its way: their groups are not these.
            if (identity.Player?.Subject == subject) experiments = list.ToDictionary(e => e.ConfigKey, e => e.Value);
            return list;
        }

        /// <summary>A remote config value, the player's A/B group's when one varies it (only while the A/B module is on).</summary>
        public JToken? Value(string key) => config.ModuleOn("abTests") && experiments.TryGetValue(key, out var value) ? value : config.Get(key);

        /// <summary>A remote config value as `T`, or `fallback` when the owner set none (or it is not a `T`).</summary>
        public T Value<T>(string key, T fallback)
        {
            var token = Value(key);
            if (token == null || token.Type == JTokenType.Null) return fallback;
            try { return token.ToObject<T>()!; }
            catch (Exception e) when (e is JsonException || e is ArgumentException || e is FormatException || e is InvalidCastException) { return fallback; }
        }

        public async Task<JObject> GetStatusAsync()
        {
            var answer = await api.PlayerAsync("/v1/me/status", new PlayerCall { Query = new Dictionary<string, string?> { ["platform"] = options.Platform } });
            return answer.Json ?? new JObject();
        }

        // ── The player's card ─────────────────────────────────────────────────

        static PlayerProfile ProfileOf(JObject? json) => new PlayerProfile
        {
            Fields = (json?["fields"] as JObject)?.Properties().ToDictionary(p => p.Name, p => p.Value) ?? new Dictionary<string, JToken>(),
            UpdatedAt = json?["updatedAt"]?.Type == JTokenType.String ? (string?)json["updatedAt"] : null,
        };

        public async Task<PlayerProfile> GetProfileAsync() => ProfileOf((await api.PlayerAsync("/v1/me/profile", new PlayerCall())).Json);

        /// <summary>Writes the named fields of the player's card (a null value clears one).</summary>
        public async Task<PlayerProfile> UpdateProfileAsync(IDictionary<string, object?> fields)
        {
            var body = new JObject { ["fields"] = JObject.FromObject(fields), ["platform"] = options.Platform, ["build"] = options.Build };
            return ProfileOf((await api.PlayerAsync("/v1/me/profile", new PlayerCall { Method = "PUT", Json = body })).Json);
        }

        // ── Files ─────────────────────────────────────────────────────────────

        public Task<SlotList> ListFilesAsync() => files.ListAsync();

        /// <summary>A slot's current version; null when `ifNoneMatch` (a version id held) is still current.</summary>
        public Task<FileRead?> ReadFileAsync(string slot, SaveDevice? device = null, string? ifNoneMatch = null) => files.ReadAsync(slot, device, ifNoneMatch);

        /// <summary>
        /// Saves a new version of a SAVE or PROFILE slot over `baseVersion` (the version the game read, null for
        /// an empty slot). Throws `file_conflict` (`Details["current"]`) when another device saved first.
        /// </summary>
        public async Task<FileVersion> SaveFileAsync(string slot, byte[] data, string? baseVersion)
        {
            var saved = await files.SaveAsync(slot, data, true, baseVersion, null);
            _ = MirrorFiles(slot);
            return saved;
        }

        /// <summary>Saves a version of a SAVE or PROFILE slot with no check against the slot's current version (the new save wins). To guard against another device's save, name the version read: <see cref="SaveFileAsync(string, byte[], string?)"/>.</summary>
        public async Task<FileVersion> SaveFileAsync(string slot, byte[] data)
        {
            var saved = await files.SaveAsync(slot, data, false, null, null);
            _ = MirrorFiles(slot);
            return saved;
        }

        /// <summary>
        /// Saves a SETTINGS slot (the latest wins), or DEVICE_SETTINGS with this device's id and description. A slot is written at most
        /// once every 30 seconds: a save inside that time waits (the call completes when it was written), and a newer save of the slot
        /// replaces it (<c>save_replaced</c>). What waits is written when the app pauses or ends.
        /// </summary>
        public async Task<FileVersion> SaveSettingsAsync(string slot, byte[] data, SaveDevice? device = null)
        {
            var saved = await files.SaveAsync(slot, data, false, null, device, null, true);
            if (device == null) _ = MirrorFiles(slot);
            return saved;
        }

        public Task<List<WaitingSave>> WaitingSavesAsync(string owner) => files.WaitingForAsync(owner);
        public Task<FileVersion> AdoptWaitingSaveAsync(string uploadId, string? baseVersion) => files.AdoptWaitingAsync(uploadId, baseVersion);
        public Task DiscardWaitingSaveAsync(string uploadId) => files.DiscardWaitingAsync(uploadId);

        /// <summary>
        /// The player's choice between a slot's version and the one waiting (a merged account's, or the platform cloud's copy,
        /// <see cref="PlatformSaveConflict"/>): keep `keep` over `current` (the one the player saw). A mirrored slot's kept
        /// version then goes to the platform's cloud too.
        /// </summary>
        public async Task<FileVersion> ResolveFileAsync(string slot, string keep, string current)
        {
            var kept = await files.ResolveAsync(slot, keep, current);
            var session = sessions.Session;
            var owner = identity.Player?.Subject;
            if (cloudMirror != null && session?.PlatformLink?.State == "ACTIVE" && owner != null && config.ModuleOn("cloudSave"))
                await Quietly(cloudMirror.AfterChoiceAsync(owner, session.PlatformLink.PlatformAccountId, session.Id, slot));
            return kept;
        }

        // ── A guest's own account (no e-mail, no account site: the device key proves it) ──

        async Task<JObject> GuestCall(string path, string purpose, Dictionary<string, string?> extra)
        {
            var fields = new Dictionary<string, string?>(extra) { ["client_id"] = options.ClientId, ["assertion"] = await identity.GuestProofAsync(purpose) };
            var answer = await Wire.Send(http, clock, Wire.Form($"{options.IdOrigin}{path}", fields));
            if (answer.Status >= 400) throw Wire.ErrorOf(answer);
            return answer.Json ?? new JObject();
        }

        /// <summary>Asks to delete this guest account (90 days; signing in as the guest is then only for cancelling or the data copy).</summary>
        public async Task<string> RequestGuestDeletionAsync()
        {
            await Quietly(FlushAsync());
            var answer = await GuestCall("/v1/guest/deletion", "delete", new Dictionary<string, string?> { ["confirm"] = "true" });
            sessions.Close();
            return (string?)answer["deletionDate"] ?? "";
        }

        public async Task<string> CancelGuestDeletionAsync() => (string?)(await GuestCall("/v1/guest/deletion/cancel", "cancel_deletion", new Dictionary<string, string?>()))["status"] ?? "";

        /// <summary>The guest's data copy: asks for a new one with `request`; a ready copy comes with a 15-minute download address.</summary>
        public Task<JObject> GuestDataExportAsync(bool request = false) =>
            GuestCall("/v1/guest/data-export", "export_download", request ? new Dictionary<string, string?> { ["request"] = "true" } : new Dictionary<string, string?>());

        // ── "Link a Krtuworks ID" for guests ───────────────────

        sealed class LinkReminderState
        {
            [JsonProperty("shownAt")] public long? ShownAt;
            [JsonProperty("achievement")] public bool Achievement;
            [JsonProperty("save")] public bool Save;
            [JsonProperty("playSeconds")] public long PlaySeconds;
        }

        async Task RemindLinking(string reason)
        {
            var player = identity.Player;
            if (player?.Kind != PlayerKind.Guest || LinkReminder == null) return;
            var setting = config.Get("linkReminder") as JObject ?? new JObject();
            if (setting["enabled"]?.Type == JTokenType.Boolean && !(bool)setting["enabled"]!) return;
            var key = $"krtu:{options.ClientId}:linkReminder:{player.Subject}";
            var state = await Json.Get<LinkReminderState>(store, key) ?? new LinkReminderState();
            var active = sessions.ActiveSeconds;
            state.PlaySeconds += Math.Max(0, active - countedActive);
            countedActive = active;
            var due = false;
            if (reason == "first_achievement" && !state.Achievement) due = state.Achievement = true;
            if (reason == "first_save" && !state.Save) due = state.Save = true;
            if (reason == "play_time") due = state.PlaySeconds >= ((double?)setting["afterMinutes"] ?? 30) * 60;
            var quietMs = (long)(((double?)setting["everyDays"] ?? 3) * 86_400_000);
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var show = due && (state.ShownAt == null || nowMs - state.ShownAt.Value >= quietMs);
            if (show) state.ShownAt = nowMs;
            await Json.Put(store, key, state);
            if (!show) return;
            try { LinkReminder?.Invoke(reason); }
            catch (Exception) { /* the app's listener */ }
        }

        // ── Sending ───────────────────────────────────────────────────────────

        /// <summary>Sends what waits: progress first (it must not wait behind telemetry), then bug reports, then events. One send at a time.</summary>
        public Task<SendResult> FlushAsync() => flushing.Run(FlushOnce);

        async Task<SendResult> FlushOnce()
        {
            lastFlushAt = uptime.ElapsedMilliseconds;
            flushSoonAt = long.MaxValue;
            await Quietly(RemindLinking("play_time"));
            var result = new SendResult { Progress = await progress.FlushAsync() };
            await Quietly(MirrorStats(result.Progress.Values));
            var blocked = config.SwitchForBuild()?.Mode == "ALL";
            result.Bugs = blocked ? new BugFlushResult { StoppedBy = "switched_off" } : await bugs.FlushAsync();
            result.Events = blocked ? new FlushResult { StoppedBy = "switched_off" } : await events.FlushAsync(config.ModuleOn("errors"));
            return result;
        }

        /// <summary>The validator's round: a session opened, kept alive once and ended, every step answered.</summary>
        internal Task SessionRoundAsync() => sessions.RoundAsync();

        /// <summary>The platform's services the SDK uses (Steam), null in an app that is not a game.</summary>
        internal IPlatformServices? PlatformServices => platform;

        /// <summary>What waits on this device for the signed-in player (or the anonymous queue).</summary>
        public async Task<(int Events, int Progress, int BugReports)> WaitingAsync()
        {
            var owner = identity.Player?.Subject ?? EventQueue.Anon;
            return (await events.SizeAsync(owner), await progress.WaitingAsync(owner), await bugs.WaitingAsync());
        }

        /// <summary>The app went to the background or came back (Unity: OnApplicationPause / OnApplicationFocus).</summary>
        public void SetForeground(bool foreground)
        {
            sessions.SetForeground(foreground);
            if (foreground) return;
            _ = Quietly(events.PersistAsync());
            _ = Quietly(files.FlushSettingsAsync());
            if (statMirror != null) _ = Quietly(statMirror.StoreAsync(true));
        }

        /// <summary>The network came back: the queues try at once.</summary>
        public void Online()
        {
            events.ResetBackoff();
            progress.ResetBackoff();
            bugs.ResetBackoff();
            sessions.RetryOpenSoon();
            if (State != StateReady) _ = Quietly(RefreshConfigAsync());
            _ = Quietly(FlushAsync());
        }

        /// <summary>Stops the SDK: the level in play ends as a quit, the session ends, what waits is written to the store.</summary>
        public async Task ShutdownAsync()
        {
            stopping.Cancel();
            levels.Finish("quit", null);
            await Quietly(files.FlushSettingsAsync());
            await events.PersistAsync();
            if (statMirror != null) await Quietly(statMirror.StoreAsync(true));
            await sessions.EndAsync();
        }
    }
}
