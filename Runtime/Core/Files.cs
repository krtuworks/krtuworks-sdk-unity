#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Krtuworks
{
    public sealed class FileVersion
    {
        [JsonProperty("id")] public string Id = "";
        [JsonProperty("slot")] public string Slot = "";
        [JsonProperty("device")] public string? Device;
        [JsonProperty("generation")] public long Generation;
        [JsonProperty("sizeBytes")] public long SizeBytes;
        [JsonProperty("sha256")] public string Sha256 = "";
        [JsonProperty("platform")] public string? Platform;
        [JsonProperty("build")] public string? Build;
        [JsonProperty("savedAt")] public string SavedAt = "";
        [JsonProperty("fromMerge")] public bool FromMerge;
        /// <summary>The platform whose cloud this copy came from (STEAM): it waits as a slot's `Conflict` for the player's choice.</summary>
        [JsonProperty("platformCloud")] public string? PlatformCloud;
    }

    public sealed class SlotView
    {
        [JsonProperty("key")] public string Key = "";
        /// <summary>SAVE, PROFILE, SETTINGS or DEVICE_SETTINGS.</summary>
        [JsonProperty("kind")] public string Kind = "";
        [JsonProperty("name")] public string Name = "";
        [JsonProperty("retired")] public bool Retired;
        /// <summary>The SDK copies the slot to the platform's cloud (Steam Cloud) where the game runs on one; never DEVICE_SETTINGS.</summary>
        [JsonProperty("platformMirror")] public bool PlatformMirror;
        [JsonProperty("file")] public FileVersion? File;
        /// <summary>A merged account's version or the platform cloud's copy (`PlatformCloud`), waiting for the player's choice.</summary>
        [JsonProperty("conflict")] public FileVersion? Conflict;
        [JsonProperty("devices")] public List<FileVersion> Devices = new List<FileVersion>();
    }

    public sealed class SlotList
    {
        [JsonProperty("slots")] public List<SlotView> Slots = new List<SlotView>();
        [JsonProperty("limits")] public Dictionary<string, long> Limits = new Dictionary<string, long>();
    }

    public sealed class FileRead
    {
        public byte[] Bytes = Array.Empty<byte>();
        /// <summary>A save of this slot started on this device while the read ran: the read is older than what the player has here.</summary>
        public bool NewerLocalSave;
        /// <summary>The version read: its id is the base of the next save (null when it is a similar device's file).</summary>
        public string? VersionId;
        public long Generation;
        public string Sha256 = "";
        public string SavedAt = "";
        /// <summary>A similar device's DEVICE_SETTINGS: a starting point, not this device's own.</summary>
        public bool SimilarDevice;
    }

    /// <summary>DEVICE_SETTINGS: this device's id and description, so a new device starts from the closest one's.</summary>
    public sealed class SaveDevice
    {
        public string Id = "";
        public Dictionary<string, string> Info = new Dictionary<string, string>();
    }

    public sealed class ResumedUpload
    {
        public string UploadId = "";
        public string Slot = "";
        /// <summary>saved, refused, waiting or replaced (a newer save of the slot took its place).</summary>
        public string Outcome = "";
        public string? Code;
    }

    public sealed class WaitingSave
    {
        public string UploadId = "";
        public string Slot = "";
        public long SavedAt;
    }
}

namespace Krtuworks.Internal
{
    /// <summary>An upload written to the store before it is sent, so a relaunch finishes it with the same id.</summary>
    internal sealed class PendingUpload
    {
        [JsonProperty("uploadId")] public string UploadId = "";
        [JsonProperty("owner")] public string Owner = "";
        [JsonProperty("slot")] public string Slot = "";
        [JsonProperty("device")] public string? Device;
        [JsonProperty("base")] public string? Base;
        /// <summary>Whether the save named a base (If-Match / If-None-Match: *); a resend sends exactly the same.</summary>
        [JsonProperty("conditional")] public bool Conditional;
        [JsonProperty("sha256")] public string Sha256 = "";
        [JsonProperty("bytes")] public byte[] Bytes = Array.Empty<byte>();
        [JsonProperty("deviceInfo")] public Dictionary<string, string>? DeviceInfo;
        [JsonProperty("savedAt")] public long SavedAt;
        /// <summary>Order of saves made in the same millisecond on this device.</summary>
        [JsonProperty("seq")] public int Seq;
        /// <summary>2: when it was last sent is in its own key (none = never sent). An older record (0) was sent when it was made.</summary>
        [JsonProperty("v")] public int V;
    }

    /// <summary>
    /// The player's files in the app's slots, the rules of
    /// krtuworks-sdk-js/src/files.ts: a save is written to the store first, sent with its upload id as the
    /// Idempotency-Key and asked about until it is final; a conflict comes back with the slot's current
    /// version for the game's choice screen.
    /// </summary>
    internal sealed class Files
    {
        static readonly TimeSpan PollLimit = TimeSpan.FromSeconds(60);
        /// <summary>
        /// How long after its last send an upload the server does not know can still arrive (an edge holds a
        /// whole request before it reaches the server). Past it, a save no one heard of is gone.
        /// </summary>
        const long ArrivalWindowMs = 15 * 60_000;
        /// <summary>SETTINGS and DEVICE_SETTINGS are written at most once per slot in this time; the newest content is the one written.</summary>
        const long SettingsGapMs = 30_000;

        readonly Resolved opts;
        readonly Api api;
        readonly IKrtuStore store;
        readonly Func<string?> subject;
        readonly Func<int> generation;
        readonly Action onSaved;
        /// <summary>Per slot (and device), bumped by every save started here: a read compares it before and after.</summary>
        readonly Dictionary<string, int> revisions = new Dictionary<string, int>();
        /// <summary>Orders the saves started in one millisecond.</summary>
        int seq;
        /// <summary>Ends the waits that retry a save after a rate limit (the client's shutdown).</summary>
        internal CancellationToken Stop;
        readonly object settingsGate = new object();
        readonly Stopwatch monotonic = Stopwatch.StartNew();
        /// <summary>Per lane: when its next settings write may go.</summary>
        readonly Dictionary<string, long> settingsNextAt = new Dictionary<string, long>();
        readonly List<Task> settingsWrites = new List<Task>();
        /// <summary>Per lane: the saves still being stored, so they join the lane in the order they were made.</summary>
        readonly Dictionary<string, Task> entering = new Dictionary<string, Task>();
        CancellationTokenSource settingsNow = new CancellationTokenSource();
        /// <summary>Lanes with a retry already waiting for a rate limit to pass.</summary>
        readonly HashSet<string> retrying = new HashSet<string>();

        internal Files(Resolved opts, Api api, IKrtuStore store, Func<string?> subject, Func<int> generation, Action onSaved)
        {
            this.opts = opts;
            this.api = api;
            this.store = store;
            this.subject = subject;
            this.generation = generation;
            this.onSaved = onSaved;
        }

        static string RevisionKey(string slot, string? device) => $"{slot}\u0000{device ?? ""}";
        int Revision(string key) => revisions.TryGetValue(key, out var r) ? r : 0;
        string FilesPath => $"/v1/apps/{Uri.EscapeDataString(opts.Slug)}/files";
        string SlotPath(string slot) => $"{FilesPath}/{Uri.EscapeDataString(slot)}";
        string UploadKey(string uploadId) => $"krtu:{opts.ClientId}:upload:{uploadId}";
        string UploadPrefix => $"krtu:{opts.ClientId}:upload:";
        string SentPrefix => $"krtu:{opts.ClientId}:uploadSent:";
        string SentKey(string uploadId) => $"{SentPrefix}{uploadId}";
        static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>One lane per player, slot and device: its saves go one at a time, oldest first.</summary>
        string LaneLock(PendingUpload upload) =>
            $"krtu:{opts.ClientId}:uploadLane:{new JArray(upload.Owner, upload.Slot, upload.Device == null ? JValue.CreateNull() : new JValue(upload.Device)).ToString(Formatting.None)}";

        async Task Forget(string uploadId)
        {
            await store.DeleteAsync(UploadKey(uploadId));
            await store.DeleteAsync(SentKey(uploadId));
        }

        /// <summary>When the upload was last sent; null when it never was.</summary>
        async Task<long?> SentAt(PendingUpload upload)
        {
            var text = await store.GetAsync(SentKey(upload.UploadId));
            if (text != null && long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var at)) return at;
            return upload.V == 2 ? (long?)null : upload.SavedAt;
        }

        static int Compare(PendingUpload a, PendingUpload b)
        {
            if (a.SavedAt != b.SavedAt) return a.SavedAt < b.SavedAt ? -1 : 1;
            if (a.Seq != b.Seq) return a.Seq < b.Seq ? -1 : 1;
            return string.CompareOrdinal(a.UploadId, b.UploadId);
        }

        /// <summary>The store's uploads of `upload`'s lane, oldest first.</summary>
        async Task<List<PendingUpload>> Lane(PendingUpload upload)
        {
            var output = new List<PendingUpload>();
            foreach (var key in await store.KeysAsync(UploadPrefix))
            {
                var u = await Json.Get<PendingUpload>(store, key);
                if (u != null && u.Owner == upload.Owner && u.Slot == upload.Slot && u.Device == upload.Device) output.Add(u);
            }
            output.Sort(Compare);
            return output;
        }

        static KrtuworksException Replaced() => new KrtuworksException("save_replaced", "A newer save of this slot took the place of this one before it was sent.", 409);

        internal Task<SlotList> ListAsync() => ListFor(null);

        internal async Task<SlotList> ListFor(string? owner)
        {
            var answer = await api.PlayerAsync(FilesPath, new PlayerCall { Owner = owner });
            return answer.Json?.ToObject<SlotList>() ?? new SlotList();
        }

        static Dictionary<string, string?> Query(params (string key, string? value)[] pairs) => pairs.ToDictionary(p => p.key, p => p.value);

        /// <summary>A slot's current version; null when `ifNoneMatch` (a version id held) is still current.</summary>
        internal async Task<FileRead?> ReadAsync(string slot, SaveDevice? device, string? ifNoneMatch, string? owner = null)
        {
            var revisionKey = RevisionKey(slot, device?.Id);
            var revision = Revision(revisionKey);
            var query = Query(("device", device?.Id), ("platform", opts.Platform));
            foreach (var info in device?.Info ?? new Dictionary<string, string>()) query[info.Key] = info.Value;
            var headers = new Dictionary<string, string>();
            if (ifNoneMatch != null) headers["if-none-match"] = ifNoneMatch;
            var answer = await api.PlayerAsync(SlotPath(slot), new PlayerCall { Query = query, Headers = headers, Accept = new[] { 304 }, Owner = owner });
            if (answer.Status == 304) return null;
            var etag = answer.Raw.Header("etag");
            return new FileRead
            {
                Bytes = answer.Raw.Body,
                NewerLocalSave = Revision(revisionKey) != revision,
                SimilarDevice = answer.Raw.Header("x-krtu-similar-device") == "true",
                VersionId = etag?.Replace("W/", "").Replace("\"", ""),
                Generation = long.TryParse(answer.Raw.Header("x-krtu-generation"), out var g) ? g : 0,
                Sha256 = answer.Raw.Header("x-krtu-sha256") ?? "",
                SavedAt = answer.Raw.Header("x-krtu-saved-at") ?? "",
            };
        }

        /// <summary>
        /// Saves a new version of `slot`. Completes with the new version once the server activated it; throws
        /// `file_conflict` (`Details["current"]`: the version now in the slot) when another one came first, and
        /// `save_replaced` when a newer save of the slot took its place before it was sent.
        /// </summary>
        /// <param name="conditional">Whether `baseVersion` is meant (null = the slot must be empty); SETTINGS-like slots need neither.</param>
        /// <param name="owner">The player the save is for; when another one plays now, nothing is saved (`context_changed`). Default: whoever plays now.</param>
        /// <param name="coalesce">SETTINGS and DEVICE_SETTINGS: written at most once per slot every 30 seconds, the newest content wins (an older one waiting is replaced).</param>
        internal async Task<FileVersion> SaveAsync(string slot, byte[] data, bool conditional, string? baseVersion, SaveDevice? device, string? owner = null, bool coalesce = false)
        {
            var player = subject() ?? throw new KrtuworksException("signed_out", "No player is signed in.");
            if (owner != null && owner != player) throw new KrtuworksException("context_changed", "The player changed; nothing was saved.");
            var revisionKey = RevisionKey(slot, device?.Id);
            revisions[revisionKey] = Revision(revisionKey) + 1;
            var upload = new PendingUpload
            {
                UploadId = Ids.UuidV4(),
                Owner = player,
                Slot = slot,
                Device = device?.Id,
                Base = baseVersion,
                Conditional = conditional,
                Sha256 = Ids.Sha256Hex(data),
                Bytes = data,
                DeviceInfo = device?.Info,
                SavedAt = Now(),
                Seq = System.Threading.Interlocked.Increment(ref seq),
                V = 2,
            };
            var lane = LaneLock(upload);
            Task<Task<FileVersion>> step;
            lock (entering)
            {
                entering.TryGetValue(lane, out var previous);
                step = Enter(previous, upload, coalesce);
                entering[lane] = step;
            }
            Task<FileVersion>? sent = null;
            try
            {
                sent = await step;
                return await sent;
            }
            finally
            {
                if (coalesce && sent != null) lock (settingsGate) settingsWrites.Remove(sent);
                lock (entering)
                {
                    if (entering.TryGetValue(lane, out var last) && last == step) entering.Remove(lane);
                }
            }
        }

        /// <summary>
        /// Stores `upload` once the lane's earlier saves are stored, then starts it: the saves of a lane join it in
        /// the order they were made, so a large save stored after a smaller, newer one still counts as the older.
        /// </summary>
        async Task<Task<FileVersion>> Enter(Task? previous, PendingUpload upload, bool coalesce)
        {
            if (previous != null)
            {
                try { await previous; }
                catch (Exception) { /* that save's caller heard of it */ }
            }
            await Json.Put(store, UploadKey(upload.UploadId), upload);
            if (!coalesce) return Pump(upload, (_, __, ___) => { });
            var write = WriteSettingsAsync(upload);
            lock (settingsGate) settingsWrites.Add(write);
            return write;
        }

        /// <summary>Waits for the lane's turn (30 s after its last write, or until a flush), then sends; a newer save of the lane replaces this one meanwhile.</summary>
        async Task<FileVersion> WriteSettingsAsync(PendingUpload upload)
        {
            var lane = LaneLock(upload);
            long wait;
            CancellationToken flush;
            lock (settingsGate)
            {
                wait = (settingsNextAt.TryGetValue(lane, out var next) ? next : 0) - monotonic.ElapsedMilliseconds;
                flush = settingsNow.Token;
            }
            if (wait > 0)
            {
                try { await opts.Delay(TimeSpan.FromMilliseconds(wait), flush); }
                catch (OperationCanceledException) { /* flushed: the app is pausing or ending, so it goes now */ }
            }
            lock (settingsGate) settingsNextAt[lane] = monotonic.ElapsedMilliseconds + SettingsGapMs;
            return await Pump(upload, (_, __, ___) => { });
        }

        /// <summary>The app pauses or ends: the settings writes that wait for their turn go now, and are waited for.</summary>
        internal async Task FlushSettingsAsync()
        {
            Task[] stored;
            lock (entering) stored = entering.Values.ToArray();
            foreach (var step in stored)
            {
                try { await step; }
                catch (Exception) { /* the caller of that save heard of it */ }
            }
            Task[] running;
            CancellationTokenSource released;
            lock (settingsGate)
            {
                released = settingsNow;
                settingsNow = new CancellationTokenSource();
                running = settingsWrites.ToArray();
            }
            released.Cancel();
            foreach (var write in running)
            {
                try { await write; }
                catch (Exception) { /* the caller of that save heard of it */ }
            }
        }

        /// <summary>
        /// Sends `target` in its lane's order. An earlier save of the lane that was never sent is replaced by the
        /// newer one; an earlier save that was sent is settled first (asked how it ended, or sent again with its
        /// id), so an older copy never lands after a newer one. `target` itself is replaced when a newer save of
        /// the lane waits behind it, unsent. `tell` hears what became of the earlier ones.
        /// </summary>
        async Task<FileVersion> Pump(PendingUpload target, Action<PendingUpload, string, string?> tell)
        {
            using (await Locks.Take(LaneLock(target)))
            {
                var lane = await Lane(target);
                var at = lane.FindIndex(u => u.UploadId == target.UploadId);
                if (at < 0) return await SettledElsewhere(target);
                // The earlier ones never sent go first: this save replaces them whatever became of the others.
                var sentEarlier = new List<PendingUpload>();
                foreach (var earlier in lane.Take(at))
                {
                    if (await SentAt(earlier) != null) sentEarlier.Add(earlier);
                    else
                    {
                        await Forget(earlier.UploadId);
                        tell(earlier, "replaced", null);
                    }
                }
                foreach (var earlier in sentEarlier)
                {
                    var (outcome, code) = await SettleEarlier(earlier);
                    tell(earlier, outcome, code);
                }
                if (at < lane.Count - 1 && await SentAt(target) == null)
                {
                    await Forget(target.UploadId);
                    throw Replaced();
                }
                return await Send(target);
            }
        }

        /// <summary>
        /// An earlier save of a lane that was sent and whose outcome is not known here: how it ended, before
        /// anything newer goes. Throws when it has not ended yet (or the server cannot be asked): the lane waits.
        /// </summary>
        async Task<(string Outcome, string? Code)> SettleEarlier(PendingUpload upload)
        {
            JObject? state;
            try
            {
                state = (await api.PlayerAsync($"{SlotPath(upload.Slot)}/uploads/{upload.UploadId}", new PlayerCall { Owner = upload.Owner })).Json;
            }
            catch (KrtuworksException err) when (err.Code == "upload_not_found")
            {
                // The server never had it. A save with a version check is replaced at once: should it still arrive,
                // the newer save holds the slot and the check refuses it. One without a check could land over the
                // newer save, so it is replaced only once it can no longer arrive.
                if (upload.Conditional || Now() - (await SentAt(upload) ?? 0) >= ArrivalWindowMs)
                {
                    await Forget(upload.UploadId);
                    return ("replaced", null);
                }
                // It may still be on its way: sent again with its id, the server takes both as one upload.
                try
                {
                    await Send(upload);
                    return ("saved", null);
                }
                catch (KrtuworksException sent) when (!sent.Retryable && !sent.KeepsQueued && sent.Code != "upload_pending")
                {
                    return ("refused", sent.Code);
                }
            }
            var status = (string?)state?["status"];
            if (status == "ACTIVATED")
            {
                await Forget(upload.UploadId);
                onSaved();
                return ("saved", null);
            }
            if (status == "REJECTED")
            {
                await Forget(upload.UploadId);
                return ("refused", (string?)state?["code"] ?? "write_failed");
            }
            throw new KrtuworksException("upload_pending", "An earlier save of this slot is still being written; this one goes after it.", 202, null, new JObject { ["uploadId"] = upload.UploadId });
        }

        /// <summary>The save's record is gone: another flow finished or replaced it; the server says how it ended.</summary>
        async Task<FileVersion> SettledElsewhere(PendingUpload upload)
        {
            JObject? state;
            try
            {
                state = (await api.PlayerAsync($"{SlotPath(upload.Slot)}/uploads/{upload.UploadId}", new PlayerCall { Owner = upload.Owner })).Json;
            }
            catch (KrtuworksException err) when (err.Code == "upload_not_found")
            {
                throw Replaced();
            }
            return await Settle(upload, state);
        }

        async Task<FileVersion> Send(PendingUpload upload)
        {
            var headers = new Dictionary<string, string> { ["idempotency-key"] = upload.UploadId, ["content-type"] = "application/octet-stream" };
            if (upload.Conditional)
            {
                if (upload.Base != null) headers["if-match"] = upload.Base;
                else headers["if-none-match"] = "*";
            }
            var query = Query(("platform", opts.Platform), ("build", opts.Build), ("device", upload.Device));
            foreach (var info in upload.DeviceInfo ?? new Dictionary<string, string>()) query[info.Key] = info.Value;
            await Json.Put(store, SentKey(upload.UploadId), Now());
            Answer answer;
            try
            {
                answer = await api.PlayerAsync(SlotPath(upload.Slot), new PlayerCall { Method = "PUT", Query = query, Headers = headers, Body = upload.Bytes, Accept = new[] { 202 }, Owner = upload.Owner });
            }
            catch (KrtuworksException err)
            {
                // Only a refusal of the save itself ends it. A failure that can pass (no answer, a busy server, a frozen app, a merge
                // or a ban being settled, a token that could not be had) leaves it for a later try; a rate limit schedules that try.
                if (err.Code == "too_many_writes") RetryLater(upload, err.RetryAfter);
                else if (!err.Retryable && !err.KeepsQueued && err.Code != "account_deleted") await Forget(upload.UploadId);
                throw;
            }
            if (answer.Status == 201) return await Done(upload, answer.Json?["file"]?.ToObject<FileVersion>(), null);
            return await Settle(upload, answer.Json);
        }

        /// <summary>
        /// A save the server rate-limited is sent again when the limit passes (one wait per lane), not only at the next save or launch.
        /// </summary>
        void RetryLater(PendingUpload upload, double? retryAfterSeconds)
        {
            var lane = LaneLock(upload);
            lock (settingsGate)
            {
                if (!retrying.Add(lane)) return;
            }
            _ = RetryAfterAsync(upload, lane, retryAfterSeconds ?? 10);
        }

        async Task RetryAfterAsync(PendingUpload upload, string lane, double seconds)
        {
            try
            {
                await opts.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds) + 0.25), Stop);
            }
            catch (OperationCanceledException)
            {
                lock (settingsGate) retrying.Remove(lane);
                return;
            }
            lock (settingsGate) retrying.Remove(lane);
            try
            {
                if (await store.GetAsync(UploadKey(upload.UploadId)) == null) return;
                await Pump(upload, (_, __, ___) => { });
            }
            catch (KrtuworksException) { /* a rate limit schedules the next try itself; anything else waits for the next launch or save */ }
        }

        /// <summary>The player's account is gone: the saves waiting for it are dropped.</summary>
        internal async Task DropOwnerAsync(string owner)
        {
            foreach (var key in await store.KeysAsync(UploadPrefix))
            {
                var upload = await Json.Get<PendingUpload>(store, key);
                if (upload?.Owner == owner) await Forget(upload.UploadId);
            }
        }

        /// <summary>Reads an upload's state (200 again or 202) until it is final.</summary>
        async Task<FileVersion> Settle(PendingUpload upload, JObject? state)
        {
            var started = DateTimeOffset.UtcNow;
            var gen = generation();
            var wait = 1000;
            var current = state;
            while (true)
            {
                var status = (string?)current?["status"];
                if (status == "ACTIVATED") return await Done(upload, null, (string?)current?["file"]?["id"]);
                if (status == "REJECTED")
                {
                    await Forget(upload.UploadId);
                    var code = (string?)current?["code"] ?? "write_failed";
                    var details = new JObject { ["upload"] = current };
                    // A refusal read back after a lost answer carries no `current`: the slot's version now is what the game's choice screen needs.
                    if (code == "file_conflict") await AddCurrentAsync(upload, details);
                    throw new KrtuworksException(code, "The save was refused.", 409, null, details);
                }
                if (DateTimeOffset.UtcNow - started > PollLimit)
                    throw new KrtuworksException("upload_pending", "The save is still being written; it is finished on a later launch.", 202, null, new JObject { ["uploadId"] = upload.UploadId });
                await opts.Delay(TimeSpan.FromMilliseconds(wait), System.Threading.CancellationToken.None);
                wait = Math.Min(8000, wait * 2);
                // Another player now: this upload is asked about with its own player's token on a later sign-in.
                if (generation() != gen) throw new KrtuworksException("context_changed", "The player changed while the save was being written; it is finished when that player signs in again.");
                var answer = await api.PlayerAsync($"{SlotPath(upload.Slot)}/uploads/{upload.UploadId}", new PlayerCall { Owner = upload.Owner });
                current = answer.Json;
            }
        }

        async Task AddCurrentAsync(PendingUpload upload, JObject details)
        {
            try
            {
                var list = await ListFor(upload.Owner);
                var slot = list.Slots.FirstOrDefault(sl => sl.Key == upload.Slot);
                var version = upload.Device == null ? slot?.File : slot?.Devices.FirstOrDefault(d => d.Device == upload.Device);
                if (version != null) details["current"] = JObject.FromObject(version);
            }
            catch (KrtuworksException) { /* without it the game reads the slot itself */ }
        }

        async Task<FileVersion> Done(PendingUpload upload, FileVersion? file, string? versionId)
        {
            await Forget(upload.UploadId);
            onSaved();
            if (file != null) return file;
            // An answer replayed from the upload's record names the version; read the slot for the whole of it.
            var list = await ListFor(upload.Owner);
            var slot = list.Slots.FirstOrDefault(s => s.Key == upload.Slot);
            var versions = new List<FileVersion?> { slot?.File }.Concat(slot?.Devices ?? new List<FileVersion>()).Where(v => v != null).Select(v => v!).ToList();
            return versions.FirstOrDefault(v => v.Id == versionId) ?? versions.FirstOrDefault()
                ?? throw new KrtuworksException("file_not_found", "The saved version is no longer the slot's.", 404);
        }

        /// <summary>
        /// Saves left unfinished by an earlier launch (an answer lost, a 202, made offline): finishes the
        /// signed-in player's, the newest of each slot (and device) last; an older one never sent is replaced.
        /// </summary>
        internal async Task<List<ResumedUpload>> ResumeAsync()
        {
            var owner = subject();
            var output = new List<ResumedUpload>();
            if (owner == null) return output;
            var ids = new HashSet<string>((await store.KeysAsync(UploadPrefix)).Select(k => k.Substring(UploadPrefix.Length)));
            foreach (var key in await store.KeysAsync(SentPrefix)) if (!ids.Contains(key.Substring(SentPrefix.Length))) await store.DeleteAsync(key);
            var newest = new Dictionary<string, PendingUpload>();
            var order = new List<string>();
            foreach (var id in ids)
            {
                var upload = await Json.Get<PendingUpload>(store, UploadKey(id));
                if (upload == null || upload.Owner != owner) continue;
                var lane = LaneLock(upload);
                if (!newest.TryGetValue(lane, out var held)) order.Add(lane);
                if (held == null || Compare(held, upload) < 0) newest[lane] = upload;
            }
            void Tell(PendingUpload u, string outcome, string? code) => output.Add(new ResumedUpload { UploadId = u.UploadId, Slot = u.Slot, Outcome = outcome, Code = code });
            foreach (var lane in order)
            {
                var upload = newest[lane];
                try
                {
                    // Sent again with the same id and bytes: the server answers what the first one did.
                    await Pump(upload, Tell);
                    Tell(upload, "saved", null);
                }
                catch (KrtuworksException err)
                {
                    Tell(upload, err.Code == "save_replaced" ? "replaced" : err.Code == "upload_pending" || err.Retryable || err.KeepsQueued ? "waiting" : "refused", err.Code);
                }
            }
            return output;
        }

        /// <summary>Saves of another player waiting on this device (a merged account's): the game asks the player what to keep.</summary>
        internal async Task<List<WaitingSave>> WaitingForAsync(string owner)
        {
            var output = new List<WaitingSave>();
            foreach (var key in await store.KeysAsync(UploadPrefix))
            {
                var upload = await Json.Get<PendingUpload>(store, key);
                if (upload?.Owner == owner) output.Add(new WaitingSave { UploadId = upload.UploadId, Slot = upload.Slot, SavedAt = upload.SavedAt });
            }
            return output;
        }

        /// <summary>
        /// A save another player left waiting (a merged account's) that the player chose to keep:
        /// saved now as this player's, over `baseVersion` (the slot's version the player saw), with a new upload id.
        /// </summary>
        internal async Task<FileVersion> AdoptWaitingAsync(string uploadId, string? baseVersion)
        {
            var waiting = await Json.Get<PendingUpload>(store, UploadKey(uploadId)) ?? throw new KrtuworksException("upload_not_found", "No such waiting save on this device.", 404);
            var device = waiting.Device == null ? null : new SaveDevice { Id = waiting.Device, Info = waiting.DeviceInfo ?? new Dictionary<string, string>() };
            var saved = await SaveAsync(waiting.Slot, waiting.Bytes, true, baseVersion, device);
            await Forget(uploadId);
            return saved;
        }

        /// <summary>A waiting save the player chose not to keep.</summary>
        internal Task DiscardWaitingAsync(string uploadId) => Forget(uploadId);

        /// <summary>
        /// Offers the platform cloud's copy of a SAVE or PROFILE slot for the player's choice (Steam sync, Steam Cloud):
        /// it waits as the slot's `Conflict`, the current version (`current`, the one it was compared with) stays.
        /// Completes with the waiting version. Not kept in the store: an offer lost is made again on the next sync,
        /// and the server answers `conflict_pending` for one already waiting.
        /// </summary>
        internal async Task<FileVersion> OfferAsync(string slot, byte[] data, string current, string sessionId, string? owner = null)
        {
            var headers = new Dictionary<string, string> { ["idempotency-key"] = Ids.UuidV4(), ["content-type"] = "application/octet-stream", ["if-match"] = current };
            var query = Query(("platform", opts.Platform), ("sessionId", sessionId), ("build", opts.Build));
            var answer = await api.PlayerAsync($"{SlotPath(slot)}/offer", new PlayerCall { Method = "PUT", Query = query, Headers = headers, Body = data, Accept = new[] { 202 }, Owner = owner });
            var started = DateTimeOffset.UtcNow;
            var wait = 1000;
            var state = answer.Json;
            while ((string?)state?["status"] != "ACTIVATED")
            {
                if ((string?)state?["status"] == "REJECTED")
                    throw new KrtuworksException((string?)state?["code"] ?? "write_failed", "The offer was refused.", 409, null, new JObject { ["upload"] = state });
                if (DateTimeOffset.UtcNow - started > PollLimit)
                    throw new KrtuworksException("upload_pending", "The offer is still being written; the next sync looks again.", 202);
                await opts.Delay(TimeSpan.FromMilliseconds(wait), System.Threading.CancellationToken.None);
                wait = Math.Min(8000, wait * 2);
                state = (await api.PlayerAsync($"{SlotPath(slot)}/uploads/{(string?)state?["uploadId"]}", new PlayerCall { Owner = owner })).Json;
            }
            var list = await ListFor(owner);
            return list.Slots.FirstOrDefault(s => s.Key == slot)?.Conflict
                ?? throw new KrtuworksException("file_not_found", "The offered copy is no longer waiting in the slot.", 404);
        }

        /// <summary>After a merge: keep `keep` (the slot's version or the merged one) over `current` (the one the player saw).</summary>
        internal async Task<FileVersion> ResolveAsync(string slot, string keep, string current)
        {
            var answer = await api.PlayerAsync($"{SlotPath(slot)}/resolve", new PlayerCall { Method = "POST", Json = new JObject { ["keep"] = keep, ["current"] = current } });
            return answer.Json?["file"]?.ToObject<FileVersion>() ?? throw new KrtuworksException("invalid_response", "The answer names no file.");
        }
    }
}
