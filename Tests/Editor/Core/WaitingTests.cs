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
    /// <summary>
    /// What is queued for a player waits when the refusal can lift or the token failed, and only a refusal of the body itself drops it;
    /// the player's own account being gone drops it too.
    /// </summary>
    public class WaitingTests
    {
        const string Terms = "TERMS:v1 PRIVACY:v1";
        const string Player = "pw_guest_aaaaaaaaaaaa";
        static readonly byte[] Png = { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3 };

        static Reply Error(int status, string code) => new Reply { Status = status, Json = new JObject { ["error"] = new JObject { ["code"] = code, ["message"] = code } } };

        static async Task<KrtuworksException> Throws(Func<Task> work, string code)
        {
            try { await work(); }
            catch (KrtuworksException e)
            {
                Assert.That(e.Code, Is.EqualTo(code), e.Message);
                return e;
            }
            Assert.Fail($"expected {code}");
            throw new InvalidOperationException();
        }

        static async Task<KrtuworksClient> Start(FakeServer server, MemoryStore? store = null)
        {
            server.GuestRoutes(Player);
            server.ConfigRoute(new JObject());
            server.AcceptEvents(new List<JObject>());
            server.ApplyProgress();
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(store));
            await krtu.RefreshConfigAsync();
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            return krtu;
        }

        /// <summary>The token is refused while the call is made: the endpoint says it expired, and the refresh answers that the app is frozen.</summary>
        static void TokenFails(FakeServer server, params (string Method, string Path)[] endpoints)
        {
            foreach (var (method, path) in endpoints) server.On(method, path, _ => Error(401, "token_expired"));
            server.On("POST", "/oidc/token", call => FakeServer.OAuthError("invalid_grant", "app_suspended"));
        }

        static void BugRoutes(FakeServer server, Func<int, Reply?>? screenshot = null)
        {
            server.On("POST", "/v1/me/bug-reports", call => new Reply { Status = 201, Json = new JObject { ["created"] = true, ["report"] = new JObject { ["id"] = "bug_aaaaaaaaaaaaaaaaaaaa" } } });
            var puts = 0;
            server.On("PUT", "/v1/me/bug-reports/bug_aaaaaaaaaaaaaaaaaaaa/screenshot", _ =>
            {
                var forced = screenshot?.Invoke(++puts);
                return forced ?? FakeServer.Ok(new JObject { ["report"] = new JObject { ["id"] = "bug_aaaaaaaaaaaaaaaaaaaa", ["screenshot"] = true } });
            });
        }

        // ── A token that failed is not a refusal of what is queued ─────────────

        [Test]
        public async Task EventsStayWhenTheTokenFails_ControlAnEndpointRefusingTheBatchDropsIt()
        {
            var server = new FakeServer();
            var krtu = await Start(server);
            for (var i = 0; i < 3; i++) krtu.Track("screen_view", new { screen = $"s{i}" });
            TokenFails(server, ("POST", "/v1/events"));
            var result = await krtu.FlushAsync();
            Assert.That(result.Events.StoppedBy, Is.EqualTo("invalid_grant"));
            Assert.That((await krtu.WaitingAsync()).Events, Is.EqualTo(3), "the batch waits for the app to be resumed");
            await krtu.ShutdownAsync();

            var control = new FakeServer();
            var other = await Start(control);
            for (var i = 0; i < 3; i++) other.Track("screen_view", new { screen = $"s{i}" });
            control.On("POST", "/v1/events", _ => Error(400, "invalid_request"));
            var refused = await other.FlushAsync();
            Assert.That(refused.Events.StoppedBy, Is.EqualTo("invalid_request"));
            Assert.That((await other.WaitingAsync()).Events, Is.EqualTo(0), "a batch the endpoint can never take is dropped");
            await other.ShutdownAsync();
        }

        [Test]
        public async Task AWaitingCodeKeepsTheBatchEvenOnA400_AndTheNextFlushWaitsALongTime()
        {
            var server = new FakeServer();
            var krtu = await Start(server);
            krtu.Track("screen_view", new { screen = "a" });
            server.On("POST", "/v1/events", _ => Error(400, "sanctioned"));
            var first = await krtu.FlushAsync();
            Assert.That(first.Events.StoppedBy, Is.EqualTo("sanctioned"));
            Assert.That((await krtu.WaitingAsync()).Events, Is.EqualTo(1));
            var again = await krtu.FlushAsync();
            Assert.That(again.Events.StoppedBy, Is.EqualTo("waiting"), "backed off for a long time, not hammering the server");
            Assert.That(server.Count("POST", "/v1/events"), Is.EqualTo(1));
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task ABugReportStaysWhenTheTokenFails_ControlAnEndpointRefusingTheReportDropsIt()
        {
            var server = new FakeServer();
            var krtu = await Start(server);
            BugRoutes(server);
            await krtu.ReportBugAsync(new BugReportInput { Category = "OTHER", Description = "the door" });
            TokenFails(server, ("POST", "/v1/me/bug-reports"));
            var result = await krtu.FlushAsync();
            Assert.That(result.Bugs.StoppedBy, Is.EqualTo("invalid_grant"));
            Assert.That(result.Bugs.Dropped, Is.Empty);
            Assert.That((await krtu.WaitingAsync()).BugReports, Is.EqualTo(1));
            await krtu.ShutdownAsync();

            var control = new FakeServer();
            var other = await Start(control);
            await other.ReportBugAsync(new BugReportInput { Category = "OTHER", Description = "the door" });
            control.On("POST", "/v1/me/bug-reports", _ => Error(400, "invalid_request"));
            var refused = await other.FlushAsync();
            Assert.That(refused.Bugs.Dropped.Select(d => d.Code), Is.EqualTo(new[] { "invalid_request" }));
            Assert.That((await other.WaitingAsync()).BugReports, Is.EqualTo(0));
            await other.ShutdownAsync();
        }

        [Test]
        public async Task ASaveStaysWhenTheTokenFailsOrTheAccountIsMerged_ControlARefusedBodyIsForgotten()
        {
            var server = new FakeServer();
            var krtu = await Start(server);
            TokenFails(server, ("PUT", "/v1/apps/buried/files/save-1"));
            await Throws(() => krtu.SaveFileAsync("save-1", new byte[] { 1 }, null), "invalid_grant");
            Assert.That(await krtu.WaitingSavesAsync(Player), Has.Count.EqualTo(1), "the save waits on the device");
            await krtu.ShutdownAsync();

            var merged = new FakeServer();
            var m = await Start(merged);
            merged.On("PUT", "/v1/apps/buried/files/save-1", _ => new Reply { Status = 401, Json = new JObject { ["error"] = new JObject { ["code"] = "account_merged", ["message"] = "merged" }, ["mergedInto"] = "pw_into_bbbbbbbbbbbb" } });
            await Throws(() => m.SaveFileAsync("save-1", new byte[] { 1 }, null), "account_merged");
            Assert.That(await m.WaitingSavesAsync(Player), Has.Count.EqualTo(1), "a merged account's save waits for the player's choice");
            await m.ShutdownAsync();

            var control = new FakeServer();
            var c = await Start(control);
            control.On("PUT", "/v1/apps/buried/files/save-1", _ => Error(400, "invalid_request"));
            await Throws(() => c.SaveFileAsync("save-1", new byte[] { 1 }, null), "invalid_request");
            Assert.That(await c.WaitingSavesAsync(Player), Is.Empty, "a save the endpoint can never take is forgotten");
            await c.ShutdownAsync();
        }

        [Test]
        public async Task ProgressStaysWhenTheAppIsFrozenAndWaitsALongTime_UntilTheNetworkComesBack()
        {
            var server = new FakeServer();
            var krtu = await Start(server);
            await krtu.IncrementStatAsync("kills", 2);
            server.On("POST", "/v1/me/progress", _ => Error(403, "app_suspended"));
            var first = await krtu.FlushAsync();
            Assert.That(first.Progress.StoppedBy, Is.EqualTo("app_suspended"));
            Assert.That((await krtu.WaitingAsync()).Progress, Is.EqualTo(1));
            Assert.That((await krtu.FlushAsync()).Progress.StoppedBy, Is.EqualTo("waiting"));
            Assert.That(server.Count("POST", "/v1/me/progress"), Is.EqualTo(1));
            krtu.Online();
            await krtu.FlushAsync();
            Assert.That(server.Count("POST", "/v1/me/progress"), Is.EqualTo(2), "back online: tried at once");
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task AProgressOpAnswered_AppSuspended_StaysQueued_ControlAnInvalidValueIsLetGo()
        {
            var server = new FakeServer();
            var krtu = await Start(server);
            await krtu.IncrementStatAsync("kills", 2);
            await krtu.IncrementStatAsync("deaths", 1);
            server.On("POST", "/v1/me/progress", call =>
            {
                var ops = ((JArray)call.Json!["ops"]!).Cast<JObject>().ToList();
                return FakeServer.Ok(new JObject
                {
                    ["results"] = new JArray(ops.Select(o => (string)o["key"]! == "kills"
                        ? new JObject { ["opId"] = o["opId"], ["status"] = "rejected", ["code"] = "app_suspended" }
                        : new JObject { ["opId"] = o["opId"], ["status"] = "rejected", ["code"] = "invalid_value" })),
                });
            });
            var result = await krtu.FlushAsync();
            Assert.That(result.Progress.StoppedBy, Is.EqualTo("app_suspended"));
            Assert.That((await krtu.WaitingAsync()).Progress, Is.EqualTo(1), "only the op a refusal that can lift was for is kept");
            await krtu.ShutdownAsync();
        }

        // ── Bug screenshots ───────────────────────────────────────────────────

        [Test]
        public async Task AScreenshotAnswered409AppTransferringIsKeptAndSentAgain_ControlA415IsLetGo()
        {
            var server = new FakeServer();
            var krtu = await Start(server);
            BugRoutes(server, put => put == 1 ? Error(409, "app_transferring") : null);
            await krtu.ReportBugAsync(new BugReportInput { Category = "OTHER", Description = "the door", Screenshot = Png });
            var first = await krtu.FlushAsync();
            Assert.That(first.Bugs.Dropped, Is.Empty, "the report is in and the screenshot is not refused for good");
            Assert.That(first.Bugs.StoppedBy, Is.EqualTo("app_transferring"));
            Assert.That((await krtu.WaitingAsync()).BugReports, Is.EqualTo(1));
            krtu.Online();
            var second = await krtu.FlushAsync();
            Assert.That(second.Bugs.Sent, Is.EqualTo(1));
            Assert.That(server.Count("POST", "/v1/me/bug-reports"), Is.EqualTo(1), "the report is not sent twice");
            Assert.That(server.Count("PUT", "/v1/me/bug-reports/bug_aaaaaaaaaaaaaaaaaaaa/screenshot"), Is.EqualTo(2));
            Assert.That((await krtu.WaitingAsync()).BugReports, Is.EqualTo(0));
            await krtu.ShutdownAsync();

            var control = new FakeServer();
            var other = await Start(control);
            BugRoutes(control, _ => Error(415, "unsupported_media_type"));
            await other.ReportBugAsync(new BugReportInput { Category = "OTHER", Description = "the door", Screenshot = Png });
            var refused = await other.FlushAsync();
            Assert.That(refused.Bugs.Dropped.Select(d => (d.Code, d.ScreenshotOnly)), Is.EqualTo(new[] { ("unsupported_media_type", true) }));
            Assert.That((await other.WaitingAsync()).BugReports, Is.EqualTo(0));
            await other.ShutdownAsync();
        }

        [Test]
        public async Task ACorruptStoredScreenshotCostsOnlyTheScreenshot_EventsStillGo()
        {
            var store = new MemoryStore();
            var server = new FakeServer();
            var krtu = await Start(server, store);
            BugRoutes(server);
            krtu.Track("screen_view", new { screen = "a" });
            await Json.Put(store, $"krtu:buried.test.native:bugs:{Player}:broken", new WaitingBug
            {
                ReportId = "broken",
                MadeAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Body = new JObject { ["reportId"] = "broken", ["category"] = "OTHER", ["description"] = "x", ["platform"] = "WINDOWS", ["build"] = "1.0.0" },
                Screenshot = new WaitingShot { Bytes = "%%% not base64 %%%", Type = "image/png" },
            });
            var result = await krtu.FlushAsync();
            Assert.That(result.Bugs.Dropped.Select(d => (d.Code, d.ScreenshotOnly)), Is.EqualTo(new[] { ("invalid_screenshot", true) }));
            Assert.That(result.Bugs.Sent, Is.EqualTo(1), "the report went without its picture");
            Assert.That(server.Count("PUT", "/v1/me/bug-reports/bug_aaaaaaaaaaaaaaaaaaaa/screenshot"), Is.EqualTo(0));
            Assert.That(result.Events.Sent, Is.EqualTo(1), "the events were sent in the same flush");
            await krtu.ShutdownAsync();
        }

        // ── The account is gone ───────────────────────────────────────────────

        [Test]
        public async Task A401AccountDeletedSignsThePlayerOutAndDropsTheirQueues_ControlAServerErrorKeepsThem()
        {
            var server = new FakeServer();
            var store = new MemoryStore();
            var krtu = await Start(server, store);
            var changes = new List<IdentityChange>();
            krtu.IdentityChanged += changes.Add;
            krtu.Track("screen_view", new { screen = "a" });
            await krtu.IncrementStatAsync("kills", 1);
            BugRoutes(server);
            await krtu.ReportBugAsync(new BugReportInput { Category = "OTHER", Description = "the door" });
            server.On("PUT", "/v1/apps/buried/files/save-1", _ => Error(503, "unavailable"));
            await Throws(() => krtu.SaveFileAsync("save-1", new byte[] { 1 }, null), "unavailable");
            Assert.That(await krtu.WaitingSavesAsync(Player), Has.Count.EqualTo(1));
            // control: a failing server drops nothing
            foreach (var path in new[] { "/v1/me/progress", "/v1/events", "/v1/me/bug-reports" }) server.On("POST", path, _ => Error(503, "unavailable"));
            await krtu.FlushAsync();
            var kept = await krtu.WaitingAsync();
            Assert.That((kept.Events, kept.Progress, kept.BugReports), Is.EqualTo((1, 1, 1)));
            Assert.That(changes, Is.Empty);

            server.On("POST", "/v1/me/progress", _ => Error(401, "account_deleted"));
            krtu.Online();
            await krtu.FlushAsync();
            await Until(async () => (await Keys(store, "events", "progress", "bugs")) == 0 && (await krtu.WaitingSavesAsync(Player)).Count == 0);
            Assert.That(krtu.Player, Is.Null);
            Assert.That(changes.Select(c => (c.Type, c.Reason, c.PreviousSubject)), Is.EqualTo(new[] { ("signed_out", (string?)"account_deleted", (string?)Player) }));
            await krtu.ShutdownAsync();
        }

        static async Task<int> Keys(MemoryStore store, params string[] kinds)
        {
            var total = 0;
            foreach (var kind in kinds) total += (await store.KeysAsync($"krtu:buried.test.native:{kind}:")).Count(k => k.Contains(Player));
            return total;
        }

        static async Task Until(Func<Task<bool>> condition)
        {
            for (var i = 0; i < 400; i++)
            {
                if (await condition()) return;
                await Task.Delay(10);
            }
            Assert.Fail("the awaited state never came");
        }

        // ── An account being merged ───────────────────────────────────────────

        [Test]
        public async Task MergingIsToldOncePerEpisodeAndOnceWhenItEnds_ControlAnotherAnsweredCallSaysNothing()
        {
            var server = new FakeServer();
            var krtu = await Start(server);
            var changes = new List<string>();
            krtu.IdentityChanged += c => changes.Add(c.Type);
            var merging = 2;
            server.On("GET", "/v1/me/status", _ => merging-- > 0 ? Error(409, "account_merging") : FakeServer.Ok(new JObject { ["ok"] = true }));
            await Throws(() => krtu.GetStatusAsync(), "account_merging");
            await Throws(() => krtu.GetStatusAsync(), "account_merging");
            Assert.That(changes, Is.EqualTo(new[] { "merging" }), "the second answer is the same episode");
            await krtu.GetStatusAsync();
            await krtu.GetStatusAsync();
            Assert.That(changes, Is.EqualTo(new[] { "merging", "merging_ended" }));
            Assert.That(krtu.Player, Is.Not.Null, "the player and the queues are as they were");
            await krtu.ShutdownAsync();
        }
    }
}
