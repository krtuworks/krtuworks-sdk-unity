#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Krtuworks.Internal;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Krtuworks.Tests
{
    /// <summary>DLC ownership: the offline rule (the server's answer of this launch, then Steam's own answer, then the last server answer kept).</summary>
    public class EntitlementTests
    {
        const string Terms = "TERMS:v1 PRIVACY:v1";

        /// <summary>A Steam client: a ticket for the session, and its own ownership answers by AppID (absent: it cannot tell).</summary>
        sealed class FakeSteamOwnership : IPlatformServices, IPlatformOwnership
        {
            internal readonly Dictionary<string, bool> Owns = new Dictionary<string, bool>();
            internal readonly List<string> Asked = new List<string>();
            internal bool Ticket = true;
            public Task<string?> GetWebApiTicketAsync() => Task.FromResult<string?>(Ticket ? "0a0b0c0d0e0f1011" : null);
            public Task<IReadOnlyList<string>> UnlockedAchievementsAsync() => Task.FromResult<IReadOnlyList<string>>(new List<string>());
            public Task UnlockAchievementAsync(string platformId) => Task.CompletedTask;
            public Task ClearAchievementAsync(string platformId) => Task.CompletedTask;

            public bool? OwnsProduct(string platformProductId)
            {
                Asked.Add(platformProductId);
                return Owns.TryGetValue(platformProductId, out var owns) ? owns : (bool?)null;
            }
        }

        static JObject Item(string sku, string? status, bool isChecked, string? productId) =>
            new JObject { ["sku"] = sku, ["source"] = "STEAM", ["status"] = status, ["checked"] = isChecked, ["platformProductId"] = productId };

        sealed class Rig
        {
            internal readonly FakeServer Server = new FakeServer();
            internal readonly FakeSteamOwnership Steam = new FakeSteamOwnership();
            internal readonly MemoryStore Store = new MemoryStore();
            /// <summary>What GET /v1/me/entitlements answers.</summary>
            internal JArray Stored = new JArray();
            /// <summary>What a session open answers (`entitlements`); omitted when null.</summary>
            internal JArray? Session;
            internal KrtuworksClient Krtu = null!;

            internal Rig()
            {
                Server.GuestRoutes();
                Server.ConfigRoute(new JObject());
                // A Steam ticket is the guest's grant on a STEAM platform: one account answers every grant.
                Server.On("POST", "/oidc/token", _ => FakeServer.Ok(FakeServer.TokenAnswer("pw_steam_dddddddddddd")));
                Server.On("GET", "/v1/me/entitlements", _ => FakeServer.Ok(new JObject { ["entitlements"] = Stored, ["serverTime"] = Clock.IsoNow() }));
                Server.On("POST", "/v1/sessions", _ =>
                {
                    var body = new JObject { ["session"] = new JObject { ["id"] = "ses_dddddddddddddddddddd", ["startedAt"] = Clock.IsoNow() }, ["heartbeatSeconds"] = 60 };
                    if (Session != null) body["entitlements"] = Session;
                    return new Reply { Status = 201, Json = body };
                });
            }

            internal async Task<KrtuworksClient> Launch(string platform = "STEAM", string? type = null)
            {
                var config = FakeServer.App;
                config.Type = type;
                var krtu = await KrtuworksClient.InitializeAsync(Server.Options(Store, null, o =>
                {
                    o.Config = config;
                    o.Platform = platform;
                    o.Sessions = true;
                    o.PlatformServices = Steam;
                }));
                await krtu.RefreshConfigAsync();
                await krtu.SignInAsGuestAsync("2000-01-01", Terms);
                await krtu.ReadyAsync();
                Krtu = krtu;
                return krtu;
            }
        }

        static JArray Licences() => new JArray(Item("base", "ACTIVE", false, "480"), Item("season-pass", "REVOKED", false, "482"), Item("soundtrack", "ACTIVE", false, "481"));

        [Test]
        public async Task ASteamLaunchFetchesTheStoredLicences_OwnsDlcIsTrueOnlyForAnActiveOne()
        {
            var rig = new Rig { Stored = Licences() };
            var krtu = await rig.Launch();
            Assert.That(rig.Server.Count("GET", "/v1/me/entitlements"), Is.EqualTo(1));
            Assert.That(krtu.OwnsDlc("soundtrack"), Is.True);
            Assert.That(krtu.OwnsDlc("season-pass"), Is.False, "refunded");
            Assert.That(krtu.OwnsDlc("never-heard-of"), Is.False);
            Assert.That(krtu.Entitlements.Select(e => (e.Sku, e.Status, e.Checked)), Is.EqualTo(new[] { ("base", "ACTIVE", false), ("season-pass", "REVOKED", false), ("soundtrack", "ACTIVE", false) }));
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task TheSessionAnswersConfirmedItemBeatsSteamsOwnAnswerAndTheKeptList_ControlAnUnconfirmedItemDoesNot()
        {
            var rig = new Rig { Stored = Licences() };
            rig.Server.Offline.Add("/v1/me/entitlements");
            rig.Steam.Owns["481"] = true;
            rig.Steam.Owns["482"] = false;
            rig.Session = new JArray(Item("base", "ACTIVE", true, "480"), Item("season-pass", "ACTIVE", true, "482"), Item("soundtrack", "REVOKED", true, "481"));
            var krtu = await rig.Launch();
            Assert.That(krtu.OwnsDlc("soundtrack"), Is.False, "the server confirmed the refund this launch, whatever the Steam client says");
            Assert.That(krtu.OwnsDlc("season-pass"), Is.True, "the server confirmed the purchase");
            Assert.That(rig.Steam.Asked, Is.Empty, "the platform client is not asked when the server confirmed");
            await krtu.ShutdownAsync();

            // The next launch: the server could not check (its items say checked false): Steam's own answer stands in.
            rig.Session = new JArray(Item("base", "ACTIVE", false, "480"), Item("season-pass", "ACTIVE", false, "482"), Item("soundtrack", "REVOKED", false, "481"));
            var again = await rig.Launch();
            Assert.That(again.OwnsDlc("soundtrack"), Is.True, "Steam took the money");
            Assert.That(again.OwnsDlc("season-pass"), Is.False, "Steam says the pass is not owned");
            Assert.That(again.Entitlements.Any(e => e.Checked), Is.False);
            await again.ShutdownAsync();
        }

        [Test]
        public async Task WithoutASessionCheckTheSteamClientsAnswerComesBeforeTheLastServerAnswer_AndWhenItCannotTellTheLastServerAnswerStands()
        {
            var rig = new Rig { Stored = Licences() };
            rig.Server.Offline.Add("/v1/sessions");
            rig.Steam.Owns["481"] = false;
            var krtu = await rig.Launch();
            Assert.That(krtu.OwnsDlc("soundtrack"), Is.False, "Steam says refunded; the kept ACTIVE does not outvote it");
            Assert.That(rig.Steam.Asked, Is.EqualTo(new[] { "481" }));
            Assert.That(krtu.OwnsDlc("season-pass"), Is.False, "the Steam client cannot tell: the kept REVOKED");
            rig.Steam.Owns["482"] = true;
            Assert.That(krtu.OwnsDlc("season-pass"), Is.True, "bought and Steam says so, though the kept answer says REVOKED");
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task TheLastServerAnswerSurvivesALaunchThatCannotReachTheServerAndHasNoSteamAnswer()
        {
            var rig = new Rig { Stored = Licences() };
            var first = await rig.Launch();
            await first.ShutdownAsync();
            rig.Server.Offline.Add("/v1/me/entitlements");
            rig.Server.Offline.Add("/v1/sessions");
            var second = await rig.Launch();
            Assert.That(second.OwnsDlc("soundtrack"), Is.True, "kept from the earlier launch");
            Assert.That(second.OwnsDlc("season-pass"), Is.False);
            Assert.That(second.Entitlements.Count, Is.EqualTo(3));
            await second.ShutdownAsync();
        }

        [Test]
        public async Task ADlcTheServerNeverSawIsListedWithItsSteamIdSoSteamCanAnswerForIt()
        {
            var rig = new Rig();
            rig.Server.Offline.Add("/v1/me/entitlements");
            rig.Session = new JArray(Item("base", "ACTIVE", false, "480"), Item("skins", null, false, "483"));
            rig.Steam.Owns["483"] = true;
            var krtu = await rig.Launch();
            Assert.That(krtu.Entitlements.Single(e => e.Sku == "skins").PlatformProductId, Is.EqualTo("483"));
            Assert.That(krtu.OwnsDlc("skins"), Is.True, "never owned by the server's word, owned by Steam's (bought while the server was away)");
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task RefreshEntitlementsKeepsTheAnswer_ADlcTheServerNoLongerListsBecomesUnowned()
        {
            var rig = new Rig { Stored = Licences() };
            var krtu = await rig.Launch();
            Assert.That(krtu.OwnsDlc("soundtrack"), Is.True);
            rig.Stored = new JArray(Item("base", "ACTIVE", false, "480"));
            var list = await krtu.RefreshEntitlementsAsync();
            Assert.That(krtu.OwnsDlc("soundtrack"), Is.False);
            var gone = list.Single(e => e.Sku == "soundtrack");
            Assert.That((gone.Status, gone.PlatformProductId), Is.EqualTo(((string?)null, "481")));
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task OnlyASteamGameFetchesOnItsOwn_ControlAnotherPlatformFetchesOnRequest_AnAppThatIsNotAGameOwnsNothingAndAsksNothing()
        {
            var windows = new Rig { Stored = Licences() };
            var onWindows = await windows.Launch("WINDOWS");
            Assert.That(windows.Server.Count("GET", "/v1/me/entitlements"), Is.EqualTo(0));
            Assert.That(onWindows.OwnsDlc("soundtrack"), Is.False);
            await onWindows.RefreshEntitlementsAsync();
            Assert.That(windows.Server.Count("GET", "/v1/me/entitlements"), Is.EqualTo(1));
            Assert.That(onWindows.OwnsDlc("soundtrack"), Is.True);
            await onWindows.ShutdownAsync();

            var app = new Rig { Stored = Licences() };
            var notGame = await app.Launch("STEAM", "MOBILE_APP");
            Assert.That(app.Server.Count("GET", "/v1/me/entitlements"), Is.EqualTo(0));
            Assert.That(notGame.OwnsDlc("soundtrack"), Is.False);
            Assert.That(notGame.Entitlements, Is.Empty);
            Assert.That(await notGame.RefreshEntitlementsAsync(), Is.Empty);
            Assert.That(app.Server.Count("GET", "/v1/me/entitlements"), Is.EqualTo(0));
            await notGame.ShutdownAsync();
        }
    }
}
