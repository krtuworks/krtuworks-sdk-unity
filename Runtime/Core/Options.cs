#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Krtuworks
{
    /// <summary>
    /// `krtuworks-config.json`, downloaded from the app's "Credentials" page: nothing secret, only who the
    /// app is and its players' sign-in clients per environment.
    /// </summary>
    public sealed class KrtuworksAppConfig
    {
        [JsonProperty("appId")] public string AppId = "";
        [JsonProperty("slug")] public string Slug = "";
        /// <summary>GAME, MOBILE_APP, WEB_APP or DESKTOP_APP. The game-only parts (levels, deaths, Steam) stay off in the other kinds; a file made before the field existed is a game's.</summary>
        [JsonProperty("type")] public string? Type;
        /// <summary>`online`: the very first launch (no player yet) needs the server; without it <see cref="KrtuworksClient.State"/> is `first_launch_offline`. Otherwise the game starts offline-first.</summary>
        [JsonProperty("firstLaunch")] public string? FirstLaunch;
        [JsonProperty("environments")] public Dictionary<string, EnvironmentClients> Environments = new Dictionary<string, EnvironmentClients>();

        public sealed class EnvironmentClients
        {
            /// <summary>The native sign-in client (desktop, mobile, Steam): `<slug>.<env>.native`.</summary>
            [JsonProperty("clientId")] public string? ClientId;
            /// <summary>The console sign-in client (device flow): `<slug>.<env>.device`.</summary>
            [JsonProperty("deviceClientId")] public string? DeviceClientId;
            [JsonProperty("webClientId")] public string? WebClientId;
        }

        public static KrtuworksAppConfig FromJson(string json) => JsonConvert.DeserializeObject<KrtuworksAppConfig>(json) ?? throw new ArgumentException("not a krtuworks-config.json");
    }

    public sealed class DeviceInfo
    {
        [JsonProperty("os")] public string? Os;
        [JsonProperty("osVersion")] public string? OsVersion;
        [JsonProperty("model")] public string? Model;
        [JsonProperty("gpu")] public string? Gpu;
        [JsonProperty("ramMb")] public int? RamMb;
        [JsonProperty("screenWidth")] public int? ScreenWidth;
        [JsonProperty("screenHeight")] public int? ScreenHeight;
    }

    public sealed class KrtuworksOptions
    {
        public KrtuworksAppConfig Config = new KrtuworksAppConfig();
        /// <summary>TEST or LIVE.</summary>
        public string Environment = "LIVE";
        /// <summary>STEAM, EPIC, XBOX, PLAYSTATION, NINTENDO, IOS, ANDROID, WEB, WINDOWS, MACOS or LINUX.</summary>
        public string Platform = "WINDOWS";
        /// <summary>The app's own version label (`1.4.2+381`), sent with every call.</summary>
        public string Build = "";
        public string? ClientId;
        public IKrtuStore? Store;
        public ISecretStore? Secrets;
        public IHttpTransport? Http;
        public IPlatformServices? PlatformServices;
        public DeviceInfo? Device;
        /// <summary>The player's language tag (`tr`, `pt-BR`).</summary>
        public string? Locale;
        /// <summary>Opens a session and keeps it alive. Default true.</summary>
        public bool Sessions = true;
        /// <summary>How often <see cref="KrtuworksClient.SamplePosition"/> tells where the player is, in seconds (1-600). Default 10.</summary>
        public int? PositionSampleSeconds;
        public string IdOrigin = "https://id.krtuworks.com";
        public string DevOrigin = "https://dev.krtuworks.com";
        /// <summary>
        /// How the SDK waits (heartbeats, sending, polling); by default <c>Task.Delay</c>. The Unity layer
        /// counts frames instead: WebGL has no threads, so a timer would never fire there.
        /// </summary>
        public Func<TimeSpan, System.Threading.CancellationToken, System.Threading.Tasks.Task>? Delay;
    }

    /// <summary>
    /// What a platform gives the SDK. The Steam module
    /// (Steamworks.NET) implements it; a platform without one passes nothing.
    /// </summary>
    public interface IPlatformServices
    {
        /// <summary>A fresh web API ticket for the `krtuworks-id` identity (Steam's GetAuthTicketForWebApi, hex), or null.</summary>
        System.Threading.Tasks.Task<string?> GetWebApiTicketAsync();

        /// <summary>The platform's ids of the achievements the player has unlocked there.</summary>
        System.Threading.Tasks.Task<IReadOnlyList<string>> UnlockedAchievementsAsync();

        /// <summary>Unlocks an achievement on the platform by its platform id.</summary>
        System.Threading.Tasks.Task UnlockAchievementAsync(string platformId);

        /// <summary>Takes an achievement back on the platform by its platform id (support took it back here).</summary>
        System.Threading.Tasks.Task ClearAchievementAsync(string platformId);
    }

    /// <summary>A platform that can take back a web API ticket that was got and never sent (Steam: <c>CancelAuthTicket</c>), so it is not left valid.</summary>
    public interface IPlatformTicketCancel
    {
        void CancelWebApiTicket(string ticket);
    }

    /// <summary>
    /// The platform client's own answer to "does the player own this product" (Steam: <c>SteamApps.BIsSubscribedApp</c> for a DLC's AppID),
    /// for a platform module that has one. The server's answer comes first; this one stands in when the server did not check in this launch.
    /// </summary>
    public interface IPlatformOwnership
    {
        /// <summary>True or false as the platform client says; null when it cannot tell (the platform client is not running).</summary>
        bool? OwnsProduct(string platformProductId);
    }

    /// <summary>
    /// The platform's stats (Steam stats), for a platform module that holds them: the SDK mirrors the server's
    /// values there (the server is the record; a stat's type `INT` or `FLOAT` is the platform's own).
    /// </summary>
    public interface IPlatformStats
    {
        /// <summary>The stat's value on the platform; null when the platform does not hold it (not published there, another type).</summary>
        System.Threading.Tasks.Task<double?> GetStatAsync(string platformId, string type);

        /// <summary>Sets the stat on the platform, locally; false when the platform refused it (not published there, another type).</summary>
        System.Threading.Tasks.Task<bool> SetStatAsync(string platformId, string type, double value);

        /// <summary>Sends the stats set locally to the platform's servers (Steam's StoreStats).</summary>
        System.Threading.Tasks.Task StoreStatsAsync();
    }

    /// <summary>
    /// The platform's own leaderboards (Steam leaderboards), for a platform module that has them. The SDK only reads: the
    /// player's own row of a board, to bring it in once as a score. It never writes a score to the platform (the server does).
    /// </summary>
    public interface IPlatformLeaderboards
    {
        /// <summary>
        /// The signed-in player's own row of the platform's board of that name. Null when the platform could not tell (no such
        /// board there, no answer in time); otherwise <see cref="PlatformLeaderboardScore.Present"/> says whether the player has a row.
        /// </summary>
        System.Threading.Tasks.Task<PlatformLeaderboardScore?> ReadOwnScoreAsync(string platformName);
    }

    /// <summary>
    /// The platform's rich presence (Steam rich presence), for a platform module that has it: what the player's friends see
    /// the player doing. The SDK decides what to show (the server's states and the player's choice); the platform only writes.
    /// </summary>
    public interface IPlatformPresence
    {
        /// <summary>Sets one presence key; an empty value removes it. False when the platform refused it (a key or value past its limits, too many keys).</summary>
        bool SetRichPresence(string key, string value);

        /// <summary>Removes every presence key this game set.</summary>
        void ClearRichPresence();
    }

    /// <summary>The player's row of a platform leaderboard.</summary>
    public sealed class PlatformLeaderboardScore
    {
        /// <summary>False when the player has no row on that board.</summary>
        public bool Present;
        public long Score;
    }

    /// <summary>
    /// The platform's per-player cloud (Steam Cloud), for a platform module that has one: the SDK mirrors the app's
    /// mirrored slots there (the server is the record; only a slot's current version is on the platform).
    /// </summary>
    public interface IPlatformCloud
    {
        /// <summary>Whether the player and the game have the platform's cloud on (Steam: the account's and the app's setting).</summary>
        bool CloudEnabled();

        /// <summary>The file of that name in the platform's cloud, or null when there is none; a file that is there but could not be read throws (a retryable <see cref="KrtuworksException"/>).</summary>
        System.Threading.Tasks.Task<PlatformCloudFile?> ReadFileAsync(string name);

        /// <summary>Writes the file; false when the platform refused it (no room, an error).</summary>
        System.Threading.Tasks.Task<bool> WriteFileAsync(string name, byte[] data);

        /// <summary>The bytes still free for this player and game in the platform's cloud; null when the platform does not tell.</summary>
        System.Threading.Tasks.Task<long?> AvailableBytesAsync();
    }

    /// <summary>A file in the platform's cloud.</summary>
    public sealed class PlatformCloudFile
    {
        public byte[] Data = Array.Empty<byte>();
        /// <summary>When it was last written, Unix seconds; 0 when the platform does not tell.</summary>
        public long WrittenAt;
    }
}

namespace Krtuworks.Internal
{
    internal sealed class Resolved
    {
        internal string AppId = "";
        internal string Slug = "";
        internal string Type = "GAME";
        internal string Environment = "";
        internal string Platform = "";
        internal string Build = "";
        internal string ClientId = "";
        internal string? DeviceClientId;
        internal string IdOrigin = "";
        internal string DevOrigin = "";
        internal string Issuer = "";
        internal DeviceInfo? Device;
        internal string? Locale;
        internal bool Sessions;
        internal int PositionSampleSeconds;
        /// <summary>The app needs the server on the very first launch (`firstLaunch: "online"`).</summary>
        internal bool FirstLaunchOnline;
        internal Func<TimeSpan, System.Threading.CancellationToken, System.Threading.Tasks.Task> Delay = System.Threading.Tasks.Task.Delay;

        /// <summary>The app is a game (type GAME): levels, deaths and the platform's services are used.</summary>
        internal bool Game => Type == "GAME";

        internal const string DevResource = "https://dev.krtuworks.com";
        internal const string PlayerScope = "openid offline_access profile app:player";
        internal const string GuestScope = "openid offline_access app:player";

        static readonly Regex BuildLabel = new Regex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$");

        internal static Resolved From(KrtuworksOptions o)
        {
            if (string.IsNullOrEmpty(o.Config.AppId) || string.IsNullOrEmpty(o.Config.Slug)) throw new ArgumentException("Config must be the app's krtuworks-config.json");
            if (o.Environment != "TEST" && o.Environment != "LIVE") throw new ArgumentException("Environment is TEST or LIVE");
            if (!BuildLabel.IsMatch(o.Build ?? "")) throw new ArgumentException("Build is the app's version label, like 1.4.2+381");
            o.Config.Environments.TryGetValue(o.Environment, out var env);
            var clientId = o.ClientId ?? env?.ClientId;
            if (string.IsNullOrEmpty(clientId)) throw new ArgumentException($"krtuworks-config.json names no client for {o.Environment}");
            var idOrigin = o.IdOrigin.TrimEnd('/');
            return new Resolved
            {
                AppId = o.Config.AppId,
                Slug = o.Config.Slug,
                Type = string.IsNullOrEmpty(o.Config.Type) ? "GAME" : o.Config.Type!,
                Environment = o.Environment,
                Platform = o.Platform,
                Build = o.Build!,
                ClientId = clientId!,
                DeviceClientId = env?.DeviceClientId,
                IdOrigin = idOrigin,
                DevOrigin = o.DevOrigin.TrimEnd('/'),
                Issuer = $"{idOrigin}/oidc",
                Device = o.Device,
                Locale = o.Locale,
                Sessions = o.Sessions,
                PositionSampleSeconds = PositionSampler.Interval(o.PositionSampleSeconds),
                FirstLaunchOnline = string.Equals(o.Config.FirstLaunch, "online", StringComparison.OrdinalIgnoreCase),
                Delay = o.Delay ?? System.Threading.Tasks.Task.Delay,
            };
        }
    }

    internal static class Modules
    {
        /// <summary>Which module a ready-made event family belongs to; any other type is the app's own.</summary>
        internal static readonly Dictionary<string, string> FamilyModule = new Dictionary<string, string>
        {
            ["screen_view"] = "ui", ["button"] = "ui", ["tutorial"] = "ui",
            ["progression"] = "progression",
            ["death"] = "combat", ["damage"] = "combat",
            ["position_sample"] = "positions",
            ["economy"] = "economy", ["balance"] = "economy",
            ["item"] = "items", ["interaction"] = "items",
            ["performance"] = "performance",
            ["settings"] = "settings",
            ["error"] = "errors",
        };

        internal static string Of(string type) => FamilyModule.TryGetValue(type, out var m) ? m : "customEvents";
    }
}
