#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Krtuworks.Internal;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Krtuworks.Tests
{
    public class PresenceTests
    {
        const string Terms = "TERMS:v1 PRIVACY:v1";

        sealed class PlainPlatform : IPlatformServices
        {
            public Task<string?> GetWebApiTicketAsync() => Task.FromResult<string?>(null);
            public Task<IReadOnlyList<string>> UnlockedAchievementsAsync() => Task.FromResult<IReadOnlyList<string>>(new List<string>());
            public Task UnlockAchievementAsync(string platformId) => Task.CompletedTask;
            public Task ClearAchievementAsync(string platformId) => Task.CompletedTask;
        }

        sealed class FakePresenceSteam : IPlatformServices, IPlatformPresence
        {
            readonly List<string> calls = new List<string>();
            /// <summary>Keys Steam refuses to take.</summary>
            internal readonly HashSet<string> Refused = new HashSet<string>();

            /// <summary>Every presence call in order: `clear` or `key=value`.</summary>
            internal List<string> Calls
            {
                get { lock (calls) return calls.ToList(); }
            }

            internal void Forget()
            {
                lock (calls) calls.Clear();
            }

            public bool SetRichPresence(string key, string value)
            {
                lock (calls) calls.Add($"{key}={value}");
                return !Refused.Contains(key);
            }

            public void ClearRichPresence()
            {
                lock (calls) calls.Add("clear");
            }

            public Task<string?> GetWebApiTicketAsync() => Task.FromResult<string?>(null);
            public Task<IReadOnlyList<string>> UnlockedAchievementsAsync() => Task.FromResult<IReadOnlyList<string>>(new List<string>());
            public Task UnlockAchievementAsync(string platformId) => Task.CompletedTask;
            public Task ClearAchievementAsync(string platformId) => Task.CompletedTask;
        }

        static JObject State(string key, string token, params string[] parameters) =>
            new JObject { ["key"] = key, ["token"] = token, ["params"] = new JArray(parameters) };

        sealed class Rig
        {
            internal readonly FakeServer Server = new FakeServer();
            internal readonly FakePresenceSteam Steam = new FakePresenceSteam();
            internal readonly MemoryStore Store = new MemoryStore();
            internal bool Visible = true;
            internal KrtuworksClient Krtu = null!;

            string Etag => Visible ? "\"p-1\"" : "\"p-0\"";

            internal void PresenceRoutes()
            {
                Server.On("GET", "/v1/me/presence-states", call =>
                {
                    if (call.Header("if-none-match") == Etag) return new Reply { Status = 304 };
                    var reply = FakeServer.Ok(new JObject
                    {
                        ["states"] = new JArray(
                            State("in_menu", "#Presence_Menu"),
                            State("in_level", "#Presence_Level", "LEVEL", "SESSION_MINUTES"),
                            State("in_match", "#Presence_Match", "MAP"),
                            State("scored", "#Presence_Scored", "SCORE")),
                        ["visible"] = Visible,
                        ["serverTime"] = Clock.IsoNow(),
                    });
                    reply.Headers["etag"] = Etag;
                    return reply;
                });
                Server.On("PUT", "/v1/me/presence-visibility", call =>
                {
                    Visible = (bool)call.Json!["visible"]!;
                    return FakeServer.Ok(new JObject { ["visible"] = Visible, ["updatedAt"] = Clock.IsoNow() });
                });
            }

            internal static async Task<Rig> Boot(Action<Rig>? before = null, Action<KrtuworksOptions>? extra = null, JObject? config = null)
            {
                var rig = new Rig();
                rig.Server.GuestRoutes();
                rig.Server.ConfigRoute(config ?? new JObject());
                rig.PresenceRoutes();
                before?.Invoke(rig);
                rig.Krtu = await rig.StartAsync(extra);
                await rig.Krtu.RefreshConfigAsync();
                await rig.Krtu.SignInAsGuestAsync("2000-01-01", Terms);
                await rig.Krtu.ReadyAsync();
                return rig;
            }

            internal Task<KrtuworksClient> StartAsync(Action<KrtuworksOptions>? extra = null) =>
                KrtuworksClient.InitializeAsync(Server.Options(Store, null, o =>
                {
                    o.PlatformServices = Steam;
                    extra?.Invoke(o);
                }));
        }

        static Dictionary<string, object> Values(params (string Name, object Value)[] pairs) => pairs.ToDictionary(p => p.Name, p => p.Value);

        [Test]
        public async Task AStateTheAppDoesNotHaveIsRefusedAndNothingIsWritten_ControlADeclaredOneIsApplied()
        {
            var rig = await Rig.Boot();
            var unknown = await rig.Krtu.SetPresenceAsync("in_lobby");
            Assert.That((unknown.Applied, unknown.Reason), Is.EqualTo((false, "unknown_state")));
            Assert.That(rig.Steam.Calls, Is.Empty, "a refused state writes nothing");
            var known = await rig.Krtu.SetPresenceAsync("in_menu");
            Assert.That((known.Applied, known.Reason), Is.EqualTo((true, (string?)null)));
            Assert.That(rig.Steam.Calls, Is.EqualTo(new[] { "clear", "steam_display=#Presence_Menu" }));
            await rig.Krtu.ShutdownAsync();
        }

        [Test]
        public async Task ADeclaredParameterWithNoValueIsRefused_ControlTheSameStateWithItIsApplied_TheDisplayTokenIsSetLast()
        {
            var rig = await Rig.Boot();
            foreach (var empty in new IDictionary<string, object>?[] { null, new Dictionary<string, object>(), Values(("MAP", "  ")), Values(("MAP", null!)), Values(("OTHER", "x")) })
            {
                var missing = await rig.Krtu.SetPresenceAsync("in_match", empty);
                Assert.That((missing.Applied, missing.Reason), Is.EqualTo((false, "param_missing")));
            }
            Assert.That(rig.Steam.Calls, Is.Empty, "a state that cannot be filled writes nothing");
            var applied = await rig.Krtu.SetPresenceAsync("in_match", Values(("MAP", " dust "), ("EXTRA", "dropped")));
            Assert.That(applied.Applied, Is.True);
            Assert.That(rig.Steam.Calls, Is.EqualTo(new[] { "clear", "MAP=dust", "steam_display=#Presence_Match" }), "clear, the declared keys, the display token last; an undeclared parameter is dropped");
            await rig.Krtu.ShutdownAsync();
        }

        [Test]
        public async Task LevelAndSessionMinutesFillThemselves_AGivenValueWins_ALevelStartWritesTheStateAgain()
        {
            var rig = await Rig.Boot();
            var noLevel = await rig.Krtu.SetPresenceAsync("in_level");
            Assert.That((noLevel.Applied, noLevel.Reason), Is.EqualTo((false, "param_missing")), "no level is open, so LEVEL has nothing to fill it");
            Assert.That(rig.Steam.Calls, Is.Empty);
            await rig.Krtu.LevelStartAsync(new LevelRef("1-1", "forest"));
            Assert.That((await rig.Krtu.SetPresenceAsync("in_level")).Applied, Is.True);
            Assert.That(rig.Steam.Calls, Is.EqualTo(new[] { "clear", "LEVEL=1-1", "SESSION_MINUTES=0", "steam_display=#Presence_Level" }), "the open attempt's level and the minutes of play");
            rig.Steam.Forget();
            await rig.Krtu.LevelStartAsync(new LevelRef("2-1"));
            Assert.That(rig.Steam.Calls, Is.EqualTo(new[] { "clear", "LEVEL=2-1", "SESSION_MINUTES=0", "steam_display=#Presence_Level" }), "a level start writes the state with the new level");
            rig.Steam.Forget();
            Assert.That((await rig.Krtu.SetPresenceAsync("in_level", Values(("LEVEL", "Secret Room")))).Applied, Is.True);
            Assert.That(rig.Steam.Calls, Contains.Item("LEVEL=Secret Room"), "a value the game gives is not replaced by the built-in");
            rig.Steam.Forget();
            await rig.Krtu.LevelStartAsync(new LevelRef("3-1"));
            Assert.That(rig.Steam.Calls, Is.Empty, "LEVEL is the game's now, so a level start writes nothing");
            await rig.Krtu.ShutdownAsync();
        }

        [Test]
        public async Task ValuesAreTextInTheInvariantCulture_CutAt256BytesOnACharacterBoundary()
        {
            var rig = await Rig.Boot();
            var old = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            try
            {
                Assert.That((await rig.Krtu.SetPresenceAsync("scored", Values(("SCORE", 1234567.5)))).Applied, Is.True);
            }
            finally
            {
                CultureInfo.CurrentCulture = old;
            }
            Assert.That(rig.Steam.Calls, Contains.Item("SCORE=1234567.5"), "no grouping, a point for the decimal");
            rig.Steam.Forget();
            Assert.That((await rig.Krtu.SetPresenceAsync("scored", Values(("SCORE", 12345)))).Applied, Is.True);
            Assert.That(rig.Steam.Calls, Contains.Item("SCORE=12345"));
            rig.Steam.Forget();
            await rig.Krtu.SetPresenceAsync("scored", Values(("SCORE", string.Concat(Enumerable.Repeat("é", 200)))));
            var cut = rig.Steam.Calls.First(c => c.StartsWith("SCORE=", StringComparison.Ordinal)).Substring("SCORE=".Length);
            Assert.That((cut.Length, Encoding.UTF8.GetByteCount(cut)), Is.EqualTo((128, 256)), "200 two-byte characters are cut to the 128 that fit");
            var pairs = string.Concat(Enumerable.Repeat("\U0001F600", 100));
            var kept = PresenceKeeper.Limit(pairs);
            Assert.That((kept.Length, Encoding.UTF8.GetByteCount(kept)), Is.EqualTo((128, 256)), "a surrogate pair is never split: 64 whole characters");
            Assert.That(PresenceKeeper.Limit("x" + pairs).Length, Is.EqualTo(1 + 2 * 63), "control: 253 bytes fit, the next 4-byte character does not");
            await rig.Krtu.ShutdownAsync();
        }

        [Test]
        public async Task ASteamRefusalClearsThePresenceAndIsTold_ControlTheNextAcceptedWriteWorks()
        {
            var rig = await Rig.Boot();
            rig.Steam.Refused.Add("MAP");
            var refused = await rig.Krtu.SetPresenceAsync("in_match", Values(("MAP", "dust")));
            Assert.That((refused.Applied, refused.Reason), Is.EqualTo((false, "platform_rejected")));
            Assert.That(rig.Steam.Calls, Is.EqualTo(new[] { "clear", "MAP=dust", "clear" }), "the write stops at the refusal and the presence is cleared, the display token never set");
            rig.Steam.Refused.Clear();
            rig.Steam.Forget();
            Assert.That((await rig.Krtu.SetPresenceAsync("in_match", Values(("MAP", "dust")))).Applied, Is.True);
            Assert.That(rig.Steam.Calls.Last(), Is.EqualTo("steam_display=#Presence_Match"));
            rig.Steam.Refused.Add("steam_display");
            rig.Steam.Forget();
            Assert.That((await rig.Krtu.SetPresenceAsync("in_menu")).Reason, Is.EqualTo("platform_rejected"), "the display token itself can be refused");
            Assert.That(rig.Steam.Calls.Last(), Is.EqualTo("clear"));
            await rig.Krtu.ShutdownAsync();
        }

        [Test]
        public async Task AHiddenPlayerWritesNothing_ShowingWritesTheWantedState_HidingClearsAtOnce()
        {
            var rig = await Rig.Boot(r => r.Visible = false);
            Assert.That(rig.Krtu.PresenceVisible, Is.False);
            var hidden = await rig.Krtu.SetPresenceAsync("in_match", Values(("MAP", "dust")));
            Assert.That((hidden.Applied, hidden.Reason), Is.EqualTo((false, "hidden")));
            Assert.That(rig.Steam.Calls, Is.Empty);
            await rig.Krtu.SetPresenceVisibleAsync(true);
            Assert.That(rig.Server.Calls.Last(c => c.Path == "/v1/me/presence-visibility").Json!["visible"]!.Value<bool>(), Is.True);
            Assert.That(rig.Krtu.PresenceVisible, Is.True);
            Assert.That(rig.Steam.Calls, Is.EqualTo(new[] { "clear", "MAP=dust", "steam_display=#Presence_Match" }), "what the game wanted while hidden is written when the player shows it");
            rig.Steam.Forget();
            await rig.Krtu.SetPresenceVisibleAsync(false);
            Assert.That(rig.Steam.Calls, Is.EqualTo(new[] { "clear" }));
            Assert.That(rig.Krtu.PresenceVisible, Is.False);
            Assert.That((await rig.Krtu.SetPresenceAsync("in_menu")).Reason, Is.EqualTo("hidden"));
            await rig.Krtu.SetPresenceVisibleAsync(true);
            Assert.That(rig.Steam.Calls.Last(), Is.EqualTo("steam_display=#Presence_Menu"), "the latest wanted state is the one that comes back");
            await rig.Krtu.ShutdownAsync();
        }

        [Test]
        public async Task AModuleTheOwnerSwitchedOffClearsAndRefuses_ControlItWasWrittenWhileOn()
        {
            var rig = await Rig.Boot();
            Assert.That((await rig.Krtu.SetPresenceAsync("in_menu")).Applied, Is.True);
            rig.Steam.Forget();
            rig.Server.ConfigRoute(new JObject { ["modules"] = new JObject { ["presence"] = false } }, startVersion: 2);
            await rig.Krtu.RefreshConfigAsync();
            var off = await rig.Krtu.SetPresenceAsync("in_menu");
            Assert.That((off.Applied, off.Reason), Is.EqualTo((false, "module_disabled")));
            Assert.That(rig.Steam.Calls, Is.EqualTo(new[] { "clear" }), "the presence it wrote is cleared, nothing new is written");
            Assert.That(rig.Krtu.ModuleStates()["presence"], Is.False);
            var visibleCalls = rig.Server.Count("PUT", "/v1/me/presence-visibility");
            try
            {
                await rig.Krtu.SetPresenceVisibleAsync(false);
                Assert.Fail("expected module_disabled");
            }
            catch (KrtuworksException e)
            {
                Assert.That(e.Code, Is.EqualTo("module_disabled"));
            }
            Assert.That(rig.Server.Count("PUT", "/v1/me/presence-visibility"), Is.EqualTo(visibleCalls), "no call goes out while the module is off");
            await rig.Krtu.ShutdownAsync();
        }

        [Test]
        public async Task TheStatesAreKeptAndRevalidatedWithTheirETag_ALaterLaunchUsesTheKeptCopyOn304()
        {
            var rig = await Rig.Boot();
            var first = rig.Server.Calls.Where(c => c.Path == "/v1/me/presence-states").ToList();
            Assert.That(first.Count, Is.EqualTo(1));
            Assert.That(first[0].Header("if-none-match"), Is.Null, "nothing kept yet");
            Assert.That(rig.Krtu.PresenceVisible, Is.True);
            await rig.Krtu.ShutdownAsync();

            var second = await rig.StartAsync();
            Assert.That(second.PresenceVisible, Is.True, "the choice kept on this device is known at launch");
            await second.ReadyAsync();
            var asks = rig.Server.Calls.Where(c => c.Path == "/v1/me/presence-states").ToList();
            Assert.That(asks.Count, Is.EqualTo(2));
            Assert.That(asks[1].Header("if-none-match"), Is.EqualTo("\"p-1\""), "revalidated with the kept ETag");
            Assert.That((await second.SetPresenceAsync("in_match", Values(("MAP", "dust")))).Applied, Is.True, "a 304 leaves the kept states in use");
            await second.ShutdownAsync();
        }

        [Test]
        public async Task WithoutKeptStatesTheAppIsToldSoAndNothingThrows_ControlAnAnsweringServerHasThem()
        {
            var rig = await Rig.Boot(r => r.Server.On("GET", "/v1/me/presence-states", _ => new Reply
            {
                Status = 403,
                Json = new JObject { ["error"] = new JObject { ["code"] = "module_disabled", ["message"] = "off" } },
            }));
            var unknown = await rig.Krtu.SetPresenceAsync("in_menu");
            Assert.That((unknown.Applied, unknown.Reason), Is.EqualTo((false, "states_unknown")));
            Assert.That(rig.Krtu.PresenceVisible, Is.Null, "nothing was read or chosen");
            Assert.That(rig.Steam.Calls, Is.Empty);
            await rig.Krtu.ShutdownAsync();

            var offline = await Rig.Boot(r => r.Server.Offline.Add("/v1/me/presence-states"));
            Assert.That((await offline.Krtu.SetPresenceAsync("in_menu")).Reason, Is.EqualTo("states_unknown"), "offline at the first launch");
            await offline.Krtu.ShutdownAsync();

            var control = await Rig.Boot();
            Assert.That((await control.Krtu.SetPresenceAsync("in_menu")).Applied, Is.True);
            await control.Krtu.ShutdownAsync();
        }

        [Test]
        public async Task AnAppThatIsNotAGameHasNoPresence_AGameWhosePlatformHasNoneIsToldSo()
        {
            var config = FakeServer.App;
            config.Type = "MOBILE_APP";
            var app = new Rig();
            app.Server.GuestRoutes();
            app.Server.ConfigRoute(new JObject());
            app.PresenceRoutes();
            app.Krtu = await app.StartAsync(o => o.Config = config);
            await app.Krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await app.Krtu.ReadyAsync();
            var notGame = await app.Krtu.SetPresenceAsync("in_menu");
            Assert.That((notGame.Applied, notGame.Reason), Is.EqualTo((false, "not_game")));
            app.Krtu.SetPresenceGroup("party", 2);
            Assert.That(app.Steam.Calls, Is.Empty);
            Assert.That(app.Server.Count("GET", "/v1/me/presence-states"), Is.EqualTo(0), "an app never asks for presence states");
            await app.Krtu.ShutdownAsync();

            var plain = await Rig.Boot(extra: o => o.PlatformServices = new PlainPlatform());
            var none = await plain.Krtu.SetPresenceAsync("in_menu");
            Assert.That((none.Applied, none.Reason), Is.EqualTo((false, "no_platform")));
            await plain.Krtu.ShutdownAsync();
        }

        [Test]
        public async Task TheGroupKeysAreWrittenWithEveryStateAndSurviveAReapply_NullClearsThem()
        {
            var rig = await Rig.Boot();
            rig.Krtu.SetPresenceGroup("party-7", 3);
            Assert.That(rig.Steam.Calls, Is.EqualTo(new[] { "steam_player_group=party-7", "steam_player_group_size=3" }), "written at once");
            rig.Steam.Forget();
            await rig.Krtu.SetPresenceAsync("in_level", Values(("LEVEL", "1-1")));
            var expected = new[] { "clear", "LEVEL=1-1", "SESSION_MINUTES=0", "steam_player_group=party-7", "steam_player_group_size=3", "steam_display=#Presence_Level" };
            Assert.That(rig.Steam.Calls, Is.EqualTo(expected), "after the clear, before the display token");
            rig.Steam.Forget();
            await rig.Krtu.LevelStartAsync(new LevelRef("1-2"));
            Assert.That(rig.Steam.Calls, Is.Empty, "control: a level start does not touch a state whose LEVEL the game gave");
            await rig.Krtu.SetPresenceAsync("in_level");
            rig.Steam.Forget();
            await rig.Krtu.LevelStartAsync(new LevelRef("1-3"));
            Assert.That(rig.Steam.Calls, Is.EqualTo(new[] { "clear", "LEVEL=1-3", "SESSION_MINUTES=0", "steam_player_group=party-7", "steam_player_group_size=3", "steam_display=#Presence_Level" }), "the group keys survive a reapply");
            rig.Steam.Forget();
            rig.Krtu.SetPresenceGroup(null);
            Assert.That(rig.Steam.Calls, Is.EqualTo(new[] { "steam_player_group=", "steam_player_group_size=" }), "an empty value removes a key");
            rig.Steam.Forget();
            await rig.Krtu.SetPresenceAsync("in_menu");
            Assert.That(rig.Steam.Calls, Is.EqualTo(new[] { "clear", "steam_display=#Presence_Menu" }), "a cleared group is gone from the next state");
            Assert.Throws<ArgumentException>(() => rig.Krtu.SetPresenceGroup("party", 0));
            await rig.Krtu.ShutdownAsync();
        }

        [Test]
        public async Task ClearingAndSigningOutClearAndForget()
        {
            var rig = await Rig.Boot();
            await rig.Krtu.SetPresenceAsync("in_match", Values(("MAP", "dust")));
            rig.Steam.Forget();
            rig.Krtu.ClearPresence();
            Assert.That(rig.Steam.Calls, Is.EqualTo(new[] { "clear" }));
            rig.Steam.Forget();
            await rig.Krtu.SetPresenceVisibleAsync(false);
            await rig.Krtu.SetPresenceVisibleAsync(true);
            Assert.That(rig.Steam.Calls.Where(c => c.StartsWith("steam_display", StringComparison.Ordinal)), Is.Empty, "a cleared state is forgotten: showing writes nothing");

            await rig.Krtu.SetPresenceAsync("in_menu");
            rig.Steam.Forget();
            await rig.Krtu.SignOutAsync();
            Assert.That(rig.Steam.Calls, Is.EqualTo(new[] { "clear" }), "signing out clears the platform's presence");
            Assert.That(rig.Krtu.PresenceVisible, Is.Null, "and forgets the player's cache");
            Assert.That((await rig.Krtu.SetPresenceAsync("in_menu")).Reason, Is.EqualTo("states_unknown"));
            await rig.Krtu.ShutdownAsync();
        }

        [Test]
        public async Task AHeartbeatWritesAStateThatUsesABuiltInAgain_ControlNotOneThatDoesNot()
        {
            var rig = await Rig.Boot(r =>
            {
                r.Server.On("POST", "/v1/sessions", _ => new Reply { Status = 201, Json = new JObject { ["session"] = new JObject { ["id"] = "ses_aaaaaaaaaaaaaaaaaaaa", ["startedAt"] = Clock.IsoNow() }, ["heartbeatSeconds"] = 1 } });
                r.Server.On("POST", "/v1/sessions/ses_aaaaaaaaaaaaaaaaaaaa/heartbeat", _ => FakeServer.Ok(new JObject { ["nextHeartbeatSeconds"] = 1 }));
                r.Server.On("POST", "/v1/sessions/ses_aaaaaaaaaaaaaaaaaaaa/end", _ => FakeServer.Ok(new JObject()));
            }, o => o.Sessions = true);
            await rig.Krtu.SetPresenceAsync("in_level", Values(("LEVEL", "1-1")));
            var waited = 0;
            while (rig.Steam.Calls.Count(c => c == "steam_display=#Presence_Level") < 2 && waited < 6000)
            {
                await Task.Delay(100);
                waited += 100;
            }
            Assert.That(rig.Steam.Calls.Count(c => c == "steam_display=#Presence_Level"), Is.GreaterThanOrEqualTo(2), "SESSION_MINUTES moved with the heartbeat");
            await rig.Krtu.SetPresenceAsync("in_match", Values(("MAP", "dust")));
            var before = rig.Steam.Calls.Count;
            await Task.Delay(2500);
            Assert.That(rig.Steam.Calls.Count, Is.EqualTo(before), "a state with no built-in is not written again by heartbeats");
            await rig.Krtu.ShutdownAsync();
        }
    }
}
