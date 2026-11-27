#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Krtuworks
{
    /// <summary>Where an event happened: a world position (x, y, z in a scene) or a screen position.</summary>
    public sealed class EventPosition
    {
        /// <summary>`world` or `screen`.</summary>
        [JsonProperty("space")] public string Space = "world";
        /// <summary>The scene or screen name.</summary>
        [JsonProperty("area")] public string Area = "";
        [JsonProperty("x")] public double X;
        [JsonProperty("y")] public double Y;
        [JsonProperty("z", NullValueHandling = NullValueHandling.Ignore)] public double? Z;
    }

    public sealed class FlushResult
    {
        public int Sent;
        public List<(string Id, string Code)> Rejected = new List<(string, string)>();
        /// <summary>Why the queue stopped early (network_error, switched_off …), null when it emptied.</summary>
        public string? StoppedBy;
    }
}

namespace Krtuworks.Internal
{
    /// <summary>An event as `/v1/events` takes it.</summary>
    internal sealed class WireEvent
    {
        [JsonProperty("id")] public string Id = "";
        [JsonProperty("type")] public string Type = "";
        [JsonProperty("createdAt")] public string CreatedAt = "";
        [JsonProperty("launchId")] public string LaunchId = "";
        [JsonProperty("monotonicMs")] public long MonotonicMs;
        [JsonProperty("serverOffsetMs")] public long? ServerOffsetMs;
        [JsonProperty("sessionId")] public string? SessionId;
        [JsonProperty("build")] public string? Build;
        [JsonProperty("schemaVersion")] public int? SchemaVersion;
        [JsonProperty("position")] public EventPosition? Position;
        [JsonProperty("data")] public JObject? Data;
    }

    /// <summary>A batch frozen before its first send: the same id always carries the same platform, build and events.</summary>
    internal sealed class FrozenBatch
    {
        [JsonProperty("batchId")] public string BatchId = "";
        [JsonProperty("platform")] public string Platform = "";
        [JsonProperty("build")] public string Build = "";
        [JsonProperty("events")] public List<WireEvent> Events = new List<WireEvent>();
    }

    internal sealed class EventQueueState
    {
        [JsonProperty("pending")] public List<WireEvent> Pending = new List<WireEvent>();
        [JsonProperty("batch")] public FrozenBatch? Batch;
        /// <summary>Events dropped here (queue full, too old, refused); the count is reported once with an error event.</summary>
        [JsonProperty("dropped")] public int Dropped;
        [JsonProperty("reported")] public int Reported;
    }

    /// <summary>
    /// Telemetry events, the rules of krtuworks-sdk-js/src/events.ts:
    /// every event is made with its id and bound to the player it is about (`anon` before any account; that
    /// queue goes to the first player). Queued events are written to the store before they are sent; a batch
    /// is frozen before its first send and sent again unchanged after a failure (the server hashes platform +
    /// build + events); each event's answer is read on its own: accepted, duplicate and rejected leave the
    /// queue, retry goes back with its id into a new batch.
    /// </summary>
    internal sealed class EventQueue
    {
        internal const string Anon = "anon";
        /// <summary>The server's refusal while the app does not ship on this platform yet: the events wait for the platform to be added.</summary>
        const string PlatformNotRegistered = "platform_not_registered";
        const int MaxEvents = 50_000;
        const int MaxBytes = 20 * 1024 * 1024;
        static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
        const int BatchEvents = 500;
        const int SentLimit = 256 * 1024;
        /// <summary>Batches are built under this raw size and halved when gzip still overshoots the server's 256 KB.</summary>
        const int RawLimit = 1_500_000;

        readonly Resolved opts;
        readonly IKrtuStore store;
        readonly Clock clock;
        readonly Api api;
        readonly Func<string?> subject;
        readonly List<(string owner, WireEvent ev)> buffer = new List<(string, WireEvent)>();
        bool persistSoon;
        long notBefore;
        long backoffMs;

        internal EventQueue(Resolved opts, IKrtuStore store, Clock clock, Api api, Func<string?> subject)
        {
            this.opts = opts;
            this.store = store;
            this.clock = clock;
            this.api = api;
            this.subject = subject;
        }

        static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string Key(string owner) => $"krtu:{opts.ClientId}:events:{owner}";
        static int SizeOf(WireEvent e) => Encoding.UTF8.GetByteCount(Json.Write(e));

        async Task<EventQueueState> Load(string owner) => await Json.Get<EventQueueState>(store, Key(owner)) ?? new EventQueueState();

        async Task Save(string owner, EventQueueState state)
        {
            if (state.Pending.Count == 0 && state.Batch == null && state.Dropped == state.Reported) await store.DeleteAsync(Key(owner));
            else await Json.Put(store, Key(owner), state);
        }

        /// <summary>Makes an event now, for whoever plays now. Kept in memory for a moment, then written to the store.</summary>
        internal WireEvent Add(string type, JObject? data, EventPosition? position, string? sessionId, int? schemaVersion)
        {
            var made = new WireEvent
            {
                Id = Ids.UuidV7(),
                Type = type,
                CreatedAt = Clock.IsoNow(),
                LaunchId = clock.LaunchId,
                MonotonicMs = clock.MonotonicMs,
                ServerOffsetMs = clock.ServerOffsetMs.HasValue ? (long?)Math.Round(clock.ServerOffsetMs.Value) : null,
                SessionId = sessionId,
                SchemaVersion = schemaVersion,
                Position = position,
                Data = data,
            };
            bool schedule;
            lock (buffer)
            {
                buffer.Add((subject() ?? Anon, made));
                schedule = !persistSoon;
                persistSoon = true;
            }
            if (schedule) _ = PersistLater();
            return made;
        }

        async Task PersistLater()
        {
            await opts.Delay(TimeSpan.FromSeconds(1), System.Threading.CancellationToken.None);
            try { await PersistAsync(); }
            catch (Exception) { /* the next persist or flush writes them */ }
        }

        /// <summary>Writes the events made since the last write; drops the oldest when the queue is full or too old.</summary>
        internal async Task PersistAsync()
        {
            List<(string owner, WireEvent ev)> taken;
            lock (buffer)
            {
                taken = new List<(string, WireEvent)>(buffer);
                buffer.Clear();
                persistSoon = false;
            }
            var written = new HashSet<string>();
            try
            {
                foreach (var group in taken.GroupBy(t => t.owner))
                {
                    using (await Locks.Take(Key(group.Key)))
                    {
                        var state = await Load(group.Key);
                        state.Pending.AddRange(group.Select(g => g.ev));
                        Trim(state);
                        await Save(group.Key, state);
                    }
                    written.Add(group.Key);
                }
            }
            catch (Exception)
            {
                // The store refused (full, locked): what it has not taken goes back to wait for the next write.
                lock (buffer) buffer.InsertRange(0, taken.Where(t => !written.Contains(t.owner)));
                throw;
            }
        }

        /// <summary>
        /// Drops the events older than the age limit, counted on the server's clock (the event's own clock reading is moved by the
        /// offset it was made with, and "now" is the server's now), so a device clock that was wrong never ages an event out.
        /// </summary>
        void Trim(EventQueueState state)
        {
            var oldest = clock.ServerNowMs - (long)MaxAge.TotalMilliseconds;
            var before = state.Pending.Count;
            state.Pending = state.Pending.Where(e => !DateTimeOffset.TryParse(e.CreatedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) || at.ToUnixTimeMilliseconds() + (e.ServerOffsetMs ?? 0) >= oldest).ToList();
            var bytes = state.Pending.Sum(SizeOf) + (state.Batch?.Events.Sum(SizeOf) ?? 0);
            var count = state.Pending.Count + (state.Batch?.Events.Count ?? 0);
            var cut = 0;
            while (state.Pending.Count > cut && (count > MaxEvents || bytes > MaxBytes))
            {
                bytes -= SizeOf(state.Pending[cut]);
                count--;
                cut++;
            }
            state.Pending = state.Pending.Skip(cut).ToList();
            state.Dropped += before - state.Pending.Count;
        }

        /// <summary>Events waiting for `owner` (written and not yet written).</summary>
        internal async Task<int> SizeAsync(string owner)
        {
            var state = await Load(owner);
            int inMemory;
            lock (buffer) inMemory = buffer.Count(b => b.owner == owner);
            return state.Pending.Count + (state.Batch?.Events.Count ?? 0) + inMemory;
        }

        /// <summary>The player's account is gone: what waited for it, written or not, is dropped.</summary>
        internal async Task DropAsync(string owner)
        {
            lock (buffer) buffer.RemoveAll(b => b.owner == owner);
            using (await Locks.Take(Key(owner))) await store.DeleteAsync(Key(owner));
        }

        /// <summary>
        /// Moves `from`'s queue to `to`: the events made before any account go to the first player; a merged
        /// account's go to the one it went into (the server counts each event once by its id, and a batch it
        /// took under the old account answers the same under the new one).
        /// </summary>
        internal async Task MoveAsync(string from, string to)
        {
            if (from == to) return;
            await PersistAsync();
            var first = string.CompareOrdinal(from, to) < 0 ? from : to;
            var second = first == from ? to : from;
            using (await Locks.Take(Key(first)))
            using (await Locks.Take(Key(second)))
            {
                var source = await Load(from);
                if (source.Pending.Count == 0 && source.Batch == null && source.Dropped == source.Reported) return;
                // The dropped count leaves the source before it reaches the target: a move cut short between
                // the writes loses it rather than says it twice.
                var unsaid = source.Dropped - source.Reported;
                if (unsaid > 0)
                {
                    source.Reported = source.Dropped;
                    await Save(from, source);
                }
                var target = await Load(to);
                // A move run again after it was cut short brings events the target holds already: they go once.
                var held = new HashSet<string>(target.Pending.Concat(target.Batch?.Events ?? new List<WireEvent>()).Select(e => e.Id));
                if (source.Batch != null && source.Batch.BatchId != target.Batch?.BatchId)
                {
                    if (target.Batch != null) target.Pending.InsertRange(0, source.Batch.Events.Where(e => !held.Contains(e.Id)));
                    else target.Batch = source.Batch;
                }
                target.Pending.AddRange(source.Pending.Where(e => !held.Contains(e.Id)));
                target.Dropped += Math.Max(0, unsaid);
                Trim(target);
                await Save(to, target);
                await store.DeleteAsync(Key(from));
            }
        }

        FrozenBatch? Freeze(EventQueueState state, int limit)
        {
            if (state.Pending.Count == 0) return null;
            var events = new List<WireEvent>();
            var bytes = 0;
            foreach (var e in state.Pending)
            {
                var size = SizeOf(e) + 1;
                if (events.Count >= limit || (events.Count > 0 && bytes + size > RawLimit)) break;
                events.Add(e);
                bytes += size;
            }
            state.Pending = state.Pending.Skip(events.Count).ToList();
            return new FrozenBatch { BatchId = Ids.UuidV7(), Platform = opts.Platform, Build = opts.Build, Events = events };
        }

        /// <summary>The dropped-events count, said once as an SDK error event (if the app keeps errors).</summary>
        WireEvent? DroppedNotice(EventQueueState state, bool errorsOn)
        {
            var unsaid = state.Dropped - state.Reported;
            if (unsaid <= 0) return null;
            state.Reported = state.Dropped;
            if (!errorsOn) return null;
            return new WireEvent
            {
                Id = Ids.UuidV7(),
                Type = "error",
                CreatedAt = Clock.IsoNow(),
                LaunchId = clock.LaunchId,
                MonotonicMs = clock.MonotonicMs,
                Data = new JObject
                {
                    ["kind"] = "other",
                    ["message"] = $"{unsaid} events were dropped on this device: the queue was full or too old",
                    ["fingerprint"] = "sdk.events_dropped",
                    ["handled"] = true,
                },
            };
        }

        /// <summary>
        /// Sends the signed-in player's queue, batch by batch, until it is empty or a send fails (the frozen
        /// batch then waits, unchanged, for the next flush).
        /// </summary>
        internal async Task<FlushResult> FlushAsync(bool errorsOn, int maxBatches = 20)
        {
            await PersistAsync();
            var owner = subject();
            var result = new FlushResult();
            if (owner == null)
            {
                result.StoppedBy = "signed_out";
                return result;
            }
            if (NowMs < notBefore)
            {
                result.StoppedBy = "waiting";
                return result;
            }
            var limit = BatchEvents;
            for (var round = 0; round < maxBatches; round++)
            {
                FrozenBatch? batch;
                using (await Locks.Take(Key(owner)))
                {
                    var state = await Load(owner);
                    if (state.Batch == null)
                    {
                        var notice = DroppedNotice(state, errorsOn);
                        if (notice != null) state.Pending.Add(notice);
                        state.Batch = Freeze(state, limit);
                    }
                    await Save(owner, state);
                    batch = state.Batch;
                }
                if (batch == null) return result;
                var outcome = await SendBatch(owner, batch, result);
                if (outcome == "split")
                {
                    limit = Math.Max(1, batch.Events.Count / 2);
                    continue;
                }
                if (outcome != "done")
                {
                    result.StoppedBy = outcome;
                    return result;
                }
                limit = BatchEvents;
            }
            return result;
        }

        static byte[] Gzip(byte[] raw)
        {
            using var output = new MemoryStream();
            using (var zip = new GZipStream(output, CompressionLevel.Optimal, true)) zip.Write(raw, 0, raw.Length);
            return output.ToArray();
        }

        async Task<string> SendBatch(string owner, FrozenBatch batch, FlushResult result)
        {
            var text = Json.Write(new JObject
            {
                ["batchId"] = batch.BatchId,
                ["platform"] = batch.Platform,
                ["build"] = batch.Build,
                ["sentAt"] = Clock.IsoNow(),
                ["launchId"] = clock.LaunchId,
                ["monotonicMs"] = clock.MonotonicMs,
                ["events"] = JArray.FromObject(batch.Events, JsonSerializer.Create(Json.Settings)),
            });
            var body = Gzip(Encoding.UTF8.GetBytes(text));
            if (body.Length > SentLimit && batch.Events.Count > 1)
            {
                await Unfreeze(owner, batch);
                return "split";
            }
            Answer answer;
            try
            {
                answer = await api.PlayerAsync("/v1/events", new PlayerCall
                {
                    Method = "POST",
                    Body = body,
                    Headers = new Dictionary<string, string> { ["content-type"] = "application/json", ["content-encoding"] = "gzip" },
                    Owner = owner,
                });
            }
            catch (KrtuworksException err)
            {
                return await Failed(owner, batch, err);
            }
            backoffMs = 0;
            var json = answer.Json ?? new JObject();
            var retryIds = new HashSet<string>((json["retry"] as JArray ?? new JArray()).Select(r => (string?)r["id"] ?? ""));
            var rejected = (json["rejected"] as JArray ?? new JArray()).Select(r => ((string?)r["id"] ?? "", (string?)r["code"] ?? "")).ToList();
            using (await Locks.Take(Key(owner)))
            {
                var state = await Load(owner);
                if (state.Batch?.BatchId == batch.BatchId)
                {
                    state.Pending.InsertRange(0, batch.Events.Where(e => retryIds.Contains(e.Id)));
                    state.Batch = null;
                    await Save(owner, state);
                }
            }
            result.Sent += batch.Events.Count - retryIds.Count - rejected.Count;
            result.Rejected.AddRange(rejected);
            if (retryIds.Count > 0)
            {
                // `retry` is for now (the app's events of the day, a build cap): wait before the next batch.
                notBefore = NowMs + 60_000;
                return "retry";
            }
            return "done";
        }

        /// <summary>A batch never taken by the server goes back to the front of the queue (only before any answer said otherwise).</summary>
        async Task Unfreeze(string owner, FrozenBatch batch)
        {
            using (await Locks.Take(Key(owner)))
            {
                var state = await Load(owner);
                if (state.Batch?.BatchId != batch.BatchId) return;
                state.Pending.InsertRange(0, batch.Events);
                state.Batch = null;
                await Save(owner, state);
            }
        }

        async Task<string> Failed(string owner, FrozenBatch batch, KrtuworksException err)
        {
            if (err.Code == "payload_too_large" && batch.Events.Count > 1)
            {
                await Unfreeze(owner, batch);
                return "split";
            }
            if (err.Code == "batch_id_reused")
            {
                // The id was spent on other events (a store restored from another device): same events, new id.
                await Unfreeze(owner, batch);
                return "done";
            }
            var notShippedYet = err.Code == PlatformNotRegistered;
            if ((err.Status == 400 || err.Status == 413) && !err.KeepsQueued && !notShippedYet)
            {
                // The batch itself can never be taken: drop it rather than block every later event.
                using (await Locks.Take(Key(owner)))
                {
                    var state = await Load(owner);
                    if (state.Batch?.BatchId == batch.BatchId)
                    {
                        state.Dropped += batch.Events.Count;
                        state.Batch = null;
                        await Save(owner, state);
                    }
                }
                return err.Code;
            }
            // A refusal that can lift (a frozen app, a merge, a module off), a failure of the token and an app that does not
            // ship on this platform yet keep the batch, with a long wait.
            notBefore = NowMs + Retry.DelayMs(err, ref backoffMs, notShippedYet);
            return err.Code;
        }

        /// <summary>Lets the next flush try at once (back online, or the player changed).</summary>
        internal void ResetBackoff()
        {
            notBefore = 0;
            backoffMs = 0;
        }
    }
}
