#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Krtuworks
{
    /// <summary>
    /// What <see cref="KrtuworksClient.SetPresenceAsync"/> did. When <see cref="Applied"/> is false, <see cref="Reason"/> is one of
    /// `no_platform` (the platform has no rich presence), `not_game` (an app is not a game), `module_disabled` (the owner switched
    /// presence off), `hidden` (the player hides their presence), `unknown_state` (the app has no such state), `param_missing`
    /// (a parameter the state declares has no value), `states_unknown` (the app's states were never read on this device) or
    /// `platform_rejected` (the platform refused a write; its presence is cleared).
    /// </summary>
    public sealed class PresenceResult
    {
        public bool Applied;
        public string? Reason;

        internal static PresenceResult Done() => new PresenceResult { Applied = true };

        internal static PresenceResult Refused(string reason) => new PresenceResult { Reason = reason };
    }
}

namespace Krtuworks.Internal
{
    /// <summary>A rich presence state of the app: the platform's display token and the parameters it fills in.</summary>
    internal sealed class PresenceState
    {
        [JsonProperty("key")] public string Key = "";
        [JsonProperty("token")] public string Token = "";
        [JsonProperty("params")] public List<string> Params = new List<string>();
    }

    /// <summary>What a player's device keeps of the presence: the states with their ETag (null until they were read) and the player's visibility choice.</summary>
    internal sealed class StoredPresence
    {
        [JsonProperty("etag")] public string? Etag;
        [JsonProperty("states")] public List<PresenceState>? States;
        [JsonProperty("visible")] public bool Visible = true;
    }

    /// <summary>
    /// Rich presence (Steam): the app names a state (`in_level`) and fills its parameters; the SDK checks it against the states the
    /// server declares, fills the built-ins (LEVEL, SESSION_MINUTES), honours the player's visibility choice and writes the platform.
    /// The state the game wants is remembered, so a visibility switch, a refreshed state list, a heartbeat (SESSION_MINUTES) and a level
    /// start (LEVEL) write it again. No timer of its own: the client's calls drive it.
    /// </summary>
    internal sealed class PresenceKeeper
    {
        const int MaxValueBytes = 256;
        const string DisplayKey = "steam_display";
        const string GroupKey = "steam_player_group";
        const string GroupSizeKey = "steam_player_group_size";
        const string Level = "LEVEL";
        const string SessionMinutes = "SESSION_MINUTES";

        sealed class Wanted
        {
            internal string Key = "";
            /// <summary>The values the game gave, trimmed; a parameter it gave nothing for is not here (a built-in fills it).</summary>
            internal Dictionary<string, string> Values = new Dictionary<string, string>();
        }

        readonly Resolved opts;
        readonly IPlatformPresence? platform;
        readonly Api api;
        readonly IKrtuStore store;
        readonly Func<bool> moduleOn;
        readonly Func<string?> level;
        readonly Func<long> activeSeconds;
        readonly Action<string> log;
        /// <summary>The player the cache and the wanted state belong to.</summary>
        string? owner;
        StoredPresence? cache;
        Wanted? wanted;
        string? groupId;
        int? groupSize;
        /// <summary>The platform holds presence this keeper wrote.</summary>
        bool written;

        internal PresenceKeeper(Resolved opts, IPlatformPresence? platform, Api api, IKrtuStore store, Func<bool> moduleOn, Func<string?> level, Func<long> activeSeconds, Action<string> log)
        {
            this.opts = opts;
            this.platform = platform;
            this.api = api;
            this.store = store;
            this.moduleOn = moduleOn;
            this.level = level;
            this.activeSeconds = activeSeconds;
            this.log = log;
        }

        string Key(string subject) => $"krtu:{opts.ClientId}:presence:{subject}";

        /// <summary>Whether the player shows their presence; null until this device knows (nothing was read or chosen).</summary>
        internal bool? Visible => cache?.Visible;

        /// <summary>The player this device had at launch: what it kept for them is read.</summary>
        internal async Task LoadAsync(string? subject)
        {
            owner = subject;
            cache = subject == null ? null : await Json.Get<StoredPresence>(store, Key(subject));
        }

        /// <summary>The player changed: the platform's presence is cleared and the wanted state forgotten (a guest that became a Krtuworks ID is the same player).</summary>
        internal void PlayerChanged(string? subject)
        {
            if (subject == owner) return;
            Silence();
            wanted = null;
            owner = subject;
            cache = null;
        }

        // ── The server's states and the player's choice ───────────────────────

        /// <summary>Reads the states (revalidated with their ETag) and the visibility; a changed answer is written to the platform. A module the owner switched off clears.</summary>
        internal async Task RefreshAsync()
        {
            var who = owner;
            if (who == null || platform == null || !opts.Game) return;
            if (!moduleOn())
            {
                Stop();
                return;
            }
            cache ??= await Json.Get<StoredPresence>(store, Key(who));
            if (owner != who) return;
            var headers = new Dictionary<string, string>();
            if (cache?.States != null && cache.Etag != null) headers["if-none-match"] = cache.Etag;
            Answer answer;
            try
            {
                answer = await api.PlayerAsync("/v1/me/presence-states", new PlayerCall { Headers = headers, Accept = new[] { 304 } });
            }
            catch (KrtuworksException err) when (err.Code == "module_disabled")
            {
                Stop();
                return;
            }
            if (owner != who || answer.Status == 304) return;
            cache = new StoredPresence
            {
                Etag = answer.Raw.Header("etag"),
                States = (answer.Json?["states"] as JArray ?? new JArray()).Select(s => s.ToObject<PresenceState>() ?? new PresenceState()).ToList(),
                Visible = answer.Json?["visible"]?.Type != JTokenType.Boolean || (bool)answer.Json["visible"]!,
            };
            await Json.Put(store, Key(who), cache);
            Reconcile();
        }

        internal async Task SetVisibleAsync(bool visible)
        {
            var who = owner ?? throw new KrtuworksException("signed_out", "No player is signed in.");
            if (!moduleOn()) throw new KrtuworksException("module_disabled", "Rich presence is off in this app.", 403);
            var answer = await api.PlayerAsync("/v1/me/presence-visibility", new PlayerCall { Method = "PUT", Json = new JObject { ["visible"] = visible } });
            if (owner != who) return;
            cache ??= new StoredPresence();
            cache.Visible = answer.Json?["visible"]?.Type == JTokenType.Boolean ? (bool)answer.Json["visible"]! : visible;
            await Json.Put(store, Key(who), cache);
            Reconcile();
        }

        // ── The game's calls ──────────────────────────────────────────────────

        internal PresenceResult Set(string key, IDictionary<string, object>? parameters)
        {
            if (!opts.Game) return PresenceResult.Refused("not_game");
            if (platform == null) return PresenceResult.Refused("no_platform");
            if (!moduleOn())
            {
                Stop();
                return PresenceResult.Refused("module_disabled");
            }
            var next = new Wanted { Key = key, Values = Texts(parameters) };
            if (cache?.Visible == false)
            {
                wanted = next;
                return PresenceResult.Refused("hidden");
            }
            if (cache?.States == null) return PresenceResult.Refused("states_unknown");
            var result = Write(next);
            if (result.Applied) wanted = next;
            else if (result.Reason == "platform_rejected") wanted = null;
            return result;
        }

        internal void Clear()
        {
            wanted = null;
            if (platform == null || !opts.Game) return;
            platform.ClearRichPresence();
            written = false;
        }

        /// <summary>The party the player is in (null clears it); written with every state and at once when there is nothing to hide it.</summary>
        internal void SetGroup(string? id, int? size)
        {
            if (size < 1) throw new ArgumentException("size is 1 or more");
            groupId = string.IsNullOrEmpty(id) ? null : id;
            groupSize = groupId == null ? null : size;
            if (platform == null || !opts.Game || !moduleOn() || cache?.Visible == false) return;
            var ok = platform.SetRichPresence(GroupKey, groupId ?? "") && platform.SetRichPresence(GroupSizeKey, groupSize?.ToString(CultureInfo.InvariantCulture) ?? "");
            if (ok) written |= groupId != null;
            else log("presence: the platform refused the group");
        }

        // ── Writing ───────────────────────────────────────────────────────────

        /// <summary>A heartbeat or a level start moved a built-in: the wanted state is written again when it uses one the game did not give (LEVEL only for a level start).</summary>
        internal void ReapplyAuto(bool levelOnly)
        {
            var state = wanted == null ? null : cache?.States?.FirstOrDefault(s => s.Key == wanted.Key);
            if (wanted == null || state == null || cache?.Visible == false || !moduleOn()) return;
            var auto = state.Params.Where(name => (name == Level || (!levelOnly && name == SessionMinutes)) && !wanted.Values.ContainsKey(name));
            if (auto.Any()) Reconcile();
        }

        /// <summary>The platform shows what the player wants shown: the wanted state, unless the player hides it or the module is off.</summary>
        void Reconcile()
        {
            if (platform == null || !opts.Game) return;
            if (cache?.Visible == false || !moduleOn())
            {
                Silence();
                return;
            }
            if (wanted == null) return;
            var result = Write(wanted);
            if (result.Applied) return;
            log($"presence: {wanted.Key} was not written ({result.Reason})");
            if (result.Reason == "platform_rejected") wanted = null;
        }

        void Silence()
        {
            if (!written || platform == null) return;
            platform.ClearRichPresence();
            written = false;
        }

        void Stop()
        {
            Silence();
            wanted = null;
        }

        /// <summary>Checks the state against the server's, fills the parameters and writes the platform: clear, the parameters, the group, then the display token last.</summary>
        PresenceResult Write(Wanted want)
        {
            var state = cache?.States?.FirstOrDefault(s => s.Key == want.Key);
            if (state == null) return PresenceResult.Refused("unknown_state");
            foreach (var extra in want.Values.Keys.Where(name => !state.Params.Contains(name)))
                log($"presence: {extra} is not a parameter of {want.Key}; dropped");
            var filled = new List<KeyValuePair<string, string>>();
            foreach (var name in state.Params)
            {
                var value = want.Values.TryGetValue(name, out var given) ? given : Builtin(name);
                if (value.Length == 0) return PresenceResult.Refused("param_missing");
                filled.Add(new KeyValuePair<string, string>(name, value));
            }
            platform!.ClearRichPresence();
            written = true;
            var ok = true;
            foreach (var pair in filled) ok = ok && platform.SetRichPresence(pair.Key, pair.Value);
            ok = ok && (groupId == null || (platform.SetRichPresence(GroupKey, groupId) && (groupSize == null || platform.SetRichPresence(GroupSizeKey, groupSize.Value.ToString(CultureInfo.InvariantCulture)))));
            ok = ok && platform.SetRichPresence(DisplayKey, state.Token);
            if (ok) return PresenceResult.Done();
            platform.ClearRichPresence();
            written = false;
            return PresenceResult.Refused("platform_rejected");
        }

        /// <summary>The value of a built-in parameter; empty for any other name and while nothing fills it (no level is open).</summary>
        string Builtin(string name) => name switch
        {
            Level => Limit(level() ?? ""),
            SessionMinutes => (activeSeconds() / 60).ToString(CultureInfo.InvariantCulture),
            _ => "",
        };

        static Dictionary<string, string> Texts(IDictionary<string, object>? parameters)
        {
            var texts = new Dictionary<string, string>();
            foreach (var pair in parameters ?? new Dictionary<string, object>())
            {
                var text = Limit(Text(pair.Value));
                if (text.Length > 0) texts[pair.Key] = text;
            }
            return texts;
        }

        static string Text(object? value) => value switch
        {
            null => "",
            string s => s,
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };

        /// <summary>The text trimmed of spaces and cut to 256 UTF-8 bytes at a character boundary (a surrogate pair stays whole).</summary>
        internal static string Limit(string text)
        {
            var value = text.Trim();
            if (Encoding.UTF8.GetByteCount(value) <= MaxValueBytes) return value;
            var end = 0;
            var bytes = 0;
            while (end < value.Length)
            {
                var width = char.IsHighSurrogate(value[end]) && end + 1 < value.Length && char.IsLowSurrogate(value[end + 1]) ? 2 : 1;
                var size = Encoding.UTF8.GetByteCount(value.Substring(end, width));
                if (bytes + size > MaxValueBytes) break;
                bytes += size;
                end += width;
            }
            return value.Substring(0, end);
        }
    }
}
