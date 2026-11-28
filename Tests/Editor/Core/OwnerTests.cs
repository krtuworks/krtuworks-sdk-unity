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
    /// <summary>A player's call goes only with that player's token, and queue moves and the file lane stay correct when cut short or run again.</summary>
    public class OwnerTests
    {
        const string A = "pw_guest_aaaaaaaaaaaa";
        const string B = "pw_other_bbbbbbbbbbbb";

        /// <summary>A store that lets a test wait in, or fail, a write or a delete of chosen keys.</summary>
        sealed class HookStore : IKrtuStore
        {
            readonly MemoryStore inner = new MemoryStore();
            internal Func<string, Task>? OnSet;
            internal Func<string, Task>? OnDelete;

            public Task<string?> GetAsync(string key) => inner.GetAsync(key);
            public async Task SetAsync(string key, string value)
            {
                if (OnSet != null) await OnSet(key);
                await inner.SetAsync(key, value);
            }
            public async Task DeleteAsync(string key)
            {
                if (OnDelete != null) await OnDelete(key);
                await inner.DeleteAsync(key);
            }
            public Task<IReadOnlyList<string>> KeysAsync(string prefix) => inner.KeysAsync(prefix);
        }

        /// <summary>Every call fails with no answer while `Offline`.</summary>
        sealed class SwitchableHttp : IHttpTransport
        {
            readonly IHttpTransport inner;
            internal bool Offline;
            internal SwitchableHttp(IHttpTransport inner) => this.inner = inner;
            public Task<HttpAnswer> SendAsync(HttpCall call) =>
                Offline ? throw new KrtuworksException("network_error", "No answer: offline") : inner.SendAsync(call);
        }

        sealed class Rig
        {
            internal Identity Identity = null!;
            internal MemoryStore Secrets = null!;
            internal IKrtuStore Store = null!;
            internal Resolved Opts = null!;
            internal ProgressQueue Progress = null!;
            internal EventQueue Events = null!;
            internal Files Files = null!;
            internal string Owner => Identity.Player!.Subject;
            internal string Upload => $"krtu:{Opts.ClientId}:upload:";
            internal string Sent => $"krtu:{Opts.ClientId}:uploadSent:";
        }

        static async Task<Rig> Build(FakeServer server, IKrtuStore? store = null, IHttpTransport? http = null)
        {
            server.GuestRoutes(A);
            var secrets = new MemoryStore();
            var transport = http ?? server;
            var opts = Resolved.From(server.Options(secrets, secrets, o => o.Http = transport));
            var clock = new Clock();
            var kept = store ?? secrets;
            var identity = new Identity(opts, kept, secrets, transport, clock, null);
            await identity.SignInAsGuestAsync("2000-01-01", "TERMS:v1 PRIVACY:v1", false);
            var api = new Api(opts, identity, transport, clock);
            string? Subject() => identity.Player?.Subject;
            return new Rig
            {
                Identity = identity,
                Secrets = secrets,
                Store = kept,
                Opts = opts,
                Progress = new ProgressQueue(opts, kept, api, Subject),
                Events = new EventQueue(opts, kept, clock, api, Subject),
                Files = new Files(opts, api, kept, Subject, () => identity.Generation, () => { }),
            };
        }

        /// <summary>Whose token a call carried.</summary>
        static string Bearer(FakeCall call)
        {
            var token = (call.Header("authorization") ?? "").Replace("Bearer ", "");
            return (string)JObject.Parse(Encoding.UTF8.GetString(Ids.FromBase64Url(token.Split('.')[1])))["sub"]!;
        }

        /// <summary>B's tokens written as another flow writes them when it signs B in.</summary>
        static Task AnotherFlowSignsIn(Rig rig)
        {
            var exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600;
            return Json.Put(rig.Secrets, $"krtu:{rig.Opts.ClientId}:tokens", new StoredTokens
            {
                Kind = "guest",
                Subject = B,
                AccessToken = FakeServer.FakeJwt(new JObject { ["sub"] = B, ["aud"] = "devapi", ["exp"] = exp, ["scope"] = "app:player" }),
                AccessExpiresAt = exp * 1000,
                RefreshToken = "rt-other",
            });
        }

        static async Task Until(Func<Task<bool>> condition)
        {
            for (var i = 0; i < 1000; i++)
            {
                if (await condition()) return;
                await Task.Delay(5);
            }
            Assert.Fail("the awaited state never came");
        }

        static Reply Applied(FakeCall call) => FakeServer.Ok(new JObject
        {
            ["results"] = new JArray(((JArray)call.Json!["ops"]!).Select(o => new JObject { ["opId"] = o["opId"], ["status"] = "applied", ["type"] = o["type"], ["key"] = o["key"], ["value"] = o["value"] })),
        });

        [Test]
        public async Task ACallRefused401AfterThePlayerChangedIsNotSentAgainWithTheNewPlayersToken_ControlTheSamePlayersIs()
        {
            foreach (var switched in new[] { true, false })
            {
                var server = new FakeServer();
                var rig = await Build(server);
                await rig.Progress.LearnKindsAsync(new[] { ("coins", "SUM") });
                await rig.Progress.ReportAsync("add", "coins", 5);
                var first = true;
                server.OnAsync("POST", "/v1/me/progress", async call =>
                {
                    if (!first) return Applied(call);
                    first = false;
                    // While A's call is on its way, another flow signs B in; then A's token is refused as expired.
                    if (switched) await AnotherFlowSignsIn(rig);
                    return new Reply { Status = 401, Json = new JObject { ["error"] = new JObject { ["code"] = "token_expired", ["message"] = "x" } } };
                });
                var output = await rig.Progress.FlushAsync();
                var sent = server.Calls.Where(c => c.Path == "/v1/me/progress").Select(Bearer).ToList();
                if (switched)
                {
                    Assert.That(output.StoppedBy, Is.EqualTo("context_changed"));
                    Assert.That(sent, Is.EqualTo(new[] { A }), "A's stat went only with A's token");
                    Assert.That(rig.Identity.Player?.Subject, Is.EqualTo(B));
                    Assert.That(await rig.Progress.WaitingAsync(A), Is.EqualTo(1), "A's op waits, frozen, for A");
                }
                else
                {
                    Assert.That(output.Applied, Is.EqualTo(1));
                    Assert.That(sent, Is.EqualTo(new[] { A, A }), "refreshed and sent again for the same player");
                }
            }
        }

        [Test]
        public async Task ALateAccountMergedAnswerForThePlayerWhoLeftDoesNotSignOutTheOneWhoPlays_ControlForThePlayerStillSignedInItDoes()
        {
            foreach (var switched in new[] { true, false })
            {
                var server = new FakeServer();
                var rig = await Build(server);
                var changes = new List<string>();
                rig.Identity.Changed += change => changes.Add(change.Type);
                await rig.Progress.LearnKindsAsync(new[] { ("coins", "SUM") });
                await rig.Progress.ReportAsync("add", "coins", 5);
                server.OnAsync("POST", "/v1/me/progress", async call =>
                {
                    // While A's call is on its way the player becomes B; then A's answer arrives: A's account was merged.
                    if (switched)
                    {
                        await AnotherFlowSignsIn(rig);
                        await rig.Identity.CredentialsAsync(true);
                    }
                    return new Reply { Status = 401, Json = new JObject { ["error"] = new JObject { ["code"] = "account_merged", ["message"] = "x" }, ["mergedInto"] = "pw_target_cccccccccccc" } };
                });
                var output = await rig.Progress.FlushAsync();
                var kept = await Json.Get<StoredTokens>(rig.Secrets, $"krtu:{rig.Opts.ClientId}:tokens");
                Assert.That(output.StoppedBy, Is.EqualTo("account_merged"));
                Assert.That(await rig.Identity.WaitingOwnersAsync(), Does.Contain(A), "A's queues wait for the merge check either way");
                if (switched)
                {
                    Assert.That(rig.Identity.Player?.Subject, Is.EqualTo(B), "B plays on");
                    Assert.That(kept?.Subject, Is.EqualTo(B), "B's tokens stay");
                    Assert.That(changes, Does.Not.Contain("merged"), "B is not told of a merge that was not its own");
                }
                else
                {
                    Assert.That(rig.Identity.Player, Is.Null, "A, merged, is signed out");
                    Assert.That(kept, Is.Null);
                    Assert.That(changes, Does.Contain("merged"));
                }
            }
        }

        [Test]
        public async Task AnAccountMergedOrDeletedAnswerToTheCallRetriedAfterARefreshIsToldToTheIdentity_ControlA200AfterTheRefreshChangesNothing()
        {
            foreach (var code in new string?[] { "account_merged", "account_deleted", null })
            {
                var server = new FakeServer();
                var rig = await Build(server);
                var changes = new List<string>();
                rig.Identity.Changed += change => changes.Add(change.Type);
                var api = new Api(rig.Opts, rig.Identity, server, new Clock());
                var calls = 0;
                server.On("GET", "/v1/me/status", _ =>
                {
                    calls++;
                    if (calls == 1) return new Reply { Status = 401, Json = new JObject { ["error"] = new JObject { ["code"] = "token_expired", ["message"] = "x" } } };
                    return code == null
                        ? new Reply { Json = new JObject { ["ok"] = true } }
                        : new Reply { Status = 401, Json = new JObject { ["error"] = new JObject { ["code"] = code, ["message"] = "x" }, ["mergedInto"] = "pw_target_cccccccccccc" } };
                });
                if (code == null)
                {
                    var answer = await api.PlayerAsync("/v1/me/status", new PlayerCall());
                    Assert.That(answer.Status, Is.EqualTo(200));
                    Assert.That(rig.Identity.Player?.Subject, Is.EqualTo(A));
                    Assert.That(changes, Is.Empty);
                    continue;
                }
                var err = await AsyncAssert.ThrowsAsync<KrtuworksException>(() => api.PlayerAsync("/v1/me/status", new PlayerCall()));
                Assert.That(err.Code, Is.EqualTo(code == "account_merged" ? "account_merged" : "account_deleted").Or.EqualTo("context_changed"));
                Assert.That(rig.Identity.Player, Is.Null, $"{code}: the player is signed out");
                Assert.That(changes, Is.EqualTo(code == "account_merged" ? new[] { "merged" } : new[] { "signed_out" }));
                Assert.That(calls, Is.EqualTo(2), "refreshed once, no second retry");
            }
        }

        [Test]
        public async Task ASessionReportsOnlyItsOwnTimeInUse_ANewOneForAnotherPlayerStartsFromZero_ControlTheFirstCountsItsTime()
        {
            var server = new FakeServer();
            var rig = await Build(server);
            var opened = 0;
            server.On("POST", "/v1/sessions", _ =>
            {
                opened++;
                return new Reply { Status = 201, Json = new JObject { ["session"] = new JObject { ["id"] = $"ses_{opened}", ["startedAt"] = Clock.IsoNow() }, ["heartbeatSeconds"] = 60 } };
            });
            server.OnPrefix("POST", "/v1/sessions/ses_", _ => FakeServer.Ok(new JObject { ["nextHeartbeatSeconds"] = 60 }));
            var sessions = new SessionKeeper(rig.Opts, new Api(rig.Opts, rig.Identity, server, new Clock()), () => rig.Identity.Player?.Subject, null);
            await sessions.OpenAsync();
            await Task.Delay(1100);
            await sessions.HeartbeatAsync();
            // The app goes to the background and the player changes: B's session starts with nothing in use.
            sessions.SetForeground(false);
            await AnotherFlowSignsIn(rig);
            await rig.Identity.CredentialsAsync(true);
            sessions.Close();
            await sessions.OpenAsync();
            await sessions.HeartbeatAsync();
            var beats = server.Calls.Where(c => c.Path.EndsWith("/heartbeat")).Select(c => ((long)c.Json!["activeSeconds"]!, (bool)c.Json!["foreground"]!)).ToList();
            Assert.That(beats.Count, Is.EqualTo(2));
            Assert.That(beats[0].Item1, Is.EqualTo(1), "A's session counts its second");
            Assert.That(beats[0].Item2, Is.True);
            Assert.That(beats[1].Item1, Is.EqualTo(0), "B's session does not take A's time");
            Assert.That(beats[1].Item2, Is.False);
            Assert.That(sessions.ActiveSeconds, Is.EqualTo(1), "the launch keeps its own total");
        }

        [Test]
        public async Task ASaveWhoseStoreWriteThePlayerOutlivedIsNotSentWithTheNewPlayersToken_ControlItIsSentWithItsOwn()
        {
            foreach (var switched in new[] { true, false })
            {
                var server = new FakeServer();
                var reached = new TaskCompletionSource<bool>();
                var release = new TaskCompletionSource<bool>();
                var store = new HookStore();
                var rig = await Build(server, store);
                store.OnSet = async key =>
                {
                    if (!key.StartsWith(rig.Upload, StringComparison.Ordinal)) return;
                    reached.TrySetResult(true);
                    await release.Task;
                };
                server.On("PUT", "/v1/apps/buried/files/save-1", call => new Reply
                {
                    Status = 201,
                    Json = new JObject { ["file"] = new JObject { ["id"] = "fil_aaaaaaaaaaaaaaaaaaaa", ["slot"] = "save-1", ["generation"] = 1 } },
                });
                var saving = rig.Files.SaveAsync("save-1", new byte[] { 1, 2, 3 }, true, null, null);
                await reached.Task;
                if (switched)
                {
                    await AnotherFlowSignsIn(rig);
                    await rig.Identity.AccessTokenAsync(true);
                    Assert.That(rig.Identity.Player?.Subject, Is.EqualTo(B));
                }
                release.SetResult(true);
                if (switched)
                {
                    var err = await AsyncAssert.ThrowsAsync<KrtuworksException>(() => saving);
                    Assert.That(err.Code, Is.EqualTo("context_changed"));
                    Assert.That(server.Calls.Count(c => c.Method == "PUT"), Is.EqualTo(0), "nothing went with B's token");
                    var kept = new List<string>();
                    foreach (var key in await store.KeysAsync(rig.Upload)) kept.Add((await Json.Get<PendingUpload>(store, key))!.Owner);
                    Assert.That(kept, Is.EqualTo(new[] { A }), "the save waits for A");
                }
                else
                {
                    await saving;
                    Assert.That(server.Calls.Where(c => c.Method == "PUT").Select(Bearer).ToList(), Is.EqualTo(new[] { A }));
                }
            }
        }

        [Test]
        public async Task AMoveCutShortAfterTheTargetWasWrittenAndRunAgainAddsNothingTwice_ControlAnUncutMove()
        {
            foreach (var cut in new[] { true, false })
            {
                var server = new FakeServer();
                var store = new HookStore();
                var failDelete = cut;
                store.OnDelete = key =>
                {
                    if (failDelete && key.EndsWith(":progress:anon", StringComparison.Ordinal))
                    {
                        failDelete = false;
                        throw new Exception("the app closed");
                    }
                    return Task.CompletedTask;
                };
                var rig = await Build(server, store);
                await rig.Progress.LearnKindsAsync(new[] { ("coins", "SUM") });
                // 7 made before any account, 3 by the player.
                await Json.Put(store, $"krtu:{rig.Opts.ClientId}:progress:anon", new ProgressState { Stats = { ["coins"] = new StatEntry { Pending = { new Contribution { Op = "add", Value = 7 } } } } });
                await rig.Progress.ReportAsync("add", "coins", 3);
                if (cut) await AsyncAssert.ThrowsAsync<Exception>(() => rig.Progress.MoveAsync("anon", rig.Owner));
                await rig.Progress.MoveAsync("anon", rig.Owner);
                Assert.That(await rig.Progress.WaitingAsync("anon"), Is.EqualTo(0));
                var ops = new List<JObject>();
                server.ApplyProgress(ops);
                await rig.Progress.FlushAsync();
                var total = ops.GroupBy(o => (string)o["opId"]!).Sum(g => (double)g.First()["value"]!);
                Assert.That(total, Is.EqualTo(10), cut ? "the source counted once after the cut and the second run" : "source and target");
            }
        }

        [Test]
        public async Task AnEventsMoveCutShortAndRunAgainHoldsEachEventOnceAndSaysTheDroppedCountOnce()
        {
            var server = new FakeServer();
            var store = new HookStore();
            var failDelete = true;
            store.OnDelete = key =>
            {
                if (failDelete && key.EndsWith(":events:anon", StringComparison.Ordinal))
                {
                    failDelete = false;
                    throw new Exception("the app closed");
                }
                return Task.CompletedTask;
            };
            var rig = await Build(server, store);
            WireEvent Event(string id) => new WireEvent { Id = id, Type = "button", CreatedAt = Clock.IsoNow(), LaunchId = "l", Data = new JObject { ["name"] = id } };
            await Json.Put(store, $"krtu:{rig.Opts.ClientId}:events:anon", new EventQueueState { Pending = { Event("e1"), Event("e2") }, Dropped = 4, Reported = 0 });
            await AsyncAssert.ThrowsAsync<Exception>(() => rig.Events.MoveAsync("anon", rig.Owner));
            await rig.Events.MoveAsync("anon", rig.Owner);
            var state = (await Json.Get<EventQueueState>(store, $"krtu:{rig.Opts.ClientId}:events:{rig.Owner}"))!;
            Assert.That(state.Pending.Select(e => e.Id).ToList(), Is.EqualTo(new[] { "e1", "e2" }));
            Assert.That(state.Dropped - state.Reported, Is.EqualTo(4));
        }

        /// <summary>A slot that keeps the last body it took, answering each upload id once; the first PUT waits for `gate` when there is one.</summary>
        sealed class SlotFake
        {
            internal readonly List<string> Taken = new List<string>();
            readonly Dictionary<string, JObject> known = new Dictionary<string, JObject>();

            internal SlotFake(FakeServer server, string slot, Task? gate = null)
            {
                var path = $"/v1/apps/buried/files/{slot}";
                server.OnAsync("PUT", path, async call =>
                {
                    if (gate != null && Taken.Count == 0) await gate;
                    var id = call.Header("idempotency-key")!;
                    lock (known)
                    {
                        if (!known.ContainsKey(id))
                        {
                            Taken.Add(Encoding.UTF8.GetString(call.Raw));
                            known[id] = new JObject { ["id"] = $"fil_{Taken.Count.ToString().PadLeft(20, 'a')}", ["slot"] = slot, ["generation"] = Taken.Count };
                        }
                        return new Reply { Status = 201, Json = new JObject { ["file"] = known[id] } };
                    }
                });
                server.OnPrefix("GET", $"{path}/uploads/", call =>
                {
                    var id = call.Path.Substring(call.Path.LastIndexOf('/') + 1);
                    lock (known)
                    {
                        return known.TryGetValue(id, out var file)
                            ? FakeServer.Ok(new JObject { ["uploadId"] = id, ["status"] = "ACTIVATED", ["file"] = file })
                            : new Reply { Status = 404, Json = new JObject { ["error"] = new JObject { ["code"] = "upload_not_found", ["message"] = "x" } } };
                    }
                });
            }
        }

        static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

        [Test]
        public async Task TwentySavesOfOneSlotMadeOfflineLeaveTwoWaitingNotTwentyOnlineTheNewestLandsLast()
        {
            var server = new FakeServer();
            var net = new SwitchableHttp(server);
            var rig = await Build(server, null, net);
            var slot = new SlotFake(server, "settings");
            net.Offline = true;
            var codes = new List<string>();
            for (var i = 1; i <= 20; i++)
            {
                try { await rig.Files.SaveAsync("settings", Text($"v{i}"), false, null, null); }
                catch (KrtuworksException err) { codes.Add(err.Code); }
            }
            Assert.That(codes.Count, Is.EqualTo(20));
            Assert.That(codes.All(c => c == "network_error"));
            Assert.That((await rig.Store.KeysAsync(rig.Upload)).Count, Is.EqualTo(2), "the first, which was sent, and the newest; the ones between were replaced");
            net.Offline = false;
            var output = await rig.Files.ResumeAsync();
            Assert.That(slot.Taken, Is.EqualTo(new[] { "v1", "v20" }), "the earlier one (it may have arrived) settles first; the newest lands last");
            Assert.That(output.Select(o => o.Outcome).ToList(), Is.EqualTo(new[] { "saved", "saved" }));
            Assert.That((await rig.Store.KeysAsync(rig.Upload)).Count, Is.EqualTo(0));
        }

        [Test]
        public async Task AnEarlierSaveTheServerNeverHeardOfIsReplacedOnceItCanNoLongerArrive_ControlsARecentOneIsSentAgainFirstAndOneWithAVersionCheckIsReplacedAtOnce()
        {
            // (age the sent time past the arrival window, the save names a base)
            foreach (var (old, conditional) in new[] { (true, false), (false, false), (false, true) })
            {
                var server = new FakeServer();
                var net = new SwitchableHttp(server);
                var rig = await Build(server, null, net);
                var slot = new SlotFake(server, "save-1");
                net.Offline = true;
                try { await rig.Files.SaveAsync("save-1", Text("v1"), conditional, null, null); } catch (KrtuworksException) { }
                try { await rig.Files.SaveAsync("save-1", Text("v2"), conditional, null, null); } catch (KrtuworksException) { }
                if (old)
                {
                    var past = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 16 * 60_000).ToString();
                    foreach (var key in await rig.Store.KeysAsync(rig.Sent)) await rig.Store.SetAsync(key, past);
                }
                net.Offline = false;
                var output = await rig.Files.ResumeAsync();
                var replacedFirst = old || conditional;
                Assert.That(slot.Taken, Is.EqualTo(replacedFirst ? new[] { "v2" } : new[] { "v1", "v2" }), $"old={old} conditional={conditional}");
                Assert.That(output.Select(o => o.Outcome).ToList(), Is.EqualTo(replacedFirst ? new[] { "replaced", "saved" } : new[] { "saved", "saved" }), $"old={old} conditional={conditional}");
            }
        }

        [Test]
        public async Task WaitingSavesGoInTheOrderTheyWereMadeNotTheOrderOfTheirIds()
        {
            var server = new FakeServer();
            var rig = await Build(server);
            var slot = new SlotFake(server, "settings");
            PendingUpload Record(string id, string text, long savedAt) => new PendingUpload { UploadId = id, Owner = A, Slot = "settings", Bytes = Text(text), Sha256 = "x", SavedAt = savedAt };
            // Written by an earlier SDK, which sent each at once: v2's id sorts (and is stored) before v1's.
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            const string low = "00000000-0000-4000-8000-000000000000";
            const string high = "ffffffff-ffff-4fff-8fff-ffffffffffff";
            await Json.Put(rig.Store, rig.Upload + low, Record(low, "v2", now - 1000));
            await Json.Put(rig.Store, rig.Upload + high, Record(high, "v1", now - 2000));
            await rig.Files.ResumeAsync();
            Assert.That(slot.Taken, Is.EqualTo(new[] { "v1", "v2" }), "the setting ends at v2");
        }

        [Test]
        public async Task ASaveStartedWhileAnEarlierOneOfTheSlotIsInFlightIsReplacedByANewerOneNeverSentAfterIt()
        {
            var server = new FakeServer();
            var rig = await Build(server);
            var release = new TaskCompletionSource<bool>();
            var slot = new SlotFake(server, "settings", release.Task);
            var first = rig.Files.SaveAsync("settings", Text("v1"), false, null, null);
            await Until(() => Task.FromResult(server.Calls.Any(c => c.Method == "PUT")));
            var second = rig.Files.SaveAsync("settings", Text("v2"), false, null, null);
            var third = rig.Files.SaveAsync("settings", Text("v3"), false, null, null);
            await Until(async () => (await rig.Store.KeysAsync(rig.Upload)).Count >= 3);
            release.SetResult(true);
            await first;
            var err = await AsyncAssert.ThrowsAsync<KrtuworksException>(() => second);
            Assert.That(err.Code, Is.EqualTo("save_replaced"));
            await third;
            Assert.That(slot.Taken, Is.EqualTo(new[] { "v1", "v3" }));
        }
    }
}
