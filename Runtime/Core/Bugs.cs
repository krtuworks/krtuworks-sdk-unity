#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Krtuworks.Internal;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Krtuworks
{
    /// <summary>A bug report from the player (the categories are a fixed menu).</summary>
    public sealed class BugReportInput
    {
        /// <summary>CRASH, GAMEPLAY, GRAPHICS, AUDIO, PERFORMANCE, CONTROLS, TEXT or OTHER.</summary>
        public string Category = "OTHER";
        /// <summary>What the player wrote, 1-4000 characters.</summary>
        public string Description = "";
        /// <summary>A picture of the screen (PNG, JPEG or WebP, at most 5 MB); the player may leave it out.</summary>
        public byte[]? Screenshot;
        /// <summary>Where the player is; the level in play when not given.</summary>
        public string? Level;
        public EventPosition? Position;
        /// <summary>The frame rate now; the last one the performance family told when not given.</summary>
        public double? Fps;
    }

    /// <summary>A report as its player sees it: never the notes, the assignee or the labels.</summary>
    public sealed class BugReportView
    {
        [JsonProperty("id")] public string Id = "";
        [JsonProperty("category")] public string Category = "";
        [JsonProperty("description")] public string? Description;
        [JsonProperty("platform")] public string Platform = "";
        [JsonProperty("build")] public string Build = "";
        [JsonProperty("createdAt")] public string CreatedAt = "";
        [JsonProperty("screenshot")] public bool Screenshot;
        /// <summary>NEW, INVESTIGATING, RESOLVED, CANNOT_REPRODUCE or DUPLICATE; a duplicate shows its original's.</summary>
        [JsonProperty("status")] public string Status = "";
        [JsonProperty("fixedIn")] public string? FixedIn;
        [JsonProperty("fixedAt")] public string? FixedAt;
        [JsonProperty("duplicate")] public bool Duplicate;
    }

    public sealed class BugDropped
    {
        public string ReportId = "";
        public string Code = "";
        /// <summary>Only the screenshot was refused; the report is in.</summary>
        public bool ScreenshotOnly;
    }

    public sealed class BugFlushResult
    {
        public int Sent;
        /// <summary>Reports or screenshots the server refused for good; they are not sent again.</summary>
        public List<BugDropped> Dropped = new List<BugDropped>();
        public string? StoppedBy;
    }
}

namespace Krtuworks.Internal
{
    internal sealed class WaitingShot
    {
        [JsonProperty("bytes")] public string Bytes = "";
        [JsonProperty("type")] public string Type = "";
    }

    internal sealed class WaitingBug
    {
        [JsonProperty("reportId")] public string ReportId = "";
        [JsonProperty("madeAt")] public long MadeAt;
        /// <summary>The report as it goes to the server, frozen when made: the same reportId always carries the same body.</summary>
        [JsonProperty("body")] public JObject Body = new JObject();
        [JsonProperty("screenshot")] public WaitingShot? Screenshot;
        /// <summary>The server's id once the report is in (the screenshot follows it).</summary>
        [JsonProperty("bugId")] public string? BugId;
    }

    internal sealed class FixedState
    {
        [JsonProperty("told")] public List<string> Told = new List<string>();
    }

    /// <summary>
    /// Bug reports from inside the app, the rules of krtuworks-sdk-js/src/bugs.ts: a report
    /// is kept on the device first and sent when it can be (offline, before the player exists, after a
    /// merge), its screenshot right after it. The body is frozen when the report is made, so sending it
    /// again after a lost answer gets the same report. The last events and log lines are in memory only.
    /// </summary>
    internal sealed class BugReports
    {
        internal static readonly string[] Categories = { "CRASH", "GAMEPLAY", "GRAPHICS", "AUDIO", "PERFORMANCE", "CONTROLS", "TEXT", "OTHER" };
        const int MaxDescription = 4000;
        const int MaxScreenshot = 5 * 1024 * 1024;
        const int MaxWaiting = 20;
        const int RingEvents = 50;
        const int EventChars = 1800;
        /// <summary>The server takes at most 100 KB of events; the SDK's own part stays under it so it never costs the player's report.</summary>
        const int EventsChars = 90_000;
        const int LogChars = 32_768;
        const int LogLine = 1000;
        const long RetryMs = 60_000;
        const int ToldKept = 200;
        /// <summary>The report can never be taken as it is: dropped rather than sent forever.</summary>
        static readonly HashSet<int> ReportRefused = new HashSet<int> { 400, 413 };
        static readonly HashSet<string> ReportRefusedCodes = new HashSet<string> { "report_id_reused" };
        /// <summary>The screenshot can never be taken (too late, too big, not a picture, the report gone); `screenshot_exists` is done.</summary>
        static readonly HashSet<int> ScreenshotRefused = new HashSet<int> { 400, 404, 409, 411, 413, 415 };

        readonly Resolved opts;
        readonly IKrtuStore store;
        readonly Api api;
        readonly Func<string> owner;
        readonly Func<string?> subject;
        /// <summary>Tells the game a fix; false when no one listens (the fix then waits for the next look).</summary>
        readonly Func<BugReportView, bool> fixedTold;
        readonly List<JObject> events = new List<JObject>();
        readonly List<string> logLines = new List<string>();
        int logChars;
        double? lastFps;
        /// <summary>Reports that wait before their next try (in memory: a new launch tries them all again).</summary>
        readonly Dictionary<string, long> waitUntil = new Dictionary<string, long>();

        internal BugReports(Resolved opts, IKrtuStore store, Api api, Func<string> owner, Func<string?> subject, Func<BugReportView, bool> fixedTold)
        {
            this.opts = opts;
            this.store = store;
            this.api = api;
            this.owner = owner;
            this.subject = subject;
            this.fixedTold = fixedTold;
        }

        static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string Prefix(string who) => $"krtu:{opts.ClientId}:bugs:{who}:";
        string FixedKey(string who) => $"krtu:{opts.ClientId}:bugsFixed:{who}";

        static KrtuworksException Refused(string message) => new KrtuworksException("invalid_request", message, 400);

        /// <summary>The picture's type from its first bytes (what the server takes).</summary>
        internal static string? ScreenshotType(byte[] b)
        {
            if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4e && b[3] == 0x47) return "image/png";
            if (b.Length >= 3 && b[0] == 0xff && b[1] == 0xd8 && b[2] == 0xff) return "image/jpeg";
            if (b.Length >= 12 && Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && Encoding.ASCII.GetString(b, 8, 4) == "WEBP") return "image/webp";
            return null;
        }

        /// <summary>
        /// The body the server takes for a report: the player's words and what the SDK knows of the moment.
        /// Checked here so a report that can never be taken is refused at once instead of waiting in the queue.
        /// </summary>
        internal static JObject ReportBody(BugReportInput input, string reportId, Resolved opts, string? level, double? fps, IReadOnlyList<JObject> events, string log)
        {
            if (!Categories.Contains(input.Category)) throw Refused($"category is one of {string.Join(", ", Categories)}");
            var description = (input.Description ?? "").Trim();
            if (description.Length == 0 || description.Length > MaxDescription) throw Refused($"description is what the player wrote, 1-{MaxDescription} characters");
            // The level the SDK saw is cut to fit; one the game names must fit.
            var place = input.Level ?? (level != null && level.Length > 200 ? level.Substring(0, 200) : level);
            if (place != null && (place.Trim().Length == 0 || place.Length > 200)) throw Refused("level is 1-200 characters");
            var rate = input.Fps ?? fps;
            if (rate.HasValue && (double.IsNaN(rate.Value) || double.IsInfinity(rate.Value) || rate.Value < 0 || rate.Value > 1000)) throw Refused("fps is a number from 0 to 1000");
            var p = input.Position;
            if (p != null && (string.IsNullOrEmpty(p.Area) || p.Area.Length > 100 || double.IsNaN(p.X) || double.IsInfinity(p.X) || double.IsNaN(p.Y) || double.IsInfinity(p.Y)))
                throw Refused("position is {area, x, y, z?}");
            var body = new JObject
            {
                ["reportId"] = reportId,
                ["category"] = input.Category,
                ["description"] = description,
                ["platform"] = opts.Platform,
                ["build"] = opts.Build,
            };
            if (opts.Device != null)
            {
                var device = JObject.FromObject(opts.Device, JsonSerializer.Create(Json.Settings));
                if (device.Count > 0) body["device"] = device;
            }
            if (!string.IsNullOrEmpty(opts.Locale)) body["locale"] = opts.Locale;
            if (place != null) body["level"] = place.Trim();
            if (p != null)
            {
                // The server's bug position is the place alone (no `space`).
                var position = new JObject { ["area"] = p.Area, ["x"] = p.X, ["y"] = p.Y };
                if (p.Z.HasValue) position["z"] = p.Z.Value;
                body["position"] = position;
            }
            if (rate.HasValue) body["fps"] = Math.Round(rate.Value * 10, MidpointRounding.AwayFromZero) / 10;
            var kept = new JArray(events.Select(e => e.DeepClone()));
            while (kept.Count > 0 && kept.ToString(Formatting.None).Length > EventsChars) kept.RemoveAt(0);
            if (kept.Count > 0) body["events"] = kept;
            if (log.Length > 0) body["log"] = log;
            return body;
        }

        /// <summary>
        /// The reports to tell about since the last check: the first check of a player on this device tells
        /// nothing (it only learns what was already fixed); a report made here starts the record empty.
        /// </summary>
        internal static (List<BugReportView> Fixed, FixedState State) NewlyFixed(List<BugReportView> reports, FixedState? state)
        {
            var fixedIds = reports.Where(r => r.Status == "RESOLVED").Select(r => r.Id).ToList();
            if (state == null) return (new List<BugReportView>(), new FixedState { Told = fixedIds.Take(ToldKept).ToList() });
            var told = new HashSet<string>(state.Told);
            var newly = reports.Where(r => r.Status == "RESOLVED" && !told.Contains(r.Id)).ToList();
            return (newly, new FixedState { Told = fixedIds.Concat(state.Told).Distinct().Take(ToldKept).ToList() });
        }

        /// <summary>An event the SDK kept, for the next report's "last events".</summary>
        internal void NoteEvent(WireEvent e)
        {
            var entry = new JObject { ["type"] = e.Type, ["at"] = e.CreatedAt };
            if (e.Position != null) entry["position"] = JObject.FromObject(e.Position, JsonSerializer.Create(Json.Settings));
            if (e.Data != null && e.Data.ToString(Formatting.None).Length <= EventChars) entry["data"] = e.Data.DeepClone();
            lock (events)
            {
                events.Add(entry);
                if (events.Count > RingEvents) events.RemoveAt(0);
                if (e.Type == "performance" && e.Data?["fps"] is JValue fps && (fps.Type == JTokenType.Float || fps.Type == JTokenType.Integer)) lastFps = (double)fps;
            }
        }

        /// <summary>A line for the next report's log (the app's own, and the errors the SDK told).</summary>
        internal void Log(string message)
        {
            var text = message ?? "";
            var line = $"{Clock.IsoNow()} {(text.Length > LogLine ? text.Substring(0, LogLine) : text)}";
            lock (logLines)
            {
                logLines.Add(line);
                logChars += line.Length + 1;
                while (logChars > LogChars && logLines.Count > 0)
                {
                    logChars -= logLines[0].Length + 1;
                    logLines.RemoveAt(0);
                }
            }
        }

        internal async Task<string> ReportAsync(BugReportInput input, string? level)
        {
            var who = owner();
            if ((await store.KeysAsync(Prefix(who))).Count >= MaxWaiting)
                throw new KrtuworksException("too_many_reports", $"At most {MaxWaiting} bug reports wait on this device; they are sent when the app is online.", 429);
            WaitingShot? shot = null;
            if (input.Screenshot != null)
            {
                var type = ScreenshotType(input.Screenshot) ?? throw Refused("The screenshot is a PNG, JPEG or WebP picture");
                if (input.Screenshot.Length > MaxScreenshot) throw Refused("The screenshot is at most 5 MB");
                shot = new WaitingShot { Bytes = Convert.ToBase64String(input.Screenshot), Type = type };
            }
            var reportId = Ids.UuidV4();
            List<JObject> recent;
            string log;
            double? fps;
            lock (events)
            {
                recent = events.ToList();
                fps = lastFps;
            }
            lock (logLines) log = string.Join("\n", logLines);
            var body = ReportBody(input, reportId, opts, level, fps, recent, log);
            await Json.Put(store, Prefix(who) + reportId, new WaitingBug { ReportId = reportId, MadeAt = NowMs, Body = body, Screenshot = shot });
            var player = subject();
            if (player != null && await store.GetAsync(FixedKey(player)) == null) await Json.Put(store, FixedKey(player), new FixedState());
            return reportId;
        }

        /// <summary>Reports (with their screenshots) that wait on this device for the player, or for the first player.</summary>
        internal async Task<int> WaitingAsync() => (await store.KeysAsync(Prefix(owner()))).Count;

        /// <summary>Hands one owner's waiting reports to another (the anonymous queue to the first player, a confirmed merge).</summary>
        internal async Task MoveAsync(string from, string to)
        {
            if (from == to) return;
            foreach (var key in await store.KeysAsync(Prefix(from)))
            {
                var w = await Json.Get<WaitingBug>(store, key);
                if (w != null) await Json.Put(store, Prefix(to) + w.ReportId, w);
                await store.DeleteAsync(key);
            }
        }

        internal async Task<BugFlushResult> FlushAsync()
        {
            var result = new BugFlushResult();
            var player = subject();
            if (player == null) return result;
            var now = NowMs;
            var queue = new List<(string Key, WaitingBug W)>();
            foreach (var key in await store.KeysAsync(Prefix(player)))
            {
                var w = await Json.Get<WaitingBug>(store, key);
                if (w != null) queue.Add((key, w));
            }
            foreach (var (key, w) in queue.OrderBy(q => q.W.MadeAt))
            {
                lock (waitUntil)
                {
                    if (waitUntil.TryGetValue(w.ReportId, out var until) && until > now) continue;
                }
                try
                {
                    if (w.BugId == null)
                    {
                        var answer = await api.PlayerAsync("/v1/me/bug-reports", new PlayerCall { Method = "POST", Json = w.Body });
                        w.BugId = (string?)answer.Json?["report"]?["id"] ?? throw new KrtuworksException("invalid_answer", "The server answered without the report.");
                        if (w.Screenshot != null) await Json.Put(store, key, w);
                    }
                    if (w.Screenshot != null)
                    {
                        try
                        {
                            await api.PlayerAsync($"/v1/me/bug-reports/{Uri.EscapeDataString(w.BugId)}/screenshot", new PlayerCall
                            {
                                Method = "PUT",
                                Body = Convert.FromBase64String(w.Screenshot.Bytes),
                                Headers = new Dictionary<string, string> { ["content-type"] = w.Screenshot.Type },
                            });
                        }
                        catch (KrtuworksException e) when (ScreenshotRefused.Contains(e.Status))
                        {
                            // The report is in: a screenshot it already has is done, one refused for good is let go.
                            if (e.Code != "screenshot_exists") result.Dropped.Add(new BugDropped { ReportId = w.ReportId, Code = e.Code, ScreenshotOnly = true });
                        }
                    }
                    await store.DeleteAsync(key);
                    lock (waitUntil) waitUntil.Remove(w.ReportId);
                    result.Sent += 1;
                }
                catch (KrtuworksException e)
                {
                    if (w.BugId == null && (ReportRefused.Contains(e.Status) || ReportRefusedCodes.Contains(e.Code)))
                    {
                        await store.DeleteAsync(key);
                        result.Dropped.Add(new BugDropped { ReportId = w.ReportId, Code = e.Code });
                        continue;
                    }
                    // Anything else (no answer, a busy or closed server, a player in a merge, a ban, the
                    // module off for now) keeps the report for later.
                    if (e.Code != "context_changed")
                    {
                        var wait = e.RetryAfter.HasValue ? (long)(e.RetryAfter.Value * 1000) : RetryMs;
                        lock (waitUntil) waitUntil[w.ReportId] = now + wait;
                    }
                    result.StoppedBy = e.Code;
                    return result;
                }
            }
            return result;
        }

        /// <summary>The player's reports in this app and environment, newest first; fixes not told yet are told.</summary>
        internal async Task<List<BugReportView>> MineAsync()
        {
            var player = subject();
            var answer = await api.PlayerAsync("/v1/me/bug-reports", new PlayerCall());
            var reports = (answer.Json?["reports"] as JArray)?.ToObject<List<BugReportView>>(JsonSerializer.Create(Json.Settings)) ?? new List<BugReportView>();
            if (player != null) await TellFixed(player, reports);
            return reports;
        }

        /// <summary>At a player's arrival: only when this device keeps a record for them (they reported here, or the app read their reports).</summary>
        internal async Task CheckFixedAsync()
        {
            var player = subject();
            if (player != null && await store.GetAsync(FixedKey(player)) != null) await MineAsync();
        }

        async Task TellFixed(string player, List<BugReportView> reports)
        {
            var (newly, state) = NewlyFixed(reports, await Json.Get<FixedState>(store, FixedKey(player)));
            var untold = new HashSet<string>();
            foreach (var report in newly)
            {
                // A fix counts as told only when the game listens; otherwise it waits for the next look.
                try { if (!fixedTold(report)) untold.Add(report.Id); }
                catch (Exception) { /* the app's listener */ }
            }
            await Json.Put(store, FixedKey(player), new FixedState { Told = state.Told.Where(id => !untold.Contains(id)).ToList() });
        }

        internal void ResetBackoff()
        {
            lock (waitUntil) waitUntil.Clear();
        }
    }
}
