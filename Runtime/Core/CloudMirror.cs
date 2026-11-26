#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Krtuworks
{
    /// <summary>
    /// The platform cloud's copy of a SAVE or PROFILE slot differs from the server's and nothing tells which is newer
    /// (the first time this device compares them, or both moved): it waits on the server for the player's choice. The
    /// game shows both (times, sizes, its own summary of <see cref="PlatformData"/>) and calls
    /// <see cref="KrtuworksClient.ResolveFileAsync"/> with the one to keep and <c>Server.Id</c>; the kept bytes then go
    /// to both sides.
    /// </summary>
    public sealed class PlatformSaveConflict
    {
        public string Slot = "";
        /// <summary>The slot's current version.</summary>
        public FileVersion Server = new FileVersion();
        /// <summary>The platform cloud's copy, waiting in the slot (<c>PlatformCloud</c> set).</summary>
        public FileVersion Platform = new FileVersion();
        /// <summary>The platform copy's bytes when they are still the ones on the platform; null otherwise.</summary>
        public byte[]? PlatformData;
    }
}

namespace Krtuworks.Internal
{
    /// <summary>What one slot's comparison calls for.</summary>
    internal enum MirrorStep
    {
        /// <summary>Both sides hold the same (or neither holds anything).</summary>
        Agree,
        /// <summary>The server's bytes go to the platform.</summary>
        ToPlatform,
        /// <summary>The platform's bytes become the server's new version.</summary>
        ToServer,
        /// <summary>The platform's copy is offered for the player's choice.</summary>
        Offer,
    }

    /// <summary>
    /// The platform's cloud mirrors the app's mirrored slots (Steam sync, Steam Cloud; owner decisions D-C1..D-C6
    /// 2026-11-07): the server is the record, the platform holds each slot's current version as
    /// <c>krtuworks-&lt;slot&gt;.bin</c> (the server <c>steamCloudFile</c>), Auto-Cloud stays off for those files. SAVE, PROFILE
    /// and SETTINGS are mirrored, DEVICE_SETTINGS never. Per slot three hashes decide (<see cref="Decide"/>): H the
    /// server's, S the platform's, L the last both sides agreed on, on this device, for this player and platform
    /// account. A slot the platform has no room for (or refused) is left alone for the session; the game never sees an
    /// error and the server's copy is never touched by a platform failure.
    /// </summary>
    internal sealed class CloudMirror
    {
        internal static string FileName(string slot) => $"krtuworks-{slot}.bin";

        static readonly HashSet<string> MirroredKinds = new HashSet<string> { "SAVE", "PROFILE", "SETTINGS" };

        readonly IPlatformCloud cloud;
        readonly Files files;
        readonly IKrtuStore store;
        readonly string clientId;
        readonly Action<PlatformSaveConflict> conflict;
        readonly Action<string, string> problem;
        readonly HashSet<string> leftAlone = new HashSet<string>();
        readonly HashSet<string> told = new HashSet<string>();
        readonly object gate = new object();
        /// <summary>The syncs run one after another (a choice's sync must not be swallowed by one already running).</summary>
        Task queue = Task.CompletedTask;

        /// <param name="conflict">The game is told a copy waits for the player's choice (once per waiting copy and launch).</param>
        /// <param name="problem">A platform failure the game does not see (code, slot), for the error events.</param>
        internal CloudMirror(IPlatformCloud cloud, Files files, IKrtuStore store, string clientId, Action<PlatformSaveConflict> conflict, Action<string, string> problem)
        {
            this.cloud = cloud;
            this.files = files;
            this.store = store;
            this.clientId = clientId;
            this.conflict = conflict;
            this.problem = problem;
        }

        /// <summary>
        /// The step for one slot from H (the server's current hash), S (the platform's), L (agreed last on this device)
        /// and, for SETTINGS, whether the platform's copy was written after the server's (the latest wins there, no
        /// question). Null means nothing is there.
        /// </summary>
        internal static MirrorStep Decide(string kind, string? h, string? s, string? l, bool platformNewer)
        {
            if (s == h) return MirrorStep.Agree;
            if (h == null) return MirrorStep.ToServer;
            if (s == null) return MirrorStep.ToPlatform;
            if (l != null && s == l) return MirrorStep.ToPlatform;
            if (l != null && h == l) return MirrorStep.ToServer;
            if (kind == "SETTINGS") return platformNewer ? MirrorStep.ToServer : MirrorStep.ToPlatform;
            return MirrorStep.Offer;
        }

        string AgreedKey(string owner, string account) => $"krtu:{clientId}:cloudAgreed:{owner}:{account}";

        Task Queue(Func<Task> work)
        {
            lock (gate)
            {
                var next = Next(queue, work);
                queue = next;
                return next;
            }
        }

        static async Task Next(Task previous, Func<Task> work)
        {
            try { await previous; }
            catch (Exception) { /* that sync's caller heard of it */ }
            await work();
        }

        /// <summary>Compares every mirrored slot (or only `slot`) and does what each calls for. One sync at a time.</summary>
        internal Task SyncAsync(string owner, string account, string sessionId, string? slot = null) =>
            Queue(() => SyncOnce(owner, account, sessionId, slot));

        async Task SyncOnce(string owner, string account, string sessionId, string? only)
        {
            if (!cloud.CloudEnabled()) return;
            // Every call below is for `owner`: when another player plays meanwhile, nothing of this sync reaches the server for them.
            var list = await files.ListFor(owner);
            var key = AgreedKey(owner, account);
            var agreed = await Json.Get<Dictionary<string, string>>(store, key) ?? new Dictionary<string, string>();
            foreach (var slot in list.Slots)
            {
                if (only != null && slot.Key != only) continue;
                if (!slot.PlatformMirror || slot.Retired || !MirroredKinds.Contains(slot.Kind) || leftAlone.Contains(slot.Key)) continue;
                try
                {
                    await SyncSlot(owner, slot, agreed, sessionId);
                }
                catch (KrtuworksException err) when (err.Code == "context_changed")
                {
                    // The player changed: this sync was theirs; the next one is the new player's.
                    return;
                }
                catch (KrtuworksException err)
                {
                    // The slot moved meanwhile, a write still settling or replaced by the game's newer save, the
                    // server away: the next sync compares again. Refused for good (too big, over the player's
                    // quota, the link): left alone for the session.
                    if (!err.Retryable && !err.KeepsQueued && err.Code != "file_conflict" && err.Code != "upload_pending" && err.Code != "save_replaced")
                    {
                        leftAlone.Add(slot.Key);
                        problem($"platform_{err.Code}", slot.Key);
                    }
                }
                await Json.Put(store, key, agreed);
            }
        }

        async Task SyncSlot(string owner, SlotView slot, Dictionary<string, string> agreed, string sessionId)
        {
            var name = FileName(slot.Key);
            var onPlatform = await cloud.ReadFileAsync(name);
            var s = onPlatform == null ? null : Ids.Sha256Hex(onPlatform.Data);
            if (slot.Conflict != null)
            {
                // A choice waits: nothing moves until the player made it. The game hears of it once per launch.
                if (slot.Conflict.PlatformCloud != null && slot.File != null && told.Add(slot.Conflict.Id))
                    conflict(new PlatformSaveConflict { Slot = slot.Key, Server = slot.File, Platform = slot.Conflict, PlatformData = s == slot.Conflict.Sha256 ? onPlatform!.Data : null });
                return;
            }
            var h = slot.File?.Sha256;
            agreed.TryGetValue(slot.Key, out var l);
            var step = Decide(slot.Kind, h, s, l, PlatformNewer(onPlatform, slot.File));
            switch (step)
            {
                case MirrorStep.Agree:
                    if (h == null) agreed.Remove(slot.Key);
                    else agreed[slot.Key] = h;
                    return;
                case MirrorStep.ToPlatform:
                    await ToPlatform(owner, slot.Key, h!, onPlatform, agreed);
                    return;
                case MirrorStep.ToServer:
                    // A SAVE or PROFILE over the version compared (or into the empty slot); SETTINGS: the latest wins.
                    var conditional = slot.Kind != "SETTINGS";
                    var saved = await files.SaveAsync(slot.Key, onPlatform!.Data, conditional, conditional ? slot.File?.Id : null, null, owner);
                    agreed[slot.Key] = saved.Sha256;
                    return;
                case MirrorStep.Offer:
                    await Offer(owner, slot, onPlatform!, s!, agreed, sessionId);
                    return;
            }
        }

        static bool PlatformNewer(PlatformCloudFile? onPlatform, FileVersion? server)
        {
            if (onPlatform == null || onPlatform.WrittenAt <= 0 || server == null) return false;
            if (!DateTimeOffset.TryParse(server.SavedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var savedAt)) return false;
            return onPlatform.WrittenAt > savedAt.ToUnixTimeSeconds();
        }

        async Task ToPlatform(string owner, string slot, string h, PlatformCloudFile? onPlatform, Dictionary<string, string> agreed)
        {
            var read = await files.ReadAsync(slot, null, null, owner);
            // The slot moved since the list was read: the next sync compares the new version.
            if (read == null || read.Sha256 != h) return;
            var free = await cloud.AvailableBytesAsync();
            if (free.HasValue && read.Bytes.LongLength > free.Value + (onPlatform?.Data.LongLength ?? 0))
            {
                leftAlone.Add(slot);
                problem("platform_quota", slot);
                return;
            }
            if (!await cloud.WriteFileAsync(FileName(slot), read.Bytes))
            {
                leftAlone.Add(slot);
                problem("platform_rejected", slot);
                return;
            }
            agreed[slot] = h;
        }

        async Task Offer(string owner, SlotView slot, PlatformCloudFile onPlatform, string s, Dictionary<string, string> agreed, string sessionId)
        {
            FileVersion offered;
            try
            {
                offered = await files.OfferAsync(slot.Key, onPlatform.Data, slot.File!.Id, sessionId, owner);
            }
            catch (KrtuworksException err) when (err.Code == "same_as_current")
            {
                agreed[slot.Key] = s;
                return;
            }
            catch (KrtuworksException err) when (err.Code == "conflict_pending" && err.Details["conflict"] != null)
            {
                offered = err.Details["conflict"]!.ToObject<FileVersion>()!;
            }
            if (told.Add(offered.Id))
                conflict(new PlatformSaveConflict { Slot = slot.Key, Server = slot.File!, Platform = offered, PlatformData = offered.Sha256 == s ? onPlatform.Data : null });
        }

        /// <summary>
        /// After the player chose (resolve): the slot's current version is what both sides hold from now on. The
        /// platform's copy counts as agreed, so the comparison writes the kept version there (or finds it there already)
        /// instead of offering the same copy again.
        /// </summary>
        internal Task AfterChoiceAsync(string owner, string account, string sessionId, string slot) =>
            Queue(async () =>
            {
                if (!cloud.CloudEnabled()) return;
                var onPlatform = await cloud.ReadFileAsync(FileName(slot));
                var key = AgreedKey(owner, account);
                var agreed = await Json.Get<Dictionary<string, string>>(store, key) ?? new Dictionary<string, string>();
                if (onPlatform != null) agreed[slot] = Ids.Sha256Hex(onPlatform.Data);
                await Json.Put(store, key, agreed);
                leftAlone.Remove(slot);
                await SyncOnce(owner, account, sessionId, slot);
            });
    }
}
