#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Krtuworks
{
    /// <summary>A leaderboard of the app with the player's own standing (<c>GET /v1/me/leaderboards</c>).</summary>
    public sealed class LeaderboardView
    {
        [JsonProperty("key")] public string Key = "";
        [JsonProperty("name")] public string Name = "";
        [JsonProperty("description")] public string? Description;
        /// <summary>`ASC` (the lowest score ranks first) or `DESC`.</summary>
        [JsonProperty("sortOrder")] public string SortOrder = "";
        /// <summary>`NUMERIC`, `TIME_SECONDS` or `TIME_MILLISECONDS`: how the game shows a score.</summary>
        [JsonProperty("display")] public string Display = "";
        /// <summary>`KEEP_BEST` or `FORCE_UPDATE` when the server says; null otherwise.</summary>
        [JsonProperty("uploadMethod")] public string? UploadMethod;
        /// <summary>The platform's own row of the player may be brought in as a score.</summary>
        [JsonProperty("importFromPlatform")] public bool ImportFromPlatform;
        /// <summary>The board's name on each platform (a Steam leaderboard's name).</summary>
        [JsonProperty("platformIds")] public Dictionary<string, string> PlatformIds = new Dictionary<string, string>();
        /// <summary>The player's score; null while the player has none there or is not on the board.</summary>
        [JsonProperty("score")] public long? Score;
        [JsonProperty("details")] public byte[]? Details;
        /// <summary>The player's rank (equal scores share one); null while not on the board.</summary>
        [JsonProperty("rank")] public int? Rank;
        /// <summary>How many rows the board has.</summary>
        [JsonProperty("entries")] public int Entries;
    }

    /// <summary>A row of a leaderboard.</summary>
    public sealed class LeaderboardEntry
    {
        [JsonProperty("rank")] public int Rank;
        /// <summary>The id this app knows the player by (the same player has the same id in this app).</summary>
        [JsonProperty("subject")] public string? Subject;
        /// <summary>The player's Krtuworks username; null when there is none or it could not be read.</summary>
        [JsonProperty("name")] public string? Name;
        [JsonProperty("score")] public long Score;
        [JsonProperty("details")] public byte[]? Details;
        [JsonProperty("updatedAt")] public string UpdatedAt = "";
        /// <summary>This row is the signed-in player's.</summary>
        [JsonProperty("you")] public bool You;

        /// <summary>The name to show: the username, or "Player" when there is none.</summary>
        public string DisplayName => Name ?? "Player";
    }

    /// <summary>A page of a leaderboard (<c>GET /v1/leaderboards/{key}/entries</c>).</summary>
    public sealed class LeaderboardEntries
    {
        [JsonProperty("key")] public string Key = "";
        /// <summary>`global` or `around`.</summary>
        [JsonProperty("scope")] public string Scope = "";
        [JsonProperty("sortOrder")] public string SortOrder = "";
        [JsonProperty("display")] public string Display = "";
        /// <summary>How many rows the board has.</summary>
        [JsonProperty("total")] public int Total;
        [JsonProperty("entries")] public List<LeaderboardEntry> Entries = new List<LeaderboardEntry>();
    }
}

namespace Krtuworks.Internal
{
    /// <summary>
    /// Brings the player's own row of a platform leaderboard in, once per board and platform account, as a score op marked
    /// `fromPlatform`. It reads the platform and never writes to it: the server is the record and writes the platform's board itself.
    /// </summary>
    internal sealed class LeaderboardImport
    {
        readonly IPlatformLeaderboards platform;
        readonly IKrtuStore store;
        readonly string clientId;

        internal LeaderboardImport(IPlatformLeaderboards platform, IKrtuStore store, string clientId)
        {
            this.platform = platform;
            this.store = store;
            this.clientId = clientId;
        }

        string Key(string subject, string platformAccountId) => $"krtu:{clientId}:lbImported:{subject}:{platformAccountId}";

        /// <summary>
        /// Reads, for each board that takes the platform's row in and has a name on `platformName`, the player's own row, and
        /// hands a row that exists to `enqueue`. A board is done once the platform answered (a row or none); one it could not
        /// answer is asked again next time.
        /// </summary>
        internal async Task<int> RunAsync(string subject, string platformName, string platformAccountId, IEnumerable<LeaderboardView> boards, Func<string, long, Task> enqueue)
        {
            var key = Key(subject, platformAccountId);
            var done = await Json.Get<List<string>>(store, key) ?? new List<string>();
            var brought = 0;
            foreach (var board in boards.Where(b => b.ImportFromPlatform && b.PlatformIds.ContainsKey(platformName) && !done.Contains(b.Key)))
            {
                var row = await platform.ReadOwnScoreAsync(board.PlatformIds[platformName]);
                if (row == null) continue;
                if (row.Present)
                {
                    await enqueue(board.Key, row.Score);
                    brought++;
                }
                done.Add(board.Key);
                await Json.Put(store, key, done);
            }
            return brought;
        }
    }
}
