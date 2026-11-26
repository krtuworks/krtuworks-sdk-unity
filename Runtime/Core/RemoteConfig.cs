#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Krtuworks
{
    /// <summary>The owner's switch for this build: `TELEMETRY` stops the statistics, `ALL` stops every call (the game shows the message).</summary>
    public sealed class BuildSwitch
    {
        public string Build = "";
        public string Mode = "";
        public string Message = "";
    }
}

namespace Krtuworks.Internal
{
    internal sealed class StoredConfig
    {
        [JsonProperty("etag")] public string? Etag;
        [JsonProperty("value")] public JObject Value = new JObject();
    }

    /// <summary>
    /// The app's remote config, read without a sign-in and revalidated with its ETag. The
    /// last good copy is kept in the store, so an offline launch starts with what the owner set last.
    /// Same rules as krtuworks-sdk-js/src/remoteConfig.ts.
    /// </summary>
    internal sealed class RemoteConfig
    {
        /// <summary>The SDK modules the owner switches per app and environment.</summary>
        internal static readonly string[] Modules =
        {
            "sessions", "progression", "combat", "economy", "items", "ui", "performance", "settings", "errors",
            "bugReports", "achievements", "stats", "leaderboards", "presence", "cloudSave", "remoteConfig", "abTests", "customEvents",
            "positions",
        };

        readonly Resolved opts;
        readonly Api api;
        readonly IKrtuStore store;
        StoredConfig? stored;

        internal RemoteConfig(Resolved opts, Api api, IKrtuStore store)
        {
            this.opts = opts;
            this.api = api;
            this.store = store;
        }

        string Key => $"krtu:{opts.ClientId}:config:{opts.Platform}";

        internal async Task LoadAsync() => stored = await Json.Get<StoredConfig>(store, Key);

        internal JObject? Value => stored?.Value;
        JObject? Config => stored?.Value["config"] as JObject;

        /// <summary>Asks the server; false when nothing changed. Throws when it cannot (the kept copy stays).</summary>
        internal async Task<bool> RefreshAsync()
        {
            var headers = new Dictionary<string, string>();
            if (stored?.Etag != null) headers["if-none-match"] = stored.Etag;
            var answer = await api.OpenAsync($"/v1/apps/{Uri.EscapeDataString(opts.Slug)}/config", new PlayerCall
            {
                // The build rides in the query, as the JS SDK sends it; the read counts for the build's release report.
                Query = new Dictionary<string, string?> { ["environment"] = opts.Environment, ["platform"] = opts.Platform, ["build"] = opts.Build },
                Headers = headers,
                Accept = new[] { 304 },
            });
            if (answer.Status == 304) return false;
            // A page that is not the config (a captive portal's, a proxy's) must not replace the kept copy.
            if (answer.Json == null) throw new KrtuworksException("invalid_response", "The config answer is not a JSON object.", answer.Status);
            stored = new StoredConfig { Etag = answer.Raw.Header("etag"), Value = answer.Json };
            await Json.Put(store, Key, stored);
            return true;
        }

        /// <summary>The modules only a game has: levels (progression) and deaths (combat).</summary>
        static readonly string[] GameModules = { "progression", "combat", "positions" };

        /// <summary>A module the owner switched off makes nothing; a switch never set is on. An app that is not a game has no game-only modules.</summary>
        internal bool ModuleOn(string module)
        {
            if (!opts.Game && GameModules.Contains(module)) return false;
            return Config?["modules"]?[module]?.Type != JTokenType.Boolean || (bool)Config!["modules"]![module]!;
        }

        internal Dictionary<string, bool> ModuleStates() => Modules.ToDictionary(m => m, ModuleOn);

        /// <summary>The owner's switch that rules this build, ALL over TELEMETRY.</summary>
        internal BuildSwitch? SwitchForBuild()
        {
            var mine = (stored?.Value["switches"] as JArray ?? new JArray())
                .Select(s => new BuildSwitch { Build = (string?)s["build"] ?? "", Mode = (string?)s["mode"] ?? "", Message = (string?)s["message"] ?? "" })
                .Where(s => s.Build == "*" || s.Build == opts.Build)
                .ToList();
            return mine.FirstOrDefault(s => s.Mode == "ALL") ?? mine.FirstOrDefault(s => s.Mode == "TELEMETRY");
        }

        internal JToken? Get(string key) => Config != null && Config.TryGetValue(key, out var value) ? value : null;

        /// <summary>True when `build` is older than `minVersion` (dotted numbers; a build's `+meta` is ignored).</summary>
        internal static bool OlderThan(string build, string minVersion)
        {
            static int[] Parts(string v) => v.Split('+')[0].Split('.', '-').Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0).ToArray();
            var a = Parts(build);
            var b = Parts(minVersion);
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var d = (i < a.Length ? a[i] : 0) - (i < b.Length ? b[i] : 0);
                if (d != 0) return d < 0;
            }
            return false;
        }
    }
}
