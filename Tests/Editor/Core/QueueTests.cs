#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Krtuworks.Internal;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Krtuworks.Tests
{
    public class QueueTests
    {
        sealed class Rig
        {
            internal Identity Identity = null!;
            internal MemoryStore Store = null!;
            internal ProgressQueue Progress = null!;
            internal EventQueue Events = null!;
            internal string Owner => Identity.Player!.Subject;
        }

        static async Task<Rig> Build(FakeServer server, bool signIn = true)
        {
            server.GuestRoutes();
            var store = new MemoryStore();
            var opts = Resolved.From(server.Options(store));
            var clock = new Clock();
            var identity = new Identity(opts, store, store, server, clock, null);
            if (signIn) await identity.SignInAsGuestAsync("2000-01-01", "TERMS:v1 PRIVACY:v1", false);
            var api = new Api(opts, identity, server, clock);
            string? Subject() => identity.Player?.Subject;
            return new Rig
            {
                Identity = identity,
                Store = store,
                Progress = new ProgressQueue(opts, store, api, Subject),
                Events = new EventQueue(opts, store, clock, api, Subject),
            };
        }

        static Contribution C(string op, double value) => new Contribution { Op = op, Value = value };
        static IEnumerable<(string, double)> Show(IEnumerable<Contribution> list) => list.Select(c => (c.Op, c.Value));

        [Test]
        public void FoldingFollowsTheStatKind_AnUnknownKindFoldsOnlyIncreases()
        {
            var values = new[] { C("set", 5), C("set", 9), C("set", 2) };
            Assert.That(Show(ProgressQueue.Fold(values, "MAX")), Is.EqualTo(new[] { ("set", 9d) }));
            Assert.That(Show(ProgressQueue.Fold(values, "MIN")), Is.EqualTo(new[] { ("set", 2d) }));
            Assert.That(Show(ProgressQueue.Fold(values, "LATEST")), Is.EqualTo(new[] { ("set", 2d) }));
            Assert.That(Show(ProgressQueue.Fold(values, "SUM")), Is.EqualTo(new[] { ("add", 16d) }));
            Assert.That(Show(ProgressQueue.Fold(values, null)), Is.EqualTo(Show(values)), "values to keep wait one by one while the kind is unknown");
            Assert.That(Show(ProgressQueue.Fold(new[] { C("add", 1), C("add", 2), C("set", 7), C("add", 3) }, null)), Is.EqualTo(new[] { ("add", 3d), ("set", 7d), ("add", 3d) }));
            Assert.That(Show(ProgressQueue.Fold(new[] { C("add", 9e14), C("add", 2e14) }, "SUM")), Is.EqualTo(new[] { ("add", 9e14), ("add", 2e14) }), "a sum never passes the server limit");
            Assert.That(values.Select(v => v.Value), Is.EqualTo(new[] { 5d, 9d, 2d }), "folding never changes its input");
        }

        [Test]
        public async Task AnOpLostOnTheWayIsSentAgainWithTheSameId_WhatCameMeanwhileWaitsFoldedBehindIt()
        {
            var server = new FakeServer();
            var rig = await Build(server);
            await rig.Progress.LearnKindsAsync(new[] { ("coins", "SUM") });
            await rig.Progress.ReportAsync("add", "coins", 5);
            server.Offline.Add("/v1/me/progress");
            Assert.That((await rig.Progress.FlushAsync()).StoppedBy, Is.EqualTo("network_error"));
            await rig.Progress.ReportAsync("add", "coins", 2);
            await rig.Progress.ReportAsync("add", "coins", 3);
            server.Offline.Clear();
            rig.Progress.ResetBackoff();
            var sent = new List<JObject>();
            server.ApplyProgress(sent);
            var output = await rig.Progress.FlushAsync();
            Assert.That(output.Applied, Is.EqualTo(2));
            var lost = (JObject)server.Calls.First(c => c.Path == "/v1/me/progress").Json!["ops"]![0]!;
            Assert.That((string?)sent[0]["opId"], Is.EqualTo((string?)lost["opId"]), "the frozen op went again with its id");
            Assert.That((double)sent[0]["value"]!, Is.EqualTo(5), "and its value");
            Assert.That((double)sent[1]["value"]!, Is.EqualTo(5), "the two reports behind it went as one (2 + 3)");
            Assert.That((string?)sent[1]["opId"], Is.Not.EqualTo((string?)lost["opId"]));
        }

        [Test]
        public async Task OneOpPerStatInFlight_ARefusalForGoodEndsWhatWaitsForThatStatOnly()
        {
            var server = new FakeServer();
            var rig = await Build(server);
            await rig.Progress.LearnKindsAsync(new[] { ("best", "MAX"), ("gone", "SUM") });
            await rig.Progress.ReportAsync("set", "best", 10);
            await rig.Progress.ReportAsync("add", "gone", 1);
            await rig.Progress.UnlockAsync("first_win");
            var round = 0;
            var gate = new TaskCompletionSource<bool>();
            server.OnAsync("POST", "/v1/me/progress", async call =>
            {
                round++;
                var ops = ((JArray)call.Json!["ops"]!).Cast<JObject>().ToList();
                if (round == 1)
                {
                    Assert.That(ops.Select(o => (string)o["key"]!).OrderBy(k => k, StringComparer.Ordinal), Is.EqualTo(new[] { "best", "first_win", "gone" }));
                    await gate.Task;
                }
                return FakeServer.Ok(new JObject
                {
                    ["results"] = new JArray(ops.Select(op => (string)op["key"]! == "gone"
                        ? new JObject { ["opId"] = op["opId"], ["status"] = "rejected", ["code"] = "stat_retired", ["type"] = op["type"], ["key"] = op["key"] }
                        : new JObject { ["opId"] = op["opId"], ["status"] = (string)op["key"]! == "best" && round > 1 ? "duplicate" : "applied", ["type"] = op["type"], ["key"] = op["key"] })),
                });
            });
            // While best's op is in flight a better score comes: it waits behind, it is not a second op.
            var first = rig.Progress.FlushAsync();
            while (round == 0) await Task.Delay(5);
            await rig.Progress.ReportAsync("set", "best", 12);
            await rig.Progress.ReportAsync("add", "gone", 4);
            gate.SetResult(true);
            await first;
            await rig.Progress.FlushAsync();
            Assert.That(await rig.Progress.WaitingAsync(rig.Owner), Is.Zero);
            Assert.That(rig.Progress.Refusals.Select(r => $"{r.Key}:{r.Code}"), Is.EqualTo(new[] { "gone:stat_retired" }), "refused once; its later report went with it");
            var bestValues = server.Calls.Where(c => c.Path == "/v1/me/progress")
                .SelectMany(c => ((JArray)c.Json!["ops"]!).Where(o => (string)o["key"]! == "best").Select(o => (double)o["value"]!));
            Assert.That(bestValues, Is.EqualTo(new[] { 10d, 12d }));
        }

        static ScoreContribution Sc(long value, string? details = null, bool fromPlatform = false) => new ScoreContribution { Value = value, Details = details, FromPlatform = fromPlatform };
        static BoardRule Rule(string sortOrder, string? method) => new BoardRule { SortOrder = sortOrder, UploadMethod = method };
        static IEnumerable<(long, string?)> Scores(IEnumerable<ScoreContribution> list) => list.Select(c => (c.Value, c.Details));

        [TestCase("DESC", new[] { 5L, 9L, 2L }, new[] { 9L })]
        [TestCase("DESC", new[] { 2L, 9L, 5L }, new[] { 9L })]
        [TestCase("ASC", new[] { 5L, 2L, 9L }, new[] { 2L })]
        [TestCase("ASC", new[] { 9L, 2L, 5L }, new[] { 2L })]
        public void KeepBestFoldsToTheBetterScoreInEitherOrder(string sortOrder, long[] reported, long[] folded)
        {
            var list = reported.Select(v => Sc(v)).ToList();
            Assert.That(ProgressQueue.FoldScores(list, Rule(sortOrder, "KEEP_BEST")).Select(c => c.Value), Is.EqualTo(folded));
            Assert.That(list.Select(c => c.Value), Is.EqualTo(reported), "folding never changes its input");
        }

        [Test]
        public void KeepBestKeepsTheEarlierOfTwoEqualScores_ForceUpdateKeepsTheLast_AnUnknownMethodFoldsNothing()
        {
            var list = new[] { Sc(7, "first"), Sc(7, "second") };
            Assert.That(Scores(ProgressQueue.FoldScores(list, Rule("DESC", "KEEP_BEST"))), Is.EqualTo(new[] { (7L, (string?)"first") }), "an equal score changes nothing, details included");
            Assert.That(Scores(ProgressQueue.FoldScores(new[] { Sc(9, "a"), Sc(3, "b"), Sc(5, "c") }, Rule("DESC", "FORCE_UPDATE"))), Is.EqualTo(new[] { (5L, (string?)"c") }), "the last one wins whatever it is");
            var order = new[] { Sc(5), Sc(9), Sc(2) };
            Assert.That(Scores(ProgressQueue.FoldScores(order, null)), Is.EqualTo(Scores(order)), "no rules known: the order is kept");
            Assert.That(Scores(ProgressQueue.FoldScores(order, Rule("DESC", null))), Is.EqualTo(Scores(order)), "a method the server did not say: the order is kept");
            Assert.That(Scores(ProgressQueue.FoldScores(order, Rule("DESC", "SOMETHING_NEW"))), Is.EqualTo(Scores(order)), "a method this SDK does not know: the order is kept");
            Assert.That(Scores(ProgressQueue.FoldScores(order, Rule("SIDEWAYS", "KEEP_BEST"))), Is.EqualTo(Scores(order)), "control: only a known order folds");
        }

        [Test]
        public void ThePlatformsOwnRowNeverFoldsWithAnotherScore()
        {
            var list = new[] { Sc(5), Sc(9, null, true), Sc(7) };
            Assert.That(ProgressQueue.FoldScores(list, Rule("DESC", "KEEP_BEST")).Select(c => (c.Value, c.FromPlatform)), Is.EqualTo(new[] { (5L, false), (9L, true), (7L, false) }),
                "an import must not stand in for the player's own score, or the other way round");
            Assert.That(ProgressQueue.FoldScores(new[] { Sc(1), Sc(2) }, Rule("DESC", "FORCE_UPDATE")).Select(c => c.Value), Is.EqualTo(new[] { 2L }), "control: two ordinary scores do fold");
        }

        [Test]
        public async Task TheRulesTheServerSaidAreKeptAndFoldWhatWaits_ControlABoardNotYetKnownKeepsEveryScore()
        {
            var server = new FakeServer();
            var rig = await Build(server);
            await rig.Progress.ReportScoreAsync("fastest", 900, null, false);
            await rig.Progress.ReportScoreAsync("fastest", 700, null, false);
            Assert.That(await rig.Progress.WaitingAsync(rig.Owner), Is.EqualTo(2), "no rules yet: both wait");
            await rig.Progress.LearnBoardsAsync(new[] { ("fastest", "ASC", (string?)"KEEP_BEST") });
            await rig.Progress.ReportScoreAsync("fastest", 800, null, false);
            Assert.That(await rig.Progress.WaitingAsync(rig.Owner), Is.EqualTo(1), "from now on the waiting scores fold: 700 is the best of 900, 700 and 800 (ASC)");
            var sent = new List<JObject>();
            server.On("POST", "/v1/me/progress", call =>
            {
                var ops = ((JArray)call.Json!["ops"]!).Cast<JObject>().ToList();
                lock (sent) sent.AddRange(ops);
                return FakeServer.Ok(new JObject { ["results"] = new JArray(ops.Select(o => new JObject { ["opId"] = o["opId"], ["status"] = "applied", ["type"] = "score", ["key"] = o["key"], ["score"] = o["value"], ["changed"] = true })) });
            });
            var output = await rig.Progress.FlushAsync();
            Assert.That(sent.Select(o => (double)o["value"]!), Is.EqualTo(new[] { 700d }), "the better of the waiting scores is the one op");
            Assert.That(output.Scores["fastest"], Is.EqualTo(700));
            var opts = Resolved.From(server.Options(rig.Store));
            var again = new ProgressQueue(opts, rig.Store, new Api(opts, rig.Identity, server, new Clock()), () => rig.Owner);
            await again.ReportScoreAsync("fastest", 600, null, false);
            await again.ReportScoreAsync("fastest", 650, null, false);
            Assert.That(await again.WaitingAsync(rig.Owner), Is.EqualTo(1), "a new instance reads the rules from the store");
        }

        [Test]
        public async Task AScoreIsCheckedHereForWhatTheServerRefusesForSure_ControlTheEdgesAreTaken()
        {
            var server = new FakeServer();
            var rig = await Build(server);
            Assert.ThrowsAsync<ArgumentException>(() => rig.Progress.ReportScoreAsync("fastest", 1_000_000_000_000_000, null, false));
            Assert.ThrowsAsync<ArgumentException>(() => rig.Progress.ReportScoreAsync("fastest", -1_000_000_000_000_000, null, false));
            Assert.ThrowsAsync<ArgumentException>(() => rig.Progress.ReportScoreAsync("fastest", 1, new byte[0], false));
            Assert.ThrowsAsync<ArgumentException>(() => rig.Progress.ReportScoreAsync("fastest", 1, new byte[65], false));
            Assert.ThrowsAsync<ArgumentException>(() => rig.Progress.ReportScoreAsync("Not A Key", 1, null, false));
            Assert.That(await rig.Progress.WaitingAsync(rig.Owner), Is.Zero, "nothing refused was kept");
            await rig.Progress.ReportScoreAsync("fastest", 999_999_999_999_999, new byte[64], false);
            await rig.Progress.ReportScoreAsync("fastest", -999_999_999_999_999, new byte[] { 1 }, false);
            Assert.That(await rig.Progress.WaitingAsync(rig.Owner), Is.EqualTo(2));
            var sent = new List<JObject>();
            server.ApplyProgress(sent);
            await rig.Progress.FlushAsync();
            Assert.That(sent.Select(o => (string?)o["details"]).First(), Is.EqualTo(Convert.ToBase64String(new byte[64])), "details ride as base64");
            Assert.That(sent[0]["fromPlatform"], Is.Null, "an ordinary score does not claim to be the platform's");
        }

        [Test]
        public async Task AScoreLostOnTheWayIsSentAgainWithTheSameIdAndCountsOnce_WhatCameMeanwhileWaitsBehindIt()
        {
            var server = new FakeServer();
            var rig = await Build(server);
            await rig.Progress.LearnBoardsAsync(new[] { ("latest", "DESC", (string?)"FORCE_UPDATE") });
            await rig.Progress.ReportScoreAsync("latest", 500, new byte[] { 1, 2 }, false);
            server.Offline.Add("/v1/me/progress");
            Assert.That((await rig.Progress.FlushAsync()).StoppedBy, Is.EqualTo("network_error"));
            await rig.Progress.ReportScoreAsync("latest", 800, null, false);
            await rig.Progress.ReportScoreAsync("latest", 300, null, false);
            server.Offline.Clear();
            rig.Progress.ResetBackoff();
            var applied = new Dictionary<string, long>();
            var seen = new List<JObject>();
            server.On("POST", "/v1/me/progress", call =>
            {
                var ops = ((JArray)call.Json!["ops"]!).Cast<JObject>().ToList();
                seen.AddRange(ops);
                return FakeServer.Ok(new JObject
                {
                    ["results"] = new JArray(ops.Select(o =>
                    {
                        var id = (string)o["opId"]!;
                        var duplicate = applied.ContainsKey(id);
                        applied[id] = (long)(double)o["value"]!;
                        return new JObject { ["opId"] = id, ["status"] = duplicate ? "duplicate" : "applied", ["type"] = "score", ["key"] = o["key"], ["score"] = o["value"] };
                    })),
                });
            });
            var output = await rig.Progress.FlushAsync();
            var lost = (JObject)server.Calls.First(c => c.Path == "/v1/me/progress").Json!["ops"]![0]!;
            Assert.That(seen.Select(o => (string)o["opId"]!).First(), Is.EqualTo((string)lost["opId"]!), "the frozen score went again with its id");
            Assert.That(applied.Values, Is.EqualTo(new[] { 500L, 300L }), "the lost one once, then the two reports behind it as one (the last, FORCE_UPDATE)");
            Assert.That((string?)seen[0]["details"], Is.EqualTo(Convert.ToBase64String(new byte[] { 1, 2 })), "and its details");
            Assert.That(output.Scores["latest"], Is.EqualTo(300));
            Assert.That(await rig.Progress.WaitingAsync(rig.Owner), Is.Zero);
        }

        [Test]
        public async Task ARefusalForGoodDropsThatScoreOnly_ControlTheValidOneIsApplied_AGoneBoardTakesWhatWaitsForIt()
        {
            var server = new FakeServer();
            var rig = await Build(server);
            await rig.Progress.LearnBoardsAsync(new[] { ("latest", "DESC", (string?)"FORCE_UPDATE"), ("fastest", "ASC", (string?)"KEEP_BEST") });
            await rig.Progress.ReportScoreAsync("latest", 2_000_000, null, false);
            await rig.Progress.ReportScoreAsync("fastest", 1200, null, false);
            await rig.Progress.ReportScoreAsync("gone", 5, null, false);
            server.On("POST", "/v1/me/progress", call =>
            {
                var ops = ((JArray)call.Json!["ops"]!).Cast<JObject>().ToList();
                return FakeServer.Ok(new JObject
                {
                    ["results"] = new JArray(ops.Select(o => (string)o["key"]! switch
                    {
                        "latest" => new JObject { ["opId"] = o["opId"], ["status"] = "rejected", ["code"] = "invalid_value", ["type"] = "score", ["key"] = "latest" },
                        "gone" => new JObject { ["opId"] = o["opId"], ["status"] = "rejected", ["code"] = "unknown_leaderboard", ["type"] = "score", ["key"] = "gone" },
                        _ => new JObject { ["opId"] = o["opId"], ["status"] = "applied", ["type"] = "score", ["key"] = o["key"], ["score"] = o["value"], ["changed"] = true },
                    })),
                });
            });
            await rig.Progress.FlushAsync();
            await rig.Progress.ReportScoreAsync("latest", 400, null, false);
            await rig.Progress.ReportScoreAsync("gone", 6, null, false);
            Assert.That(await rig.Progress.WaitingAsync(rig.Owner), Is.EqualTo(2), "a later score for the refused board is a new score");
            var output = await rig.Progress.FlushAsync();
            Assert.That(rig.Progress.Refusals.Select(r => $"{r.Type}:{r.Key}:{r.Code}"), Is.EqualTo(new[] { "score:latest:invalid_value", "score:gone:unknown_leaderboard", "score:latest:invalid_value", "score:gone:unknown_leaderboard" }));
            Assert.That(output.Scores.Count, Is.Zero);
            Assert.That(await rig.Progress.WaitingAsync(rig.Owner), Is.Zero, "refused for good: nothing is kept for them");
            var sentFastest = server.Calls.Where(c => c.Path == "/v1/me/progress").SelectMany(c => (JArray)c.Json!["ops"]!).Count(o => (string)o["key"]! == "fastest");
            Assert.That(sentFastest, Is.EqualTo(1), "control: the valid score was applied once and not sent again");
        }

        [Test]
        public async Task ScoresAnsweredModuleDisabledStayQueuedWithTheirId_OtherProgressGoesThrough_TheyGoWhenTheModuleIsOn()
        {
            var server = new FakeServer();
            var rig = await Build(server);
            await rig.Progress.LearnKindsAsync(new[] { ("coins", "SUM") });
            await rig.Progress.ReportScoreAsync("latest", 10, null, false);
            await rig.Progress.ReportAsync("add", "coins", 3);
            var moduleOn = false;
            var scoreIds = new List<string>();
            server.On("POST", "/v1/me/progress", call =>
            {
                var ops = ((JArray)call.Json!["ops"]!).Cast<JObject>().ToList();
                return FakeServer.Ok(new JObject
                {
                    ["results"] = new JArray(ops.Select(o =>
                    {
                        if ((string)o["type"]! != "score") return new JObject { ["opId"] = o["opId"], ["status"] = "applied", ["type"] = o["type"], ["key"] = o["key"], ["value"] = o["value"] };
                        scoreIds.Add((string)o["opId"]!);
                        return moduleOn
                            ? new JObject { ["opId"] = o["opId"], ["status"] = "applied", ["type"] = "score", ["key"] = o["key"], ["score"] = o["value"], ["changed"] = true }
                            : new JObject { ["opId"] = o["opId"], ["status"] = "rejected", ["code"] = "module_disabled", ["type"] = "score", ["key"] = o["key"] };
                    })),
                });
            });
            var off = await rig.Progress.FlushAsync();
            Assert.That((off.StoppedBy, off.Applied, off.Values["coins"]), Is.EqualTo(("module_disabled", 1, 3d)), "the stat went through; the score is not a refusal for good");
            Assert.That(rig.Progress.Refusals, Is.Empty);
            Assert.That(await rig.Progress.WaitingAsync(rig.Owner), Is.EqualTo(1));
            Assert.That(scoreIds.Count, Is.EqualTo(1), "sent once in the flush, not in a loop");
            moduleOn = true;
            var on = await rig.Progress.FlushAsync();
            Assert.That((on.StoppedBy, on.Applied, on.Scores["latest"]), Is.EqualTo(((string?)null, 1, 10L)));
            Assert.That(scoreIds.Distinct().Count(), Is.EqualTo(1), "the same opId the whole time");
            Assert.That(await rig.Progress.WaitingAsync(rig.Owner), Is.Zero);
        }

        [Test]
        public async Task RefusalsAreMatchedByOpId_TheServersRealShapeCarriesNoTypeAndOpIdReusedNoKeyEither()
        {
            var server = new FakeServer();
            var rig = await Build(server);
            await rig.Progress.LearnKindsAsync(new[] { ("coins", "SUM"), ("best", "MAX") });
            await rig.Progress.UnlockAsync("ghost");
            await rig.Progress.ReportScoreAsync("latest", 2_000_000, null, false);
            await rig.Progress.ReportAsync("set", "best", 4);
            await rig.Progress.ReportAsync("add", "coins", 1);
            server.On("POST", "/v1/me/progress", call =>
            {
                var ops = ((JArray)call.Json!["ops"]!).Cast<JObject>().ToList();
                return FakeServer.Ok(new JObject
                {
                    ["results"] = new JArray(ops.Select(o => (string)o["key"]! switch
                    {
                        "ghost" => new JObject { ["opId"] = o["opId"], ["status"] = "rejected", ["code"] = "unknown_achievement", ["key"] = "ghost" },
                        "latest" => new JObject { ["opId"] = o["opId"], ["status"] = "rejected", ["code"] = "invalid_value", ["key"] = "latest" },
                        "best" => new JObject { ["opId"] = o["opId"], ["status"] = "rejected", ["code"] = "op_id_reused" },
                        _ => new JObject { ["opId"] = o["opId"], ["status"] = "applied", ["type"] = "stat", ["key"] = "coins", ["value"] = 1 },
                    })),
                });
            });
            var output = await rig.Progress.FlushAsync();
            Assert.That(rig.Progress.Refusals.Select(r => $"{r.Type}:{r.Key}:{r.Code}").OrderBy(x => x, StringComparer.Ordinal),
                Is.EqualTo(new[] { "score:latest:invalid_value", "stat:best:op_id_reused", "unlock:ghost:unknown_achievement" }));
            Assert.That(output.Applied, Is.EqualTo(1), "control: the applied one counts");
            Assert.That(output.StoppedBy, Is.Null);
            Assert.That(await rig.Progress.WaitingAsync(rig.Owner), Is.Zero, "nothing stays frozen");
        }

        [Test]
        public async Task ScoresMoveWithTheirIdsToTheFirstPlayer()
        {
            var server = new FakeServer();
            var rig = await Build(server, signIn: false);
            await rig.Progress.ReportScoreAsync("latest", 42, null, false);
            await rig.Identity.SignInAsGuestAsync("2000-01-01", "TERMS:v1 PRIVACY:v1", false);
            await rig.Progress.MoveAsync(EventQueue.Anon, rig.Owner);
            Assert.That(await rig.Progress.WaitingAsync(EventQueue.Anon), Is.Zero);
            Assert.That(await rig.Progress.WaitingAsync(rig.Owner), Is.EqualTo(1));
        }

        [Test]
        public async Task ProgressMadeBeforeAnyAccountGoesToTheFirstPlayer()
        {
            var server = new FakeServer();
            var rig = await Build(server, signIn: false);
            await rig.Progress.ReportAsync("add", "coins", 3);
            await rig.Progress.UnlockAsync("tutorial");
            Assert.That((await rig.Progress.FlushAsync()).StoppedBy, Is.EqualTo("signed_out"));
            await rig.Identity.SignInAsGuestAsync("2000-01-01", "TERMS:v1 PRIVACY:v1", false);
            await rig.Progress.MoveAsync(EventQueue.Anon, rig.Owner);
            Assert.That(await rig.Progress.WaitingAsync(EventQueue.Anon), Is.Zero);
            Assert.That(await rig.Progress.WaitingAsync(rig.Owner), Is.EqualTo(2));
        }

        [Test]
        public async Task AMergeMovesFrozenOpsWithTheirIds()
        {
            var server = new FakeServer();
            var rig = await Build(server);
            await rig.Progress.LearnKindsAsync(new[] { ("coins", "SUM") });
            // The merged account's queue, as another device left it: one op frozen.
            await rig.Store.SetAsync("krtu:buried.test.native:progress:pw_old_aaaaaaaaaaaa",
                "{\"stats\":{\"coins\":{\"inFlight\":[{\"opId\":\"old-op\",\"type\":\"stat\",\"key\":\"coins\",\"value\":7}],\"pending\":[{\"op\":\"add\",\"value\":1}]}},\"unlocks\":{}}");
            await rig.Progress.ReportAsync("add", "coins", 2);
            server.Offline.Add("/v1/me/progress");
            await rig.Progress.FlushAsync();
            server.Offline.Clear();
            rig.Progress.ResetBackoff();
            await rig.Progress.MoveAsync("pw_old_aaaaaaaaaaaa", rig.Owner);
            var sent = new List<JObject>();
            server.ApplyProgress(sent);
            await rig.Progress.FlushAsync();
            var shown = sent.Select(o => $"{((string)o["opId"]! == "old-op" ? "old-op" : "new")}:{(double)o["value"]!}").ToList();
            Assert.That(shown.Take(2).OrderBy(s => s, StringComparer.Ordinal), Is.EqualTo(new[] { "new:2", "old-op:7" }), "both frozen ops went, each with its own id");
            Assert.That(shown.Skip(2), Is.EqualTo(new[] { "new:1" }), "the waiting increase went after them");
        }

        static JObject Accepting(JObject body, int retryFrom = int.MaxValue)
        {
            var ids = ((JArray)body["events"]!).Select(e => (string)e["id"]!).ToList();
            return new JObject
            {
                ["batchId"] = body["batchId"],
                ["accepted"] = new JArray(ids.Take(retryFrom)),
                ["duplicate"] = new JArray(),
                ["rejected"] = new JArray(),
                ["retry"] = new JArray(ids.Skip(retryFrom).Select(id => new JObject { ["id"] = id, ["code"] = "too_many_builds" })),
                ["telemetry"] = true,
            };
        }

        [Test]
        public async Task ABatchIsFrozenBeforeItsFirstSendAndSentAgainUnchanged_RetryEventsGoInANewBatch()
        {
            var server = new FakeServer();
            var rig = await Build(server);
            for (var i = 0; i < 3; i++) rig.Events.Add("screen_view", new JObject { ["screen"] = $"s{i}" }, null, null, null);
            server.Offline.Add("/v1/events");
            Assert.That((await rig.Events.FlushAsync(true)).StoppedBy, Is.EqualTo("network_error"));
            server.Offline.Clear();
            rig.Events.ResetBackoff();
            var bodies = new List<JObject>();
            server.On("POST", "/v1/events", call =>
            {
                bodies.Add(call.Json!);
                return FakeServer.Ok(Accepting(call.Json!, bodies.Count == 1 ? 2 : int.MaxValue));
            });
            var lost = server.Calls.First(c => c.Path == "/v1/events");
            await rig.Events.FlushAsync(true);
            Assert.That((string?)bodies[0]["batchId"], Is.EqualTo((string?)lost.Json!["batchId"]), "the same batch id");
            Assert.That(JToken.DeepEquals(bodies[0]["events"], lost.Json!["events"]), "the same events (the server hashes platform, build and events)");
            rig.Events.ResetBackoff();
            await rig.Events.FlushAsync(true);
            Assert.That((string?)bodies[1]["batchId"], Is.Not.EqualTo((string?)lost.Json!["batchId"]), "the retried event went in a new batch");
            Assert.That(((JArray)bodies[1]["events"]!).Select(e => (string?)e["id"]), Is.EqualTo(new[] { (string?)lost.Json!["events"]![2]!["id"] }), "with its own id");
            Assert.That(lost.Header("content-encoding"), Is.EqualTo("gzip"));
        }

        [Test]
        public async Task AFullQueueDropsTheOldestAndSaysSoOnce()
        {
            var server = new FakeServer();
            var rig = await Build(server);
            var old = new JObject
            {
                ["id"] = "x", ["type"] = "button", ["createdAt"] = DateTimeOffset.UtcNow.AddDays(-31).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                ["launchId"] = "l", ["monotonicMs"] = 0, ["data"] = new JObject { ["name"] = "old" },
            };
            await rig.Store.SetAsync($"krtu:buried.test.native:events:{rig.Owner}", new JObject { ["pending"] = new JArray(old), ["dropped"] = 0, ["reported"] = 0 }.ToString());
            rig.Events.Add("button", new JObject { ["name"] = "new" }, null, null, null);
            var sent = new List<JObject>();
            server.AcceptEvents(sent);
            await rig.Events.FlushAsync(true);
            Assert.That(sent.Select(e => (string?)e["data"]!["name"] ?? (string?)e["data"]!["fingerprint"]), Is.EqualTo(new[] { "new", "sdk.events_dropped" }), "the 31-day-old event went, the count was said");
            sent.Clear();
            rig.Events.Add("button", new JObject { ["name"] = "later" }, null, null, null);
            await rig.Events.FlushAsync(true);
            Assert.That(sent.Select(e => (string?)e["data"]!["name"]), Is.EqualTo(new[] { "later" }), "said once");
        }

        [Test]
        public async Task EventsMadeBeforeAnyAccountGoToTheFirstPlayer()
        {
            var server = new FakeServer();
            var rig = await Build(server, signIn: false);
            rig.Events.Add("screen_view", new JObject { ["screen"] = "age" }, null, null, null);
            await rig.Identity.SignInAsGuestAsync("2000-01-01", "TERMS:v1 PRIVACY:v1", false);
            rig.Events.Add("screen_view", new JObject { ["screen"] = "menu" }, null, null, null);
            await rig.Events.MoveAsync(EventQueue.Anon, rig.Owner);
            Assert.That(await rig.Events.SizeAsync(EventQueue.Anon), Is.Zero);
            Assert.That(await rig.Events.SizeAsync(rig.Owner), Is.EqualTo(2));
        }

        [Test]
        public async Task ABatchThatGzipStillLeavesTooBigIsHalvedBeforeItIsSent()
        {
            var server = new FakeServer();
            var rig = await Build(server);
            var random = new Random(7);
            for (var i = 0; i < 400; i++)
            {
                // Incompressible text: 400 events of ~1.2 KB each do not fit 256 KB even gzipped.
                var noise = new string(Enumerable.Range(0, 1200).Select(_ => (char)random.Next(33, 126)).ToArray());
                rig.Events.Add("button", new JObject { ["name"] = noise }, null, null, null);
            }
            var sent = new List<JObject>();
            server.AcceptEvents(sent);
            var output = await rig.Events.FlushAsync(true);
            Assert.That(output.Sent, Is.EqualTo(400));
            Assert.That(sent.Count, Is.EqualTo(400));
            var batches = server.Calls.Where(c => c.Path == "/v1/events").ToList();
            Assert.That(batches.Count, Is.GreaterThan(1), "split");
            Assert.That(batches.Select(b => b.Json!["batchId"]!.ToString()).Distinct().Count(), Is.EqualTo(batches.Count), "every batch has its own id");
        }
    }
}
