#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
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
        /// <summary>saved, refused or waiting.</summary>
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

        readonly Resolved opts;
        readonly Api api;
        readonly IKrtuStore store;
        readonly Func<string?> subject;
        readonly Func<int> generation;
        readonly Action onSaved;
        /// <summary>Per slot (and device), bumped by every save started here: a read compares it before and after.</summary>
        readonly Dictionary<string, int> revisions = new Dictionary<string, int>();

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

        internal async Task<SlotList> ListAsync()
        {
            var answer = await api.PlayerAsync(FilesPath, new PlayerCall());
            return answer.Json?.ToObject<SlotList>() ?? new SlotList();
        }

        static Dictionary<string, string?> Query(params (string key, string? value)[] pairs) => pairs.ToDictionary(p => p.key, p => p.value);

        /// <summary>A slot's current version; null when `ifNoneMatch` (a version id held) is still current.</summary>
        internal async Task<FileRead?> ReadAsync(string slot, SaveDevice? device, string? ifNoneMatch)
        {
            var revisionKey = RevisionKey(slot, device?.Id);
            var revision = Revision(revisionKey);
            var query = Query(("device", device?.Id), ("platform", opts.Platform));
            foreach (var info in device?.Info ?? new Dictionary<string, string>()) query[info.Key] = info.Value;
            var headers = new Dictionary<string, string>();
            if (ifNoneMatch != null) headers["if-none-match"] = ifNoneMatch;
            var answer = await api.PlayerAsync(SlotPath(slot), new PlayerCall { Query = query, Headers = headers, Accept = new[] { 304 } });
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
        /// `file_conflict` (`Details["current"]`: the version now in the slot) when another one came first.
        /// </summary>
        /// <param name="conditional">Whether `baseVersion` is meant (null = the slot must be empty); SETTINGS-like slots need neither.</param>
        internal async Task<FileVersion> SaveAsync(string slot, byte[] data, bool conditional, string? baseVersion, SaveDevice? device)
        {
            var owner = subject() ?? throw new KrtuworksException("signed_out", "No player is signed in.");
            var revisionKey = RevisionKey(slot, device?.Id);
            revisions[revisionKey] = Revision(revisionKey) + 1;
            var upload = new PendingUpload
            {
                UploadId = Ids.UuidV4(),
                Owner = owner,
                Slot = slot,
                Device = device?.Id,
                Base = baseVersion,
                Conditional = conditional,
                Sha256 = Ids.Sha256Hex(data),
                Bytes = data,
                DeviceInfo = device?.Info,
                SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            await Json.Put(store, UploadKey(upload.UploadId), upload);
            return await Send(upload);
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
            Answer answer;
            try
            {
                answer = await api.PlayerAsync(SlotPath(upload.Slot), new PlayerCall { Method = "PUT", Query = query, Headers = headers, Body = upload.Bytes, Accept = new[] { 202 } });
            }
            catch (KrtuworksException err)
            {
                if (!err.Retryable) await store.DeleteAsync(UploadKey(upload.UploadId));
                throw;
            }
            if (answer.Status == 201) return await Done(upload, answer.Json?["file"]?.ToObject<FileVersion>(), null);
            return await Settle(upload, answer.Json);
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
                    await store.DeleteAsync(UploadKey(upload.UploadId));
                    throw new KrtuworksException((string?)current?["code"] ?? "write_failed", "The save was refused.", 409, null, new JObject { ["upload"] = current });
                }
                if (DateTimeOffset.UtcNow - started > PollLimit)
                    throw new KrtuworksException("upload_pending", "The save is still being written; it is finished on a later launch.", 202, null, new JObject { ["uploadId"] = upload.UploadId });
                await opts.Delay(TimeSpan.FromMilliseconds(wait), System.Threading.CancellationToken.None);
                wait = Math.Min(8000, wait * 2);
                // Another player now: this upload is asked about with its own player's token on a later sign-in.
                if (generation() != gen) throw new KrtuworksException("context_changed", "The player changed while the save was being written; it is finished when that player signs in again.");
                var answer = await api.PlayerAsync($"{SlotPath(upload.Slot)}/uploads/{upload.UploadId}", new PlayerCall());
                current = answer.Json;
            }
        }

        async Task<FileVersion> Done(PendingUpload upload, FileVersion? file, string? versionId)
        {
            await store.DeleteAsync(UploadKey(upload.UploadId));
            onSaved();
            if (file != null) return file;
            // An answer replayed from the upload's record names the version; read the slot for the whole of it.
            var list = await ListAsync();
            var slot = list.Slots.FirstOrDefault(s => s.Key == upload.Slot);
            var versions = new List<FileVersion?> { slot?.File }.Concat(slot?.Devices ?? new List<FileVersion>()).Where(v => v != null).Select(v => v!).ToList();
            return versions.FirstOrDefault(v => v.Id == versionId) ?? versions.FirstOrDefault()
                ?? throw new KrtuworksException("file_not_found", "The saved version is no longer the slot's.", 404);
        }

        /// <summary>Saves left unfinished by an earlier launch (an answer lost, a 202): finishes the signed-in player's.</summary>
        internal async Task<List<ResumedUpload>> ResumeAsync()
        {
            var owner = subject();
            var output = new List<ResumedUpload>();
            if (owner == null) return output;
            foreach (var key in await store.KeysAsync(UploadPrefix))
            {
                var upload = await Json.Get<PendingUpload>(store, key);
                if (upload == null || upload.Owner != owner) continue;
                try
                {
                    // Sent again with the same id and bytes: the server answers what the first one did.
                    await Send(upload);
                    output.Add(new ResumedUpload { UploadId = upload.UploadId, Slot = upload.Slot, Outcome = "saved" });
                }
                catch (KrtuworksException err)
                {
                    output.Add(new ResumedUpload { UploadId = upload.UploadId, Slot = upload.Slot, Outcome = err.Code == "upload_pending" || err.Retryable ? "waiting" : "refused", Code = err.Code });
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
            await store.DeleteAsync(UploadKey(uploadId));
            return saved;
        }

        /// <summary>A waiting save the player chose not to keep.</summary>
        internal Task DiscardWaitingAsync(string uploadId) => store.DeleteAsync(UploadKey(uploadId));

        /// <summary>
        /// Offers the platform cloud's copy of a SAVE or PROFILE slot for the player's choice (Steam sync, Steam Cloud):
        /// it waits as the slot's `Conflict`, the current version (`current`, the one it was compared with) stays.
        /// Completes with the waiting version. Not kept in the store: an offer lost is made again on the next sync,
        /// and the server answers `conflict_pending` for one already waiting.
        /// </summary>
        internal async Task<FileVersion> OfferAsync(string slot, byte[] data, string current, string sessionId)
        {
            var headers = new Dictionary<string, string> { ["idempotency-key"] = Ids.UuidV4(), ["content-type"] = "application/octet-stream", ["if-match"] = current };
            var query = Query(("platform", opts.Platform), ("sessionId", sessionId), ("build", opts.Build));
            var answer = await api.PlayerAsync($"{SlotPath(slot)}/offer", new PlayerCall { Method = "PUT", Query = query, Headers = headers, Body = data, Accept = new[] { 202 } });
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
                state = (await api.PlayerAsync($"{SlotPath(slot)}/uploads/{(string?)state?["uploadId"]}", new PlayerCall())).Json;
            }
            var list = await ListAsync();
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
