#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Krtuworks
{
    /// <summary>One licence of the player in this app: the game itself (<see cref="Sku"/> `base`) or one of its DLCs (the DLC's product key).</summary>
    public sealed class Entitlement
    {
        public string Sku = "";
        public string Source = "";
        /// <summary>`ACTIVE`, `REVOKED` (owned before, refunded or the sharing ended), or null: the server never saw a licence (a DLC the player never owned).</summary>
        public string? Status;
        /// <summary>The platform's id the licence is read for (a Steam AppID); null when the server has none.</summary>
        public string? PlatformProductId;
        /// <summary>The server checked it with the platform in this launch (the session's answer).</summary>
        public bool Checked;
    }
}

namespace Krtuworks.Internal
{
    /// <summary>One licence as this device keeps it (the server's last word).</summary>
    internal sealed class StoredEntitlement
    {
        [JsonProperty("sku")] public string Sku = "";
        [JsonProperty("source")] public string Source = "";
        [JsonProperty("status")] public string? Status;
        [JsonProperty("platformProductId")] public string? PlatformProductId;
    }

    /// <summary>
    /// What the player owns (the server `entitlements`; krtuworks-sdk-js/src/entitlements.ts has the same rules): the game's licence and its DLCs as the
    /// server last said, kept per player and environment in the store. <see cref="OwnsDlc"/> reads, in this order: what the server confirmed
    /// with the platform in this launch (a session answer item with `checked`), then the platform client's own answer (Steam took the money
    /// and is authoritative when the server could not check), then the last answer the server gave, kept from an earlier launch or fetched.
    /// </summary>
    internal sealed class EntitlementKeeper
    {
        readonly Resolved opts;
        readonly IPlatformOwnership? platform;
        readonly Api api;
        readonly IKrtuStore store;
        readonly Func<string?> subject;
        /// <summary>The player the lists belong to.</summary>
        string? owner;
        List<StoredEntitlement> stored = new List<StoredEntitlement>();
        /// <summary>The items the server confirmed with the platform in this launch, by sku.</summary>
        Dictionary<string, Entitlement> confirmed = new Dictionary<string, Entitlement>();

        internal EntitlementKeeper(Resolved opts, IPlatformOwnership? platform, Api api, IKrtuStore store, Func<string?> subject)
        {
            this.opts = opts;
            this.platform = platform;
            this.api = api;
            this.store = store;
            this.subject = subject;
        }

        string Key(string who) => $"krtu:{opts.ClientId}:entitlements:{who}";

        /// <summary>Reads the current player's kept list unless it is already held (a launch's confirmed answers stay).</summary>
        internal async Task EnsureLoadedAsync()
        {
            var who = subject();
            if (who == null || who == owner) return;
            var kept = await Json.Get<List<StoredEntitlement>>(store, Key(who));
            if (subject() != who) return;
            owner = who;
            stored = kept ?? new List<StoredEntitlement>();
            confirmed = new Dictionary<string, Entitlement>();
        }

        /// <summary>The player changed or signed out: nothing of the old player stays in memory.</summary>
        internal void Forget()
        {
            owner = null;
            stored = new List<StoredEntitlement>();
            confirmed = new Dictionary<string, Entitlement>();
        }

        bool Held => owner != null && owner == subject();

        /// <summary>The licences as known now: the server's last answer, with `Checked` on what it confirmed in this launch.</summary>
        internal IReadOnlyList<Entitlement> List() =>
            !Held
                ? new List<Entitlement>()
                : stored.Select(s => confirmed.TryGetValue(s.Sku, out var sure) ? sure : Of(s, false)).ToList();

        static Entitlement Of(StoredEntitlement s, bool isChecked) =>
            new Entitlement { Sku = s.Sku, Source = s.Source, Status = s.Status, PlatformProductId = s.PlatformProductId, Checked = isChecked };

        /// <summary>The server's items (the `entitlements` of a session answer, the list of `GET /v1/me/entitlements`); anything else in the list is ignored.</summary>
        internal static List<Entitlement> ItemsOf(JToken? token)
        {
            var items = new List<Entitlement>();
            if (!(token is JArray array)) return items;
            foreach (var raw in array)
            {
                if (!(raw is JObject o) || o["sku"]?.Type != JTokenType.String || o["source"]?.Type != JTokenType.String) continue;
                var status = (string?)o["status"];
                items.Add(new Entitlement
                {
                    Sku = (string)o["sku"]!,
                    Source = (string)o["source"]!,
                    Status = status == "ACTIVE" || status == "REVOKED" ? status : null,
                    PlatformProductId = o["platformProductId"]?.Type == JTokenType.String ? (string)o["platformProductId"]! : null,
                    Checked = o["checked"]?.Type == JTokenType.Boolean && (bool)o["checked"]!,
                });
            }
            return items;
        }

        async Task KeepAsync(string who, List<StoredEntitlement> list)
        {
            stored = list;
            await Json.Put(store, Key(who), list);
        }

        /// <summary>The session answer's list: its checked items are this launch's confirmed answers, the whole list the server's last word.</summary>
        internal async Task FromSessionAsync(JToken? token)
        {
            var who = subject();
            var items = ItemsOf(token);
            if (who == null || items.Count == 0) return;
            await EnsureLoadedAsync();
            if (!Held) return;
            confirmed = items.Where(i => i.Checked).GroupBy(i => i.Sku).ToDictionary(g => g.Key, g => g.Last());
            await KeepAsync(who, items.Select(i => new StoredEntitlement { Sku = i.Sku, Source = i.Source, Status = i.Status, PlatformProductId = i.PlatformProductId }).ToList());
        }

        /// <summary>`GET /v1/me/entitlements`: the stored licences replace what the kept list says (a DLC it no longer holds stays as unowned, for its id).</summary>
        internal async Task<IReadOnlyList<Entitlement>> RefreshAsync()
        {
            var who = subject();
            if (who == null) return new List<Entitlement>();
            await EnsureLoadedAsync();
            var answer = await api.PlayerAsync("/v1/me/entitlements", new PlayerCall());
            if (!Held) return new List<Entitlement>();
            var items = ItemsOf(answer.Json?["entitlements"]);
            var listed = new HashSet<string>(items.Select(i => i.Sku));
            var next = items.Select(i => new StoredEntitlement { Sku = i.Sku, Source = i.Source, Status = i.Status, PlatformProductId = i.PlatformProductId }).ToList();
            next.AddRange(stored.Where(s => !listed.Contains(s.Sku)).Select(s => new StoredEntitlement { Sku = s.Sku, Source = s.Source, Status = null, PlatformProductId = s.PlatformProductId }));
            await KeepAsync(who, next);
            return List();
        }

        /// <summary>Whether the player owns the DLC with this product key.</summary>
        internal bool OwnsDlc(string key)
        {
            if (!Held) return false;
            if (confirmed.TryGetValue(key, out var sure)) return sure.Status == "ACTIVE";
            var kept = stored.FirstOrDefault(s => s.Sku == key);
            if (platform != null && kept?.PlatformProductId != null)
            {
                var said = platform.OwnsProduct(kept.PlatformProductId);
                if (said.HasValue) return said.Value;
            }
            return kept?.Status == "ACTIVE";
        }
    }
}
