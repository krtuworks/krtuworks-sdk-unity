#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Krtuworks.Internal;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Krtuworks.Tests
{
    public class IdentityTests
    {
        const string Terms = "TERMS:v1 PRIVACY:v1";

        static (Identity identity, MemoryStore store, Clock clock) IdentityOn(FakeServer server, MemoryStore? store = null)
        {
            var shared = store ?? new MemoryStore();
            var opts = Resolved.From(server.Options(shared));
            var clock = new Clock();
            return (new Identity(opts, shared, shared, server, clock, null), shared, clock);
        }

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

        static FakeCall DeviceKeyGrant(FakeServer server, int index = 0) => server.Calls.Where(c => c.Field("grant_type") == Identity.DeviceKeyGrant).ElementAt(index);

        [Test]
        public async Task NewGuestNeedsTheAgeScreen_ControlWithItTheKeySignsAVerifiableProof()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            var (identity, _, _) = IdentityOn(server);
            await Throws(() => identity.SignInAsGuestAsync(null, null, false), "birth_date_required");
            Assert.That(server.Count("POST", "/oidc/token"), Is.Zero, "nothing reached the token endpoint without the age screen");
            var player = await identity.SignInAsGuestAsync("2000-01-01", Terms, false);
            Assert.That(player.Kind, Is.EqualTo(PlayerKind.Guest));
            Assert.That(player.Subject, Is.EqualTo("pw_guest_aaaaaaaaaaaa"));
            var grant = DeviceKeyGrant(server);
            var (header, payload, ok) = FakeServer.VerifyProof(grant.Field("assertion")!);
            Assert.That(ok, "the signature verifies with the header key");
            Assert.That(((JObject)header["jwk"]!).Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal), Is.EqualTo(new[] { "crv", "kty", "x", "y" }), "the header carries the public key and nothing more");
            Assert.That((string?)header["typ"], Is.EqualTo("krtu-device-key+jwt"));
            Assert.That((string?)payload["purpose"], Is.EqualTo("login"));
            Assert.That((string?)payload["client_id"], Is.EqualTo("buried.test.native"));
            Assert.That(grant.Field("scope"), Is.EqualTo("openid offline_access app:player"));
            Assert.That(grant.Field("resource"), Is.EqualTo("https://dev.krtuworks.com"));
        }

        [Test]
        public async Task OutdatedTermsComeBackWithTheDocumentsToShow()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            var (identity, _, _) = IdentityOn(server);
            var e = await Throws(() => identity.SignInAsGuestAsync("2000-01-01", "TERMS:v0 PRIVACY:v1", false), "terms_required");
            Assert.That((string?)e.Details["terms"], Is.EqualTo("TERMS:v2 PRIVACY:v1"));
            Assert.That(Identity.TermsIn("Accept the current terms: TERMS:v1 PRIVACY:v3."), Is.EqualTo("TERMS:v1 PRIVACY:v3"));
            Assert.That(Identity.TermsIn("nothing here"), Is.Null);
        }

        [Test]
        public async Task ADeviceClockAnHourOffStillSignsInsideTheServerMinute()
        {
            var server = new FakeServer { ClockOffset = TimeSpan.FromHours(1) };
            server.GuestRoutes();
            var (identity, _, _) = IdentityOn(server);
            await identity.SignInAsGuestAsync("2000-01-01", Terms, false);
            var (_, payload, _) = FakeServer.VerifyProof(DeviceKeyGrant(server).Field("assertion")!);
            var serverNow = (DateTimeOffset.UtcNow + server.ClockOffset).ToUnixTimeSeconds();
            Assert.That(Math.Abs((long)payload["iat"]! - serverNow), Is.LessThanOrEqualTo(2), "iat is the server's time");
        }

        [Test]
        public async Task TheSameKnownKeySignsInAgainWithoutTheAgeScreen()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            var store = new MemoryStore();
            var first = IdentityOn(server, store).identity;
            await first.SignInAsGuestAsync("2000-01-01", Terms, false);
            await first.SignOutAsync();
            var again = await IdentityOn(server, store).identity.SignInAsGuestAsync(null, null, false);
            Assert.That(again.Subject, Is.EqualTo("pw_guest_aaaaaaaaaaaa"));
            Assert.That(server.Calls.Count(c => c.Field("grant_type") == Identity.DeviceKeyGrant), Is.EqualTo(2));
            Assert.That(DeviceKeyGrant(server, 1).Field("birth_date"), Is.Null, "a known key sends no age answers");
        }

        [Test]
        public async Task TwoRefreshesAtOnceSpendTheRefreshTokenOnce()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            var (identity, _, _) = IdentityOn(server);
            await identity.SignInAsGuestAsync("2000-01-01", Terms, false);
            var both = await Task.WhenAll(identity.AccessTokenAsync(true), identity.AccessTokenAsync(true));
            Assert.That(both[0], Is.EqualTo(both[1]));
            Assert.That(server.Calls.Count(c => c.Field("grant_type") == "refresh_token"), Is.EqualTo(1));
        }

        [Test]
        public async Task ASecondInstanceThatAlreadyRefreshedIsUsedInsteadOfSpendingTheTokenAgain()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            var store = new MemoryStore();
            var a = IdentityOn(server, store).identity;
            await a.SignInAsGuestAsync("2000-01-01", Terms, false);
            var b = IdentityOn(server, store).identity;
            await b.LoadAsync();
            var stale = await a.AccessTokenAsync();
            await b.AccessTokenAsync(true);
            Assert.That(server.Calls.Count(c => c.Field("grant_type") == "refresh_token"), Is.EqualTo(1));
            var fresh = await a.AccessTokenAsync(true);
            Assert.That(fresh, Is.Not.EqualTo(stale));
            Assert.That(server.Calls.Count(c => c.Field("grant_type") == "refresh_token"), Is.EqualTo(1), "no second refresh");
        }

        [Test]
        public async Task ARefreshRefusedAsMergedSignsOutAndKeepsThePlayerWaitingForTheMergeCheck()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            var (identity, _, _) = IdentityOn(server);
            await identity.SignInAsGuestAsync("2000-01-01", Terms, false);
            server.On("POST", "/oidc/token", _ => FakeServer.OAuthError("invalid_grant", "account_merged"));
            var changes = new List<string>();
            identity.Changed += c => changes.Add(c.Type);
            await Throws(() => identity.AccessTokenAsync(true), "account_merged");
            Assert.That(identity.Player, Is.Null);
            Assert.That(changes, Is.EqualTo(new[] { "merged" }));
            Assert.That(await identity.WaitingOwnersAsync(), Is.EqualTo(new[] { "pw_guest_aaaaaaaaaaaa" }));
        }

        [Test]
        public async Task AFrozenAppKeepsTheTokens_ControlARevokedOneSignsTheGuestInAgainWithItsKey()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            var (identity, _, _) = IdentityOn(server);
            await identity.SignInAsGuestAsync("2000-01-01", Terms, false);
            var before = server.Calls.Count;
            var refused = "app_suspended";
            server.On("POST", "/oidc/token", call => call.Field("grant_type") == "refresh_token"
                ? FakeServer.OAuthError("invalid_grant", refused)
                : FakeServer.Ok(FakeServer.TokenAnswer("pw_guest_aaaaaaaaaaaa")));
            await Throws(() => identity.AccessTokenAsync(true), "invalid_grant");
            Assert.That(identity.Player, Is.Not.Null, "still signed in: the same refresh token works once the app is resumed");
            refused = "grant request is invalid";
            await identity.AccessTokenAsync(true);
            Assert.That(identity.Player, Is.Not.Null);
            Assert.That(server.Calls.Skip(before).Any(c => c.Path == "/oidc/device-key/challenge"), "signed in again with the key");
        }

        [Test]
        public async Task AGuestMadeInTheBackgroundIsNotUsedWhenASignInStartedMeanwhile()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            var (identity, store, _) = IdentityOn(server);
            var gate = new TaskCompletionSource<bool>();
            server.OnAsync("POST", "/oidc/device-key/challenge", async _ =>
            {
                await gate.Task;
                return FakeServer.Ok(new JObject { ["nonce"] = new string('n', 43), ["expires_in"] = 60 });
            });
            var guest = identity.SignInAsGuestAsync("2000-01-01", Terms, false);
            await Task.Delay(20);
            // A sign-in started elsewhere (another process on the same store) bumped the epoch.
            var meta = await identity.MetaAsync();
            meta.Epoch++;
            await Json.Put(store, "krtu:buried.test.native:meta", meta);
            gate.SetResult(true);
            await Throws(() => guest, "context_changed");
            Assert.That(identity.Player, Is.Null);
        }

        [Test]
        public async Task SignInAddressCarriesPkceConsentResourceAndTheGuest_CallbackChecksStateAndNonceOnce()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            var (identity, _, _) = IdentityOn(server);
            await identity.SignInAsGuestAsync("2000-01-01", Terms, false);
            var url = new Uri(await identity.BeginSignInAsync("http://127.0.0.1:5555/callback"));
            var q = url.Query.TrimStart('?').Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            Assert.That(url.GetLeftPart(UriPartial.Path), Is.EqualTo("https://id.krtuworks.com/oidc/auth"));
            Assert.That(q["code_challenge_method"], Is.EqualTo("S256"));
            Assert.That(q["prompt"], Is.EqualTo("consent"));
            Assert.That(q["resource"], Is.EqualTo("https://dev.krtuworks.com"));
            Assert.That(q["scope"], Does.Contain("offline_access"));
            Assert.That((string?)FakeServer.VerifyProof(q["krtu_guest"]).payload["purpose"], Is.EqualTo("promote"), "the guest is carried with a promote proof");
            var sentVerifier = "";
            server.On("POST", "/oidc/token", call =>
            {
                sentVerifier = call.Field("code_verifier")!;
                Assert.That(call.Field("redirect_uri"), Is.EqualTo("http://127.0.0.1:5555/callback"));
                return FakeServer.Ok(FakeServer.TokenAnswer("pw_guest_aaaaaaaaaaaa", new JObject { ["nonce"] = q["nonce"], ["preferred_username"] = "player_one" }));
            });
            await Throws(() => identity.CompleteSignInAsync("http://127.0.0.1:5555/callback?code=c&state=wrong"), "invalid_callback");
            var changes = new List<IdentityChange>();
            identity.Changed += c => changes.Add(c);
            var player = await identity.CompleteSignInAsync($"http://127.0.0.1:5555/callback?code=c&state={q["state"]}");
            Assert.That(player.Kind, Is.EqualTo(PlayerKind.Account));
            Assert.That(player.Username, Is.EqualTo("player_one"));
            Assert.That(Ids.Base64Url(Ids.Sha256(sentVerifier)), Is.EqualTo(q["code_challenge"]), "the verifier matches the challenge");
            Assert.That(changes.Select(c => (c.Type, c.Promoted)), Is.EqualTo(new[] { ("signed_in", true) }), "the guest became this Krtuworks ID");
            await Throws(() => identity.CompleteSignInAsync($"http://127.0.0.1:5555/callback?code=c&state={q["state"]}"), "invalid_callback");
        }

        [Test]
        public async Task AnIdTokenWithAnotherNonceIsRefused_ControlTheRightOneIsTaken()
        {
            var server = new FakeServer();
            var (identity, _, _) = IdentityOn(server);
            static Dictionary<string, string> Query(string url) => new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            var q = Query(await identity.BeginSignInAsync("http://127.0.0.1:5555/callback"));
            server.On("POST", "/oidc/token", _ => FakeServer.Ok(FakeServer.TokenAnswer("pw_acct_aaaaaaaaaaaa", new JObject { ["nonce"] = "not-it" })));
            await Throws(() => identity.CompleteSignInAsync($"http://127.0.0.1:5555/callback?code=c&state={q["state"]}"), "invalid_response");
            Assert.That(identity.Player, Is.Null);
            var q2 = Query(await identity.BeginSignInAsync("http://127.0.0.1:5555/callback"));
            server.On("POST", "/oidc/token", _ => FakeServer.Ok(FakeServer.TokenAnswer("pw_acct_aaaaaaaaaaaa", new JObject { ["nonce"] = q2["nonce"] })));
            Assert.That((await identity.CompleteSignInAsync($"http://127.0.0.1:5555/callback?code=c&state={q2["state"]}")).Subject, Is.EqualTo("pw_acct_aaaaaaaaaaaa"));
        }

        [Test]
        public async Task SigningInToAnotherAccountLeavesTheGuestWaitingForTheMergeCheck()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            var (identity, _, _) = IdentityOn(server);
            await identity.SignInAsGuestAsync("2000-01-01", Terms, false);
            var url = await identity.BeginSignInAsync("http://127.0.0.1:5555/callback");
            var q = new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            server.On("POST", "/oidc/token", _ => FakeServer.Ok(FakeServer.TokenAnswer("pw_acct_bbbbbbbbbbbb", new JObject { ["nonce"] = q["nonce"] })));
            await identity.CompleteSignInAsync($"http://127.0.0.1:5555/callback?code=c&state={q["state"]}");
            Assert.That(await identity.WaitingOwnersAsync(), Is.EqualTo(new[] { "pw_guest_aaaaaaaaaaaa" }));
        }

        [Test]
        public async Task DesktopSignInListensOnLoopback_TheBrowsersReturnFinishesIt()
        {
            var server = new FakeServer();
            var (identity, _, _) = IdentityOn(server);
            string? opened = null;
            server.On("POST", "/oidc/token", call =>
            {
                var nonce = new Uri(opened!).Query.TrimStart('?').Split('&').Select(p => p.Split('=')).First(p => p[0] == "nonce")[1];
                return FakeServer.Ok(FakeServer.TokenAnswer("pw_acct_cccccccccccc", new JObject { ["nonce"] = Uri.UnescapeDataString(nonce) }));
            });
            var signIn = identity.SignInWithBrowserAsync(url => opened = url, CancellationToken.None);
            while (opened == null) await Task.Delay(5);
            var q = new Uri(opened).Query.TrimStart('?').Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            var redirect = new Uri(q["redirect_uri"]);
            Assert.That(redirect.Host, Is.EqualTo("127.0.0.1"));
            Assert.That(redirect.AbsolutePath, Is.EqualTo("/callback"));
            // A stray request first (a favicon): answered 404, the listener keeps waiting.
            Assert.That(await Get(redirect.Port, "/favicon.ico"), Does.StartWith("HTTP/1.1 404"));
            var page = await Get(redirect.Port, $"/callback?code=c&state={q["state"]}&iss={Uri.EscapeDataString("https://id.krtuworks.com/oidc")}");
            Assert.That(page, Does.StartWith("HTTP/1.1 200").And.Contain("You are signed in"));
            Assert.That((await signIn).Subject, Is.EqualTo("pw_acct_cccccccccccc"));
        }

        [Test]
        public async Task ACancelledDesktopSignInStopsListening()
        {
            var server = new FakeServer();
            var (identity, _, _) = IdentityOn(server);
            using var cancel = new CancellationTokenSource();
            string? opened = null;
            var signIn = identity.SignInWithBrowserAsync(url => opened = url, cancel.Token);
            while (opened == null) await Task.Delay(5);
            cancel.Cancel();
            try
            {
                await signIn;
                Assert.Fail("expected a cancellation");
            }
            catch (OperationCanceledException) { }
            var port = new Uri(new Uri(opened).Query.TrimStart('?').Split('&').Select(p => p.Split('=')).Where(p => p[0] == "redirect_uri").Select(p => Uri.UnescapeDataString(p[1])).First()).Port;
            await AsyncAssert.ThrowsAsync<SocketException>(async () => await Get(port, "/callback"), "control: nothing listens any more");
        }

        static async Task<string> Get(int port, string path)
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            var stream = client.GetStream();
            var request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(request, 0, request.Length);
            var buffer = new byte[8192];
            var total = new StringBuilder();
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0) total.Append(Encoding.UTF8.GetString(buffer, 0, read));
            return total.ToString();
        }

        [Test]
        public async Task APhoneSignsInThroughTheAppsOwnScheme_ControlAReturnForAnotherSignInIsRefused()
        {
            var server = new FakeServer();
            var (identity, _, _) = IdentityOn(server);
            Dictionary<string, string>? asked = null;
            string? schemeGiven = null;
            server.On("POST", "/oidc/token", call =>
            {
                Assert.That(call.Field("redirect_uri"), Is.EqualTo("com.krtuworks.buried:/callback"), "the code is exchanged with the same return");
                return FakeServer.Ok(FakeServer.TokenAnswer("pw_phone_aaaaaaaaaaaa", new JObject { ["nonce"] = asked!["nonce"] }));
            });
            Task<string> Session(string url, string scheme, CancellationToken cancel, string? state = null)
            {
                asked = new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                schemeGiven = scheme;
                return Task.FromResult($"{scheme}:/callback?code=c&state={state ?? asked["state"]}&iss={Uri.EscapeDataString("https://id.krtuworks.com/oidc")}");
            }
            await Throws(() => identity.SignInWithAppAsync((url, scheme, cancel) => Session(url, scheme, cancel, "another-sign-in"), CancellationToken.None), "invalid_callback");
            Assert.That(identity.Player, Is.Null);
            var player = await identity.SignInWithAppAsync((url, scheme, cancel) => Session(url, scheme, cancel), CancellationToken.None);
            Assert.That(asked!["redirect_uri"], Is.EqualTo("com.krtuworks.buried:/callback"));
            Assert.That(schemeGiven, Is.EqualTo("com.krtuworks.buried"), "the platform session waits for the app's scheme");
            Assert.That(player.Subject, Is.EqualTo("pw_phone_aaaaaaaaaaaa"));
        }

        [Test]
        public async Task AConsoleSignsInWithACode_SlowDownWidensThePolling_RefreshGoesThroughTheBroker()
        {
            var server = new FakeServer();
            var (identity, _, _) = IdentityOn(server);
            server.On("POST", "/v1/device/authorize", call =>
            {
                Assert.That(call.Field("client_id"), Is.EqualTo("buried.test.device"));
                Assert.That(call.Field("device_name"), Is.EqualTo("Living room"));
                return FakeServer.Ok(new JObject
                {
                    ["device_code"] = "dc-1", ["user_code"] = "ABCD-EFGH-JK", ["verification_uri"] = "https://id.krtuworks.com/link",
                    ["verification_uri_complete"] = "https://id.krtuworks.com/link?code=ABCD-EFGH-JK", ["expires_in"] = 600, ["interval"] = 1,
                });
            });
            var polls = new List<DateTimeOffset>();
            server.On("POST", "/v1/device/token", call =>
            {
                Assert.That(call.Field("client_id"), Is.EqualTo("buried.test.device"));
                if (call.Field("grant_type") == "refresh_token") return FakeServer.Ok(FakeServer.TokenAnswer("pw_console_aaaaaaaa"));
                Assert.That(call.Field("grant_type"), Is.EqualTo(Identity.DeviceCodeGrant));
                Assert.That(call.Field("device_code"), Is.EqualTo("dc-1"));
                polls.Add(DateTimeOffset.UtcNow);
                if (polls.Count == 1) return FakeServer.OAuthError("authorization_pending", "");
                if (polls.Count == 2) return FakeServer.OAuthError("slow_down", "");
                return FakeServer.Ok(FakeServer.TokenAnswer("pw_console_aaaaaaaa", new JObject { ["preferred_username"] = "player_one" }));
            });
            var link = await identity.StartDeviceLinkAsync("Living room");
            Assert.That(link.UserCode, Is.EqualTo("ABCD-EFGH-JK"));
            var player = await identity.FinishDeviceLinkAsync(new DeviceLink
            {
                UserCode = link.UserCode, VerificationUri = link.VerificationUri, VerificationUriComplete = link.VerificationUriComplete,
                ExpiresIn = 600, Interval = 1, DeviceCode = "dc-1",
            }, CancellationToken.None);
            Assert.That(player.Subject, Is.EqualTo("pw_console_aaaaaaaa"));
            Assert.That(player.Username, Is.EqualTo("player_one"));
            Assert.That((polls[2] - polls[1]).TotalSeconds, Is.GreaterThan(5), "slow_down added five seconds to the interval");
            await identity.AccessTokenAsync(true);
            Assert.That(server.Count("POST", "/oidc/token"), Is.Zero, "a console's refresh goes through the broker, never the token endpoint");
            Assert.That(server.Calls.Count(c => c.Path == "/v1/device/token" && c.Field("grant_type") == "refresh_token"), Is.EqualTo(1));
        }

        [Test]
        public async Task SignOutRevokesTheRefreshTokenButKeepsTheGuestKey()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            server.On("POST", "/oidc/token/revocation", _ => new Reply { Status = 200 });
            var (identity, store, _) = IdentityOn(server);
            await identity.SignInAsGuestAsync("2000-01-01", Terms, false);
            await identity.SignOutAsync();
            Assert.That(identity.Player, Is.Null);
            Assert.That(server.Calls.Single(c => c.Path == "/oidc/token/revocation").Field("token_type_hint"), Is.EqualTo("refresh_token"));
            Assert.That(await store.GetAsync("krtu:buried.test.native:deviceKey"), Is.Not.Null, "the key stays: signing in as the guest again reaches it");
        }
    }
}
