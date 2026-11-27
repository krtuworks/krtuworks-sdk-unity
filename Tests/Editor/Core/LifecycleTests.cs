#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Krtuworks.Internal;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Krtuworks.Tests
{
    /// <summary>The remote config, the loopback receiver, the secret files, the mirror, sessions, the settings pace, the clocks and the screens' small decisions.</summary>
    public class LifecycleTests
    {
        const string Terms = "TERMS:v1 PRIVACY:v1";
        const string A = "pw_guest_aaaaaaaaaaaa";
        const string B = "pw_other_bbbbbbbbbbbb";
        const string ConfigPath = "/v1/apps/buried/config";

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

        static async Task Until(Func<bool> condition, string what, int tenMs = 500)
        {
            for (var i = 0; i < tenMs && !condition(); i++) await Task.Delay(10);
            Assert.That(condition(), what);
        }

        static async Task<KrtuworksClient> Start(FakeServer server, MemoryStore? store = null, Action<KrtuworksOptions>? extra = null)
        {
            server.GuestRoutes(A);
            server.ConfigRoute(new JObject());
            server.AcceptEvents(new List<JObject>());
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(store, extra: extra));
            await krtu.RefreshConfigAsync();
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            return krtu;
        }

        static JObject FileAnswer(string slot) => new JObject
        {
            ["id"] = "fil_aaaaaaaaaaaaaaaaaaaa", ["slot"] = slot, ["generation"] = 1, ["sizeBytes"] = 1, ["sha256"] = "", ["savedAt"] = "2026-11-25T10:00:00.000Z", ["fromMerge"] = false,
        };

        // ── The remote config ─────────────────────────────────────────────────

        [Test]
        public async Task APageThatIsNotTheConfigLeavesTheKeptCopyAlone_ControlAnObjectReplacesIt()
        {
            var store = new MemoryStore();
            var server = new FakeServer();
            server.ConfigRoute(new JObject { ["speed"] = 3 });
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(store));
            await krtu.RefreshConfigAsync();
            var kept = await store.GetAsync("krtu:buried.test.native:config:WINDOWS");
            Assert.That(krtu.Value("speed", 0), Is.EqualTo(3));
            server.On("GET", ConfigPath, _ =>
            {
                var portal = new Reply { Body = Encoding.UTF8.GetBytes("<html>sign in to the wifi</html>") };
                portal.Headers["content-type"] = "text/html";
                return portal;
            });
            await Throws(() => krtu.RefreshConfigAsync(), "invalid_response");
            Assert.That(krtu.Value("speed", 0), Is.EqualTo(3), "the kept copy stays");
            Assert.That(await store.GetAsync("krtu:buried.test.native:config:WINDOWS"), Is.EqualTo(kept), "and nothing was written over it");
            server.ConfigRoute(new JObject { ["speed"] = 4 }, startVersion: 2);
            await krtu.RefreshConfigAsync();
            Assert.That(krtu.Value("speed", 0), Is.EqualTo(4), "control: a real answer replaces it");
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task FirstLaunchOnlineNamesAStateWhenTheServerCannotBeReached_ControlsOfflineFirstAndAKeptConfig()
        {
            static void Online(KrtuworksOptions o) => o.Config = KrtuworksAppConfig.FromJson(
                "{\"appId\":\"app_aaaaaaaaaaaaaaaaaaaa\",\"slug\":\"buried\",\"firstLaunch\":\"online\",\"environments\":{\"TEST\":{\"clientId\":\"buried.test.native\"}}}");
            var server = new FakeServer();
            server.GuestRoutes(A);
            server.ConfigRoute(new JObject());
            server.Offline.Add(ConfigPath);
            var store = new MemoryStore();
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(store, extra: Online));
            Assert.That(krtu.State, Is.EqualTo(KrtuworksClient.StateFirstLaunchOffline));
            server.Offline.Remove(ConfigPath);
            await krtu.RefreshConfigAsync();
            Assert.That(krtu.State, Is.EqualTo(KrtuworksClient.StateReady), "the state ends when the server answers");
            await krtu.ShutdownAsync();

            // control: with a kept copy (an earlier launch) the game starts offline
            server.Offline.Add(ConfigPath);
            var later = await KrtuworksClient.InitializeAsync(server.Options(store, extra: Online));
            Assert.That(later.State, Is.EqualTo(KrtuworksClient.StateReady));
            await later.ShutdownAsync();

            // control: without the option the first launch is offline-first
            var plain = await KrtuworksClient.InitializeAsync(server.Options(new MemoryStore()));
            Assert.That(plain.State, Is.EqualTo(KrtuworksClient.StateReady));
            await plain.ShutdownAsync();
        }

        // ── The loopback receiver ─────────────────────────────────────────────

        static async Task<string> WithTimeout(Task<string> work)
        {
            var first = await Task.WhenAny(work, Task.Delay(6000));
            Assert.That(first, Is.SameAs(work), "the receiver never answered");
            return await work;
        }

        [Test]
        public async Task AnIdleConnectionDoesNotHoldUpTheRealRequest_ControlAnAnswerInTwoPartsAndAForeignPath()
        {
            using var receiver = new LoopbackReceiver { ReadTimeout = TimeSpan.FromMilliseconds(300) };
            var waiting = receiver.WaitAsync(CancellationToken.None);
            using var idle = new TcpClient();
            await idle.ConnectAsync("127.0.0.1", receiver.Port);
            using var real = new TcpClient();
            await real.ConnectAsync("127.0.0.1", receiver.Port);
            var bytes = Encoding.ASCII.GetBytes("GET /callback?code=c&state=s HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
            await real.GetStream().WriteAsync(bytes, 0, bytes.Length);
            Assert.That(await WithTimeout(waiting), Is.EqualTo($"http://127.0.0.1:{receiver.Port}/callback?code=c&state=s"));

            using var second = new LoopbackReceiver { ReadTimeout = TimeSpan.FromSeconds(3) };
            var parts = second.WaitAsync(CancellationToken.None);
            using var browser = new TcpClient();
            await browser.ConnectAsync("127.0.0.1", second.Port);
            var head = Encoding.ASCII.GetBytes("GET /favicon.ico HTTP/1.1\r\n\r\n");
            await browser.GetStream().WriteAsync(head, 0, head.Length);
            using var back = new TcpClient();
            await back.ConnectAsync("127.0.0.1", second.Port);
            var first = Encoding.ASCII.GetBytes("GET /call");
            await back.GetStream().WriteAsync(first, 0, first.Length);
            await Task.Delay(100);
            var rest = Encoding.ASCII.GetBytes("back?code=2 HTTP/1.1\r\n\r\n");
            await back.GetStream().WriteAsync(rest, 0, rest.Length);
            Assert.That(await WithTimeout(parts), Is.EqualTo($"http://127.0.0.1:{second.Port}/callback?code=2"), "a foreign path is answered 404 and the wait goes on");
        }

        [Test]
        public async Task CancellingEndsTheWaitEvenWithAConnectionOpen()
        {
            using var receiver = new LoopbackReceiver();
            using var cancel = new CancellationTokenSource();
            var waiting = receiver.WaitAsync(cancel.Token);
            using var idle = new TcpClient();
            await idle.ConnectAsync("127.0.0.1", receiver.Port);
            await Task.Delay(100);
            cancel.Cancel();
            var first = await Task.WhenAny(waiting, Task.Delay(3000));
            Assert.That(first, Is.SameAs(waiting), "the read of the open connection did not hold the cancel");
            try
            {
                await waiting;
                Assert.Fail("the wait ended with an address");
            }
            catch (OperationCanceledException) { /* the wait ended because it was cancelled */ }
        }

        // ── The secret files ──────────────────────────────────────────────────

        sealed class SlowKey : IMasterKey
        {
            readonly byte[] key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

            public byte[]? GetOrCreate()
            {
                Thread.Sleep(30);
                return key;
            }
        }

        [Test]
        public async Task ManyThreadsOnAFreshSealedStoreAllWorkWithTheKeysMadeTogether()
        {
            var dir = Path.Combine(Path.GetTempPath(), "krtu-sealed-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new SealedFileStore(dir, new SlowKey());
                var errors = new List<Exception>();
                await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
                {
                    try
                    {
                        await store.SetAsync($"k{i}", $"v{i}");
                        Assert.That(await store.GetAsync($"k{i}"), Is.EqualTo($"v{i}"));
                    }
                    catch (Exception e)
                    {
                        lock (errors) errors.Add(e);
                    }
                })));
                Assert.That(errors, Is.Empty, string.Join("; ", errors.Select(e => e.Message)));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        // ── The platform mirror belongs to its player ─────────────────────────

        [Test]
        public async Task ASyncForAnotherPlayerReachesTheServerForNobody_ControlTheSignedInPlayersOwnSyncRuns()
        {
            var server = new FakeServer();
            server.GuestRoutes(A);
            CloudMirrorTests.SlotRoutes(server, new Dictionary<string, CloudMirrorTests.Slot> { ["save-1"] = new CloudMirrorTests.Slot { Text = "server", Gen = 3 } });
            var shared = new MemoryStore();
            var opts = Resolved.From(server.Options(shared));
            var clock = new Clock();
            var identity = new Identity(opts, shared, shared, server, clock, null);
            await identity.SignInAsGuestAsync("2000-01-01", Terms, false);
            var api = new Api(opts, identity, server, clock);
            string? Subject() => identity.Player?.Subject;
            var files = new Files(opts, api, shared, Subject, () => identity.Generation, () => { });
            var cloud = new CloudMirrorTests.FakeCloud();
            var mirror = new CloudMirror(cloud, files, shared, opts.ClientId, _ => { }, (_, __) => { });

            await Throws(() => mirror.SyncAsync(B, "76561198000000000", "ses_cccccccccccccccccccc"), "context_changed");
            Assert.That(server.Count("GET", "/v1/apps/buried/files"), Is.Zero, "nothing was asked of the server for the player who is not here");
            Assert.That(cloud.Writes, Is.Empty);

            await mirror.SyncAsync(A, "76561198000000000", "ses_cccccccccccccccccccc");
            Assert.That(cloud.Text("save-1"), Is.EqualTo("server"), "control: the player's own sync copies the server's save to the platform");

            await Throws(() => files.SaveAsync("save-1", new byte[] { 1 }, true, null, null, B), "context_changed");
            await Throws(() => files.ReadAsync("save-1", null, null, B), "context_changed");
            Assert.That(server.Count("PUT", "/v1/apps/buried/files/save-1"), Is.Zero, "nothing was written for the player who is not here");
        }

        // ── A Steam guest on a new PC ─────────────────────────────────────────

        sealed class FakeSteam : IPlatformServices, IPlatformTicketCancel
        {
            internal readonly List<string> Cancelled = new List<string>();
            public Task<string?> GetWebApiTicketAsync() => Task.FromResult<string?>("0a0b0c");
            public Task<IReadOnlyList<string>> UnlockedAchievementsAsync() => Task.FromResult<IReadOnlyList<string>>(new List<string>());
            public Task UnlockAchievementAsync(string platformId) => Task.CompletedTask;
            public Task ClearAchievementAsync(string platformId) => Task.CompletedTask;
            public void CancelWebApiTicket(string ticket) => Cancelled.Add(ticket);
        }

        static (Identity, FakeSteam, MemoryStore) SteamIdentity(FakeServer server, bool knownAccount)
        {
            server.GuestRoutes(A);
            server.On("POST", "/oidc/token", call =>
            {
                if (call.Field("grant_type") != Identity.SteamTicketGrant) return FakeServer.OAuthError("unsupported_grant_type", "");
                if (!knownAccount && call.Field("birth_date") == null) return FakeServer.OAuthError("invalid_birth_date", "Enter a valid date of birth.");
                return FakeServer.Ok(FakeServer.TokenAnswer(A));
            });
            var shared = new MemoryStore();
            var steam = new FakeSteam();
            var opts = Resolved.From(server.Options(shared, extra: o => o.Platform = "STEAM"));
            return (new Identity(opts, shared, shared, server, new Clock(), steam), steam, shared);
        }

        [Test]
        public async Task ASteamAccountsGuestOnANewPcSignsInWithoutTheAgeScreen_ControlANewSteamAccountAsksForIt()
        {
            var returning = new FakeServer();
            var (identity, steam, _) = SteamIdentity(returning, knownAccount: true);
            var player = await identity.SignInAsGuestAsync(null, null, true);
            Assert.That(player.Subject, Is.EqualTo(A));
            Assert.That(returning.Calls.Single(c => c.Field("grant_type") == Identity.SteamTicketGrant).Field("birth_date"), Is.Null, "the server knows this Steam account: no age was sent");
            Assert.That(steam.Cancelled, Is.Empty, "the ticket was used");

            var fresh = new FakeServer();
            var (newcomer, newSteam, _) = SteamIdentity(fresh, knownAccount: false);
            await Throws(() => newcomer.SignInAsGuestAsync(null, null, true), "birth_date_required");
            Assert.That(newSteam.Cancelled, Is.Empty, "the server read the ticket");
            var made = await newcomer.SignInAsGuestAsync("2000-01-01", Terms, true);
            Assert.That(made.Subject, Is.EqualTo(A));
            Assert.That(fresh.Calls.Where(c => c.Field("grant_type") == Identity.SteamTicketGrant).Last().Field("birth_date"), Is.EqualTo("2000-01-01"));
        }

        [Test]
        public async Task ATicketThatNeverReachedTheServerIsCancelled_ControlOneThatDidIsNot()
        {
            var server = new FakeServer();
            var (identity, steam, _) = SteamIdentity(server, knownAccount: true);
            server.Offline.Add("/oidc/device-key/challenge");
            await Throws(() => identity.SignInAsGuestAsync(null, null, true), "network_error");
            Assert.That(steam.Cancelled, Is.EqualTo(new[] { "0a0b0c" }), "the proof could not be made: the ticket was never sent");
            server.Offline.Remove("/oidc/device-key/challenge");
            await identity.SignInAsGuestAsync(null, null, true);
            Assert.That(steam.Cancelled, Has.Count.EqualTo(1), "control: a ticket the server received is left to it");
        }

        [Test]
        public async Task ASessionOpenWhoseCredentialsFailCancelsItsTicket()
        {
            var server = new FakeServer();
            var (identity, steam, shared) = SteamIdentity(server, knownAccount: true);
            await identity.SignInAsGuestAsync(null, null, true);
            var opts = Resolved.From(server.Options(shared, extra: o => o.Platform = "STEAM"));
            var api = new Api(opts, identity, server, new Clock());
            // The keeper opens for a player the identity does not hold: the call is never made.
            var keeper = new SessionKeeper(opts, api, () => B, () => Task.FromResult<string?>("0d0e0f")) { CancelTicket = identity.CancelTicket };
            await Throws(() => keeper.OpenAsync(), "context_changed");
            Assert.That(steam.Cancelled, Is.EqualTo(new[] { "0d0e0f" }));
            Assert.That(server.Count("POST", "/v1/sessions"), Is.Zero);
        }

        // ── Sessions ──────────────────────────────────────────────────────────

        static void SessionRoutes(FakeServer server, Func<FakeCall, Task<Reply>> open)
        {
            server.OnAsync("POST", "/v1/sessions", open);
            server.OnPrefix("POST", "/v1/sessions/", _ => FakeServer.Ok(new JObject { ["ok"] = true }));
        }

        static Reply Opened(string owner) => FakeServer.Ok(new JObject
        {
            ["session"] = new JObject { ["id"] = $"ses_{owner.Substring(3, 6)}aaaaaaaaaaaa", ["startedAt"] = "2026-11-25T10:00:00.000Z" },
            ["heartbeatSeconds"] = 60,
        });

        [Test]
        public async Task AFailedSessionOpenIsTriedAgainAfterTheWait_ControlItIsNotTriedBeforeIt()
        {
            var server = new FakeServer();
            var attempts = 0;
            SessionRoutes(server, _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    var busy = Error(503, "unavailable");
                    busy.Headers["retry-after"] = "1";
                    return Task.FromResult(busy);
                }
                return Task.FromResult(Opened(A));
            });
            var krtu = await Start(server, extra: o => o.Sessions = true);
            Assert.That(server.Count("POST", "/v1/sessions"), Is.EqualTo(1), "the open failed once");
            await Task.Delay(400);
            Assert.That(server.Count("POST", "/v1/sessions"), Is.EqualTo(1), "control: not before the server's wait");
            await Until(() => server.Count("POST", "/v1/sessions") == 2, "the open was tried again", 500);
            await Task.Delay(1200);
            Assert.That(server.Count("POST", "/v1/sessions"), Is.EqualTo(2), "control: once it is open it is not opened again");
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task AnOpenRunningForAnotherPlayerDoesNotStandInForThisOnes()
        {
            var server = new FakeServer();
            var sub = A;
            var gate = new TaskCompletionSource<bool>();
            var calls = 0;
            server.GuestRoutes(A);
            server.On("POST", "/oidc/token", call =>
            {
                if (call.Field("grant_type") == Identity.DeviceKeyGrant) return FakeServer.Ok(FakeServer.TokenAnswer(sub));
                return FakeServer.OAuthError("unsupported_grant_type", "");
            });
            SessionRoutes(server, async call =>
            {
                var owner = (string)JObject.Parse(Encoding.UTF8.GetString(Ids.FromBase64Url(call.Header("authorization")!.Replace("Bearer ", "").Split('.')[1])))["sub"]!;
                if (Interlocked.Increment(ref calls) == 1) await gate.Task;
                return Opened(owner);
            });
            var shared = new MemoryStore();
            var opts = Resolved.From(server.Options(shared, extra: o => o.Sessions = true));
            var clock = new Clock();
            var identity = new Identity(opts, shared, shared, server, clock, null);
            await identity.SignInAsGuestAsync("2000-01-01", Terms, false);
            var keeper = new SessionKeeper(opts, new Api(opts, identity, server, clock), () => identity.Player?.Subject, null);
            var forA = keeper.OpenAsync();
            await Until(() => Volatile.Read(ref calls) == 1, "the first player's open is on its way");
            await identity.SignOutAsync();
            sub = B;
            await identity.SignInAsGuestAsync(null, null, false);
            var forB = keeper.OpenAsync();
            gate.SetResult(true);
            var session = await forB;
            Assert.That(session?.Owner, Is.EqualTo(B), "the second player got a session of their own, not the first one's nothing");
            try { await forA; }
            catch (KrtuworksException) { /* the first player's open was dropped with the player */ }
            Assert.That(keeper.Session?.Owner, Is.EqualTo(B));
        }

        // ── The pace of settings writes and a rate limit ──────────────────────

        sealed class Waits
        {
            internal readonly List<(TimeSpan Span, TaskCompletionSource<bool> Done)> Pending = new List<(TimeSpan, TaskCompletionSource<bool>)>();

            internal Task Delay(TimeSpan span, CancellationToken cancel)
            {
                var done = new TaskCompletionSource<bool>();
                if (span < TimeSpan.FromSeconds(10)) return Task.Delay(span, cancel);
                cancel.Register(() => done.TrySetCanceled(cancel));
                lock (Pending) Pending.Add((span, done));
                return done.Task;
            }

            internal int Count { get { lock (Pending) return Pending.Count; } }
        }

        static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

        [Test]
        public async Task SettingsAreWrittenOncePerThirtySecondsTheNewestWins_PausingWritesAtOnce_ControlASaveSlotIsNotHeld()
        {
            var waits = new Waits();
            var server = new FakeServer();
            var puts = new List<string>();
            server.On("PUT", "/v1/apps/buried/files/settings", call =>
            {
                lock (puts) puts.Add(Encoding.UTF8.GetString(call.Raw));
                return new Reply { Status = 201, Json = new JObject { ["file"] = FileAnswer("settings") } };
            });
            server.On("PUT", "/v1/apps/buried/files/save-1", call =>
            {
                lock (puts) puts.Add("save:" + Encoding.UTF8.GetString(call.Raw));
                return new Reply { Status = 201, Json = new JObject { ["file"] = FileAnswer("save-1") } };
            });
            var krtu = await Start(server, extra: o => o.Delay = waits.Delay);
            await krtu.SaveSettingsAsync("settings", Bytes("one"));
            Assert.That(puts, Is.EqualTo(new[] { "one" }), "the first write of a slot goes at once");

            var two = krtu.SaveSettingsAsync("settings", Bytes("two"));
            var three = krtu.SaveSettingsAsync("settings", Bytes("three"));
            await Until(() => waits.Count == 2, "both wait for the slot's turn");
            Assert.That(puts, Has.Count.EqualTo(1));
            Assert.That(waits.Pending.All(w => w.Span > TimeSpan.FromSeconds(28) && w.Span <= TimeSpan.FromSeconds(30)), "the turn is thirty seconds after the last write");
            foreach (var wait in waits.Pending.ToList()) wait.Done.TrySetResult(true);
            await Throws(() => two, "save_replaced");
            await three;
            Assert.That(puts, Is.EqualTo(new[] { "one", "three" }), "the older content was never written: the newest won");

            var four = krtu.SaveSettingsAsync("settings", Bytes("four"));
            await Until(() => waits.Count == 3, "the next one waits again");
            krtu.SetForeground(false);
            await four;
            Assert.That(puts, Is.EqualTo(new[] { "one", "three", "four" }), "the app pausing wrote what waited");

            var before = waits.Count;
            await krtu.SaveFileAsync("save-1", Bytes("a"), null);
            await krtu.SaveFileAsync("save-1", Bytes("b"));
            Assert.That(waits.Count, Is.EqualTo(before), "control: a save slot is not paced by the SDK");
            Assert.That(puts.Skip(3), Is.EqualTo(new[] { "save:a", "save:b" }));
            await krtu.ShutdownAsync();
        }

        /// <summary>A store whose writes of large values take longer, as a real disk does.</summary>
        sealed class SlowLargeStore : IKrtuStore
        {
            readonly MemoryStore inner = new MemoryStore();
            public Task<string?> GetAsync(string key) => inner.GetAsync(key);
            public async Task SetAsync(string key, string value)
            {
                if (value.Length > 1_000_000) await Task.Delay(300);
                await inner.SetAsync(key, value);
            }
            public Task DeleteAsync(string key) => inner.DeleteAsync(key);
            public Task<IReadOnlyList<string>> KeysAsync(string prefix) => inner.KeysAsync(prefix);
        }

        [Test]
        public async Task ALargeSaveStoredAfterASmallerNewerOneNeverLandsAfterIt_ControlSavesAwaitedInTurnGoInTurn()
        {
            var server = new FakeServer();
            var puts = new List<string>();
            server.On("PUT", "/v1/apps/buried/files/settings", call =>
            {
                lock (puts) puts.Add(call.Raw.Length > 1000 ? "large" : Encoding.UTF8.GetString(call.Raw));
                return new Reply { Status = 201, Json = new JObject { ["file"] = FileAnswer("settings") } };
            });
            var krtu = await Start(server, extra: o => o.Store = new SlowLargeStore());
            var large = new byte[2 * 1024 * 1024];
            var older = krtu.SaveFileAsync("settings", large);
            var newer = krtu.SaveFileAsync("settings", Bytes("B"));
            try { await older; } catch (KrtuworksException e) { Assert.That(e.Code, Is.EqualTo("save_replaced")); }
            await newer;
            Assert.That(puts.Last(), Is.EqualTo("B"), "the newest content is the last written");

            await krtu.SaveFileAsync("settings", Bytes("C"));
            await krtu.SaveFileAsync("settings", large);
            Assert.That(puts.Last(), Is.EqualTo("large"), "control: awaited in turn, the later save is the last written");
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task ARateLimitedSaveGoesAgainWhenTheLimitPasses_ControlARefusedBodyDoesNot()
        {
            var server = new FakeServer();
            var keys = new List<string?>();
            var started = DateTimeOffset.UtcNow;
            DateTimeOffset? second = null;
            server.On("PUT", "/v1/apps/buried/files/save-1", call =>
            {
                lock (keys) keys.Add(call.Header("idempotency-key"));
                if (keys.Count == 1)
                {
                    var limit = Error(429, "too_many_writes");
                    limit.Headers["retry-after"] = "1";
                    return limit;
                }
                second = DateTimeOffset.UtcNow;
                return new Reply { Status = 201, Json = new JObject { ["file"] = FileAnswer("save-1") } };
            });
            var krtu = await Start(server);
            await Throws(() => krtu.SaveFileAsync("save-1", Bytes("a"), null), "too_many_writes");
            await Until(() => keys.Count == 2, "the save was sent again by itself", 500);
            Assert.That(keys[1], Is.EqualTo(keys[0]), "the same upload id");
            Assert.That(second!.Value - started, Is.GreaterThan(TimeSpan.FromSeconds(1)), "after the limit, not at once");
            for (var i = 0; i < 300 && (await krtu.WaitingSavesAsync(A)).Count > 0; i++) await Task.Delay(10);
            Assert.That(await krtu.WaitingSavesAsync(A), Is.Empty, "and finished");
            await krtu.ShutdownAsync();

            var refused = new FakeServer();
            var calls = 0;
            refused.On("PUT", "/v1/apps/buried/files/save-1", _ =>
            {
                Interlocked.Increment(ref calls);
                return Error(400, "invalid_request");
            });
            var other = await Start(refused);
            await Throws(() => other.SaveFileAsync("save-1", Bytes("a"), null), "invalid_request");
            await Task.Delay(1500);
            Assert.That(calls, Is.EqualTo(1), "a refused body is not sent again");
            await other.ShutdownAsync();
        }

        [Test]
        public async Task AReplayedRejectedConflictCarriesTheSlotsCurrentVersion()
        {
            var server = new FakeServer();
            var current = FileAnswer("save-1");
            current["id"] = "fil_currentcccccccccccc";
            server.On("PUT", "/v1/apps/buried/files/save-1", call => new Reply { Status = 202, Json = new JObject { ["uploadId"] = call.Header("idempotency-key"), ["status"] = "PENDING" } });
            server.OnPrefix("GET", "/v1/apps/buried/files/save-1/uploads/", _ => FakeServer.Ok(new JObject { ["status"] = "REJECTED", ["code"] = "file_conflict" }));
            server.On("GET", "/v1/apps/buried/files", _ => FakeServer.Ok(new JObject
            {
                ["slots"] = new JArray(new JObject { ["key"] = "save-1", ["kind"] = "SAVE", ["name"] = "save-1", ["retired"] = false, ["platformMirror"] = false, ["file"] = current, ["devices"] = new JArray() }),
                ["limits"] = new JObject(),
            }));
            var krtu = await Start(server, extra: o => o.Delay = (span, cancel) => Task.Delay(5, cancel));
            var failure = await Throws(() => krtu.SaveFileAsync("save-1", Bytes("a"), "fil_oldoldoldoldoldoldo"), "file_conflict");
            Assert.That((string?)failure.Details["current"]?["id"], Is.EqualTo("fil_currentcccccccccccc"), "the game's choice screen can open");
            await krtu.ShutdownAsync();
        }

        // ── The clocks ────────────────────────────────────────────────────────

        [Test]
        public async Task ARetryAfterDateIsCountedOnTheServersClock_ControlSecondsAreSeconds()
        {
            var server = new FakeServer { ClockOffset = TimeSpan.FromHours(2) };
            var krtu = await Start(server);
            server.On("GET", "/v1/me/status", _ =>
            {
                var busy = Error(429, "too_busy");
                busy.Headers["retry-after"] = (DateTimeOffset.UtcNow + server.ClockOffset + TimeSpan.FromSeconds(30)).ToString("r");
                return busy;
            });
            var failure = await Throws(() => krtu.GetStatusAsync(), "too_busy");
            Assert.That(failure.RetryAfter, Is.InRange(27.0, 31.0), "30 seconds from the server's now, not two hours and 30 seconds from this device's");
            server.On("GET", "/v1/me/status", _ =>
            {
                var busy = Error(429, "too_busy");
                busy.Headers["retry-after"] = "5";
                return busy;
            });
            Assert.That((await Throws(() => krtu.GetStatusAsync(), "too_busy")).RetryAfter, Is.EqualTo(5));
            await krtu.ShutdownAsync();
        }

        static EventQueue Queue(ISecretStore store, FakeServer server, out Clock clock)
        {
            var opts = Resolved.From(server.Options(store as MemoryStore));
            clock = new Clock();
            var identity = new Identity(opts, store, store, server, clock, null);
            return new EventQueue(opts, store, clock, new Api(opts, identity, server, clock), () => A);
        }

        static WireEvent Old(string id, int daysAgo, long? offsetMs) => new WireEvent
        {
            Id = id, Type = "screen_view", LaunchId = "l", CreatedAt = (DateTimeOffset.UtcNow - TimeSpan.FromDays(daysAgo)).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), ServerOffsetMs = offsetMs,
        };

        [Test]
        public async Task AnEventsAgeIsCountedOnTheServersClock_ControlAnOldEventWithNoOffsetIsDropped()
        {
            var store = new MemoryStore();
            var server = new FakeServer();
            var queue = Queue(store, server, out _);
            // A device clock that was 39 days behind when these were made: the server's reading says they are a day old.
            var state = new EventQueueState { Pending = { Old("behind", 40, (long)TimeSpan.FromDays(39).TotalMilliseconds), Old("really-old", 40, null), Old("fresh", 1, null) } };
            await Json.Put(store, "krtu:buried.test.native:events:" + A, state);
            queue.Add("screen_view", null, null, null, null);
            await queue.PersistAsync();
            var kept = await Json.Get<EventQueueState>(store, "krtu:buried.test.native:events:" + A);
            Assert.That(kept!.Pending.Select(e => e.Id), Does.Contain("behind"), "40 days by the device clock, one day by the server's");
            Assert.That(kept.Pending.Select(e => e.Id), Does.Not.Contain("really-old"));
            Assert.That(kept.Pending.Select(e => e.Id), Does.Contain("fresh"));
            Assert.That(kept.Dropped, Is.EqualTo(1));
        }

        sealed class FailingStore : ISecretStore
        {
            readonly MemoryStore inner = new MemoryStore();
            internal bool Fail;
            public Task<string?> GetAsync(string key) => inner.GetAsync(key);
            public Task SetAsync(string key, string value) => Fail ? throw new IOException("the store is full") : inner.SetAsync(key, value);
            public Task DeleteAsync(string key) => inner.DeleteAsync(key);
            public Task<IReadOnlyList<string>> KeysAsync(string prefix) => inner.KeysAsync(prefix);
        }

        [Test]
        public async Task EventsTheStoreRefusedAreStillThereForTheNextWrite_ControlTheyAreNotCountedTwice()
        {
            var store = new FailingStore { Fail = true };
            var queue = Queue(store, new FakeServer(), out _);
            queue.Add("screen_view", null, null, null, null);
            queue.Add("screen_view", null, null, null, null);
            await AsyncAssert.ThrowsAsync<IOException>(() => queue.PersistAsync());
            Assert.That(await queue.SizeAsync(A), Is.EqualTo(2), "still waiting in memory");
            store.Fail = false;
            await queue.PersistAsync();
            Assert.That(await queue.SizeAsync(A), Is.EqualTo(2), "written once, not twice");
            var state = await Json.Get<EventQueueState>(store, "krtu:buried.test.native:events:" + A);
            Assert.That(state!.Pending, Has.Count.EqualTo(2));
        }

        // ── Who plays ─────────────────────────────────────────────────────────

        static (Identity, MemoryStore) IdentityOn(FakeServer server)
        {
            var shared = new MemoryStore();
            var opts = Resolved.From(server.Options(shared));
            return (new Identity(opts, shared, shared, server, new Clock(), null), shared);
        }

        [Test]
        public async Task AnAgeTheServerWillNeverTakeIsNotKeptForEveryLaunch_ControlAnUnreachableServerKeepsIt()
        {
            var server = new FakeServer();
            server.GuestRoutes(A);
            server.On("POST", "/oidc/token", _ => FakeServer.OAuthError("too_young", "Krtuworks ID is for people aged 13 or older."));
            var (identity, _) = IdentityOn(server);
            await Throws(() => identity.SignInAsGuestAsync("2020-01-01", Terms, false), "too_young");
            Assert.That((await identity.MetaAsync()).GuestRequest, Is.Null, "the refusal will come again: nothing is kept to send at every launch");

            var offline = new FakeServer();
            offline.GuestRoutes(A);
            offline.Offline.Add("/oidc/device-key/challenge");
            var (other, _) = IdentityOn(offline);
            await Throws(() => other.SignInAsGuestAsync("2000-01-01", Terms, false), "network_error");
            Assert.That((await other.MetaAsync()).GuestRequest?.BirthDate, Is.EqualTo("2000-01-01"), "control: no answer is not a refusal");
        }

        [Test]
        public async Task AcceptedTermsWithoutABirthDateUpdateTheKeptRequest()
        {
            var server = new FakeServer();
            server.GuestRoutes(A);
            server.Offline.Add("/oidc/device-key/challenge");
            var (identity, _) = IdentityOn(server);
            await Throws(() => identity.SignInAsGuestAsync("2000-01-01", Terms, false), "network_error");
            await Throws(() => identity.SignInAsGuestAsync(null, "TERMS:v2 PRIVACY:v1", false), "network_error");
            var request = (await identity.MetaAsync()).GuestRequest!;
            Assert.That((request.BirthDate, request.AcceptedTerms), Is.EqualTo(("2000-01-01", "TERMS:v2 PRIVACY:v1")), "the birth date stays, the terms are the ones accepted now");
        }

        [Test]
        public async Task ASecretStoreEmptiedByAnotherProcessTellsTheAppItSignedOut_ControlNobodyPlayingTellsNothing()
        {
            var server = new FakeServer();
            server.GuestRoutes(A);
            var (identity, store) = IdentityOn(server);
            var changes = new List<IdentityChange>();
            identity.Changed += changes.Add;
            await identity.SignInAsGuestAsync("2000-01-01", Terms, false);
            changes.Clear();
            await store.DeleteAsync("krtu:buried.test.native:tokens");
            await Throws(() => identity.AccessTokenAsync(true), "signed_out");
            Assert.That(changes.Select(c => (c.Type, c.Reason)), Is.EqualTo(new[] { ("signed_out", (string?)"signed_out") }));
            await Throws(() => identity.AccessTokenAsync(true), "signed_out");
            Assert.That(changes, Has.Count.EqualTo(1), "control: nobody is playing any more, nothing more to tell");
        }

        [Test]
        public async Task SignInRecordsNobodyCameBackToAreSweptWhenAnotherStarts_ControlAFreshOneStays()
        {
            var server = new FakeServer();
            server.GuestRoutes(A);
            var (identity, store) = IdentityOn(server);
            await identity.SignInAsGuestAsync("2000-01-01", Terms, false);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await Json.Put(store, "krtu:buried.test.native:signin:old", new PendingSignIn { Verifier = "v", Nonce = "n", RedirectUri = "r", StartedAt = now - 3_600_000 });
            await Json.Put(store, "krtu:buried.test.native:signin:fresh", new PendingSignIn { Verifier = "v", Nonce = "n", RedirectUri = "r", StartedAt = now - 60_000 });
            await identity.BeginSignInAsync("http://127.0.0.1:5555/callback");
            var left = await store.KeysAsync("krtu:buried.test.native:signin:");
            Assert.That(left.Count(k => k.EndsWith(":old")), Is.Zero);
            Assert.That(left.Count(k => k.EndsWith(":fresh")), Is.EqualTo(1));
            Assert.That(left, Has.Count.EqualTo(2), "the fresh one and the one just made");
        }

        [Test]
        public async Task ALevelAttemptBelongsToItsPlayer_ControlsTheSameOwnerAndOneMadeBeforeAnyPlayer()
        {
            foreach (var scenario in new[] { "other", "same", "anon" })
            {
                var owner = scenario == "anon" ? EventQueue.Anon : A;
                var told = new List<JObject>();
                var tracker = new ProgressionTracker(new MemoryStore(), "buried.test.native", () => owner, () => 0, told.Add);
                await tracker.StartAsync(new LevelRef("cave"));
                if (scenario == "other") owner = B;
                if (scenario == "anon") owner = A;
                tracker.Finish("complete", null);
                var actions = told.Select(t => (string?)t["action"]).ToList();
                Assert.That(actions, Is.EqualTo(scenario == "other" ? new[] { "start" } : new[] { "start", "complete" }), scenario);
                Assert.That(tracker.InPlay, Is.Null, scenario);
            }
        }

        // ── The screens ───────────────────────────────────────────────────────

        sealed class Dialog : IUiDialog
        {
            internal readonly UiAnswerQueue Answers = new UiAnswerQueue();
            public Task<UiAnswer> NextAnswerAsync() => Answers.NextAsync();
            public void Update(UiScreen screen) { }
            public void Close() => Answers.Close();
        }

        sealed class Host : IUiHost
        {
            readonly Func<UiScreen, UiAnswer> script;
            internal readonly List<UiScreen> Shown = new List<UiScreen>();
            internal Host(Func<UiScreen, UiAnswer> script) => this.script = script;

            public IUiDialog Show(UiScreen screen)
            {
                Shown.Add(screen);
                var dialog = new Dialog();
                dialog.Answers.Post(script(screen));
                return dialog;
            }

            public IUiReminder Remind() => throw new InvalidOperationException("no reminder expected");
        }

        static KrtuworksUiOptions UiOptions() => new KrtuworksUiOptions
        {
            Locale = "en",
            Mode = UiSignInMode.Browser,
            OpenUrl = _ => { },
            Delay = (span, cancel) => Task.Delay(5, cancel),
            PlatformSaveConflicts = false,
        };

        [Test]
        public async Task ASaveWithNoBaseIsWrittenWithoutACheck_ControlANamedBaseIsChecked()
        {
            var server = new FakeServer();
            var seen = new List<(string? Match, string? NoneMatch)>();
            server.On("PUT", "/v1/apps/buried/files/save-1", call =>
            {
                lock (seen) seen.Add((call.Header("if-match"), call.Header("if-none-match")));
                return new Reply { Status = 201, Json = new JObject { ["file"] = FileAnswer("save-1") } };
            });
            var krtu = await Start(server);
            var ui = new KrtuworksUi(krtu, new Host(_ => UiAnswer.Continue), UiOptions());
            await ui.SaveAsync("save-1", Bytes("a"));
            await ui.SaveAsync("save-1", Bytes("b"), "fil_aaaaaaaaaaaaaaaaaaaa");
            Assert.That(seen, Is.EqualTo(new[] { ((string?)null, (string?)null), ("fil_aaaaaaaaaaaaaaaaaaaa", (string?)null) }));
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task TheNewerSaveIsReadOnTheServersClock_AClockBehindDoesNotFlipIt()
        {
            // This device is three days behind the server: the cloud save's time is "in the future" for it.
            var server = new FakeServer { ClockOffset = TimeSpan.FromDays(3) };
            var serverNow = DateTimeOffset.UtcNow + server.ClockOffset;
            var theirsAt = (serverNow - TimeSpan.FromMinutes(1)).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            server.On("GET", "/v1/apps/buried/files/save-1", _ =>
            {
                var reply = new Reply { Body = new byte[] { 9 } };
                reply.Headers["etag"] = "\"fil_bbbbbbbbbbbbbbbbbbbb\"";
                reply.Headers["x-krtu-generation"] = "4";
                reply.Headers["x-krtu-saved-at"] = theirsAt;
                return reply;
            });
            server.On("PUT", "/v1/apps/buried/files/save-1", _ => new Reply { Status = 201, Json = new JObject { ["file"] = FileAnswer("save-1") } });
            var krtu = await Start(server);
            var host = new Host(screen => UiAnswer.Keep(0));
            var ui = new KrtuworksUi(krtu, host, UiOptions());
            var theirs = FileAnswer("save-1").ToObject<FileVersion>()!;
            theirs.Id = "fil_bbbbbbbbbbbbbbbbbbbb";
            theirs.SavedAt = theirsAt;
            await ui.ChooseSaveAsync("save-1", new byte[] { 1, 2 }, theirs);
            var cards = ((ConflictScreen)host.Shown.Single()).Cards;
            Assert.That(cards.Select(c => c.Newer), Is.EqualTo(new[] { true, false }), "this device's save is the newer one, as the server's clock says");
            await krtu.ShutdownAsync();
        }

        [Test]
        public void AnAccountWaitingToBeDeletedIsToldSoNotARetryLoop_ControlAnOfflineErrorStillOffersRetry()
        {
            var texts = new UiTexts();
            var pending = ErrorScreen.From(new KrtuworksException("account_deletion_pending", "pending", 403), texts, "en");
            Assert.That((pending.Retry, pending.Message), Is.EqualTo((false, texts.Get("error.deletionPending"))));
            Assert.That(ErrorScreen.From(new KrtuworksException("network_error", "no answer"), texts, "en").Retry, Is.True);
        }

        [Test]
        public async Task AnAppThatNeedsTheServerAsksThePlayerToConnectFirst_ControlTheOfflineFirstDefaultDoesNot()
        {
            static void Online(KrtuworksOptions o) => o.Config = KrtuworksAppConfig.FromJson(
                "{\"appId\":\"app_aaaaaaaaaaaaaaaaaaaa\",\"slug\":\"buried\",\"firstLaunch\":\"online\",\"environments\":{\"TEST\":{\"clientId\":\"buried.test.native\"}}}");
            var server = new FakeServer();
            server.GuestRoutes(A);
            server.ConfigRoute(new JObject());
            server.Offline.Add(ConfigPath);
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(extra: Online));
            var host = new Host(screen =>
            {
                if (screen is ErrorScreen error)
                {
                    Assert.That(error.Retry, "the screen offers Try again");
                    server.Offline.Remove(ConfigPath);
                    return UiAnswer.Retry;
                }
                return UiAnswer.Birth(2000, 1, 1, true);
            });
            var result = await new KrtuworksUi(krtu, host, UiOptions()).EnsurePlayerAsync();
            Assert.That(host.Shown.Select(s => s.Kind), Is.EqualTo(new[] { UiScreenKind.Error, UiScreenKind.Age }));
            Assert.That(result.Outcome, Is.EqualTo(UiOutcome.SignedIn));
            await krtu.ShutdownAsync();

            var offlineFirst = new FakeServer();
            offlineFirst.GuestRoutes(A);
            offlineFirst.ConfigRoute(new JObject());
            offlineFirst.Offline.Add(ConfigPath);
            var plain = await KrtuworksClient.InitializeAsync(offlineFirst.Options());
            var quiet = new Host(screen => UiAnswer.Birth(2000, 1, 1, true));
            await new KrtuworksUi(plain, quiet, UiOptions()).EnsurePlayerAsync();
            Assert.That(quiet.Shown.Select(s => s.Kind), Is.EqualTo(new[] { UiScreenKind.Age }), "control: no connect screen without the option");
            await plain.ShutdownAsync();
        }

        [Test]
        public async Task TheSessionsSwitchTakesEffectOnTheNextConfigRead_OnOpensASessionOffEndsItAndItsHeartbeats_ControlOnAgainOpensANewOne()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            var config = new JObject { ["modules"] = new JObject { ["sessions"] = false } };
            var bump = server.ConfigRoute(config);
            server.AcceptEvents(new List<JObject>());
            var ids = 0;
            server.On("POST", "/v1/sessions", _ =>
            {
                ids++;
                return new Reply
                {
                    Status = 201,
                    Json = new JObject { ["session"] = new JObject { ["id"] = $"ses_{new string((char)('a' + ids), 20)}", ["startedAt"] = Clock.IsoNow() }, ["heartbeatSeconds"] = 1, ["serverTime"] = Clock.IsoNow() },
                };
            });
            server.OnPrefix("POST", "/v1/sessions/", _ => FakeServer.Ok(new JObject { ["nextHeartbeatSeconds"] = 1 }));
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(extra: o => o.Sessions = true));
            await krtu.RefreshConfigAsync();
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            await krtu.FlushAsync();
            Assert.That(server.Count("POST", "/v1/sessions"), Is.Zero, "off at the start: no session");

            async Task Flip(bool on)
            {
                config["modules"]!["sessions"] = on;
                bump();
                await krtu.RefreshConfigAsync();
            }
            int Ends() => server.Calls.Count(c => c.Path.EndsWith("/end"));
            int Beats() => server.Calls.Count(c => c.Path.EndsWith("/heartbeat"));

            await Flip(true);
            Assert.That(server.Count("POST", "/v1/sessions"), Is.EqualTo(1), "switched on: the session opens");
            await Until(() => Beats() > 0, "the open session sends heartbeats", 500);

            await Flip(false);
            Assert.That(Ends(), Is.EqualTo(1), "switched off: the session ends");
            var before = Beats();
            await krtu.FlushAsync();
            await Task.Delay(1500);
            Assert.That(Beats(), Is.EqualTo(before), "no heartbeat for an ended session");
            Assert.That(server.Count("POST", "/v1/sessions"), Is.EqualTo(1), "a flush does not open one while off");

            await Flip(true);
            Assert.That(server.Count("POST", "/v1/sessions"), Is.EqualTo(2), "on again: a new session");
            await krtu.ShutdownAsync();
        }
    }
}
