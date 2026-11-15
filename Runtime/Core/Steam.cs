#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Krtuworks
{
    /// <summary>
    /// The Steam calls the SDK makes, and no more. The Unity adapter (Steamworks.NET) is the real one; the tests
    /// give <see cref="SteamAchievementSync"/> a fake, so the decisions run without Steam.
    /// </summary>
    public interface ISteamBackend
    {
        /// <summary>Steam's <c>SteamAPI.IsSteamRunning()</c>.</summary>
        bool IsSteamRunning();

        /// <summary>
        /// <c>SteamUser.GetAuthTicketForWebApi(identity)</c>: the handle the answer will carry (0 when Steam refused to
        /// start one). The answer arrives later, as a callback the adapter forwards to
        /// <see cref="SteamAchievementSync.OnTicketAnswer"/>.
        /// </summary>
        uint RequestWebApiTicket(string identity);

        /// <summary><c>SteamUser.CancelAuthTicket(handle)</c>: a ticket nobody will use stops being valid.</summary>
        void CancelAuthTicket(uint handle);

        /// <summary><c>SteamUserStats.GetNumAchievements()</c>.</summary>
        uint GetNumAchievements();

        /// <summary><c>SteamUserStats.GetAchievementName(index)</c>.</summary>
        string GetAchievementName(uint index);

        /// <summary><c>SteamUserStats.GetAchievement(name, out achieved)</c>: false when the call itself failed.</summary>
        bool TryGetAchievement(string name, out bool achieved);

        /// <summary><c>SteamUserStats.SetAchievement(name)</c>: true when Steam took it.</summary>
        bool SetAchievement(string name);

        /// <summary><c>SteamUserStats.ClearAchievement(name)</c>: true when Steam took it.</summary>
        bool ClearAchievement(string name);

        /// <summary><c>SteamUserStats.GetStat(name, out int)</c>: false when Steam has no such INT stat.</summary>
        bool TryGetStatInt(string name, out int value);

        /// <summary><c>SteamUserStats.GetStat(name, out float)</c>: false when Steam has no such FLOAT stat.</summary>
        bool TryGetStatFloat(string name, out float value);

        /// <summary><c>SteamUserStats.SetStat(name, int)</c>: true when Steam took it.</summary>
        bool SetStatInt(string name, int value);

        /// <summary><c>SteamUserStats.SetStat(name, float)</c>: true when Steam took it.</summary>
        bool SetStatFloat(string name, float value);

        /// <summary><c>SteamUserStats.StoreStats()</c>.</summary>
        void StoreStats();

        /// <summary><c>SteamRemoteStorage.IsCloudEnabledForAccount() &amp;&amp; IsCloudEnabledForApp()</c>.</summary>
        bool IsCloudEnabled();

        /// <summary><c>SteamRemoteStorage.FileExists(name)</c>.</summary>
        bool FileExists(string name);

        /// <summary><c>SteamRemoteStorage.FileRead(name, …)</c> over <c>GetFileSize(name)</c> bytes; null when the read failed.</summary>
        byte[]? FileRead(string name);

        /// <summary><c>SteamRemoteStorage.GetFileTimestamp(name)</c>: Unix seconds.</summary>
        long FileTimestamp(string name);

        /// <summary><c>SteamRemoteStorage.FileWrite(name, data, data.Length)</c>: true when Steam took it.</summary>
        bool FileWrite(string name, byte[] data);

        /// <summary><c>SteamRemoteStorage.GetQuota(out total, out available)</c>: false when Steam could not tell.</summary>
        bool TryGetQuota(out ulong total, out ulong available);

        /// <summary>
        /// <c>SteamUserStats.FindLeaderboard(name)</c> and its <c>LeaderboardFindResult_t</c>: the board's handle; null when
        /// Steam has no such board or did not answer in time. The SDK only reads leaderboards: there is no upload call here.
        /// </summary>
        Task<ulong?> FindLeaderboardAsync(string name);

        /// <summary>
        /// <c>SteamUserStats.DownloadLeaderboardEntriesForUsers(handle, [the local SteamID])</c>, its
        /// <c>LeaderboardScoresDownloaded_t</c> and <c>GetDownloadedLeaderboardEntry</c>: the local player's own row. Null when Steam
        /// did not answer in time or failed; otherwise whether the player has a row and its score.
        /// </summary>
        Task<PlatformLeaderboardScore?> DownloadOwnEntryAsync(ulong leaderboard);

        /// <summary><c>SteamApps.BIsSubscribedApp(appId)</c>: the Steam client's own answer for a game or a DLC's AppID.</summary>
        bool IsSubscribedApp(uint appId);

        /// <summary><c>SteamFriends.SetRichPresence(key, value)</c> (an empty value removes the key): true when Steam took it.</summary>
        bool SetRichPresence(string key, string value);

        /// <summary><c>SteamFriends.ClearRichPresence()</c>.</summary>
        void ClearRichPresence();
    }

    /// <summary>
    /// The Steam half of <see cref="IPlatformServices"/>, <see cref="IPlatformStats"/>, <see cref="IPlatformCloud"/>,
    /// <see cref="IPlatformLeaderboards"/>, <see cref="IPlatformPresence"/> and <see cref="IPlatformOwnership"/> over an <see cref="ISteamBackend"/>: the web API
    /// ticket for the `krtuworks-id` identity (the Steam guest and every session's licence check), the achievements, the stats,
    /// the Steam Cloud files, the player's own row of a Steam leaderboard (read only), Steam rich presence and the Steam client's own ownership answer for a DLC.
    /// </summary>
    public sealed class SteamAchievementSync : IPlatformServices, IPlatformStats, IPlatformCloud, IPlatformLeaderboards, IPlatformPresence, IPlatformOwnership
    {
        const string TicketIdentity = "krtuworks-id";

        /// <summary>How long a ticket's answer is waited for.</summary>
        public const int DefaultTicketWaitMs = 10000;

        readonly ISteamBackend steam;
        readonly int ticketWaitMs;
        readonly Dictionary<uint, TaskCompletionSource<string?>> waiting = new Dictionary<uint, TaskCompletionSource<string?>>();

        public SteamAchievementSync(ISteamBackend steam, int ticketWaitMs = DefaultTicketWaitMs)
        {
            this.steam = steam;
            this.ticketWaitMs = ticketWaitMs;
        }

        public async Task<string?> GetWebApiTicketAsync()
        {
            if (!steam.IsSteamRunning()) return null;
            var answer = new TaskCompletionSource<string?>();
            var handle = steam.RequestWebApiTicket(TicketIdentity);
            if (handle == 0) return null;
            waiting[handle] = answer;
            var first = await Task.WhenAny(answer.Task, Task.Delay(ticketWaitMs));
            waiting.Remove(handle);
            var ticket = first == answer.Task ? answer.Task.Result : null;
            // A ticket that came too late, was refused or came back empty is never sent: cancel it so it is not left valid.
            if (ticket == null) steam.CancelAuthTicket(handle);
            return ticket;
        }

        /// <summary>
        /// Steam's answer to a ticket (<c>GetTicketForWebApiResponse_t</c>): the first <paramref name="length"/> bytes of
        /// <paramref name="ticket"/>, or null when Steam's result was not OK. An empty ticket counts as no ticket, as in the
        /// Unreal core. An answer nobody waits for is dropped.
        /// </summary>
        public void OnTicketAnswer(uint handle, byte[]? ticket, int length)
        {
            if (!waiting.TryGetValue(handle, out var asked)) return;
            if (ticket == null || length <= 0)
            {
                asked.TrySetResult(null);
                return;
            }
            var hex = new StringBuilder(length * 2);
            for (var i = 0; i < length; i++) hex.Append(ticket[i].ToString("x2"));
            asked.TrySetResult(hex.ToString());
        }

        public Task<IReadOnlyList<string>> UnlockedAchievementsAsync()
        {
            var unlocked = new List<string>();
            var count = steam.GetNumAchievements();
            for (uint i = 0; i < count; i++)
            {
                var name = steam.GetAchievementName(i);
                if (steam.TryGetAchievement(name, out var achieved) && achieved) unlocked.Add(name);
            }
            return Task.FromResult<IReadOnlyList<string>>(unlocked);
        }

        public Task UnlockAchievementAsync(string platformId)
        {
            if (steam.SetAchievement(platformId)) steam.StoreStats();
            return Task.CompletedTask;
        }

        public Task ClearAchievementAsync(string platformId)
        {
            if (steam.ClearAchievement(platformId)) steam.StoreStats();
            return Task.CompletedTask;
        }

        public Task<double?> GetStatAsync(string platformId, string type)
        {
            if (type == "INT") return Task.FromResult(steam.TryGetStatInt(platformId, out var whole) ? (double?)whole : null);
            return Task.FromResult(steam.TryGetStatFloat(platformId, out var real) ? (double?)real : null);
        }

        /// <summary>The value is the platform's already (an INT within 32 bits, a FLOAT in single precision; <see cref="StatMirror.PlatformValue"/>).</summary>
        public Task<bool> SetStatAsync(string platformId, string type, double value) =>
            Task.FromResult(type == "INT" ? steam.SetStatInt(platformId, (int)value) : steam.SetStatFloat(platformId, (float)value));

        public Task StoreStatsAsync()
        {
            steam.StoreStats();
            return Task.CompletedTask;
        }

        public bool CloudEnabled() => steam.IsSteamRunning() && steam.IsCloudEnabled();

        public Task<PlatformCloudFile?> ReadFileAsync(string name)
        {
            if (!steam.FileExists(name)) return Task.FromResult<PlatformCloudFile?>(null);
            // A file Steam lists but could not read is not "no file": writing over it could lose another PC's newer
            // save, so the slot waits for the next sync.
            var data = steam.FileRead(name) ?? throw new KrtuworksException("platform_read_failed", $"Steam Cloud could not read {name}.");
            return Task.FromResult<PlatformCloudFile?>(new PlatformCloudFile { Data = data, WrittenAt = steam.FileTimestamp(name) });
        }

        public Task<bool> WriteFileAsync(string name, byte[] data) => Task.FromResult(steam.FileWrite(name, data));

        public Task<long?> AvailableBytesAsync() =>
            Task.FromResult(steam.TryGetQuota(out _, out var available) ? (long?)(long)Math.Min(available, long.MaxValue) : null);

        public async Task<PlatformLeaderboardScore?> ReadOwnScoreAsync(string platformName)
        {
            if (!steam.IsSteamRunning()) return null;
            var board = await steam.FindLeaderboardAsync(platformName);
            return board.HasValue ? await steam.DownloadOwnEntryAsync(board.Value) : null;
        }

        public bool? OwnsProduct(string platformProductId) =>
            steam.IsSteamRunning() && uint.TryParse(platformProductId, out var appId) ? steam.IsSubscribedApp(appId) : (bool?)null;

        public bool SetRichPresence(string key, string value) => steam.IsSteamRunning() && steam.SetRichPresence(key, value);

        public void ClearRichPresence()
        {
            if (steam.IsSteamRunning()) steam.ClearRichPresence();
        }
    }
}
