#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Krtuworks.Internal;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Krtuworks.Tests
{
    /// <summary>Bug reports from the game (krtuworks-sdk-js/test/bugs.test.ts).</summary>
    public class BugTests
    {
        const string Terms = "TERMS:v1 PRIVACY:v1";
        static readonly byte[] Png = { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3 };

        sealed class Kept
        {
            internal string Id = "";
            internal JObject Body = new JObject();
            internal string? Shot;
        }

        static BugReportView View(string id, string status, string? fixedIn = null) =>
            new BugReportView { Id = id, Category = "GAMEPLAY", Description = "x", Platform = "WINDOWS", Build = "1.0.0", CreatedAt = "2026-10-29T10:00:00Z", Status = status, FixedIn = fixedIn };

        /// <summary>The server's bug endpoints: a report is kept once per reportId, its screenshot once.</summary>
        static void BugRoutes(FakeServer server, Dictionary<string, Kept> reports, Func<Reply?>? screenshot = null)
        {
            var seq = 0;
            server.On("POST", "/v1/me/bug-reports", call =>
            {
                var reportId = (string)call.Json!["reportId"]!;
                lock (reports)
                {
                    if (reports.TryGetValue(reportId, out var known)) return FakeServer.Ok(new JObject { ["created"] = false, ["report"] = new JObject { ["id"] = known.Id } });
                    seq += 1;
                    var id = $"bug_{seq.ToString().PadLeft(20, 'a')}";
                    reports[reportId] = new Kept { Id = id, Body = call.Json };
                    return new Reply { Status = 201, Json = new JObject { ["created"] = true, ["report"] = new JObject { ["id"] = id } } };
                }
            });
            for (var i = 1; i <= 5; i += 1)
            {
                var id = $"bug_{i.ToString().PadLeft(20, 'a')}";
                server.On("PUT", $"/v1/me/bug-reports/{id}/screenshot", call =>
                {
                    var forced = screenshot?.Invoke();
                    if (forced != null) return forced;
                    lock (reports) reports.Values.First(r => r.Id == id).Shot = call.Header("content-type");
                    return FakeServer.Ok(new JObject { ["report"] = new JObject { ["id"] = id, ["screenshot"] = true } });
                });
            }
        }

        static async Task<KrtuworksClient> Started(FakeServer server, MemoryStore? store = null, JObject? config = null)
        {
            server.GuestRoutes();
            server.ConfigRoute(config ?? new JObject());
            server.AcceptEvents(new List<JObject>());
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(store));
            await krtu.RefreshConfigAsync();
            return krtu;
        }

        static void Refused(Action work)
        {
            var e = Assert.Throws<KrtuworksException>(() => work())!;
            Assert.That((e.Code, e.Status), Is.EqualTo(("invalid_request", 400)));
        }

        [Test]
        public void ThePictureTypeComesFromItsFirstBytes()
        {
            Assert.That(BugReports.ScreenshotType(Png), Is.EqualTo("image/png"));
            Assert.That(BugReports.ScreenshotType(new byte[] { 0xff, 0xd8, 0xff, 0xe0 }), Is.EqualTo("image/jpeg"));
            Assert.That(BugReports.ScreenshotType(Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ")), Is.EqualTo("image/webp"));
            Assert.That(BugReports.ScreenshotType(Encoding.ASCII.GetBytes("GIF89a......")), Is.Null);
        }

        [Test]
        public void AReportBodyCarriesTheMomentAndIsRefusedWhenItCanNeverBeTaken_ControlAFullReportPasses()
        {
            var server = new FakeServer();
            var opts = Resolved.From(server.Options(extra: o =>
            {
                o.Device = new DeviceInfo { Os = "Linux", RamMb = 8192 };
                o.Locale = "tr-TR";
            }));
            var events = new List<JObject> { new JObject { ["type"] = "death", ["at"] = "t" } };
            var body = BugReports.ReportBody(new BugReportInput { Category = "GAMEPLAY", Description = "  The door  ", Position = new EventPosition { Space = "world", Area = "cave", X = 1, Y = 2 } }, "r1", opts, "cave/2", 59.94, events, "boot");
            Assert.That(JToken.DeepEquals(body, JObject.Parse(
                "{\"reportId\":\"r1\",\"category\":\"GAMEPLAY\",\"description\":\"The door\",\"platform\":\"WINDOWS\",\"build\":\"1.0.0\",\"device\":{\"os\":\"Linux\",\"ramMb\":8192},\"locale\":\"tr-TR\",\"level\":\"cave/2\",\"position\":{\"area\":\"cave\",\"x\":1.0,\"y\":2.0},\"fps\":59.9,\"events\":[{\"type\":\"death\",\"at\":\"t\"}],\"log\":\"boot\"}")), body.ToString());
            Refused(() => BugReports.ReportBody(new BugReportInput { Category = "gameplay", Description = "x" }, "r1", opts, null, null, events, ""));
            Refused(() => BugReports.ReportBody(new BugReportInput { Category = "CRASH", Description = "   " }, "r1", opts, null, null, events, ""));
            Refused(() => BugReports.ReportBody(new BugReportInput { Category = "CRASH", Description = new string('x', 4001) }, "r1", opts, null, null, events, ""));
            Refused(() => BugReports.ReportBody(new BugReportInput { Category = "CRASH", Description = "x", Fps = 1001 }, "r1", opts, null, null, events, ""));
            Refused(() => BugReports.ReportBody(new BugReportInput { Category = "CRASH", Description = "x", Position = new EventPosition { Area = "", X = 1, Y = 2 } }, "r1", opts, null, null, events, ""));
            Refused(() => BugReports.ReportBody(new BugReportInput { Category = "CRASH", Description = "x", Level = new string('x', 201) }, "r1", opts, null, null, events, ""));
            // control: what the SDK adds itself is cut to fit (the level it saw; its events, from the oldest), never refused
            var many = Enumerable.Range(0, 50).Select(i => new JObject { ["type"] = "screen_view", ["at"] = i.ToString(), ["data"] = new JObject { ["screen"] = new string('s', 1790) } }).ToList();
            var trimmed = BugReports.ReportBody(new BugReportInput { Category = "OTHER", Description = "x" }, "r1", opts, new string('l', 250), null, many, "");
            Assert.That(((string)trimmed["level"]!).Length, Is.EqualTo(200));
            var kept = (JArray)trimmed["events"]!;
            Assert.That(kept.Count, Is.InRange(41, 49));
            Assert.That(kept.ToString(Newtonsoft.Json.Formatting.None).Length, Is.LessThanOrEqualTo(90_000));
            Assert.That((string?)kept.Last!["at"], Is.EqualTo("49"));
            // control: the player's level and frame rate win over what the SDK saw; nothing optional goes empty
            var own = BugReports.ReportBody(new BugReportInput { Category = "OTHER", Description = "x", Level = "boss", Fps = 30 }, "r1", Resolved.From(server.Options()), "cave/2", 59.94, new List<JObject>(), "");
            Assert.That(JToken.DeepEquals(own, JObject.Parse("{\"reportId\":\"r1\",\"category\":\"OTHER\",\"description\":\"x\",\"platform\":\"WINDOWS\",\"build\":\"1.0.0\",\"level\":\"boss\",\"fps\":30.0}")), own.ToString());
        }

        [Test]
        public void FixesAreToldOnce_ADeviceThatNeverHadThePlayersReportsLearnsThemWithoutTelling()
        {
            var reports = new List<BugReportView> { View("bug_1", "RESOLVED", "1.1"), View("bug_2", "NEW") };
            var fresh = BugReports.NewlyFixed(reports, null);
            Assert.That(fresh.Fixed, Is.Empty);
            Assert.That(fresh.State.Told, Is.EqualTo(new[] { "bug_1" }));
            var madeHere = BugReports.NewlyFixed(reports, new FixedState());
            Assert.That(madeHere.Fixed.Select(r => r.Id), Is.EqualTo(new[] { "bug_1" }));
            var later = BugReports.NewlyFixed(new List<BugReportView> { View("bug_1", "RESOLVED", "1.1"), View("bug_2", "RESOLVED", "1.2") }, madeHere.State);
            Assert.That(later.Fixed.Select(r => r.Id), Is.EqualTo(new[] { "bug_2" }));
        }

        [Test]
        public async Task AReportWaitsOnTheDeviceAndGoesWithTheMoment_ALostAnswerSendsTheSameReportAgain()
        {
            var server = new FakeServer();
            var reports = new Dictionary<string, Kept>();
            BugRoutes(server, reports);
            var store = new MemoryStore();
            var krtu = await Started(server, store);
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            await krtu.LevelStartAsync(new LevelRef { World = "cave", Level = "2" });
            for (var i = 0; i < 55; i += 1) krtu.Track("screen_view", new { screen = $"s{i}" });
            krtu.Track("performance", new { fps = 48.25 });
            krtu.Log("loaded save 3");
            krtu.Error(new InvalidOperationException("door stuck"));
            // Injected: the server keeps the report, its answer is lost on the way.
            var keep = true;
            server.OnAsync("POST", "/v1/me/bug-reports", call =>
            {
                lock (reports)
                {
                    var reportId = (string)call.Json!["reportId"]!;
                    if (!reports.ContainsKey(reportId)) reports[reportId] = new Kept { Id = $"bug_{5.ToString().PadLeft(20, 'a')}", Body = call.Json };
                }
                if (keep) throw new KrtuworksException("network_error", "No answer from dev.krtuworks.com: lost");
                return Task.FromResult(new Reply());
            });
            var reportId = await krtu.ReportBugAsync(new BugReportInput { Category = "GAMEPLAY", Description = "The cave door does not open", Screenshot = Png });
            var lost = await krtu.FlushAsync();
            Assert.That(lost.Bugs.StoppedBy, Is.EqualTo("network_error"));
            Assert.That((await krtu.WaitingAsync()).BugReports, Is.EqualTo(1), "kept on the device");
            await krtu.ShutdownAsync();
            keep = false;
            BugRoutes(server, reports);
            // The next launch sends it at the player's arrival: the same report (the server knows it), then its screenshot.
            var next = await KrtuworksClient.InitializeAsync(server.Options(store));
            await next.ReadyAsync();
            Assert.That((await next.WaitingAsync()).BugReports, Is.EqualTo(0));
            var second = await next.ReportBugAsync(new BugReportInput { Category = "TEXT", Description = "A typo on the map" });
            Assert.That((await next.FlushAsync()).Bugs.Sent, Is.EqualTo(1));
            var posted = server.Calls.Where(c => c.Method == "POST" && c.Path == "/v1/me/bug-reports").Select(c => c.Json!).ToList();
            Assert.That(posted.Count, Is.EqualTo(3), "the lost one, its resend, the second report");
            Assert.That(JToken.DeepEquals(posted[1], posted[0]), Is.True, "the lost report went again exactly as made");
            Assert.That(reports.Count, Is.EqualTo(2), "The server has each report once");
            var body = reports[reportId].Body;
            Assert.That((string?)body["level"], Is.EqualTo("cave/2"));
            Assert.That((double?)body["fps"], Is.EqualTo(48.3));
            var events = (JArray)body["events"]!;
            Assert.That(events.Count, Is.EqualTo(50), "the last 50 events");
            Assert.That((string?)events.Last!["type"], Is.EqualTo("error"));
            StringAssert.IsMatch("loaded save 3\n.*error InvalidOperationException: door stuck$", (string)body["log"]!);
            Assert.That(reports[reportId].Shot, Is.EqualTo("image/png"));
            Assert.That(reports[second].Shot, Is.Null);
            await next.ShutdownAsync();
        }

        [Test]
        public async Task AReportRefusedForGoodIsDropped_OneThatCannotGoNowWaits_AScreenshotTooLateIsLetGo()
        {
            var server = new FakeServer();
            var reports = new Dictionary<string, Kept>();
            Reply? shot = new Reply { Status = 503, Json = new JObject { ["error"] = new JObject { ["code"] = "storage_not_ready", ["message"] = "later" } } };
            BugRoutes(server, reports, () => shot);
            var krtu = await Started(server);
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            await krtu.ReportBugAsync(new BugReportInput { Category = "CRASH", Description = "Crashed on load", Screenshot = Png });
            var busy = await krtu.FlushAsync();
            Assert.That(busy.Bugs.StoppedBy, Is.EqualTo("storage_not_ready"));
            Assert.That(server.Count("POST", "/v1/me/bug-reports"), Is.EqualTo(1));
            // The wait is in memory: the network coming back sends it now.
            krtu.Online();
            shot = new Reply { Status = 409, Json = new JObject { ["error"] = new JObject { ["code"] = "screenshot_too_late", ["message"] = "late" } } };
            var late = await krtu.FlushAsync();
            Assert.That(server.Count("POST", "/v1/me/bug-reports"), Is.EqualTo(1), "the report is in: only its screenshot was tried again");
            Assert.That(late.Bugs.Dropped.Select(d => (d.Code, d.ScreenshotOnly)), Is.EqualTo(new[] { ("screenshot_too_late", true) }));
            Assert.That((await krtu.WaitingAsync()).BugReports, Is.EqualTo(0));
            server.On("POST", "/v1/me/bug-reports", _ => new Reply { Status = 400, Json = new JObject { ["error"] = new JObject { ["code"] = "platform_not_registered", ["message"] = "no" } } });
            await krtu.ReportBugAsync(new BugReportInput { Category = "AUDIO", Description = "No sound" });
            var refused = await krtu.FlushAsync();
            Assert.That(refused.Bugs.Dropped.Select(d => (d.Code, d.ScreenshotOnly)), Is.EqualTo(new[] { ("platform_not_registered", false) }));
            Assert.That((await krtu.WaitingAsync()).BugReports, Is.EqualTo(0));
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task BugReportsOffAreRefusedAtOnce_AReportMadeBeforeAnyPlayerGoesWithTheFirstOne()
        {
            var off = new FakeServer();
            var k1 = await Started(off, config: new JObject { ["modules"] = new JObject { ["bugReports"] = false } });
            var e = await AsyncAssert.ThrowsAsync<KrtuworksException>(() => k1.ReportBugAsync(new BugReportInput { Category = "OTHER", Description = "x" }));
            Assert.That(e.Code, Is.EqualTo("module_disabled"));
            Assert.That((await k1.WaitingAsync()).BugReports, Is.EqualTo(0));
            await k1.ShutdownAsync();

            var server = new FakeServer();
            var reports = new Dictionary<string, Kept>();
            BugRoutes(server, reports);
            var krtu = await Started(server);
            await krtu.ReportBugAsync(new BugReportInput { Category = "CONTROLS", Description = "The pad does nothing" });
            Assert.That((await krtu.FlushAsync()).Bugs.Sent, Is.EqualTo(0), "no player yet");
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            Assert.That(reports.Count, Is.EqualTo(1), "sent once the first player arrived");
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task APlayersFixIsToldOnceNeverToNoOne_ADeviceThatOnlyReadsThemTellsNothingAtFirst()
        {
            var server = new FakeServer();
            BugRoutes(server, new Dictionary<string, Kept>());
            var list = new List<BugReportView> { View("bug_old", "RESOLVED", "0.9"), View("bug_open", "NEW") };
            server.On("GET", "/v1/me/bug-reports", _ => FakeServer.Ok(new JObject { ["reports"] = JArray.FromObject(list), ["serverTime"] = DateTimeOffset.UtcNow.ToString("o") }));
            var krtu = await Started(server);
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            var told = new List<string>();
            Assert.That((await krtu.MyBugReportsAsync()).Count, Is.EqualTo(2), "the first look learns what was already fixed");
            list = new List<BugReportView> { View("bug_old", "RESOLVED", "0.9"), View("bug_open", "RESOLVED", "1.0.1") };
            await krtu.MyBugReportsAsync();
            // No one listened yet: the fix waits for the next look instead of being lost.
            krtu.BugFixed += r => told.Add($"{r.Id}@{r.FixedIn}");
            await krtu.MyBugReportsAsync();
            await krtu.MyBugReportsAsync();
            Assert.That(told, Is.EqualTo(new[] { "bug_open@1.0.1" }), "told once, and not the fix the first look learned");
            await krtu.ShutdownAsync();

            // A device where the player reported: the next arrival checks by itself and tells the fix.
            var other = new FakeServer();
            BugRoutes(other, new Dictionary<string, Kept>());
            var mine = new List<BugReportView> { View("bug_x", "NEW") };
            other.On("GET", "/v1/me/bug-reports", _ => FakeServer.Ok(new JObject { ["reports"] = JArray.FromObject(mine), ["serverTime"] = DateTimeOffset.UtcNow.ToString("o") }));
            var store = new MemoryStore();
            var k2 = await Started(other, store);
            await k2.SignInAsGuestAsync("2000-01-01", Terms);
            await k2.ReadyAsync();
            await k2.ReportBugAsync(new BugReportInput { Category = "TEXT", Description = "Typo" });
            await k2.FlushAsync();
            await k2.ShutdownAsync();
            mine = new List<BugReportView> { View("bug_x", "RESOLVED", "1.0.2") };
            var k3 = await KrtuworksClient.InitializeAsync(other.Options(store));
            var toldAgain = new List<string>();
            k3.BugFixed += r => toldAgain.Add(r.FixedIn ?? "");
            await k3.ReadyAsync();
            Assert.That(toldAgain, Is.EqualTo(new[] { "1.0.2" }));
            await k3.ShutdownAsync();
        }

        [Test]
        public async Task A_player_reports_another_players_picture_by_subject_and_a_report_that_can_never_be_taken_makes_no_call()
        {
            var server = new FakeServer();
            var seen = new List<JObject>();
            server.On("POST", "/v1/players/pw_other_player/avatar-report", call =>
            {
                lock (seen) seen.Add(call.Json!);
                return FakeServer.Ok(new JObject { ["reported"] = true, ["already"] = seen.Count > 1 });
            });
            var krtu = await Started(server);
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            Assert.That(await krtu.ReportAvatarAsync("pw_other_player", AvatarReportReasons.Impersonation, "  pretends to be staff  "), Is.False);
            Assert.That(JToken.DeepEquals(seen[0], new JObject { ["reason"] = "IMPERSONATION", ["note"] = "pretends to be staff" }), Is.True, seen[0].ToString());
            // Control: the same picture again is the server's "already".
            Assert.That(await krtu.ReportAvatarAsync("pw_other_player", AvatarReportReasons.Spam), Is.True);
            Assert.That(JToken.DeepEquals(seen[1], new JObject { ["reason"] = "SPAM" }), Is.True, seen[1].ToString());
            foreach (var (subject, reason, note) in new[] { ("", "SPAM", (string?)null), ("pw_other_player", "RUDE", null), ("pw_other_player", "HATE", new string('x', 301)) })
            {
                var e = await AsyncAssert.ThrowsAsync<KrtuworksException>(() => krtu.ReportAvatarAsync(subject, reason, note));
                Assert.That(e!.Code, Is.EqualTo("invalid_request"));
            }
            Assert.That(seen.Count, Is.EqualTo(2));
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task AReportCarriesOnlyItsOwnPlayersEventsAndLogLines_ControlThePlayersOwnStay()
        {
            var server = new FakeServer();
            var reports = new Dictionary<string, Kept>();
            BugRoutes(server, reports);
            var store = new MemoryStore();
            var krtu = await Started(server, store);
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            krtu.Track("screen_view", new { screen = "a-menu" });
            krtu.Log("a line");
            var a = await krtu.ReportBugAsync(new BugReportInput { Category = "OTHER", Description = "from A" });
            await krtu.SignOutAsync();
            krtu.Log("between players");
            // Another person on the same device: a new device key, a new guest.
            await store.DeleteAsync("krtu:buried.test.native:deviceKey");
            server.GuestRoutes("pw_other_bbbbbbbbbbbb");
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            krtu.Track("screen_view", new { screen = "b-menu" });
            krtu.Log("b line");
            var b = await krtu.ReportBugAsync(new BugReportInput { Category = "OTHER", Description = "from B" });
            await krtu.FlushAsync();
            var forA = reports[a].Body;
            Assert.That(((JArray)forA["events"]!).Select(e => (string?)e["data"]!["screen"]), Is.EqualTo(new[] { "a-menu" }), "A's report keeps A's event");
            StringAssert.Contains("a line", (string)forA["log"]!);
            var forB = reports[b].Body;
            Assert.That(((JArray)forB["events"]!).Select(e => (string?)e["data"]!["screen"]), Is.EqualTo(new[] { "b-menu" }), "B's report has no event of A");
            StringAssert.DoesNotContain("a line", (string)forB["log"]!);
            StringAssert.IsMatch("between players\n.*b line$", (string)forB["log"]!);
            await krtu.ShutdownAsync();
        }

        [Test]
        public void WhatTheSdkAddsToAReportIsCutByBytes_MultiByteTextStaysUnderTheServerLimit_ControlAsciiIsCutAsBefore()
        {
            var server = new FakeServer();
            var opts = Resolved.From(server.Options());
            var cjk = new string('\u6f22', 1700);
            var events = Enumerable.Range(0, 50).Select(i => new JObject { ["type"] = "screen_view", ["at"] = i.ToString(), ["data"] = new JObject { ["text"] = cjk } }).ToList();
            var log = string.Join("\n", Enumerable.Range(0, 33).Select(i => $"{i} {new string('\u5b57', 990)}"));
            var body = BugReports.ReportBody(new BugReportInput { Category = "OTHER", Description = new string('\u9577', 4000) }, "r1", opts, null, null, events, log);
            Assert.That(Encoding.UTF8.GetByteCount(body.ToString(Newtonsoft.Json.Formatting.None)), Is.LessThan(262_144 - 10_000), "room left under the 256 KiB body limit");
            var kept = (JArray)body["events"]!;
            Assert.That((string?)kept.Last!["at"], Is.EqualTo("49"), "the newest event stays");
            Assert.That(kept.Count, Is.LessThan(50));
            var lines = ((string)body["log"]!).Split('\n');
            Assert.That(lines.Length, Is.LessThan(33));
            Assert.That(lines.All(l => Regex.IsMatch(l, "^\\d+ \u5b57{990}$")), Is.True, "whole lines, from the oldest cut");
            Assert.That(lines.Last().StartsWith("32 "), Is.True, "the newest log line stays");
            // control: ASCII within the character budgets is not cut by the byte budgets
            var ascii = Enumerable.Range(0, 50).Select(i => new JObject { ["type"] = "screen_view", ["at"] = i.ToString(), ["data"] = new JObject { ["screen"] = new string('s', 1790) } }).ToList();
            var plain = BugReports.ReportBody(new BugReportInput { Category = "OTHER", Description = "x" }, "r1", opts, null, null, ascii, new string('l', 32_768));
            Assert.That(((JArray)plain["events"]!).Count, Is.EqualTo(48));
            Assert.That(((string)plain["log"]!).Length, Is.EqualTo(32_768));
        }

        [Test]
        public async Task AReportOfMultiByteEventsAndLogIsTakenByAServerThatRefusesABodyOver256KiB_ControlAnAsciiOneToo()
        {
            foreach (var text in new[] { "\u6f22", "x" })
            {
                var server = new FakeServer();
                var reports = new Dictionary<string, Kept>();
                BugRoutes(server, reports);
                var keep = server.RouteFor("POST", "/v1/me/bug-reports");
                server.OnAsync("POST", "/v1/me/bug-reports", call => call.Raw.Length > 262_144
                    ? Task.FromResult(new Reply { Status = 413, Json = new JObject { ["error"] = new JObject { ["code"] = "payload_too_large", ["message"] = "big" } } })
                    : keep(call));
                var krtu = await Started(server);
                await krtu.SignInAsGuestAsync("2000-01-01", Terms);
                await krtu.ReadyAsync();
                for (var i = 0; i < 50; i += 1) krtu.Track("screen_view", new { text = string.Concat(Enumerable.Repeat(text, 1700)), i });
                for (var i = 0; i < 33; i += 1) krtu.Log(string.Concat(Enumerable.Repeat(text, 990)));
                await krtu.ReportBugAsync(new BugReportInput { Category = "OTHER", Description = string.Concat(Enumerable.Repeat(text, 4000)) });
                var result = await krtu.FlushAsync();
                Assert.That(result.Bugs.Dropped, Is.Empty, $"kept, not refused for good ({text})");
                Assert.That(result.Bugs.Sent, Is.EqualTo(1));
                Assert.That(reports.Count, Is.EqualTo(1));
                await krtu.ShutdownAsync();
            }
        }
    }
}
