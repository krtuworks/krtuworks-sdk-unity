#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Krtuworks.Internal;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Krtuworks.Tests
{
    public class ClientTests
    {
        const string Terms = "TERMS:v1 PRIVACY:v1";

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

        [Test]
        public async Task AModuleTheOwnerSwitchedOffMakesNoEvent_ControlAnEventOfAModuleThatIsOnIsKept()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            server.ConfigRoute(new JObject { ["modules"] = new JObject { ["economy"] = false } });
            var sent = new List<JObject>();
            server.AcceptEvents(sent);
            var krtu = await KrtuworksClient.InitializeAsync(server.Options());
            await krtu.RefreshConfigAsync();
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            Assert.That(krtu.Track("economy", new { direction = "earn", currency = "gold", amount = 5 }), Is.False);
            Assert.That(krtu.Track("screen_view", new { screen = "shop" }), Is.True);
            await krtu.FlushAsync();
            Assert.That(sent.Select(e => (string?)e["type"]), Is.EqualTo(new[] { "screen_view" }));
            Assert.That((string?)sent[0]["data"]!["screen"], Is.EqualTo("shop"), "an anonymous object's fields are the event's data");
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task TheOwnersSwitchOnThisBuildStopsEvents_AnotherBuildsDoesNot()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            server.ConfigRoute(new JObject(), new JArray(new JObject { ["build"] = "9.9.9", ["mode"] = "TELEMETRY", ["message"] = "x" }));
            var krtu = await KrtuworksClient.InitializeAsync(server.Options());
            await krtu.RefreshConfigAsync();
            Assert.That(krtu.Track("button", new { name = "a" }), Is.True, "another build is switched off, not this one");
            server.ConfigRoute(new JObject(), new JArray(new JObject { ["build"] = "1.0.0", ["mode"] = "ALL", ["message"] = "This build is off" }), 2);
            await krtu.RefreshConfigAsync();
            Assert.That(krtu.Track("button", new { name = "b" }), Is.False);
            var off = krtu.GetGate().SwitchedOff!;
            Assert.That((off.Build, off.Mode, off.Message), Is.EqualTo(("1.0.0", "ALL", "This build is off")));
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task RemoteConfigIsRevalidatedWithItsETag_MaintenanceAndMinimumVersionGateTheBuild()
        {
            var server = new FakeServer();
            server.ConfigRoute(new JObject { ["maintenance"] = new JObject { ["enabled"] = true, ["message"] = "Back soon" }, ["minVersion"] = "1.2", ["speed"] = 3 });
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(extra: o => o.Build = "1.1.9+40"));
            await krtu.RefreshConfigAsync();
            await krtu.RefreshConfigAsync();
            var asks = server.Calls.Where(c => c.Path == "/v1/apps/buried/config").ToList();
            Assert.That(asks.Last().Header("if-none-match"), Is.EqualTo("\"v1\""));
            var gate = krtu.GetGate();
            Assert.That((gate.Maintenance, gate.MaintenanceMessage, gate.UpdateRequired, gate.SwitchedOff), Is.EqualTo((true, "Back soon", true, (BuildSwitch?)null)));
            Assert.That(krtu.Value("speed", 0), Is.EqualTo(3));
            Assert.That(krtu.Value("missing", "fallback"), Is.EqualTo("fallback"));
            await krtu.ShutdownAsync();
            var newer = await KrtuworksClient.InitializeAsync(server.Options(extra: o => o.Build = "1.2.0"));
            await newer.RefreshConfigAsync();
            Assert.That(newer.GetGate().UpdateRequired, Is.False, "control: a build at the minimum plays");
            await newer.ShutdownAsync();
            Assert.That(RemoteConfig.OlderThan("1.10.0", "1.9"), Is.False, "numbers, not text");
            Assert.That(RemoteConfig.OlderThan("2.0.0-rc1", "2.0.1"), Is.True);
        }

        [Test]
        public async Task APlayerWhoTurnedStatisticsOffMakesNoEvents_ProgressStillGoes()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            server.ConfigRoute(new JObject());
            var sent = new List<JObject>();
            server.AcceptEvents(sent);
            server.On("GET", "/v1/me/telemetry-opt-out", _ => FakeServer.Ok(new JObject { ["optedOut"] = true, ["updatedAt"] = Clock.IsoNow() }));
            var ops = new List<JObject>();
            server.ApplyProgress(ops);
            var krtu = await KrtuworksClient.InitializeAsync(server.Options());
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            Assert.That(krtu.Track("button", new { name = "x" }), Is.False);
            await krtu.IncrementStatAsync("coins", 4);
            await krtu.FlushAsync();
            Assert.That(sent, Is.Empty);
            Assert.That(ops.Select(o => $"{o["key"]}:{(double)o["value"]!}"), Is.EqualTo(new[] { "coins:4" }));
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task TheSessionOpensOncePerPlayerAndItsIdRidesOnTheEvents()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            server.ConfigRoute(new JObject());
            var sent = new List<JObject>();
            server.AcceptEvents(sent);
            server.On("POST", "/v1/sessions", _ => new Reply
            {
                Status = 201,
                Json = new JObject { ["session"] = new JObject { ["id"] = "ses_aaaaaaaaaaaaaaaaaaaa", ["startedAt"] = Clock.IsoNow() }, ["heartbeatSeconds"] = 60, ["serverTime"] = Clock.IsoNow() },
            });
            server.On("POST", "/v1/sessions/ses_aaaaaaaaaaaaaaaaaaaa/end", _ => FakeServer.Ok(new JObject()));
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(extra: o =>
            {
                o.Sessions = true;
                o.Device = new DeviceInfo { Os = "Windows 11", ScreenWidth = 1920, ScreenHeight = 1080 };
            }));
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            krtu.Track("screen_view", new { screen = "menu" });
            await krtu.FlushAsync();
            var open = server.Calls.First(c => c.Path == "/v1/sessions");
            Assert.That((string?)open.Json!["platform"], Is.EqualTo("WINDOWS"));
            Assert.That((string?)open.Json!["build"], Is.EqualTo("1.0.0"));
            Assert.That((string?)open.Json!["device"]!["os"], Is.EqualTo("Windows 11"));
            Assert.That(open.Json!["device"]!["gpu"], Is.Null, "unknown fields are left out, not sent as null");
            Assert.That(open.Json!["platformProof"], Is.Null, "no Steam ticket off Steam");
            Assert.That(open.Header("x-krtu-build"), Is.EqualTo("1.0.0"));
            Assert.That((string?)sent[0]["sessionId"], Is.EqualTo("ses_aaaaaaaaaaaaaaaaaaaa"));
            Assert.That(server.Count("POST", "/v1/sessions"), Is.EqualTo(1));
            await krtu.ShutdownAsync();
            Assert.That(server.Count("POST", "/v1/sessions/ses_aaaaaaaaaaaaaaaaaaaa/end"), Is.EqualTo(1));
        }

        sealed class FakeSteam : IPlatformServices
        {
            internal readonly List<string> Unlocked = new List<string> { "ACH_STEAM_ONLY", "ACH_TAKEN_BACK" };
            internal readonly List<string> UnlockCalls = new List<string>();
            internal readonly List<string> ClearCalls = new List<string>();
            public Task<string?> GetWebApiTicketAsync() => Task.FromResult<string?>("0a0b0c0d0e0f1011");
            public Task<IReadOnlyList<string>> UnlockedAchievementsAsync() => Task.FromResult<IReadOnlyList<string>>(Unlocked.ToList());
            public Task UnlockAchievementAsync(string platformId)
            {
                UnlockCalls.Add(platformId);
                return Task.CompletedTask;
            }
            public Task ClearAchievementAsync(string platformId)
            {
                ClearCalls.Add(platformId);
                return Task.CompletedTask;
            }
        }

        [Test]
        public async Task AnAppThatIsNotAGameKeepsNoLevelsOrDeathsAndIgnoresSteam_ControlAConfigWithoutATypeIsAGame()
        {
            foreach (var type in new[] { "MOBILE_APP", null })
            {
                var server = new FakeServer();
                server.GuestRoutes();
                server.ConfigRoute(new JObject());
                var sent = new List<JObject>();
                server.AcceptEvents(sent);
                var config = FakeServer.App;
                config.Type = type;
                var steam = new FakeSteam();
                var krtu = await KrtuworksClient.InitializeAsync(server.Options(extra: o =>
                {
                    o.Config = config;
                    if (type != null)
                    {
                        o.Platform = "STEAM";
                        o.PlatformServices = steam;
                    }
                }));
                await krtu.RefreshConfigAsync();
                await krtu.SignInAsGuestAsync("2000-01-01", Terms);
                await krtu.ReadyAsync();
                await krtu.LevelStartAsync(new LevelRef("1-1"));
                var died = krtu.Death("spikes");
                krtu.LevelComplete(10);
                krtu.Track("screen_view", new { screen = "home" });
                await krtu.FlushAsync();
                var game = type == null;
                Assert.That(krtu.AppType, Is.EqualTo(type ?? "GAME"));
                Assert.That(died, Is.EqualTo(game));
                Assert.That((krtu.ModuleStates()["progression"], krtu.ModuleStates()["combat"], krtu.ModuleStates()["positions"], krtu.ModuleStates()["ui"]), Is.EqualTo((game, game, game, true)));
                Assert.That(Modules.Of("position_sample"), Is.EqualTo("positions"));
                Assert.That(sent.Select(e => (string?)e["type"]), Is.EqualTo(game ? new[] { "progression", "death", "progression", "screen_view" } : new[] { "screen_view" }));
                if (!game)
                {
                    Assert.That(server.Calls.Where(c => c.Path == "/oidc/token").Select(c => c.Field("grant_type")), Is.EqualTo(new[] { Identity.DeviceKeyGrant }), "Steam's ticket is a game's only");
                    Assert.That(server.Count("POST", "/v1/me/progress/platform-sync"), Is.EqualTo(0));
                }
                await krtu.ShutdownAsync();
            }
        }

        [Test]
        public async Task OnSteamTheGuestUsesTheTicket_SessionsCarryIt_AchievementsAreLinedUpBothWays_ATakenBackOneIsClearedThere()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            server.ConfigRoute(new JObject());
            server.AcceptEvents(new List<JObject>());
            server.ApplyProgress();
            server.On("POST", "/oidc/token", call =>
            {
                Assert.That(call.Field("grant_type"), Is.EqualTo(Identity.SteamTicketGrant));
                Assert.That(call.Field("ticket"), Is.EqualTo("0a0b0c0d0e0f1011"));
                Assert.That((bool)FakeServer.VerifyProof(call.Field("assertion")!).ok, "the device key proof rides along");
                return FakeServer.Ok(FakeServer.TokenAnswer("pw_steam_aaaaaaaaaaaa"));
            });
            server.On("POST", "/v1/sessions", _ => new Reply { Status = 201, Json = new JObject { ["session"] = new JObject { ["id"] = "ses_bbbbbbbbbbbbbbbbbbbb", ["startedAt"] = Clock.IsoNow() }, ["platformLink"] = SteamLink("ACTIVE") } });
            server.On("POST", "/v1/me/progress/platform-sync", call =>
            {
                Assert.That(call.Json!["unlocked"]!.Select(v => (string?)v), Is.EqualTo(new[] { "ACH_STEAM_ONLY", "ACH_TAKEN_BACK" }));
                Assert.That((string?)call.Json!["sessionId"], Is.EqualTo("ses_bbbbbbbbbbbbbbbbbbbb"), "the server mirrors only with the Steam account the session proved");
                return FakeServer.Ok(new JObject { ["unlockedHere"] = new JArray("steam_only"), ["toPlatform"] = new JArray("ACH_KRTU_ONLY"), ["toClear"] = new JArray("ACH_TAKEN_BACK") });
            });
            server.On("GET", "/v1/me/progress", _ => FakeServer.Ok(new JObject
            {
                ["stats"] = new JArray(),
                ["achievements"] = new JArray(new JObject { ["key"] = "first_win", ["id"] = "ach_x", ["platformIds"] = new JObject { ["STEAM"] = "ACH_FIRST_WIN" } }),
            }));
            var steam = new FakeSteam();
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(extra: o =>
            {
                o.Platform = "STEAM";
                o.Sessions = true;
                o.PlatformServices = steam;
            }));
            var player = await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            Assert.That(player.Subject, Is.EqualTo("pw_steam_aaaaaaaaaaaa"));
            await krtu.ReadyAsync();
            Assert.That((string?)server.Calls.First(c => c.Path == "/v1/sessions").Json!["platformProof"]!["steamTicket"], Is.EqualTo("0a0b0c0d0e0f1011"));
            Assert.That(steam.UnlockCalls, Is.EqualTo(new[] { "ACH_KRTU_ONLY" }), "what the player has here was unlocked on Steam");
            Assert.That(steam.ClearCalls, Is.EqualTo(new[] { "ACH_TAKEN_BACK" }), "what support took back here comes off Steam; control: nothing else is cleared");
            await krtu.GetProgressAsync();
            await krtu.UnlockAchievementAsync("first_win");
            Assert.That(steam.UnlockCalls.Last(), Is.EqualTo("ACH_FIRST_WIN"), "an unlock goes to Steam at once when its Steam id is known");
            await krtu.ShutdownAsync();
        }

        static JObject SteamLink(string state) => new JObject { ["platform"] = "STEAM", ["platformAccountId"] = "76561198000000302", ["state"] = state };

        [Test]
        public async Task ASecondSteamAccountWaitsForThePlayer_TheGameIsToldAndNothingMirrors_AllowingItLinesTheAchievementsUp()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            server.ConfigRoute(new JObject());
            server.AcceptEvents(new List<JObject>());
            server.On("POST", "/oidc/token", _ => FakeServer.Ok(FakeServer.TokenAnswer("pw_steam_bbbbbbbbbbbb")));
            server.On("POST", "/v1/sessions", _ => new Reply { Status = 201, Json = new JObject { ["session"] = new JObject { ["id"] = "ses_cccccccccccccccccccc", ["startedAt"] = Clock.IsoNow() }, ["platformLink"] = SteamLink("PENDING_CONSENT") } });
            server.On("POST", "/v1/me/platform-links/consent", call => FakeServer.Ok(new JObject { ["platformLink"] = SteamLink((bool)call.Json!["allow"]! ? "ACTIVE" : "REFUSED") }));
            server.On("POST", "/v1/me/progress/platform-sync", _ => FakeServer.Ok(new JObject { ["unlockedHere"] = new JArray(), ["toPlatform"] = new JArray("ACH_KRTU_ONLY"), ["toClear"] = new JArray() }));
            server.On("GET", "/v1/me/progress", _ => FakeServer.Ok(new JObject
            {
                ["stats"] = new JArray(),
                ["achievements"] = new JArray(new JObject { ["key"] = "first_win", ["id"] = "ach_x", ["platformIds"] = new JObject { ["STEAM"] = "ACH_FIRST_WIN" } }),
            }));
            var steam = new FakeSteam();
            var told = new List<PlatformLink>();
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(extra: o =>
            {
                o.Platform = "STEAM";
                o.Sessions = true;
                o.PlatformServices = steam;
            }));
            krtu.PlatformLinkNotice += told.Add;
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            Assert.That(told.Select(l => (l.PlatformAccountId, l.State)), Is.EqualTo(new[] { ("76561198000000302", "PENDING_CONSENT") }), "the game is told once, with the Steam account");
            Assert.That(krtu.PlatformLink?.State, Is.EqualTo("PENDING_CONSENT"));
            Assert.That(server.Count("POST", "/v1/me/progress/platform-sync"), Is.EqualTo(0), "nothing mirrors with an account the player has not allowed");
            await krtu.GetProgressAsync();
            await krtu.UnlockAchievementAsync("first_win");
            Assert.That(steam.UnlockCalls, Is.Empty, "an unlock does not go to a Steam account the player has not allowed (the sync brings it later)");

            var after = await krtu.AnswerPlatformConsentAsync(true);
            var consent = server.Calls.Single(c => c.Path == "/v1/me/platform-links/consent").Json!;
            Assert.That(((string?)consent["platform"], (string?)consent["platformAccountId"], (bool)consent["allow"]!, (string?)consent["sessionId"]),
                Is.EqualTo(("STEAM", "76561198000000302", true, "ses_cccccccccccccccccccc")));
            Assert.That((after.State, krtu.PlatformLink?.State), Is.EqualTo(("ACTIVE", "ACTIVE")));
            Assert.That(server.Count("POST", "/v1/me/progress/platform-sync"), Is.EqualTo(1), "allowed, the achievements line up at once");
            Assert.That(steam.UnlockCalls, Is.EqualTo(new[] { "ACH_KRTU_ONLY" }));

            var notPending = await AsyncAssert.ThrowsAsync<KrtuworksException>(() => krtu.AnswerPlatformConsentAsync(false));
            Assert.That(notPending!.Code, Is.EqualTo("consent_not_pending"));
            Assert.That(server.Count("POST", "/v1/me/platform-links/consent"), Is.EqualTo(1), "control: nothing waiting, nothing sent");
            await krtu.ShutdownAsync();
        }

        sealed class FakeSteamStats : IPlatformServices, IPlatformStats
        {
            internal readonly Dictionary<string, double> Held = new Dictionary<string, double> { ["STAT_KILLS"] = 3, ["STAT_BEST"] = 1.5 };
            internal readonly List<(string Id, string Type, double Value)> SetCalls = new List<(string, string, double)>();
            internal readonly HashSet<string> Unpublished = new HashSet<string>();
            internal int Stores;
            public Task<string?> GetWebApiTicketAsync() => Task.FromResult<string?>("0a0b0c0d0e0f1011");
            public Task<IReadOnlyList<string>> UnlockedAchievementsAsync() => Task.FromResult<IReadOnlyList<string>>(new List<string>());
            public Task UnlockAchievementAsync(string platformId) => Task.CompletedTask;
            public Task ClearAchievementAsync(string platformId) => Task.CompletedTask;
            public Task<double?> GetStatAsync(string platformId, string type) =>
                Task.FromResult(!Unpublished.Contains(platformId) && Held.TryGetValue(platformId, out var v) ? (double?)v : null);
            public Task<bool> SetStatAsync(string platformId, string type, double value)
            {
                SetCalls.Add((platformId, type, value));
                if (Unpublished.Contains(platformId)) return Task.FromResult(false);
                Held[platformId] = value;
                return Task.FromResult(true);
            }
            public Task StoreStatsAsync()
            {
                Stores++;
                return Task.CompletedTask;
            }
        }

        /// <summary>A server that adds a SUM's increases and answers the total, as the server does.</summary>
        static void SumProgress(FakeServer server, Dictionary<string, double> totals) =>
            server.On("POST", "/v1/me/progress", call =>
            {
                var ops = ((JArray)call.Json!["ops"]!).Cast<JObject>().ToList();
                return FakeServer.Ok(new JObject
                {
                    ["results"] = new JArray(ops.Select(o =>
                    {
                        var key = (string)o["key"]!;
                        totals[key] = (totals.TryGetValue(key, out var t) ? t : 0) + (double)o["value"]!;
                        return new JObject { ["opId"] = o["opId"], ["status"] = "applied", ["type"] = "stat", ["key"] = key, ["value"] = totals[key] };
                    })),
                });
            });

        static JObject StatsView() => new JObject
        {
            ["stats"] = new JArray(
                new JObject { ["key"] = "kills", ["kind"] = "SUM", ["valueType"] = "integer", ["platformIds"] = new JObject { ["STEAM"] = "STAT_KILLS" } },
                new JObject { ["key"] = "best", ["kind"] = "MAX", ["valueType"] = "number", ["platformIds"] = new JObject { ["STEAM"] = "STAT_BEST" } },
                new JObject { ["key"] = "secret", ["kind"] = "SUM", ["valueType"] = "integer", ["platformIds"] = new JObject { ["STEAM"] = "STAT_SECRET" } },
                new JObject { ["key"] = "plain", ["kind"] = "SUM", ["valueType"] = "integer" }),
            ["achievements"] = new JArray(),
        };

        [Test]
        public async Task SteamStatsMirrorTheServer_TheLaunchSyncSendsSteamsValues_LaterTheServersTotalsGoThere_StoredEveryTwoMinutesAndOnPause()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            server.ConfigRoute(new JObject());
            server.AcceptEvents(new List<JObject>());
            var totals = new Dictionary<string, double> { ["kills"] = 7 };
            SumProgress(server, totals);
            server.On("POST", "/oidc/token", _ => FakeServer.Ok(FakeServer.TokenAnswer("pw_steam_dddddddddddd")));
            server.On("POST", "/v1/sessions", _ => new Reply { Status = 201, Json = new JObject { ["session"] = new JObject { ["id"] = "ses_eeeeeeeeeeeeeeeeeeee", ["startedAt"] = Clock.IsoNow() }, ["platformLink"] = SteamLink("ACTIVE") } });
            server.On("GET", "/v1/me/progress", _ => FakeServer.Ok(StatsView()));
            server.On("POST", "/v1/me/progress/platform-sync", call =>
            {
                var held = call.Json!["stats"]!.ToDictionary(x => (string)x["id"]!, x => (double)x["value"]!);
                Assert.That(held, Is.EqualTo(new Dictionary<string, double> { ["STAT_KILLS"] = 3, ["STAT_BEST"] = 1.5 }), "the stats Steam holds are read and sent; an unpublished one and an unmapped one are not");
                return FakeServer.Ok(new JObject
                {
                    ["unlockedHere"] = new JArray(), ["toPlatform"] = new JArray(), ["toClear"] = new JArray(),
                    ["statsImported"] = new JArray("best"), ["statsToPlatform"] = new JArray(new JObject { ["id"] = "STAT_KILLS", ["value"] = 7 }),
                });
            });
            var steam = new FakeSteamStats();
            steam.Unpublished.Add("STAT_SECRET");
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(extra: o =>
            {
                o.Platform = "STEAM";
                o.Sessions = true;
                o.PlatformServices = steam;
            }));
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            Assert.That(steam.SetCalls, Is.EqualTo(new[] { ("STAT_KILLS", "INT", 7.0) }), "Steam is set to the server's value the server named");
            Assert.That(steam.Stores, Is.EqualTo(1), "the launch's sync is stored at once");

            await krtu.IncrementStatAsync("kills", 2);
            await krtu.FlushAsync();
            Assert.That(steam.SetCalls.Last(), Is.EqualTo(("STAT_KILLS", "INT", 9.0)), "the server's total goes to Steam, never the increase (nothing counts twice there)");
            Assert.That(steam.Stores, Is.EqualTo(1), "Steam's servers get stats at most every 2 minutes");
            krtu.SetForeground(false);
            Assert.That(steam.Stores, Is.EqualTo(2), "a pause stores what was set");
            krtu.SetForeground(false);
            Assert.That(steam.Stores, Is.EqualTo(2), "control: nothing new set, nothing stored");

            await krtu.IncrementStatAsync("kills", 5e9);
            await krtu.IncrementStatAsync("plain", 1);
            await krtu.FlushAsync();
            Assert.That(steam.SetCalls.Last(), Is.EqualTo(("STAT_KILLS", "INT", (double)int.MaxValue)), "past 32 bits Steam holds the INT maximum");
            Assert.That(steam.SetCalls.Any(c => c.Id != "STAT_KILLS"), Is.False, "control: a stat without a Steam id is not set there");
            await krtu.IncrementStatAsync("secret", 1);
            await krtu.IncrementStatAsync("secret", 1);
            await krtu.FlushAsync();
            await krtu.IncrementStatAsync("secret", 1);
            await krtu.FlushAsync();
            Assert.That(steam.SetCalls.Count(c => c.Id == "STAT_SECRET"), Is.EqualTo(1), "a stat Steam refused is left alone for the session");
            await krtu.ShutdownAsync();
            Assert.That(steam.Stores, Is.EqualTo(3), "the exit stores what was set");
        }

        [Test]
        public async Task SteamStatsDoNotMirrorWithASteamAccountThePlayerHasNotAllowed()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            server.ConfigRoute(new JObject());
            server.AcceptEvents(new List<JObject>());
            SumProgress(server, new Dictionary<string, double>());
            server.On("POST", "/oidc/token", _ => FakeServer.Ok(FakeServer.TokenAnswer("pw_steam_eeeeeeeeeeee")));
            server.On("POST", "/v1/sessions", _ => new Reply { Status = 201, Json = new JObject { ["session"] = new JObject { ["id"] = "ses_ffffffffffffffffffff", ["startedAt"] = Clock.IsoNow() }, ["platformLink"] = SteamLink("PENDING_CONSENT") } });
            server.On("GET", "/v1/me/progress", _ => FakeServer.Ok(StatsView()));
            var steam = new FakeSteamStats();
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(extra: o =>
            {
                o.Platform = "STEAM";
                o.Sessions = true;
                o.PlatformServices = steam;
            }));
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            await krtu.GetProgressAsync();
            await krtu.IncrementStatAsync("kills", 2);
            var sent = await krtu.FlushAsync();
            Assert.That(sent.Progress.Values["kills"], Is.EqualTo(2), "control: the server took the stat");
            Assert.That(steam.SetCalls, Is.Empty, "nothing is set on a Steam account the player has not allowed");
            await krtu.ShutdownAsync();
            Assert.That(steam.Stores, Is.EqualTo(0));
        }

        [TestCase("MISMATCH", true)]
        [TestCase("LIMIT", true)]
        [TestCase("REFUSED", false)]
        public async Task ASteamAccountThatDoesNotMirrorIsToldWhenThePlayerShouldHear_NothingIsSynced(string state, bool told)
        {
            var server = new FakeServer();
            server.GuestRoutes();
            server.ConfigRoute(new JObject());
            server.AcceptEvents(new List<JObject>());
            server.On("POST", "/oidc/token", _ => FakeServer.Ok(FakeServer.TokenAnswer("pw_steam_cccccccccccc")));
            server.On("POST", "/v1/sessions", _ => new Reply { Status = 201, Json = new JObject { ["session"] = new JObject { ["id"] = "ses_dddddddddddddddddddd", ["startedAt"] = Clock.IsoNow() }, ["platformLink"] = SteamLink(state) } });
            var notices = new List<string>();
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(extra: o =>
            {
                o.Platform = "STEAM";
                o.Sessions = true;
                o.PlatformServices = new FakeSteam();
            }));
            krtu.PlatformLinkNotice += link => notices.Add(link.State);
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            Assert.That(notices, Is.EqualTo(told ? new[] { state } : new string[0]), "a refused one is the player's own answer: not told");
            Assert.That(server.Count("POST", "/v1/me/progress/platform-sync"), Is.EqualTo(0));
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task AMergedAccountsWaitingQueueMovesOnlyWhenTheServerConfirmsTheMerge()
        {
            var server = new FakeServer();
            server.GuestRoutes("pw_target_aaaaaaaaaa");
            server.ConfigRoute(new JObject());
            var sent = new List<JObject>();
            server.AcceptEvents(sent);
            var confirmed = false;
            server.On("POST", "/v1/me/merge-check", _ => FakeServer.Ok(new JObject { ["confirmed"] = confirmed }));
            var store = new MemoryStore();
            await store.SetAsync("krtu:buried.test.native:events:pw_source_aaaaaaaaaa", new JObject
            {
                ["pending"] = new JArray(new JObject
                {
                    ["id"] = "0192f2a1-0000-7000-8000-000000000001", ["type"] = "button", ["createdAt"] = Clock.IsoNow(),
                    ["launchId"] = "0192f2a1-0000-7000-8000-000000000002", ["monotonicMs"] = 5, ["data"] = new JObject { ["name"] = "old" },
                }),
                ["dropped"] = 0,
                ["reported"] = 0,
            }.ToString());
            await store.SetAsync("krtu:buried.test.native:meta", "{\"epoch\":0,\"waitingOwners\":[\"pw_source_aaaaaaaaaa\"]}");
            var settled = new List<string>();
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(store));
            krtu.MergeSettled += m => settled.Add(m.PreviousSubject);
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            Assert.That(sent, Is.Empty, "not confirmed: the queue waits for its own account");
            Assert.That(settled, Is.Empty);
            confirmed = true;
            await krtu.SignInAsGuestAsync();
            await krtu.SignOutAsync();
            await krtu.SignInAsGuestAsync();
            await krtu.ReadyAsync();
            Assert.That(settled, Is.EqualTo(new[] { "pw_source_aaaaaaaaaa" }));
            Assert.That(sent.Select(e => (string?)e["data"]!["name"]), Is.EqualTo(new[] { "old" }), "confirmed: the waiting event went with this token");
            await krtu.ShutdownAsync();
        }

        static FileVersion Version => new FileVersion { Id = "fil_aaaaaaaaaaaaaaaaaaaa", Slot = "save-1", Generation = 1, SizeBytes = 3, Sha256 = "x", SavedAt = Clock.IsoNow() };

        [Test]
        public async Task ASaveAnswered202IsAskedAboutUntilFinal_ARelaunchFinishesALostOneWithTheSameUploadId()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            server.ConfigRoute(new JObject());
            server.AcceptEvents(new List<JObject>());
            var version = JObject.FromObject(Version);
            var polls = 0;
            var keys = new List<string>();
            server.On("PUT", "/v1/apps/buried/files/save-1", call =>
            {
                lock (keys) keys.Add(call.Header("idempotency-key")!);
                Assert.That(call.Header("if-none-match"), Is.EqualTo("*"));
                return keys.Count == 1
                    ? new Reply { Status = 202, Json = new JObject { ["uploadId"] = keys[0], ["status"] = "PENDING" } }
                    : new Reply { Status = 201, Json = new JObject { ["uploadId"] = keys.Last(), ["status"] = "ACTIVATED", ["file"] = version } };
            });
            server.On("GET", "/v1/apps/buried/files", _ => FakeServer.Ok(new JObject
            {
                ["slots"] = new JArray(new JObject { ["key"] = "save-1", ["kind"] = "SAVE", ["name"] = "Save", ["retired"] = false, ["file"] = version, ["devices"] = new JArray() }),
                ["limits"] = new JObject(),
            }));
            var store = new MemoryStore();
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(store));
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            var saving = krtu.SaveFileAsync("save-1", new byte[] { 1, 2, 3 }, null);
            while (keys.Count == 0) await Task.Delay(5);
            server.On("GET", $"/v1/apps/buried/files/save-1/uploads/{keys[0]}", _ =>
            {
                polls++;
                return FakeServer.Ok(new JObject { ["uploadId"] = keys[0], ["status"] = polls < 2 ? "PENDING" : "ACTIVATED", ["file"] = polls < 2 ? null : new JObject { ["id"] = Version.Id, ["generation"] = 1 } });
            });
            var saved = await saving;
            Assert.That(saved.Id, Is.EqualTo(Version.Id));
            Assert.That(polls, Is.EqualTo(2));
            await krtu.ShutdownAsync();

            // A lost answer: the upload stays in the store; the next launch sends it again with the same id.
            server.Offline.Add("/v1/apps/buried/files/save-1");
            var first = await KrtuworksClient.InitializeAsync(server.Options(store));
            await first.ReadyAsync();
            await Throws(() => first.SaveFileAsync("save-1", new byte[] { 9 }, null), "network_error");
            await first.ShutdownAsync();
            server.Offline.Clear();
            var before = keys.Count;
            var second = await KrtuworksClient.InitializeAsync(server.Options(store));
            await second.ReadyAsync();
            Assert.That(keys.Count, Is.EqualTo(before + 1), "resumed on launch");
            var lostKey = server.Calls.Where(c => c.Method == "PUT").Reverse().Skip(1).First().Header("idempotency-key");
            Assert.That(keys.Last(), Is.EqualTo(lostKey), "with the same upload id");
            await second.ShutdownAsync();
        }

        [Test]
        public async Task AGuestIsRemindedToLinkAtTheFirstAchievementOncePerQuietWindow_ControlNoneWhenTheAppTurnsItOff()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            server.ConfigRoute(new JObject { ["linkReminder"] = new JObject { ["everyDays"] = 3 } });
            server.ApplyProgress();
            server.AcceptEvents(new List<JObject>());
            var krtu = await KrtuworksClient.InitializeAsync(server.Options());
            await krtu.RefreshConfigAsync();
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            var reasons = new List<string>();
            krtu.LinkReminder += r => reasons.Add(r);
            await krtu.UnlockAchievementAsync("first_win");
            await krtu.UnlockAchievementAsync("second_win");
            Assert.That(reasons, Is.EqualTo(new[] { "first_achievement" }), "once: the second is not a first, and the window is quiet");
            await krtu.ShutdownAsync();

            var off = new FakeServer();
            off.GuestRoutes("pw_other_aaaaaaaaaaaa");
            off.ConfigRoute(new JObject { ["linkReminder"] = new JObject { ["enabled"] = false } });
            off.AcceptEvents(new List<JObject>());
            var quiet = await KrtuworksClient.InitializeAsync(off.Options());
            await quiet.RefreshConfigAsync();
            await quiet.SignInAsGuestAsync("2000-01-01", Terms);
            var none = new List<string>();
            quiet.LinkReminder += r => none.Add(r);
            await quiet.UnlockAchievementAsync("first_win");
            Assert.That(none, Is.Empty);
            await quiet.ShutdownAsync();
        }

        [Test]
        public async Task ASaveStillBeingWrittenWhenThePlayerChangesEndsWithContextChanged()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            server.ConfigRoute(new JObject());
            server.AcceptEvents(new List<JObject>());
            server.On("POST", "/oidc/token/revocation", _ => new Reply());
            server.On("PUT", "/v1/apps/buried/files/save-1", call => new Reply { Status = 202, Json = new JObject { ["uploadId"] = call.Header("idempotency-key"), ["status"] = "PENDING" } });
            var krtu = await KrtuworksClient.InitializeAsync(server.Options());
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            var saving = krtu.SaveFileAsync("save-1", new byte[] { 1 }, null);
            while (server.Count("PUT", "/v1/apps/buried/files/save-1") == 0) await Task.Delay(5);
            await krtu.SignOutAsync();
            await Throws(() => saving, "context_changed");
            Assert.That(server.Calls.Count(c => c.Path.Contains("/uploads/")), Is.Zero, "no status call under another player");
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task AReadThatANewerLocalSaveOvertookSaysSo()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            server.ConfigRoute(new JObject());
            server.AcceptEvents(new List<JObject>());
            var gate = new TaskCompletionSource<bool>();
            server.OnAsync("GET", "/v1/apps/buried/files/save-1", async _ =>
            {
                await gate.Task;
                var reply = new Reply { Body = new byte[] { 7 } };
                reply.Headers["etag"] = "\"fil_aaaaaaaaaaaaaaaaaaaa\"";
                reply.Headers["x-krtu-generation"] = "1";
                return reply;
            });
            server.On("PUT", "/v1/apps/buried/files/save-1", _ => new Reply { Status = 409, Json = new JObject { ["error"] = new JObject { ["code"] = "file_conflict", ["message"] = "x" } } });
            var krtu = await KrtuworksClient.InitializeAsync(server.Options());
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            var reading = krtu.ReadFileAsync("save-1");
            while (server.Count("GET", "/v1/apps/buried/files/save-1") == 0) await Task.Delay(5);
            await Throws(() => krtu.SaveFileAsync("save-1", new byte[] { 8 }, "fil_aaaaaaaaaaaaaaaaaaaa"), "file_conflict");
            gate.SetResult(true);
            var read = await reading;
            Assert.That(read!.NewerLocalSave, Is.True);
            Assert.That(read.VersionId, Is.EqualTo("fil_aaaaaaaaaaaaaaaaaaaa"));
            Assert.That(read.Bytes, Is.EqualTo(new byte[] { 7 }));
            var plain = await krtu.ReadFileAsync("save-1");
            Assert.That(plain!.NewerLocalSave, Is.False, "control: a read with no save in between");
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task LevelsCountAttemptsAcrossLaunchesAndADeathCountsTowardTheLevelInPlay()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            server.ConfigRoute(new JObject());
            var sent = new List<JObject>();
            server.AcceptEvents(sent);
            var store = new MemoryStore();
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(store));
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            await krtu.LevelStartAsync(new LevelRef("caves", "act1"));
            krtu.Death("spikes", position: new EventPosition { Area = "caves", X = 1.5, Y = 2, Z = -3 });
            krtu.LevelFail();
            await krtu.LevelStartAsync(new LevelRef("caves", "act1"));
            await krtu.ShutdownAsync();
            var again = await KrtuworksClient.InitializeAsync(server.Options(store));
            await again.ReadyAsync();
            await again.LevelStartAsync(new LevelRef("caves", "act1"));
            again.LevelComplete(120);
            await again.FlushAsync();
            var progression = sent.Where(e => (string?)e["type"] == "progression").Select(e => (JObject)e["data"]!).ToList();
            Assert.That(progression.Select(d => $"{d["action"]}#{d["attempt"]}"), Is.EqualTo(new[] { "start#1", "fail#1", "start#2", "quit#2", "start#3", "complete#3" }));
            Assert.That((int)progression[1]["deaths"]!, Is.EqualTo(1));
            Assert.That((double)progression[5]["score"]!, Is.EqualTo(120));
            var death = sent.Single(e => (string?)e["type"] == "death");
            Assert.That(((string?)death["data"]!["level"], (string?)death["data"]!["world"], (double)death["position"]!["z"]!), Is.EqualTo(("caves", "act1", -3d)));
            await again.ShutdownAsync();
        }

        [Test]
        public async Task AnEventTypeOutsideTheGrammarIsRefusedBeforeAnythingIsKept()
        {
            var server = new FakeServer();
            var krtu = await KrtuworksClient.InitializeAsync(server.Options());
            Assert.Throws<ArgumentException>(() => krtu.Track("Boss Fight"));
            Assert.That(krtu.Track("boss_fight"), Is.True, "control");
            await krtu.ShutdownAsync();
        }

        static JObject BoardsView() => new JObject
        {
            ["locale"] = "tr",
            ["leaderboards"] = new JArray(
                new JObject
                {
                    ["key"] = "fastest", ["name"] = "En hizli", ["description"] = null, ["sortOrder"] = "ASC", ["display"] = "TIME_MILLISECONDS", ["uploadMethod"] = "KEEP_BEST",
                    ["importFromPlatform"] = true, ["platformIds"] = new JObject { ["STEAM"] = "fastest_run" }, ["score"] = 91234, ["details"] = Convert.ToBase64String(new byte[] { 7, 8 }), ["rank"] = 3, ["entries"] = 40,
                },
                new JObject
                {
                    ["key"] = "latest", ["name"] = "Latest", ["sortOrder"] = "DESC", ["display"] = "NUMERIC", ["uploadMethod"] = "FORCE_UPDATE",
                    ["importFromPlatform"] = false, ["platformIds"] = new JObject { ["STEAM"] = "latest_run" }, ["score"] = null, ["details"] = null, ["rank"] = null, ["entries"] = 0,
                },
                new JObject
                {
                    ["key"] = "nosteam", ["name"] = "No Steam", ["sortOrder"] = "DESC", ["display"] = "NUMERIC", ["uploadMethod"] = "KEEP_BEST",
                    ["importFromPlatform"] = true, ["platformIds"] = new JObject(), ["score"] = null, ["details"] = null, ["rank"] = null, ["entries"] = 0,
                }),
        };

        [Test]
        public async Task LeaderboardsReadAsTypedClasses_ReadsWantTheModuleOn_ArgumentsAreCheckedBeforeAnyCall()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            var config = new JObject();
            server.ConfigRoute(config);
            server.On("GET", "/v1/me/leaderboards", call => FakeServer.Ok(BoardsView()));
            server.On("GET", "/v1/leaderboards/fastest/entries", call => FakeServer.Ok(new JObject
            {
                ["key"] = "fastest", ["scope"] = call.Query["scope"], ["sortOrder"] = "ASC", ["display"] = "TIME_MILLISECONDS", ["total"] = 40,
                ["entries"] = new JArray(
                    new JObject { ["rank"] = 1, ["subject"] = "pw_a", ["name"] = "ada", ["score"] = 80000, ["details"] = null, ["updatedAt"] = "2026-11-09T10:00:00.000Z", ["you"] = false },
                    new JObject { ["rank"] = 2, ["subject"] = "pw_b", ["name"] = null, ["score"] = 91234, ["details"] = Convert.ToBase64String(new byte[] { 7, 8 }), ["updatedAt"] = "2026-11-09T11:00:00.000Z", ["you"] = true }),
            }));
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(extra: o => o.Locale = "tr"));
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();

            var boards = await krtu.GetLeaderboardsAsync();
            Assert.That(server.Calls.Single(c => c.Path == "/v1/me/leaderboards").Query["locale"], Is.EqualTo("tr"), "the SDK's language unless one is given");
            Assert.That(boards.Select(b => (b.Key, b.SortOrder, b.Display, b.UploadMethod, b.ImportFromPlatform, b.Score, b.Rank, b.Entries)),
                Is.EqualTo(new[] { ("fastest", "ASC", "TIME_MILLISECONDS", (string?)"KEEP_BEST", true, (long?)91234, (int?)3, 40), ("latest", "DESC", "NUMERIC", "FORCE_UPDATE", false, null, null, 0), ("nosteam", "DESC", "NUMERIC", "KEEP_BEST", true, null, null, 0) }));
            Assert.That(boards[0].Details, Is.EqualTo(new byte[] { 7, 8 }), "details come back as bytes");
            Assert.That(boards[0].PlatformIds["STEAM"], Is.EqualTo("fastest_run"));
            await krtu.GetLeaderboardsAsync("de");
            Assert.That(server.Calls.Where(c => c.Path == "/v1/me/leaderboards").Last().Query["locale"], Is.EqualTo("de"));

            var page = await krtu.GetLeaderboardEntriesAsync("fastest", "around", 2);
            var call = server.Calls.Single(c => c.Path == "/v1/leaderboards/fastest/entries");
            Assert.That((call.Query["scope"], call.Query["limit"], call.Query["offset"]), Is.EqualTo(("around", "2", "0")));
            Assert.That((page.Key, page.Scope, page.Total, page.Entries.Count), Is.EqualTo(("fastest", "around", 40, 2)));
            Assert.That(page.Entries.Select(e => (e.Rank, e.DisplayName, e.Score, e.You)), Is.EqualTo(new[] { (1, "ada", 80000L, false), (2, "Player", 91234L, true) }), "a player with no username is shown as Player");
            Assert.That(page.Entries[1].Details, Is.EqualTo(new byte[] { 7, 8 }));

            var calls = server.Calls.Count;
            await AsyncAssert.ThrowsAsync<ArgumentException>(() => krtu.GetLeaderboardEntriesAsync("fastest", "friends"));
            await AsyncAssert.ThrowsAsync<ArgumentException>(() => krtu.GetLeaderboardEntriesAsync("fastest", "global", 0));
            await AsyncAssert.ThrowsAsync<ArgumentException>(() => krtu.GetLeaderboardEntriesAsync("fastest", "global", 101));
            await AsyncAssert.ThrowsAsync<ArgumentException>(() => krtu.GetLeaderboardEntriesAsync("fastest", "around", 10, 5));
            Assert.That(server.Calls.Count, Is.EqualTo(calls), "nothing was sent for a call that can never be taken");
            await krtu.GetLeaderboardEntriesAsync("fastest", "global", 100, 200);
            Assert.That(server.Calls.Last().Query["limit"], Is.EqualTo("100"), "control: the edges are taken");

            config["modules"] = new JObject { ["leaderboards"] = false };
            server.ConfigRoute(config, null, 2);
            await krtu.RefreshConfigAsync();
            calls = server.Calls.Count;
            await Throws(() => krtu.GetLeaderboardsAsync(), "module_disabled");
            await Throws(() => krtu.GetLeaderboardEntriesAsync("fastest"), "module_disabled");
            Assert.That(server.Calls.Count, Is.EqualTo(calls), "reads of a board that is off are not sent");
            await krtu.SubmitScoreAsync("fastest", 5);
            Assert.That((await krtu.WaitingAsync()).Progress, Is.EqualTo(1), "a score is kept while the module is off: the server answers module_disabled and it waits");
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task TheServersRulesFoldWhatWaits_ASubmittedScoreGoesThroughTheProgressQueue()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            server.ConfigRoute(new JObject());
            server.On("GET", "/v1/me/leaderboards", _ => FakeServer.Ok(BoardsView()));
            var krtu = await KrtuworksClient.InitializeAsync(server.Options());
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            await krtu.SubmitScoreAsync("fastest", 95000);
            await krtu.SubmitScoreAsync("fastest", 99000);
            Assert.That((await krtu.WaitingAsync()).Progress, Is.EqualTo(2), "rules not known yet: nothing folds");
            await krtu.GetLeaderboardsAsync();
            await krtu.SubmitScoreAsync("fastest", 97000);
            Assert.That((await krtu.WaitingAsync()).Progress, Is.EqualTo(1), "ASC KEEP_BEST: 95000 is the best of the three");
            await krtu.SubmitScoreAsync("latest", 10);
            await krtu.SubmitScoreAsync("latest", 30);
            Assert.That((await krtu.WaitingAsync()).Progress, Is.EqualTo(2), "FORCE_UPDATE: the last of the two; control: another board folds on its own");
            var sent = new List<JObject>();
            server.On("POST", "/v1/me/progress", call =>
            {
                var ops = ((JArray)call.Json!["ops"]!).Cast<JObject>().ToList();
                sent.AddRange(ops);
                return FakeServer.Ok(new JObject { ["results"] = new JArray(ops.Select(o => new JObject { ["opId"] = o["opId"], ["status"] = "applied", ["type"] = o["type"], ["key"] = o["key"], ["score"] = o["value"], ["changed"] = true })) });
            });
            await krtu.FlushAsync();
            Assert.That(sent.Select(o => ((string)o["type"]!, (string)o["key"]!, (double)o["value"]!)), Is.EquivalentTo(new[] { ("score", "fastest", 95000d), ("score", "latest", 30d) }));
            await krtu.ShutdownAsync();
        }

        sealed class FakeSteamBoards : IPlatformServices, IPlatformLeaderboards
        {
            internal readonly Dictionary<string, PlatformLeaderboardScore?> Rows = new Dictionary<string, PlatformLeaderboardScore?>();
            internal readonly List<string> Reads = new List<string>();
            public Task<string?> GetWebApiTicketAsync() => Task.FromResult<string?>("0a0b0c0d0e0f1011");
            public Task<IReadOnlyList<string>> UnlockedAchievementsAsync() => Task.FromResult<IReadOnlyList<string>>(new List<string>());
            public Task UnlockAchievementAsync(string platformId) => Task.CompletedTask;
            public Task ClearAchievementAsync(string platformId) => Task.CompletedTask;
            public Task<PlatformLeaderboardScore?> ReadOwnScoreAsync(string platformName)
            {
                Reads.Add(platformName);
                return Task.FromResult(Rows.TryGetValue(platformName, out var row) ? row : null);
            }
        }

        static void SteamBoardRoutes(FakeServer server, Func<string> account, Func<string> state, List<JObject> scoreOps)
        {
            server.GuestRoutes();
            server.AcceptEvents(new List<JObject>());
            server.On("POST", "/oidc/token", _ => FakeServer.Ok(FakeServer.TokenAnswer("pw_steam_gggggggggggg")));
            server.On("POST", "/v1/sessions", _ => new Reply
            {
                Status = 201,
                Json = new JObject
                {
                    ["session"] = new JObject { ["id"] = "ses_gggggggggggggggggggg", ["startedAt"] = Clock.IsoNow() },
                    ["platformLink"] = new JObject { ["platform"] = "STEAM", ["platformAccountId"] = account(), ["state"] = state() },
                },
            });
            server.On("POST", "/v1/me/platform-links/consent", _ => FakeServer.Ok(new JObject { ["platformLink"] = new JObject { ["platform"] = "STEAM", ["platformAccountId"] = account(), ["state"] = "ACTIVE" } }));
            server.On("POST", "/v1/me/progress/platform-sync", _ => FakeServer.Ok(new JObject { ["unlockedHere"] = new JArray(), ["toPlatform"] = new JArray(), ["toClear"] = new JArray() }));
            server.On("GET", "/v1/me/progress", _ => FakeServer.Ok(new JObject { ["stats"] = new JArray(), ["achievements"] = new JArray() }));
            server.On("GET", "/v1/me/leaderboards", _ => FakeServer.Ok(BoardsView()));
            server.On("POST", "/v1/me/progress", call =>
            {
                var ops = ((JArray)call.Json!["ops"]!).Cast<JObject>().ToList();
                lock (scoreOps) scoreOps.AddRange(ops.Where(o => (string)o["type"]! == "score"));
                return FakeServer.Ok(new JObject { ["results"] = new JArray(ops.Select(o => new JObject { ["opId"] = o["opId"], ["status"] = "applied", ["type"] = o["type"], ["key"] = o["key"], ["score"] = o["value"], ["changed"] = true })) });
            });
        }

        [Test]
        public async Task TheOwnSteamRowIsBroughtInOncePerBoardAndSteamAccount_OnlyForABoardThatTakesItAndHasASteamName()
        {
            var server = new FakeServer();
            var account = "76561198000000302";
            var scoreOps = new List<JObject>();
            server.ConfigRoute(new JObject());
            SteamBoardRoutes(server, () => account, () => "ACTIVE", scoreOps);
            var steam = new FakeSteamBoards();
            steam.Rows["fastest_run"] = new PlatformLeaderboardScore { Present = true, Score = 88123 };
            steam.Rows["latest_run"] = new PlatformLeaderboardScore { Present = true, Score = 5 };
            var store = new MemoryStore();
            KrtuworksOptions Launch(KrtuworksOptions o)
            {
                o.Platform = "STEAM";
                o.Sessions = true;
                o.PlatformServices = steam;
                return o;
            }

            var first = await KrtuworksClient.InitializeAsync(Launch(server.Options(store)));
            await first.SignInAsGuestAsync("2000-01-01", Terms);
            await first.ReadyAsync();
            await first.FlushAsync();
            Assert.That(steam.Reads, Is.EqualTo(new[] { "fastest_run" }), "control: latest has a Steam row but does not take imports, nosteam has no Steam name: neither is read");
            Assert.That(scoreOps.Select(o => ((string)o["key"]!, (double)o["value"]!, (bool?)o["fromPlatform"])), Is.EqualTo(new[] { ("fastest", 88123d, (bool?)true) }));
            Assert.That(scoreOps[0]["details"], Is.Null, "the platform's row brings no details");
            await first.ShutdownAsync();

            var second = await KrtuworksClient.InitializeAsync(Launch(server.Options(store)));
            await second.ReadyAsync();
            await second.FlushAsync();
            Assert.That(steam.Reads, Is.EqualTo(new[] { "fastest_run" }), "the next launch with the same Steam account does not read again");
            Assert.That(scoreOps.Count, Is.EqualTo(1));
            await second.ShutdownAsync();

            account = "76561198000000999";
            var third = await KrtuworksClient.InitializeAsync(Launch(server.Options(store)));
            await third.ReadyAsync();
            await third.FlushAsync();
            Assert.That(steam.Reads, Is.EqualTo(new[] { "fastest_run", "fastest_run" }), "control: another Steam account is its own once");
            Assert.That(scoreOps.Count, Is.EqualTo(2));
            await third.ShutdownAsync();
        }

        [Test]
        public async Task ABoardWithNoSteamRowOrNoAnswerIsHandledOnItsOwn_AnUnansweredReadIsAskedAgainNextLaunch()
        {
            var server = new FakeServer();
            var scoreOps = new List<JObject>();
            server.ConfigRoute(new JObject());
            SteamBoardRoutes(server, () => "76561198000000302", () => "ACTIVE", scoreOps);
            var steam = new FakeSteamBoards();
            steam.Rows["fastest_run"] = new PlatformLeaderboardScore { Present = false };
            var store = new MemoryStore();
            KrtuworksOptions Launch(KrtuworksOptions o)
            {
                o.Platform = "STEAM";
                o.Sessions = true;
                o.PlatformServices = steam;
                return o;
            }

            var first = await KrtuworksClient.InitializeAsync(Launch(server.Options(store)));
            await first.SignInAsGuestAsync("2000-01-01", Terms);
            await first.ReadyAsync();
            await first.FlushAsync();
            Assert.That((steam.Reads.Count, scoreOps.Count), Is.EqualTo((1, 0)), "no row on Steam: nothing to bring in");
            await first.ShutdownAsync();
            steam.Rows["fastest_run"] = new PlatformLeaderboardScore { Present = true, Score = 70000 };
            var second = await KrtuworksClient.InitializeAsync(Launch(server.Options(store)));
            await second.ReadyAsync();
            await second.FlushAsync();
            Assert.That((steam.Reads.Count, scoreOps.Count), Is.EqualTo((1, 0)), "an answered board is done: a row that appears later is the server's own mirror, not an import");
            await second.ShutdownAsync();

            var other = new MemoryStore();
            steam.Rows.Remove("fastest_run");
            var third = await KrtuworksClient.InitializeAsync(Launch(server.Options(other)));
            await third.SignInAsGuestAsync("2000-01-01", Terms);
            await third.ReadyAsync();
            Assert.That((steam.Reads.Count, scoreOps.Count), Is.EqualTo((2, 0)), "Steam did not answer");
            await third.ShutdownAsync();
            steam.Rows["fastest_run"] = new PlatformLeaderboardScore { Present = true, Score = 61000 };
            var fourth = await KrtuworksClient.InitializeAsync(Launch(server.Options(other)));
            await fourth.ReadyAsync();
            await fourth.FlushAsync();
            Assert.That(steam.Reads.Count, Is.EqualTo(3), "asked again");
            Assert.That(scoreOps.Select(o => (double)o["value"]!), Is.EqualTo(new[] { 61000d }), "answered, brought in");
            await fourth.ShutdownAsync();
        }

        [TestCase("PENDING_CONSENT")]
        [TestCase("MISMATCH")]
        [TestCase("LIMIT")]
        [TestCase("REFUSED")]
        public async Task NothingIsReadOrBroughtInWhileTheSteamAccountIsNotMirrored_ConsentThenDoesIt(string state)
        {
            var server = new FakeServer();
            var scoreOps = new List<JObject>();
            server.ConfigRoute(new JObject());
            SteamBoardRoutes(server, () => "76561198000000302", () => state, scoreOps);
            var steam = new FakeSteamBoards();
            steam.Rows["fastest_run"] = new PlatformLeaderboardScore { Present = true, Score = 88123 };
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(extra: o =>
            {
                o.Platform = "STEAM";
                o.Sessions = true;
                o.PlatformServices = steam;
            }));
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            await krtu.FlushAsync();
            Assert.That(steam.Reads, Is.Empty);
            Assert.That(scoreOps, Is.Empty);
            Assert.That(server.Count("GET", "/v1/me/leaderboards"), Is.EqualTo(0));
            if (state == "PENDING_CONSENT")
            {
                await krtu.AnswerPlatformConsentAsync(true);
                await krtu.FlushAsync();
                Assert.That(steam.Reads, Is.EqualTo(new[] { "fastest_run" }), "control: allowed, the row is brought in");
                Assert.That(scoreOps.Count, Is.EqualTo(1));
            }
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task NothingIsReadWhileTheLeaderboardsModuleIsOff_ControlAGameWithoutSteamLeaderboardsReadsNothing()
        {
            var server = new FakeServer();
            var scoreOps = new List<JObject>();
            server.ConfigRoute(new JObject { ["modules"] = new JObject { ["leaderboards"] = false } });
            SteamBoardRoutes(server, () => "76561198000000302", () => "ACTIVE", scoreOps);
            var steam = new FakeSteamBoards();
            steam.Rows["fastest_run"] = new PlatformLeaderboardScore { Present = true, Score = 88123 };
            var store = new MemoryStore();
            var first = await KrtuworksClient.InitializeAsync(server.Options(store, extra: o =>
            {
                o.Platform = "STEAM";
                o.Sessions = true;
                o.PlatformServices = steam;
            }));
            await first.RefreshConfigAsync();
            await first.SignInAsGuestAsync("2000-01-01", Terms);
            await first.ReadyAsync();
            Assert.That((steam.Reads.Count, server.Count("GET", "/v1/me/leaderboards")), Is.EqualTo((0, 0)));
            await first.ShutdownAsync();

            var plain = await KrtuworksClient.InitializeAsync(server.Options(new MemoryStore(), extra: o =>
            {
                o.Platform = "STEAM";
                o.Sessions = true;
                o.PlatformServices = new FakeSteam();
            }));
            await plain.SignInAsGuestAsync("2000-01-01", Terms);
            await plain.ReadyAsync();
            Assert.That(server.Count("GET", "/v1/me/leaderboards"), Is.EqualTo(0), "a platform without leaderboards imports nothing");
            await plain.ShutdownAsync();
        }

        [Test]
        public async Task AnotherFlowsDifferentPlayerTakenUpOnARefreshIsAnIdentityChange_ControlOnlyARefreshOfAIsSilent()
        {
            const string A = "pw_guest_aaaaaaaaaaaa";
            const string B = "pw_other_bbbbbbbbbbbb";
            foreach (var otherPlayer in new[] { true, false })
            {
                var server = new FakeServer();
                server.GuestRoutes(A);
                server.ConfigRoute(new JObject());
                server.AcceptEvents(new List<JObject>());
                var sessionFor = new List<string>();
                server.On("POST", "/v1/sessions", call =>
                {
                    var token = (call.Header("authorization") ?? "").Replace("Bearer ", "");
                    lock (sessionFor) sessionFor.Add((string)JObject.Parse(Encoding.UTF8.GetString(Ids.FromBase64Url(token.Split('.')[1])))["sub"]!);
                    return new Reply { Status = 201, Json = new JObject { ["session"] = new JObject { ["id"] = $"ses_{sessionFor.Count.ToString().PadLeft(20, 'a')}", ["startedAt"] = Clock.IsoNow() }, ["heartbeatSeconds"] = 60, ["serverTime"] = Clock.IsoNow() } };
                });
                server.On("POST", "/v1/sessions/ses_aaaaaaaaaaaaaaaaaaaa/end", _ => FakeServer.Ok(new JObject()));
                var store = new MemoryStore();
                var tab1 = await KrtuworksClient.InitializeAsync(server.Options(store, extra: o => o.Sessions = true));
                await tab1.SignInAsGuestAsync("2000-01-01", Terms);
                await tab1.ReadyAsync();
                Assert.That(sessionFor, Is.EqualTo(new[] { A }));
                var changes = new List<(string Type, string? Subject)>();
                tab1.IdentityChanged += c => { lock (changes) changes.Add((c.Type, c.Player?.Subject)); };
                var first = true;
                server.OnAsync("GET", "/v1/me/bug-reports", async _ =>
                {
                    if (!first) return FakeServer.Ok(new JObject { ["reports"] = new JArray(), ["serverTime"] = Clock.IsoNow() });
                    first = false;
                    // Another flow writes its tokens (B, or a fresher A) while this call is on its way; this one's token is then refused.
                    var exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600;
                    var subject = otherPlayer ? B : A;
                    await Json.Put(store, "krtu:buried.test.native:tokens", new StoredTokens
                    {
                        Kind = "guest",
                        Subject = subject,
                        AccessToken = FakeServer.FakeJwt(new JObject { ["sub"] = subject, ["aud"] = "devapi", ["exp"] = exp, ["scope"] = "app:player", ["n"] = "tab2" }),
                        AccessExpiresAt = exp * 1000,
                        RefreshToken = "rt-tab2",
                    });
                    return new Reply { Status = 401, Json = new JObject { ["error"] = new JObject { ["code"] = "token_expired", ["message"] = "x" } } };
                });
                if (otherPlayer)
                {
                    var e = await AsyncAssert.ThrowsAsync<KrtuworksException>(() => tab1.MyBugReportsAsync());
                    Assert.That(e!.Code, Is.EqualTo("context_changed"));
                    await tab1.ReadyAsync();
                    Assert.That(changes, Is.EqualTo(new[] { ("guest", (string?)B) }), "told exactly once, of B");
                    Assert.That(tab1.Player?.Subject, Is.EqualTo(B));
                    Assert.That(sessionFor, Is.EqualTo(new[] { A, B }), "B's session opened by the settle after the change");
                }
                else
                {
                    Assert.That(await tab1.MyBugReportsAsync(), Is.Empty);
                    await tab1.ReadyAsync();
                    Assert.That(changes, Is.Empty, "a plain refresh by another flow is silent");
                    Assert.That(sessionFor, Is.EqualTo(new[] { A }));
                }
                await tab1.ShutdownAsync();
            }
        }

        static JObject Groups(int value) => new JObject
        {
            ["experiments"] = new JArray(new JObject { ["key"] = "exp_hard", ["configKey"] = "difficulty", ["group"] = "B", ["value"] = value }),
        };

        [Test]
        public async Task AnAbGroupsValueIsUsedOnlyWhileTheAbModuleIsOn_ControlWithTheModuleOnTheGroupWins()
        {
            foreach (var abOn in new[] { true, false })
            {
                var server = new FakeServer();
                server.GuestRoutes();
                var config = new JObject { ["difficulty"] = 100 };
                if (!abOn) config["modules"] = new JObject { ["abTests"] = false };
                server.ConfigRoute(config);
                server.AcceptEvents(new List<JObject>());
                server.On("GET", "/v1/me/experiments", _ => FakeServer.Ok(Groups(200)));
                var krtu = await KrtuworksClient.InitializeAsync(server.Options());
                await krtu.RefreshConfigAsync();
                await krtu.SignInAsGuestAsync("2000-01-01", Terms);
                await krtu.ReadyAsync();
                await krtu.RefreshExperimentsAsync();
                Assert.That(krtu.Value("difficulty", 0), Is.EqualTo(abOn ? 200 : 100));
                await krtu.ShutdownAsync();
            }
        }

        [Test]
        public async Task AnotherPlayerDoesNotInheritTheLastPlayersAbValuesOrStatisticsChoice_ControlTheirOwnAnswersApply()
        {
            const string b = "pw_other_bbbbbbbbbbbb";
            var server = new FakeServer();
            server.GuestRoutes();
            server.ConfigRoute(new JObject { ["difficulty"] = 100 });
            server.AcceptEvents(new List<JObject>());
            var optedOut = true;
            server.On("GET", "/v1/me/telemetry-opt-out", _ => FakeServer.Ok(new JObject { ["optedOut"] = optedOut, ["updatedAt"] = null }));
            var groupValue = 200;
            server.On("GET", "/v1/me/experiments", _ => FakeServer.Ok(Groups(groupValue)));
            var krtu = await KrtuworksClient.InitializeAsync(server.Options());
            await krtu.RefreshConfigAsync();
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            await krtu.RefreshExperimentsAsync();
            Assert.That(krtu.Value("difficulty", 0), Is.EqualTo(200));
            Assert.That(krtu.Track("button", new { name = "x" }), Is.False, "A turned statistics off");

            await krtu.SignOutAsync();
            server.On("POST", "/oidc/token", _ => FakeServer.Ok(FakeServer.TokenAnswer(b)));
            // B's choice cannot be read (no answer): B has none kept on this device, so B is on.
            server.Offline.Add("/v1/me/telemetry-opt-out");
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            Assert.That(krtu.Player?.Subject, Is.EqualTo(b));
            Assert.That(krtu.Value("difficulty", 0), Is.EqualTo(100), "A's group is gone");
            Assert.That(krtu.Track("button", new { name = "x" }), Is.True, "A's choice is gone");

            server.Offline.Remove("/v1/me/telemetry-opt-out");
            groupValue = 150;
            await krtu.RefreshExperimentsAsync();
            Assert.That(krtu.Value("difficulty", 0), Is.EqualTo(150), "B's own group");
            Assert.That(await krtu.GetTelemetryOptOutAsync(), Is.True, "B's own choice");
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task ASessionOpenStillOnItsWayWhenTheSdkShutsDownDoesNotComeBack_TheLateSessionIsEnded()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            server.ConfigRoute(new JObject());
            server.AcceptEvents(new List<JObject>());
            var release = new TaskCompletionSource<bool>();
            server.OnAsync("POST", "/v1/sessions", async _ =>
            {
                await release.Task;
                return new Reply
                {
                    Status = 201,
                    Json = new JObject { ["session"] = new JObject { ["id"] = "ses_aaaaaaaaaaaaaaaaaaaa", ["startedAt"] = Clock.IsoNow() }, ["heartbeatSeconds"] = 1, ["serverTime"] = Clock.IsoNow() },
                };
            });
            server.OnPrefix("POST", "/v1/sessions/", _ => FakeServer.Ok(new JObject { ["nextHeartbeatSeconds"] = 1 }));
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(extra: o => o.Sessions = true));
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            while (server.Count("POST", "/v1/sessions") == 0) await Task.Delay(5);
            await krtu.ShutdownAsync();
            release.SetResult(true);
            await krtu.ReadyAsync();
            await Task.Delay(1500);
            Assert.That(server.Count("POST", "/v1/sessions/ses_aaaaaaaaaaaaaaaaaaaa/heartbeat"), Is.Zero, "no heartbeat after shutdown");
            Assert.That(server.Count("POST", "/v1/sessions/ses_aaaaaaaaaaaaaaaaaaaa/end"), Is.EqualTo(1), "the session the late answer named is ended");
        }
    }
}
