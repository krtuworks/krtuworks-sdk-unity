#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Krtuworks.Internal
{
    /// <summary>A stat's id and type on the platform (from <c>GET /v1/me/progress</c>).</summary>
    internal sealed class StatPlatformId
    {
        [JsonProperty("id")] public string Id = "";
        /// <summary>`INT` (an integer stat) or `FLOAT`.</summary>
        [JsonProperty("type")] public string Type = "";
    }

    /// <summary>
    /// The stats mirror on the platform (Steam sync, stats; owner decision D-S5 2026-11-06). The server is the
    /// record: the platform is set to the server's value as soon as the server has it (never an increase, so
    /// nothing counts twice), and the platform's servers get the values at most every 2 minutes, on pause and on
    /// exit. A stat the platform refuses (not published there, another type) is left alone for the session.
    /// </summary>
    internal sealed class StatMirror
    {
        internal const long StoreEveryMs = 120_000;

        readonly IPlatformStats platform;
        readonly Func<long> nowMs;
        readonly HashSet<string> refused = new HashSet<string>();
        bool dirty;
        long lastStoreAt = long.MinValue;

        internal StatMirror(IPlatformStats platform, Func<long> nowMs)
        {
            this.platform = platform;
            this.nowMs = nowMs;
        }

        /// <summary>The value the platform holds for the server's: an INT clamped to 32 bits, a FLOAT in single precision.</summary>
        internal static double PlatformValue(string type, double value) =>
            type == "INT" ? Math.Max(int.MinValue, Math.Min(int.MaxValue, Math.Truncate(value))) : (float)value;

        internal Task<double?> ReadAsync(StatPlatformId stat) => refused.Contains(stat.Id) ? Task.FromResult<double?>(null) : platform.GetStatAsync(stat.Id, stat.Type);

        /// <summary>Sets the platform's stat to the server's value.</summary>
        internal async Task SetAsync(StatPlatformId stat, double serverValue)
        {
            if (refused.Contains(stat.Id)) return;
            if (await platform.SetStatAsync(stat.Id, stat.Type, PlatformValue(stat.Type, serverValue))) dirty = true;
            else refused.Add(stat.Id);
        }

        /// <summary>Sends what was set to the platform's servers when 2 minutes passed since the last time, or now (`force`: pause, exit, the launch's sync).</summary>
        internal async Task StoreAsync(bool force)
        {
            if (!dirty) return;
            var now = nowMs();
            if (!force && lastStoreAt != long.MinValue && now - lastStoreAt < StoreEveryMs) return;
            dirty = false;
            lastStoreAt = now;
            await platform.StoreStatsAsync();
        }
    }
}
