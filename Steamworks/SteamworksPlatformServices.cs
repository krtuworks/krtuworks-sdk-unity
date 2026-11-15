#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Steamworks;

namespace Krtuworks.Steamworks
{
    /// <summary>
    /// Steam for the Krtuworks SDK (Steamworks.NET): the web API ticket for the `krtuworks-id` identity
    /// (the Steam guest and every session's licence check), the achievements, the stats, the Steam Cloud
    /// files of the mirrored slots (<c>krtuworks-&lt;slot&gt;.bin</c>; Auto-Cloud must leave them alone) and the local player's own row
    /// of a Steam leaderboard (read only: the server writes the Steam boards) and Steam rich presence.
    /// The decisions live in <see cref="SteamAchievementSync"/>; this class is Steamworks.NET behind
    /// <see cref="ISteamBackend"/> plus the ticket callback.
    /// The game initializes Steam and pumps <c>SteamAPI.RunCallbacks()</c> every frame (Steamworks.NET's
    /// SteamManager does both); its Steam stats are loaded before achievements are read.
    /// <code>await KrtuworksUnity.InitializeAsync(new KrtuworksUnityOptions { PlatformServices = new SteamworksPlatformServices() });</code>
    /// </summary>
    public sealed class SteamworksPlatformServices : IPlatformServices, IPlatformStats, IPlatformCloud, IPlatformLeaderboards, IPlatformPresence
    {
        readonly SteamAchievementSync sync = new SteamAchievementSync(new SteamworksBackend());
        readonly Callback<GetTicketForWebApiResponse_t> ticketAnswers;

        public SteamworksPlatformServices()
        {
            ticketAnswers = Callback<GetTicketForWebApiResponse_t>.Create(OnTicket);
        }

        public Task<string?> GetWebApiTicketAsync() => sync.GetWebApiTicketAsync();

        public Task<IReadOnlyList<string>> UnlockedAchievementsAsync() => sync.UnlockedAchievementsAsync();

        public Task UnlockAchievementAsync(string platformId) => sync.UnlockAchievementAsync(platformId);

        public Task ClearAchievementAsync(string platformId) => sync.ClearAchievementAsync(platformId);

        public Task<double?> GetStatAsync(string platformId, string type) => sync.GetStatAsync(platformId, type);

        public Task<bool> SetStatAsync(string platformId, string type, double value) => sync.SetStatAsync(platformId, type, value);

        public Task StoreStatsAsync() => sync.StoreStatsAsync();

        public bool CloudEnabled() => sync.CloudEnabled();

        public Task<PlatformCloudFile?> ReadFileAsync(string name) => sync.ReadFileAsync(name);

        public Task<bool> WriteFileAsync(string name, byte[] data) => sync.WriteFileAsync(name, data);

        public Task<long?> AvailableBytesAsync() => sync.AvailableBytesAsync();

        public Task<PlatformLeaderboardScore?> ReadOwnScoreAsync(string platformName) => sync.ReadOwnScoreAsync(platformName);

        public bool SetRichPresence(string key, string value) => sync.SetRichPresence(key, value);

        public void ClearRichPresence() => sync.ClearRichPresence();

        void OnTicket(GetTicketForWebApiResponse_t answer)
        {
            var ok = answer.m_eResult == EResult.k_EResultOK;
            sync.OnTicketAnswer(answer.m_hAuthTicket.m_HAuthTicket, ok ? answer.m_rgubTicket : null, ok ? answer.m_cubTicket : 0);
        }

        /// <summary>How long a Steam call result is waited for.</summary>
        const int CallResultWaitMs = 10000;

        sealed class SteamworksBackend : ISteamBackend
        {
            public bool IsSteamRunning() => SteamAPI.IsSteamRunning();

            public uint RequestWebApiTicket(string identity) => SteamUser.GetAuthTicketForWebApi(identity).m_HAuthTicket;

            public void CancelAuthTicket(uint handle) => SteamUser.CancelAuthTicket(new HAuthTicket(handle));

            public uint GetNumAchievements() => SteamUserStats.GetNumAchievements();

            public string GetAchievementName(uint index) => SteamUserStats.GetAchievementName(index);

            public bool TryGetAchievement(string name, out bool achieved) => SteamUserStats.GetAchievement(name, out achieved);

            public bool SetAchievement(string name) => SteamUserStats.SetAchievement(name);

            public bool ClearAchievement(string name) => SteamUserStats.ClearAchievement(name);

            public bool TryGetStatInt(string name, out int value) => SteamUserStats.GetStat(name, out value);

            public bool TryGetStatFloat(string name, out float value) => SteamUserStats.GetStat(name, out value);

            public bool SetStatInt(string name, int value) => SteamUserStats.SetStat(name, value);

            public bool SetStatFloat(string name, float value) => SteamUserStats.SetStat(name, value);

            public void StoreStats() => SteamUserStats.StoreStats();

            public bool IsCloudEnabled() => SteamRemoteStorage.IsCloudEnabledForAccount() && SteamRemoteStorage.IsCloudEnabledForApp();

            public bool FileExists(string name) => SteamRemoteStorage.FileExists(name);

            public byte[]? FileRead(string name)
            {
                var size = SteamRemoteStorage.GetFileSize(name);
                if (size < 0) return null;
                var data = new byte[size];
                return size == 0 || SteamRemoteStorage.FileRead(name, data, size) == size ? data : null;
            }

            public long FileTimestamp(string name) => SteamRemoteStorage.GetFileTimestamp(name);

            public bool FileWrite(string name, byte[] data) => SteamRemoteStorage.FileWrite(name, data, data.Length);

            public bool TryGetQuota(out ulong total, out ulong available) => SteamRemoteStorage.GetQuota(out total, out available);

            public bool IsSubscribedApp(uint appId) => SteamApps.BIsSubscribedApp(new AppId_t(appId));

            public bool SetRichPresence(string key, string value) => SteamFriends.SetRichPresence(key, value);

            public void ClearRichPresence() => SteamFriends.ClearRichPresence();

            public async Task<ulong?> FindLeaderboardAsync(string name)
            {
                var found = await Answer<LeaderboardFindResult_t>(SteamUserStats.FindLeaderboard(name));
                return found.HasValue && found.Value.m_bLeaderboardFound != 0 ? (ulong?)found.Value.m_hSteamLeaderboard.m_SteamLeaderboard : null;
            }

            public async Task<PlatformLeaderboardScore?> DownloadOwnEntryAsync(ulong leaderboard)
            {
                var me = new[] { SteamUser.GetSteamID() };
                var rows = await Answer<LeaderboardScoresDownloaded_t>(SteamUserStats.DownloadLeaderboardEntriesForUsers(new SteamLeaderboard_t(leaderboard), me, me.Length));
                if (!rows.HasValue) return null;
                if (rows.Value.m_cEntryCount < 1) return new PlatformLeaderboardScore { Present = false };
                if (!SteamUserStats.GetDownloadedLeaderboardEntry(rows.Value.m_hSteamLeaderboardEntries, 0, out var entry, null, 0)) return null;
                return new PlatformLeaderboardScore { Present = true, Score = entry.m_nScore };
            }

            /// <summary>A Steam call's result, or null when Steam failed it or did not answer within <see cref="CallResultWaitMs"/> (like the ticket).</summary>
            static async Task<T?> Answer<T>(SteamAPICall_t call) where T : struct
            {
                var answer = new TaskCompletionSource<T?>();
                using (var result = CallResult<T>.Create())
                {
                    result.Set(call, (value, ioFailure) => answer.TrySetResult(ioFailure ? (T?)null : value));
                    var first = await Task.WhenAny(answer.Task, Task.Delay(CallResultWaitMs));
                    if (first != answer.Task) result.Cancel();
                    return first == answer.Task ? answer.Task.Result : null;
                }
            }
        }
    }
}
