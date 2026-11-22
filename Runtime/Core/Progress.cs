#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Krtuworks
{
    /// <summary>A stat or achievement op the server refused for good (it left the queue).</summary>
    public sealed class ProgressRefusal
    {
        /// <summary>`stat`, `unlock` or `score`.</summary>
        public string Type = "";
        public string Key = "";
        public string Code = "";
    }

    public sealed class ProgressFlushResult
    {
        public int Applied;
        public string? StoppedBy;
        /// <summary>The stats' values after the ops the server applied.</summary>
        public Dictionary<string, double> Values = new Dictionary<string, double>();
        /// <summary>The player's score on each leaderboard after the score ops the server applied.</summary>
        public Dictionary<string, long> Scores = new Dictionary<string, long>();
    }
}

namespace Krtuworks.Internal
{
    internal sealed class FrozenOp
    {
        [JsonProperty("opId")] public string OpId = "";
        /// <summary>`stat`, `unlock` or `score`.</summary>
        [JsonProperty("type")] public string Type = "";
        [JsonProperty("key")] public string Key = "";
        [JsonProperty("value", NullValueHandling = NullValueHandling.Ignore)] public double? Value;
        /// <summary>Score ops only: base64 of the bytes the game keeps with the score.</summary>
        [JsonProperty("details", NullValueHandling = NullValueHandling.Ignore)] public string? Details;
        /// <summary>Score ops only: the platform's own row of the player (set only when true).</summary>
        [JsonProperty("fromPlatform", NullValueHandling = NullValueHandling.Ignore)] public bool? FromPlatform;
    }

    /// <summary>A value the game reported and not yet frozen into an op: an increase (`add`), or a value to keep (`set`).</summary>
    internal sealed class Contribution
    {
        [JsonProperty("op")] public string Op = "add";
        [JsonProperty("value")] public double Value;
        internal Contribution Copy() => new Contribution { Op = Op, Value = Value };
    }

    internal sealed class StatEntry
    {
        [JsonProperty("inFlight")] public List<FrozenOp> InFlight = new List<FrozenOp>();
        [JsonProperty("pending")] public List<Contribution> Pending = new List<Contribution>();
    }

    /// <summary>A score the game reported and not yet frozen into an op.</summary>
    internal sealed class ScoreContribution
    {
        [JsonProperty("value")] public long Value;
        [JsonProperty("details")] public string? Details;
        [JsonProperty("fromPlatform")] public bool FromPlatform;
        internal ScoreContribution Copy() => new ScoreContribution { Value = Value, Details = Details, FromPlatform = FromPlatform };
    }

    internal sealed class ScoreEntry
    {
        [JsonProperty("inFlight")] public List<FrozenOp> InFlight = new List<FrozenOp>();
        [JsonProperty("pending")] public List<ScoreContribution> Pending = new List<ScoreContribution>();
    }

    /// <summary>What decides whether a leaderboard's waiting scores fold (from <c>GET /v1/me/leaderboards</c>).</summary>
    internal sealed class BoardRule
    {
        /// <summary>`ASC` (lower is better) or `DESC`.</summary>
        [JsonProperty("sortOrder")] public string SortOrder = "";
        /// <summary>`KEEP_BEST` or `FORCE_UPDATE`; null while the server has not said.</summary>
        [JsonProperty("uploadMethod")] public string? UploadMethod;
    }

    internal sealed class UnlockEntry
    {
        [JsonProperty("inFlight")] public List<FrozenOp> InFlight = new List<FrozenOp>();
        [JsonProperty("pending")] public bool Pending;
    }

    /// <summary>Per stat, achievement or leaderboard: the ops frozen with their ids (normally one; a merge can bring a second) and what waits behind them.</summary>
    internal sealed class ProgressState
    {
        [JsonProperty("stats")] public Dictionary<string, StatEntry> Stats = new Dictionary<string, StatEntry>();
        [JsonProperty("unlocks")] public Dictionary<string, UnlockEntry> Unlocks = new Dictionary<string, UnlockEntry>();
        [JsonProperty("scores")] public Dictionary<string, ScoreEntry> Scores = new Dictionary<string, ScoreEntry>();
    }

    /// <summary>
    /// Stats, achievements and leaderboard scores, the rules of krtuworks-sdk-js/src/progress.ts:
    /// progress is never lost and never counted twice. Every report is written to the store at once; per
    /// stat at most one op is in flight, frozen with its opId until it has an answer (sent again unchanged
    /// after a failure), and the rest waits folded behind it. Ops have no age limit and do not depend on the
    /// statistics choice.
    /// </summary>
    internal sealed class ProgressQueue
    {
        static readonly Regex KeyPattern = new Regex("^[a-z][a-z0-9_.]{0,63}$");
        const double MaxValue = 1e15;
        const int MaxOps = 100;
        /// <summary>Refusals that say the stat, achievement or leaderboard itself is gone: what waits for it goes too.</summary>
        static readonly HashSet<string> KeyGone = new HashSet<string> { "unknown_stat", "stat_retired", "unknown_achievement", "achievement_retired", "unknown_leaderboard", "leaderboard_retired" };
        /// <summary>The leaderboards module is off: a score op is not refused for good, it stays queued.</summary>
        const string ModuleDisabled = "module_disabled";
        const int MaxDetailsBytes = 64;

        readonly Resolved opts;
        readonly IKrtuStore store;
        readonly Api api;
        readonly Func<string?> subject;
        Dictionary<string, string> kinds = new Dictionary<string, string>();
        bool kindsLoaded;
        Dictionary<string, BoardRule> boards = new Dictionary<string, BoardRule>();
        bool boardsLoaded;
        long notBefore;
        long backoffMs;
        internal readonly List<ProgressRefusal> Refusals = new List<ProgressRefusal>();

        internal ProgressQueue(Resolved opts, IKrtuStore store, Api api, Func<string?> subject)
        {
            this.opts = opts;
            this.store = store;
            this.api = api;
            this.subject = subject;
        }

        static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string Key(string owner) => $"krtu:{opts.ClientId}:progress:{owner}";
        string KindsKey => $"krtu:{opts.ClientId}:statKinds";
        string BoardsKey => $"krtu:{opts.ClientId}:boardRules";

        /// <summary>
        /// Folds what waits for one stat into as few values as its kind allows: SUM adds, MAX
        /// keeps the largest, MIN the smallest, LATEST the last. A stat whose kind is not known yet only folds
        /// its runs of increases; values to keep wait one by one, in order.
        /// </summary>
        internal static List<Contribution> Fold(IEnumerable<Contribution> pending, string? kind)
        {
            var output = new List<Contribution>();
            foreach (var c in pending)
            {
                var last = output.Count > 0 ? output[output.Count - 1] : null;
                if (last == null)
                {
                    output.Add(c.Copy());
                    continue;
                }
                if (kind == "SUM" || (kind == null && c.Op == "add" && last.Op == "add"))
                {
                    var sum = last.Value + c.Value;
                    if (Math.Abs(sum) < MaxValue)
                    {
                        last.Value = sum;
                        last.Op = "add";
                        continue;
                    }
                }
                else if (kind == "MAX")
                {
                    last.Value = Math.Max(last.Value, c.Value);
                    continue;
                }
                else if (kind == "MIN")
                {
                    last.Value = Math.Min(last.Value, c.Value);
                    continue;
                }
                else if (kind == "LATEST")
                {
                    last.Value = c.Value;
                    last.Op = c.Op;
                    continue;
                }
                output.Add(c.Copy());
            }
            return output;
        }

        static bool Better(string sortOrder, long a, long b) => sortOrder == "ASC" ? a < b : a > b;

        /// <summary>
        /// Folds what waits for one leaderboard once its rules are known: KEEP_BEST keeps the better score (DESC the larger, ASC
        /// the smaller; an equal one keeps the earlier), FORCE_UPDATE keeps the last. Unknown rules fold nothing (the order
        /// is kept), and a score that is the platform's own row never folds with another.
        /// </summary>
        internal static List<ScoreContribution> FoldScores(IEnumerable<ScoreContribution> pending, BoardRule? rule)
        {
            var output = new List<ScoreContribution>();
            foreach (var c in pending)
            {
                var last = output.Count > 0 ? output[output.Count - 1] : null;
                if (last != null && rule != null && !last.FromPlatform && !c.FromPlatform)
                {
                    if (rule.UploadMethod == "FORCE_UPDATE")
                    {
                        output[output.Count - 1] = c.Copy();
                        continue;
                    }
                    if (rule.UploadMethod == "KEEP_BEST" && (rule.SortOrder == "ASC" || rule.SortOrder == "DESC"))
                    {
                        if (Better(rule.SortOrder, c.Value, last.Value)) output[output.Count - 1] = c.Copy();
                        continue;
                    }
                }
                output.Add(c.Copy());
            }
            return output;
        }

        string? KindOf(string key) => kinds.TryGetValue(key, out var k) ? k : null;

        BoardRule? RuleOf(string key) => boards.TryGetValue(key, out var r) ? r : null;

        async Task<ProgressState> Load(string owner) => await Json.Get<ProgressState>(store, Key(owner)) ?? new ProgressState();

        async Task Save(string owner, ProgressState state)
        {
            foreach (var key in state.Stats.Where(s => s.Value.InFlight.Count == 0 && s.Value.Pending.Count == 0).Select(s => s.Key).ToList()) state.Stats.Remove(key);
            foreach (var key in state.Unlocks.Where(u => u.Value.InFlight.Count == 0 && !u.Value.Pending).Select(u => u.Key).ToList()) state.Unlocks.Remove(key);
            foreach (var key in state.Scores.Where(s => s.Value.InFlight.Count == 0 && s.Value.Pending.Count == 0).Select(s => s.Key).ToList()) state.Scores.Remove(key);
            if (state.Stats.Count > 0 || state.Unlocks.Count > 0 || state.Scores.Count > 0) await Json.Put(store, Key(owner), state);
            else await store.DeleteAsync(Key(owner));
        }

        async Task LoadKinds()
        {
            if (kindsLoaded) return;
            kinds = await Json.Get<Dictionary<string, string>>(store, KindsKey) ?? new Dictionary<string, string>();
            kindsLoaded = true;
        }

        /// <summary>Learns the stats' kinds (from `GET /v1/me/progress`): what waits folds tighter from now on.</summary>
        internal async Task LearnKindsAsync(IEnumerable<(string key, string kind)> stats)
        {
            await LoadKinds();
            foreach (var (key, kind) in stats) kinds[key] = kind;
            await Json.Put(store, KindsKey, kinds);
        }

        async Task LoadBoards()
        {
            if (boardsLoaded) return;
            boards = await Json.Get<Dictionary<string, BoardRule>>(store, BoardsKey) ?? new Dictionary<string, BoardRule>();
            boardsLoaded = true;
        }

        /// <summary>Learns the leaderboards' rules (from <c>GET /v1/me/leaderboards</c>): what waits for a board folds from now on.</summary>
        internal async Task LearnBoardsAsync(IEnumerable<(string key, string sortOrder, string? uploadMethod)> learned)
        {
            await LoadBoards();
            foreach (var (key, sortOrder, uploadMethod) in learned) boards[key] = new BoardRule { SortOrder = sortOrder, UploadMethod = uploadMethod };
            await Json.Put(store, BoardsKey, boards);
        }

        /// <summary>A score for a leaderboard: kept until the server has it, like a stat; the server rules (range, order, method) decide.</summary>
        internal async Task ReportScoreAsync(string key, long value, byte[]? details, bool fromPlatform)
        {
            if (!KeyPattern.IsMatch(key)) throw new ArgumentException($"\"{key}\" is not a leaderboard key (lowercase letters, digits, _ and ., at most 64)");
            if (Math.Abs((double)value) >= MaxValue) throw new ArgumentException("a score is a whole number below 10^15");
            if (details != null && (details.Length < 1 || details.Length > MaxDetailsBytes)) throw new ArgumentException($"a score's details are 1 to {MaxDetailsBytes} bytes");
            await LoadBoards();
            var owner = subject() ?? EventQueue.Anon;
            using (await Locks.Take(Key(owner)))
            {
                var state = await Load(owner);
                if (!state.Scores.TryGetValue(key, out var entry)) state.Scores[key] = entry = new ScoreEntry();
                entry.Pending.Add(new ScoreContribution { Value = value, Details = details == null ? null : Convert.ToBase64String(details), FromPlatform = fromPlatform });
                entry.Pending = FoldScores(entry.Pending, RuleOf(key));
                await Save(owner, state);
            }
        }

        /// <param name="type">`add` (SUM increase) or `set` (MAX/MIN/LATEST value).</param>
        internal async Task ReportAsync(string type, string key, double value)
        {
            if (!KeyPattern.IsMatch(key)) throw new ArgumentException($"\"{key}\" is not a stat key (lowercase letters, digits, _ and ., at most 64)");
            if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) >= MaxValue) throw new ArgumentException("a stat value is a finite number below 10^15");
            await LoadKinds();
            var owner = subject() ?? EventQueue.Anon;
            using (await Locks.Take(Key(owner)))
            {
                var state = await Load(owner);
                if (!state.Stats.TryGetValue(key, out var entry)) state.Stats[key] = entry = new StatEntry();
                entry.Pending.Add(new Contribution { Op = type, Value = value });
                entry.Pending = Fold(entry.Pending, KindOf(key));
                await Save(owner, state);
            }
        }

        internal async Task UnlockAsync(string key)
        {
            if (!KeyPattern.IsMatch(key)) throw new ArgumentException($"\"{key}\" is not an achievement key");
            var owner = subject() ?? EventQueue.Anon;
            using (await Locks.Take(Key(owner)))
            {
                var state = await Load(owner);
                if (!state.Unlocks.TryGetValue(key, out var entry)) state.Unlocks[key] = entry = new UnlockEntry();
                if (entry.InFlight.Count == 0) entry.Pending = true;
                await Save(owner, state);
            }
        }

        /// <summary>How many ops wait for `owner` (in flight or not yet frozen).</summary>
        internal async Task<int> WaitingAsync(string owner)
        {
            var state = await Load(owner);
            return state.Stats.Values.Sum(s => s.InFlight.Count + s.Pending.Count) + state.Unlocks.Values.Sum(u => u.InFlight.Count + (u.Pending ? 1 : 0))
                + state.Scores.Values.Sum(s => s.InFlight.Count + s.Pending.Count);
        }

        /// <summary>
        /// Moves `from`'s waiting progress to `to` (the first player after anon play, or a merge target).
        /// Everything that waits for `from` is first frozen into ops with ids and written back; only then do the
        /// ops move, and an op the target already holds is not added again. The target and the source are two
        /// writes: a move cut short between them (the app closed, the delete failed) and run again brings the
        /// same ids, so nothing is added twice. One the server already applied under the old account answers
        /// `duplicate` under the new one (the merge moved its receipt).
        /// </summary>
        internal async Task MoveAsync(string from, string to)
        {
            if (from == to) return;
            await LoadKinds();
            await LoadBoards();
            var first = string.CompareOrdinal(from, to) < 0 ? from : to;
            var second = first == from ? to : from;
            using (await Locks.Take(Key(first)))
            using (await Locks.Take(Key(second)))
            {
                var source = await Load(from);
                if (FreezeAll(source)) await Save(from, source);
                var target = await Load(to);
                foreach (var s in source.Stats)
                {
                    if (!target.Stats.TryGetValue(s.Key, out var t)) target.Stats[s.Key] = t = new StatEntry();
                    AddOps(t.InFlight, s.Value.InFlight);
                }
                foreach (var u in source.Unlocks)
                {
                    if (!target.Unlocks.TryGetValue(u.Key, out var t)) target.Unlocks[u.Key] = t = new UnlockEntry();
                    AddOps(t.InFlight, u.Value.InFlight);
                    if (t.InFlight.Count > 0) t.Pending = false;
                }
                foreach (var s in source.Scores)
                {
                    if (!target.Scores.TryGetValue(s.Key, out var t)) target.Scores[s.Key] = t = new ScoreEntry();
                    AddOps(t.InFlight, s.Value.InFlight);
                }
                await Save(to, target);
                await store.DeleteAsync(Key(from));
            }
        }

        /// <summary>Adds the ops the target does not hold yet (by opId).</summary>
        static void AddOps(List<FrozenOp> into, List<FrozenOp> ops)
        {
            foreach (var op in ops) if (!into.Any(held => held.OpId == op.OpId)) into.Add(op);
        }

        /// <summary>Freezes everything that waits in `state` into ops with ids (a move's first step); whether anything was.</summary>
        bool FreezeAll(ProgressState state)
        {
            var frozen = false;
            foreach (var s in state.Stats)
            {
                foreach (var c in Fold(s.Value.Pending, KindOf(s.Key))) s.Value.InFlight.Add(new FrozenOp { OpId = Ids.UuidV7(), Type = "stat", Key = s.Key, Value = c.Value });
                frozen |= s.Value.Pending.Count > 0;
                s.Value.Pending = new List<Contribution>();
            }
            foreach (var u in state.Unlocks)
            {
                if (u.Value.Pending && u.Value.InFlight.Count == 0) u.Value.InFlight.Add(new FrozenOp { OpId = Ids.UuidV7(), Type = "unlock", Key = u.Key });
                frozen |= u.Value.Pending;
                u.Value.Pending = false;
            }
            foreach (var s in state.Scores)
            {
                foreach (var c in FoldScores(s.Value.Pending, RuleOf(s.Key)))
                    s.Value.InFlight.Add(new FrozenOp { OpId = Ids.UuidV7(), Type = "score", Key = s.Key, Value = c.Value, Details = c.Details, FromPlatform = c.FromPlatform ? true : (bool?)null });
                frozen |= s.Value.Pending.Count > 0;
                s.Value.Pending = new List<ScoreContribution>();
            }
            return frozen;
        }

        /// <summary>Freezes the next op of every stat, achievement and leaderboard with none in flight; returns every op to send (not the `parked` ones).</summary>
        List<FrozenOp> Freeze(ProgressState state, HashSet<string> parked)
        {
            var ops = new List<FrozenOp>();
            foreach (var s in state.Stats)
            {
                if (s.Value.InFlight.Count == 0 && s.Value.Pending.Count > 0)
                {
                    s.Value.Pending = Fold(s.Value.Pending, KindOf(s.Key));
                    var next = s.Value.Pending[0];
                    s.Value.Pending.RemoveAt(0);
                    s.Value.InFlight.Add(new FrozenOp { OpId = Ids.UuidV7(), Type = "stat", Key = s.Key, Value = next.Value });
                }
                ops.AddRange(s.Value.InFlight);
            }
            foreach (var u in state.Unlocks)
            {
                if (u.Value.InFlight.Count == 0 && u.Value.Pending)
                {
                    u.Value.InFlight.Add(new FrozenOp { OpId = Ids.UuidV7(), Type = "unlock", Key = u.Key });
                    u.Value.Pending = false;
                }
                ops.AddRange(u.Value.InFlight);
            }
            foreach (var s in state.Scores)
            {
                if (s.Value.InFlight.Count == 0 && s.Value.Pending.Count > 0)
                {
                    s.Value.Pending = FoldScores(s.Value.Pending, RuleOf(s.Key));
                    var next = s.Value.Pending[0];
                    s.Value.Pending.RemoveAt(0);
                    s.Value.InFlight.Add(new FrozenOp { OpId = Ids.UuidV7(), Type = "score", Key = s.Key, Value = next.Value, Details = next.Details, FromPlatform = next.FromPlatform ? true : (bool?)null });
                }
                ops.AddRange(s.Value.InFlight);
            }
            return ops.Where(op => !parked.Contains(op.OpId)).Take(MaxOps).ToList();
        }

        /// <summary>
        /// Sends the signed-in player's ops until nothing waits or a send fails. Answers per op: applied and
        /// duplicate end the op; a refusal for good ends it too (and everything waiting for a stat or
        /// achievement or leaderboard that no longer exists); anything else keeps it frozen for the next flush. A score op
        /// answered `module_disabled` stays queued and is not sent again within this flush.
        /// </summary>
        internal async Task<ProgressFlushResult> FlushAsync()
        {
            var owner = subject();
            var output = new ProgressFlushResult();
            if (owner == null)
            {
                output.StoppedBy = "signed_out";
                return output;
            }
            if (NowMs < notBefore)
            {
                output.StoppedBy = "waiting";
                return output;
            }
            await LoadKinds();
            await LoadBoards();
            var parked = new HashSet<string>();
            for (var round = 0; round < 50; round++)
            {
                List<FrozenOp> ops;
                using (await Locks.Take(Key(owner)))
                {
                    var state = await Load(owner);
                    ops = Freeze(state, parked);
                    await Save(owner, state);
                }
                if (ops.Count == 0)
                {
                    if (parked.Count > 0) output.StoppedBy = ModuleDisabled;
                    return output;
                }
                Answer answer;
                try
                {
                    answer = await api.PlayerAsync("/v1/me/progress", new PlayerCall
                    {
                        Method = "POST",
                        Json = new JObject { ["platform"] = opts.Platform, ["build"] = opts.Build, ["ops"] = JArray.FromObject(ops) },
                        Owner = owner,
                    });
                }
                catch (KrtuworksException err)
                {
                    backoffMs = Math.Min(300_000, Math.Max(5_000, backoffMs * 2));
                    notBefore = NowMs + (err.RetryAfter.HasValue ? (long)(err.RetryAfter.Value * 1000) : backoffMs);
                    output.StoppedBy = err.Code;
                    return output;
                }
                backoffMs = 0;
                var results = answer.Json?["results"] as JArray ?? new JArray();
                int settled;
                var held = 0;
                using (await Locks.Take(Key(owner)))
                {
                    var state = await Load(owner);
                    settled = 0;
                    foreach (var r in results)
                    {
                        var opId = (string?)r["opId"];
                        // Matched by opId: a refusal carries no `type`, and op_id_reused no `key` either.
                        var frozen = opId == null ? null : state.Stats.Values.SelectMany(e => e.InFlight).Concat(state.Unlocks.Values.SelectMany(e => e.InFlight)).Concat(state.Scores.Values.SelectMany(e => e.InFlight)).FirstOrDefault(op => op.OpId == opId);
                        if (frozen == null) continue;
                        var type = frozen.Type;
                        var key = frozen.Key;
                        var inFlight = InFlightOf(state, type, key)!;
                        if (type == "score" && (string?)r["status"] == "rejected" && (string?)r["code"] == ModuleDisabled)
                        {
                            parked.Add(opId!);
                            held++;
                            continue;
                        }
                        inFlight.RemoveAll(op => op.OpId == opId);
                        settled++;
                        if ((string?)r["status"] == "rejected")
                        {
                            var code = (string?)r["code"] ?? "rejected";
                            Refusals.Add(new ProgressRefusal { Type = type, Key = key, Code = code });
                            if (KeyGone.Contains(code))
                            {
                                if (type == "unlock") state.Unlocks.Remove(key);
                                else if (type == "score") state.Scores.Remove(key);
                                else state.Stats.Remove(key);
                            }
                        }
                        else
                        {
                            output.Applied++;
                            if (type == "stat" && (r["value"]?.Type == JTokenType.Integer || r["value"]?.Type == JTokenType.Float)) output.Values[key] = (double)r["value"]!;
                            if (type == "score" && (r["score"]?.Type == JTokenType.Integer || r["score"]?.Type == JTokenType.Float)) output.Scores[key] = (long)r["score"]!;
                        }
                    }
                    await Save(owner, state);
                }
                if (settled == 0 && held == 0)
                {
                    // An answer that settles none of the ops sent: keep them frozen and try again later.
                    output.StoppedBy = "unanswered";
                    return output;
                }
            }
            return output;
        }

        static List<FrozenOp>? InFlightOf(ProgressState state, string type, string key)
        {
            if (type == "unlock") return state.Unlocks.TryGetValue(key, out var u) ? u.InFlight : null;
            if (type == "score") return state.Scores.TryGetValue(key, out var sc) ? sc.InFlight : null;
            return state.Stats.TryGetValue(key, out var s) ? s.InFlight : null;
        }

        internal void ResetBackoff()
        {
            notBefore = 0;
            backoffMs = 0;
        }
    }
}
