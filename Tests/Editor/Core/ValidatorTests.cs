#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Krtuworks.Internal;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System.Threading.Tasks;

namespace Krtuworks.Tests
{
    /// <summary>The validator (krtuworks-sdk-js/test/validator.test.ts): the local checks, then the TEST round against a fake server.</summary>
    public class ValidatorTests
    {
        static KrtuworksAppConfig Config => KrtuworksAppConfig.FromJson(
            "{\"appId\":\"app_aaaaaaaaaaaaaaaaaaaa\",\"slug\":\"buried\",\"type\":\"GAME\",\"environments\":{" +
            "\"TEST\":{\"clientId\":\"buried.test.native\",\"deviceClientId\":\"buried.test.device\",\"webClientId\":\"buried.test.web\"}," +
            "\"LIVE\":{\"clientId\":\"buried.live.native\",\"deviceClientId\":\"buried.live.device\",\"webClientId\":\"buried.live.web\"}}}");

        sealed class ServerState
        {
            internal JObject ModuleConfig = new JObject();
            internal byte[]? Stored;
            internal readonly List<JObject> Ops = new List<JObject>();
            internal readonly List<JObject> Reports = new List<JObject>();
            internal Func<FakeCall, Reply>? SessionOpen;
            internal Func<byte[], byte[]> ReadBack = b => b;
        }

        static JObject Version(ServerState d) => new JObject
        {
            ["id"] = "fil_aaaaaaaaaaaaaaaaaaaa", ["slot"] = "save-1", ["device"] = null, ["generation"] = 1, ["sizeBytes"] = d.Stored?.Length ?? 0,
            ["sha256"] = "x", ["platform"] = "WINDOWS", ["build"] = "1.0.0", ["savedAt"] = Clock.IsoNow(), ["fromMerge"] = false,
        };

        /// <summary>The server as the validator meets it: config, opt-out, a session, events, a SAVE slot, progress, bug reports.</summary>
        static ServerState Routes(FakeServer server, ServerState? d = null)
        {
            d ??= new ServerState();
            server.GuestRoutes();
            server.On("GET", "/v1/apps/buried/config", _ =>
            {
                var reply = FakeServer.Ok(new JObject
                {
                    ["appId"] = "app_aaaaaaaaaaaaaaaaaaaa", ["environment"] = "TEST", ["platform"] = "WINDOWS", ["version"] = "1.0",
                    ["config"] = d.ModuleConfig, ["switches"] = new JArray(), ["serverTime"] = Clock.IsoNow(),
                });
                reply.Headers["etag"] = "\"v1\"";
                return reply;
            });
            server.AcceptEvents(new List<JObject>());
            server.On("POST", "/v1/sessions", call => d.SessionOpen?.Invoke(call) ?? new Reply
            {
                Status = 201,
                Json = new JObject { ["session"] = new JObject { ["id"] = "ses_aaaaaaaaaaaaaaaaaaaa", ["startedAt"] = Clock.IsoNow() }, ["heartbeatSeconds"] = 60 },
            });
            server.On("POST", "/v1/sessions/ses_aaaaaaaaaaaaaaaaaaaa/heartbeat", _ => FakeServer.Ok(new JObject { ["nextHeartbeatSeconds"] = 60 }));
            server.On("POST", "/v1/sessions/ses_aaaaaaaaaaaaaaaaaaaa/end", _ => FakeServer.Ok(new JObject()));
            server.On("GET", "/v1/apps/buried/files", _ => FakeServer.Ok(new JObject
            {
                ["slots"] = new JArray(new JObject
                {
                    ["key"] = "save-1", ["kind"] = "SAVE", ["name"] = "Save", ["retired"] = false,
                    ["file"] = d.Stored != null ? Version(d) : null, ["conflict"] = null, ["devices"] = new JArray(),
                }),
                ["limits"] = new JObject(),
            }));
            server.On("PUT", "/v1/apps/buried/files/save-1", call =>
            {
                d.Stored = call.Raw;
                return new Reply { Status = 201, Json = new JObject { ["uploadId"] = call.Header("idempotency-key"), ["status"] = "ACTIVATED", ["file"] = Version(d) } };
            });
            server.On("GET", "/v1/apps/buried/files/save-1", _ =>
            {
                var reply = new Reply { Body = d.ReadBack(d.Stored ?? Array.Empty<byte>()) };
                reply.Headers["etag"] = "\"fil_aaaaaaaaaaaaaaaaaaaa\"";
                reply.Headers["x-krtu-generation"] = "1";
                return reply;
            });
            server.On("GET", "/v1/me/progress", _ => FakeServer.Ok(new JObject
            {
                ["locale"] = null,
                ["stats"] = new JArray(),
                ["achievements"] = new JArray(new JObject
                {
                    ["key"] = "first_win", ["id"] = "ach_aaaaaaaaaaaaaaaaaaaa", ["name"] = "First win", ["hidden"] = false, ["retired"] = false,
                    ["unlocked"] = false, ["platformIds"] = new JObject(),
                }),
            }));
            server.ApplyProgress(d.Ops);
            server.On("POST", "/v1/me/bug-reports", call =>
            {
                lock (d.Reports) d.Reports.Add(call.Json!);
                return new Reply { Status = 201, Json = new JObject { ["created"] = true, ["report"] = new JObject { ["id"] = "bug_aaaaaaaaaaaaaaaaaaaa" } } };
            });
            return d;
        }

        static KrtuworksOptions Options(FakeServer server, MemoryStore? store = null) => server.Options(store, extra: o => o.Config = Config);

        static Dictionary<string, string> Statuses(ValidationReport report) => report.Checks.ToDictionary(c => c.Key, c => c.Status);

        [Test]
        public void ConfigProblems_BothEnvironmentsNameTheClientOfThisKind_ControlAWholeConfigHasNone()
        {
            Assert.That(KrtuworksValidator.ConfigProblems(Config, "WINDOWS", false), Is.Empty);
            Assert.That(KrtuworksValidator.ConfigProblems(Config, "XBOX", false), Is.Empty);
            Assert.That(KrtuworksValidator.ConfigProblems(Config, "WEB", true), Is.Empty);
            var noLive = KrtuworksAppConfig.FromJson("{\"appId\":\"app_aaaaaaaaaaaaaaaaaaaa\",\"slug\":\"buried\",\"environments\":{\"TEST\":{\"clientId\":\"buried.test.native\"}}}");
            Assert.That(KrtuworksValidator.ConfigProblems(noLive, "WINDOWS", false), Is.EqualTo(new[] { "environments.LIVE has no clientId" }));
            Assert.That(KrtuworksValidator.ConfigProblems(noLive, "XBOX", false), Does.Contain("environments.TEST has no deviceClientId"), "a console signs in with the device client");
            var wrong = KrtuworksAppConfig.FromJson("{\"appId\":\"app_aaaaaaaaaaaaaaaaaaaa\",\"slug\":\"buried\",\"environments\":{\"TEST\":{\"clientId\":\"other.test.native\"},\"LIVE\":{\"clientId\":\"buried.live.native\"}}}");
            Assert.That(KrtuworksValidator.ConfigProblems(wrong, "WINDOWS", false), Is.EqualTo(new[] { "environments.TEST.clientId is other.test.native, not buried.test.native" }));
            Assert.That(KrtuworksValidator.ConfigProblems(null, "WINDOWS", false), Is.EqualTo(new[] { "the config is missing" }));
        }

        [Test]
        public void PlatformProblems_SteamNeedsItsServices_ControlAKnownPlatformPasses()
        {
            Assert.That(KrtuworksValidator.PlatformProblems("WINDOWS", false), Is.Empty);
            Assert.That(KrtuworksValidator.PlatformProblems("STEAM", true), Is.Empty);
            Assert.That(KrtuworksValidator.PlatformProblems("STEAM", false).Single(), Does.StartWith("STEAM needs the Steamworks services"));
            Assert.That(KrtuworksValidator.PlatformProblems("SWITCH", false).Single(), Does.StartWith("platform is one of"));
        }

        [Test]
        public void ASendCountsWhenItWentOrLeftThisDeviceUnrefused_OtherwiseWhy()
        {
            Assert.That(KrtuworksValidator.SentOrWhy(1, 0, null), Is.Null);
            Assert.That(KrtuworksValidator.SentOrWhy(1, 2, "network_error"), Is.Null, "this send went, later ones may wait");
            Assert.That(KrtuworksValidator.SentOrWhy(0, 0, null), Is.Null, "the game's own SDK on the same store took it");
            Assert.That(KrtuworksValidator.SentOrWhy(0, 1, "network_error"), Is.EqualTo("network_error"));
            Assert.That(KrtuworksValidator.SentOrWhy(0, 1, null), Is.EqualTo("not sent"));
            Assert.That(KrtuworksValidator.SentOrWhy(0, 0, "switched_off"), Is.EqualTo("switched_off"));
        }

        [Test]
        public async Task ABrokenConfigSkipsTheTestRunAndCallsNoServer_ControlAWholeConfigRunsIt()
        {
            var server = new FakeServer();
            Routes(server);
            var bad = await KrtuworksValidator.ValidateAsync(server.Options());
            Assert.That(bad.Ok, Is.False);
            Assert.That(Statuses(bad)["config"], Is.EqualTo("FAILED"));
            foreach (var key in new[] { "sign_in", "session", "events", "remote_config", "cloud_save", "progress", "bug_report" }) Assert.That(Statuses(bad)[key], Is.EqualTo("SKIPPED"), key);
            Assert.That(server.Calls, Is.Empty);
            var good = await KrtuworksValidator.ValidateAsync(Options(server));
            Assert.That(good.Ok, Is.True, string.Join("; ", good.Checks.Select(c => $"{c.Key} {c.Status} {c.Detail}")));
        }

        [Test]
        public async Task TheTestRoundPassesEveryCheckTheServerCounts_EachCallNamingTheBuild()
        {
            var server = new FakeServer();
            var d = Routes(server);
            var report = await KrtuworksValidator.ValidateAsync(Options(server));
            Assert.That(report.Ok, Is.True, string.Join("; ", report.Checks.Select(c => $"{c.Key} {c.Status} {c.Detail}")));
            Assert.That(report.Checks.Select(c => c.Key), Is.EqualTo(new[] { "config", "build", "platform", "sign_in", "session", "events", "remote_config", "cloud_save", "progress", "bug_report", "sentry" }));
            Assert.That(report.Checks.Where(c => c.Status != "PASSED").Select(c => c.Key), Is.EqualTo(new[] { "sentry" }));
            Assert.That(server.Count("POST", "/v1/sessions"), Is.EqualTo(1));
            Assert.That(server.Count("POST", "/v1/sessions/ses_aaaaaaaaaaaaaaaaaaaa/heartbeat"), Is.EqualTo(1));
            Assert.That(server.Count("POST", "/v1/sessions/ses_aaaaaaaaaaaaaaaaaaaa/end"), Is.EqualTo(1));
            Assert.That(Encoding.UTF8.GetString(d.Stored!), Does.StartWith("krtuworks-validator "));
            Assert.That(server.Calls.First(c => c.Method == "PUT").Header("if-none-match"), Is.EqualTo("*"), "an empty slot is written as new");
            Assert.That(d.Ops.Select(o => ((string?)o["type"], (string?)o["key"])), Is.EqualTo(new[] { ("unlock", "first_win") }));
            Assert.That((string?)d.Reports.Single()["category"], Is.EqualTo("OTHER"));
            var reads = server.Calls.Where(c => c.Path == "/v1/apps/buried/config").ToList();
            Assert.That(reads.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(reads.All(r => r.Query.TryGetValue("build", out var b) && b == "1.0.0"), Is.True, "the config read names the build in its query");
            var calls = server.Calls;
            var lastRead = calls.LastIndexOf(reads.Last());
            Assert.That(lastRead, Is.GreaterThan(calls.FindIndex(c => c.Path == "/v1/sessions")), "the read that counts comes after the build is known in TEST");
            Assert.That(lastRead, Is.GreaterThan(calls.FindIndex(c => c.Path == "/v1/events")));
        }

        [Test]
        public async Task TheSameTestGuestComesBackOnTheNextRun_ControlTheFirstRunMadeIt()
        {
            var server = new FakeServer();
            Routes(server);
            var store = new MemoryStore();
            await KrtuworksValidator.ValidateAsync(Options(server, store));
            Assert.That(server.Count("POST", "/oidc/device-key/challenge"), Is.EqualTo(1));
            var again = await KrtuworksValidator.ValidateAsync(Options(server, store));
            Assert.That(again.Ok, Is.True);
            Assert.That(server.Count("POST", "/oidc/device-key/challenge"), Is.EqualTo(1), "no new guest");
            Assert.That(server.Calls.Last(c => c.Method == "PUT").Header("if-match"), Is.EqualTo("fil_aaaaaaaaaaaaaaaaaaaa"), "the second save replaces the first");
        }

        [Test]
        public async Task AModuleTheOwnerSwitchedOffIsSkippedNotFailed_ControlTheModuleThatIsOnStillRuns()
        {
            var server = new FakeServer();
            Routes(server, new ServerState { ModuleConfig = new JObject { ["modules"] = new JObject { ["sessions"] = false, ["cloudSave"] = false, ["achievements"] = false, ["bugReports"] = false } } });
            var report = await KrtuworksValidator.ValidateAsync(Options(server));
            Assert.That(report.Ok, Is.True);
            var s = Statuses(report);
            Assert.That((s["session"], s["cloud_save"], s["progress"], s["bug_report"], s["events"]), Is.EqualTo(("SKIPPED", "SKIPPED", "SKIPPED", "SKIPPED", "PASSED")));
            Assert.That(server.Count("POST", "/v1/sessions"), Is.EqualTo(0));
            Assert.That(server.Count("POST", "/v1/me/bug-reports"), Is.EqualTo(0));
        }

        [Test]
        public async Task AServerRefusalFailsItsOwnCheckWithTheServersCode_TheRestStillRun()
        {
            var server = new FakeServer();
            Routes(server, new ServerState
            {
                SessionOpen = _ => new Reply { Status = 400, Json = new JObject { ["error"] = new JObject { ["code"] = "platform_not_registered", ["message"] = "WINDOWS is not one of the app platforms." } } },
                ReadBack = b => b.Skip(1).ToArray(),
            });
            var report = await KrtuworksValidator.ValidateAsync(Options(server));
            Assert.That(report.Ok, Is.False);
            var session = report.Checks.Single(c => c.Key == "session");
            Assert.That(session.Status, Is.EqualTo("FAILED"));
            Assert.That(session.Detail, Does.StartWith("platform_not_registered"));
            var s = Statuses(report);
            Assert.That((s["cloud_save"], s["events"], s["bug_report"]), Is.EqualTo(("FAILED", "PASSED", "PASSED")));
        }

        [Test]
        public async Task ASlotsWriteLimitIsSatOut_TheSameSaveGoesAgainWithTheSameUploadId_ControlALimitTooLongFails()
        {
            foreach (var retryAfter in new[] { 1, 60 })
            {
                var server = new FakeServer();
                var d = Routes(server);
                var refused = 0;
                server.On("PUT", "/v1/apps/buried/files/save-1", call =>
                {
                    if (System.Threading.Interlocked.Increment(ref refused) == 1)
                    {
                        var limit = new Reply { Status = 429, Json = new JObject { ["error"] = new JObject { ["code"] = "too_many_writes", ["message"] = "One write per 10 seconds." }, ["retryAfter"] = retryAfter } };
                        limit.Headers["retry-after"] = retryAfter.ToString();
                        return limit;
                    }
                    d.Stored = call.Raw;
                    return new Reply { Status = 201, Json = new JObject { ["uploadId"] = call.Header("idempotency-key"), ["status"] = "ACTIVATED", ["file"] = Version(d) } };
                });
                var report = await KrtuworksValidator.ValidateAsync(Options(server));
                var save = report.Checks.Single(c => c.Key == "cloud_save");
                var puts = server.Calls.Where(c => c.Method == "PUT").ToList();
                if (retryAfter == 1)
                {
                    Assert.That(save.Status, Is.EqualTo("PASSED"), save.Detail);
                    Assert.That(puts.Count, Is.EqualTo(2));
                    Assert.That(puts[1].Header("idempotency-key"), Is.EqualTo(puts[0].Header("idempotency-key")));
                }
                else
                {
                    Assert.That(save.Status, Is.EqualTo("FAILED"));
                    Assert.That(save.Detail, Does.StartWith("too_many_writes"));
                    Assert.That(puts.Count, Is.EqualTo(1));
                }
            }
        }

        [Test]
        public async Task AGuestThatCannotBeMadeFailsSignInAndSkipsTheRest()
        {
            var server = new FakeServer();
            Routes(server);
            var report = await KrtuworksValidator.ValidateAsync(Options(server), acceptedTerms: "TERMS:v0");
            Assert.That(report.Ok, Is.False);
            var s = Statuses(report);
            Assert.That(s["sign_in"], Is.EqualTo("FAILED"));
            foreach (var key in new[] { "session", "events", "remote_config", "cloud_save", "progress", "bug_report" }) Assert.That(s[key], Is.EqualTo("SKIPPED"), key);
            Assert.That(server.Count("POST", "/v1/sessions"), Is.EqualTo(0));
        }
    }
}
