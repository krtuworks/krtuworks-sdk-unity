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
    public class CloudMirrorTests
    {
        const string Terms = "TERMS:v1 PRIVACY:v1";

        [Test]
        public void TheStepFollowsTheThreeHashes()
        {
            Assert.That(CloudMirror.Decide("SAVE", "a", "a", null, false), Is.EqualTo(MirrorStep.Agree), "the same on both sides");
            Assert.That(CloudMirror.Decide("SAVE", null, null, "a", false), Is.EqualTo(MirrorStep.Agree), "nothing on either side");
            Assert.That(CloudMirror.Decide("SAVE", "b", "a", "a", false), Is.EqualTo(MirrorStep.ToPlatform), "the server moved since both agreed");
            Assert.That(CloudMirror.Decide("SAVE", "a", "b", "a", false), Is.EqualTo(MirrorStep.ToServer), "Steam moved since both agreed");
            Assert.That(CloudMirror.Decide("SAVE", "a", null, null, false), Is.EqualTo(MirrorStep.ToPlatform), "Steam has no copy");
            Assert.That(CloudMirror.Decide("SAVE", null, "a", null, false), Is.EqualTo(MirrorStep.ToServer), "the server's slot is empty");
            Assert.That(CloudMirror.Decide("SAVE", "a", "b", null, false), Is.EqualTo(MirrorStep.Offer), "the first comparison on this device: the player chooses");
            Assert.That(CloudMirror.Decide("PROFILE", "a", "b", "c", true), Is.EqualTo(MirrorStep.Offer), "both moved: the player chooses, whichever is newer");
            Assert.That(CloudMirror.Decide("SETTINGS", "a", "b", "c", true), Is.EqualTo(MirrorStep.ToServer), "SETTINGS: the latest wins with no question");
            Assert.That(CloudMirror.Decide("SETTINGS", "a", "b", null, false), Is.EqualTo(MirrorStep.ToPlatform), "control: the server's is the later one");
        }

        internal sealed class FakeCloud : IPlatformServices, IPlatformCloud
        {
            internal bool On = true;
            internal long? Free = 1_000_000;
            internal readonly Dictionary<string, (byte[] Data, long At)> Files = new Dictionary<string, (byte[], long)>();
            internal readonly List<string> Writes = new List<string>();
            internal bool Refuse;

            public Task<string?> GetWebApiTicketAsync() => Task.FromResult<string?>("0a0b0c0d");
            public Task<IReadOnlyList<string>> UnlockedAchievementsAsync() => Task.FromResult<IReadOnlyList<string>>(new List<string>());
            public Task UnlockAchievementAsync(string platformId) => Task.CompletedTask;
            public Task ClearAchievementAsync(string platformId) => Task.CompletedTask;
            public bool CloudEnabled() => On;
            public Task<PlatformCloudFile?> ReadFileAsync(string name) =>
                Task.FromResult(Files.TryGetValue(name, out var f) ? new PlatformCloudFile { Data = f.Data, WrittenAt = f.At } : null);
            public Task<bool> WriteFileAsync(string name, byte[] data)
            {
                Writes.Add($"{name}={Encoding.UTF8.GetString(data)}");
                if (Refuse) return Task.FromResult(false);
                Files[name] = (data, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                return Task.FromResult(true);
            }
            public Task<long?> AvailableBytesAsync() => Task.FromResult(Free);

            internal string? Text(string slot) => Files.TryGetValue(CloudMirror.FileName(slot), out var f) ? Encoding.UTF8.GetString(f.Data) : null;
            internal void Put(string slot, string text, long at = 0) => Files[CloudMirror.FileName(slot)] = (Encoding.UTF8.GetBytes(text), at);
        }

        /// <summary>The app's slots as the server keeps them: the current version, the one waiting for a choice.</summary>
        internal sealed class Slot
        {
            internal string Kind = "SAVE";
            internal bool Mirror = true;
            internal string? Text;
            internal int Gen;
            internal string SavedAt = "2026-11-08T10:00:00.000Z";
            internal string? Waiting;
            internal int WaitingGen;
        }

        static string Sha(string text) => Ids.Sha256Hex(Encoding.UTF8.GetBytes(text));

        static JObject Version(string key, string id, int gen, string text, string savedAt, string? platformCloud = null) => new JObject
        {
            ["id"] = id, ["slot"] = key, ["device"] = null, ["generation"] = gen, ["sizeBytes"] = Encoding.UTF8.GetByteCount(text),
            ["sha256"] = Sha(text), ["platform"] = "STEAM", ["build"] = "1.0.0", ["savedAt"] = savedAt, ["fromMerge"] = false, ["platformCloud"] = platformCloud,
        };

        static string Id(string key, int gen) => $"fil_{key.Replace("-", "")}{gen}".PadRight(24, '0');

        internal static void SlotRoutes(FakeServer server, Dictionary<string, Slot> slots)
        {
            JToken? Current(string key, Slot s) => s.Text == null ? null : Version(key, Id(key, s.Gen), s.Gen, s.Text, s.SavedAt);
            JToken? Waiting(string key, Slot s) => s.Waiting == null ? null : Version(key, Id(key, s.WaitingGen) + "w", s.WaitingGen, s.Waiting, "2026-11-08T11:00:00.000Z", "STEAM");
            server.On("GET", "/v1/apps/buried/files", _ => FakeServer.Ok(new JObject
            {
                ["slots"] = new JArray(slots.Select(p => new JObject
                {
                    ["key"] = p.Key, ["kind"] = p.Value.Kind, ["name"] = p.Key, ["retired"] = false, ["platformMirror"] = p.Value.Mirror,
                    ["file"] = Current(p.Key, p.Value), ["conflict"] = Waiting(p.Key, p.Value), ["devices"] = new JArray(),
                })),
                ["limits"] = new JObject { ["maxFileBytes"] = 8_388_608 },
            }));
            foreach (var key in slots.Keys)
            {
                var s = slots[key];
                server.On("GET", $"/v1/apps/buried/files/{key}", _ =>
                {
                    var reply = new Reply { Body = Encoding.UTF8.GetBytes(s.Text ?? "") };
                    reply.Headers["etag"] = $"\"{Id(key, s.Gen)}\"";
                    reply.Headers["x-krtu-sha256"] = Sha(s.Text ?? "");
                    reply.Headers["x-krtu-generation"] = s.Gen.ToString();
                    reply.Headers["x-krtu-saved-at"] = s.SavedAt;
                    return reply;
                });
                server.On("PUT", $"/v1/apps/buried/files/{key}", call =>
                {
                    var match = call.Header("if-match");
                    var empty = call.Header("if-none-match") == "*";
                    if ((match != null && match != Id(key, s.Gen)) || (empty && s.Text != null))
                        return new Reply { Status = 409, Json = new JObject { ["error"] = new JObject { ["code"] = "file_conflict", ["message"] = "moved" }, ["current"] = Current(key, s) } };
                    s.Text = Encoding.UTF8.GetString(call.Raw);
                    s.Gen++;
                    s.SavedAt = DateTimeOffset.UtcNow.ToString("o");
                    return new Reply { Status = 201, Json = new JObject { ["file"] = Current(key, s) } };
                });
                server.On("PUT", $"/v1/apps/buried/files/{key}/offer", call =>
                {
                    Assert.That((call.Query["platform"], call.Query["sessionId"]), Is.EqualTo(("STEAM", "ses_cccccccccccccccccccc")), "the session proves the Steam account");
                    var text = Encoding.UTF8.GetString(call.Raw);
                    if (call.Header("if-match") != Id(key, s.Gen))
                        return new Reply { Status = 409, Json = new JObject { ["error"] = new JObject { ["code"] = "file_conflict", ["message"] = "moved" } } };
                    if (s.Waiting != null)
                        return new Reply { Status = 409, Json = new JObject { ["error"] = new JObject { ["code"] = "conflict_pending", ["message"] = "waiting" }, ["conflict"] = Waiting(key, s) } };
                    if (text == s.Text)
                        return new Reply { Status = 422, Json = new JObject { ["error"] = new JObject { ["code"] = "same_as_current", ["message"] = "same" } } };
                    s.Waiting = text;
                    s.WaitingGen = s.Gen + 1;
                    return new Reply { Status = 201, Json = new JObject { ["uploadId"] = Guid.NewGuid().ToString(), ["status"] = "ACTIVATED", ["file"] = Waiting(key, s) } };
                });
                server.On("POST", $"/v1/apps/buried/files/{key}/resolve", call =>
                {
                    if (s.Waiting == null || (string?)call.Json!["current"] != Id(key, s.Gen))
                        return new Reply { Status = 409, Json = new JObject { ["error"] = new JObject { ["code"] = "stale_choice", ["message"] = "read again" } } };
                    if ((string?)call.Json!["keep"] == Id(key, s.WaitingGen) + "w")
                    {
                        s.Text = s.Waiting;
                        s.Gen = s.WaitingGen + 1;
                    }
                    s.Waiting = null;
                    return FakeServer.Ok(new JObject { ["file"] = Current(key, s) });
                });
            }
        }

        internal static async Task<KrtuworksClient> Start(FakeServer server, FakeCloud cloud, Dictionary<string, Slot> slots, List<JObject>? events = null, string link = "ACTIVE", List<PlatformSaveConflict>? told = null, Action<KrtuworksClient>? beforeSignIn = null)
        {
            server.GuestRoutes();
            server.ConfigRoute(new JObject());
            server.AcceptEvents(events ?? new List<JObject>());
            server.On("POST", "/oidc/token", _ => FakeServer.Ok(FakeServer.TokenAnswer("pw_steam_cccccccccccc")));
            server.On("POST", "/v1/sessions", _ => new Reply
            {
                Status = 201,
                Json = new JObject
                {
                    ["session"] = new JObject { ["id"] = "ses_cccccccccccccccccccc", ["startedAt"] = Clock.IsoNow() },
                    ["platformLink"] = new JObject { ["platform"] = "STEAM", ["platformAccountId"] = "76561198000000302", ["state"] = link },
                },
            });
            server.On("POST", "/v1/me/progress/platform-sync", _ => FakeServer.Ok(new JObject { ["unlockedHere"] = new JArray(), ["toPlatform"] = new JArray(), ["toClear"] = new JArray() }));
            SlotRoutes(server, slots);
            var krtu = await KrtuworksClient.InitializeAsync(server.Options(extra: o =>
            {
                o.Platform = "STEAM";
                o.Sessions = true;
                o.PlatformServices = cloud;
            }));
            if (told != null) krtu.PlatformSaveConflict += told.Add;
            beforeSignIn?.Invoke(krtu);
            await krtu.SignInAsGuestAsync("2000-01-01", Terms);
            await krtu.ReadyAsync();
            await krtu.SyncPlatformFilesAsync();
            return krtu;
        }

        [Test]
        public async Task TheServerIsTheRecord_SteamGetsItsVersion_ThenEachSideThatMovedAlone()
        {
            var server = new FakeServer();
            var cloud = new FakeCloud();
            var slots = new Dictionary<string, Slot>
            {
                ["save-1"] = new Slot { Text = "A", Gen = 1 },
                ["device"] = new Slot { Kind = "DEVICE_SETTINGS", Text = "gfx", Gen = 1 },
                ["unmirrored"] = new Slot { Mirror = false, Text = "U", Gen = 1 },
            };
            var krtu = await Start(server, cloud, slots);
            Assert.That(cloud.Text("save-1"), Is.EqualTo("A"), "Steam had no copy: it gets the server's");
            Assert.That(cloud.Writes.Count, Is.EqualTo(1), "DEVICE_SETTINGS and a slot the owner did not mirror never go to Steam");

            await krtu.SyncPlatformFilesAsync();
            Assert.That(cloud.Writes.Count, Is.EqualTo(1), "control: the same on both sides, nothing is written");

            var read = await krtu.ReadFileAsync("save-1");
            await krtu.SaveFileAsync("save-1", Encoding.UTF8.GetBytes("B"), read!.VersionId);
            await krtu.SyncPlatformFilesAsync();
            Assert.That(cloud.Text("save-1"), Is.EqualTo("B"), "the game saved here: Steam follows");

            cloud.Put("save-1", "C");
            await krtu.SyncPlatformFilesAsync();
            Assert.That(slots["save-1"].Text, Is.EqualTo("C"), "Steam moved alone (another PC through Steam): the server takes it");
            var put = server.Calls.Last(c => c.Method == "PUT" && c.Path == "/v1/apps/buried/files/save-1");
            Assert.That(put.Header("if-match"), Is.EqualTo(Id("save-1", 2)), "over the version compared, so a save in between is not lost");
            Assert.That(server.Count("PUT", "/v1/apps/buried/files/save-1/offer"), Is.EqualTo(0), "control: nothing asked the player");
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task TheFirstComparisonWithADifferentCopyAsksThePlayer_NothingMovesUntilTheChoice_KeepingTheServersPutsItOnSteam()
        {
            var server = new FakeServer();
            var cloud = new FakeCloud();
            cloud.Put("save-1", "steam");
            var slots = new Dictionary<string, Slot> { ["save-1"] = new Slot { Text = "server", Gen = 3 } };
            var told = new List<PlatformSaveConflict>();
            var krtu = await Start(server, cloud, slots, told: told);

            Assert.That(told.Count, Is.EqualTo(1));
            Assert.That((told[0].Slot, told[0].Server.Sha256, told[0].Platform.Sha256, Encoding.UTF8.GetString(told[0].PlatformData!)), Is.EqualTo(("save-1", Sha("server"), Sha("steam"), "steam")));
            Assert.That(told[0].Platform.PlatformCloud, Is.EqualTo("STEAM"));
            Assert.That((slots["save-1"].Text, cloud.Text("save-1")), Is.EqualTo(("server", "steam")), "neither side is written over before the player chose");

            await krtu.SyncPlatformFilesAsync();
            Assert.That(server.Count("PUT", "/v1/apps/buried/files/save-1/offer"), Is.EqualTo(1), "a waiting copy is not offered again");
            Assert.That(told.Count, Is.EqualTo(1), "and the game is told once per launch");

            await krtu.ResolveFileAsync("save-1", told[0].Server.Id, told[0].Server.Id);
            Assert.That(cloud.Text("save-1"), Is.EqualTo("server"), "the kept version goes to Steam too");
            await krtu.SyncPlatformFilesAsync();
            Assert.That(server.Count("PUT", "/v1/apps/buried/files/save-1/offer"), Is.EqualTo(1), "control: after the choice the same copy is not offered again");
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task KeepingSteamsCopyMakesItTheServersVersion_AndSteamIsNotWrittenAgain()
        {
            var server = new FakeServer();
            var cloud = new FakeCloud();
            cloud.Put("save-1", "steam");
            var slots = new Dictionary<string, Slot> { ["save-1"] = new Slot { Text = "server", Gen = 3 } };
            var told = new List<PlatformSaveConflict>();
            var krtu = await Start(server, cloud, slots, told: told);
            await krtu.ResolveFileAsync("save-1", told[0].Platform.Id, told[0].Server.Id);
            Assert.That(slots["save-1"].Text, Is.EqualTo("steam"));
            Assert.That(cloud.Writes, Is.Empty, "Steam already holds the kept bytes");
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task SettingsTakeTheLatestWithNoQuestion_AnEmptyServerSlotTakesSteamsCopy()
        {
            var server = new FakeServer();
            var cloud = new FakeCloud();
            cloud.Put("options", "steam-newer", DateTimeOffset.Parse("2026-11-08T12:00:00Z").ToUnixTimeSeconds());
            cloud.Put("profile", "steam-profile");
            var slots = new Dictionary<string, Slot>
            {
                ["options"] = new Slot { Kind = "SETTINGS", Text = "server-older", Gen = 1, SavedAt = "2026-11-08T10:00:00.000Z" },
                ["profile"] = new Slot { Kind = "PROFILE" },
            };
            var krtu = await Start(server, cloud, slots);
            Assert.That(slots["options"].Text, Is.EqualTo("steam-newer"), "Steam's SETTINGS were written later: they win");
            var settings = server.Calls.Last(c => c.Method == "PUT" && c.Path == "/v1/apps/buried/files/options");
            Assert.That((settings.Header("if-match"), settings.Header("if-none-match")), Is.EqualTo(((string?)null, (string?)null)), "SETTINGS are saved without a base: the latest wins");
            Assert.That(slots["profile"].Text, Is.EqualTo("steam-profile"), "nothing on the server: Steam's copy becomes the first version");
            var profile = server.Calls.Last(c => c.Method == "PUT" && c.Path == "/v1/apps/buried/files/profile");
            Assert.That(profile.Header("if-none-match"), Is.EqualTo("*"), "only into an empty slot");
            Assert.That(server.Count("PUT", "/v1/apps/buried/files/options/offer") + server.Count("PUT", "/v1/apps/buried/files/profile/offer"), Is.EqualTo(0));
            await krtu.ShutdownAsync();

            var server2 = new FakeServer();
            var cloud2 = new FakeCloud();
            cloud2.Put("options", "steam-older", DateTimeOffset.Parse("2026-11-08T09:00:00Z").ToUnixTimeSeconds());
            var slots2 = new Dictionary<string, Slot> { ["options"] = new Slot { Kind = "SETTINGS", Text = "server-newer", Gen = 1, SavedAt = "2026-11-08T10:00:00.000Z" } };
            var krtu2 = await Start(server2, cloud2, slots2);
            Assert.That((slots2["options"].Text, cloud2.Text("options")), Is.EqualTo(("server-newer", "server-newer")), "control: the server's are later, Steam takes them");
            await krtu2.ShutdownAsync();
        }

        [Test]
        public async Task NoRoomOnSteamLeavesTheSlotAlone_TheGameSeesNoErrorAndTheServerIsUntouched()
        {
            var server = new FakeServer();
            var cloud = new FakeCloud { Free = 1 };
            var events = new List<JObject>();
            var slots = new Dictionary<string, Slot> { ["save-1"] = new Slot { Text = "a bigger save", Gen = 2 }, ["save-2"] = new Slot { Text = "x", Gen = 1 } };
            var krtu = await Start(server, cloud, slots, events);
            Assert.That(cloud.Text("save-1"), Is.Null, "no room: not written");
            Assert.That(cloud.Text("save-2"), Is.EqualTo("x"), "control: a slot that fits is written");
            Assert.That(slots["save-1"].Text, Is.EqualTo("a bigger save"));
            await krtu.SyncPlatformFilesAsync();
            Assert.That(cloud.Writes.Count, Is.EqualTo(1), "left alone for the session");
            await krtu.FlushAsync();
            Assert.That(events.Any(e => (string?)e["type"] == "error" && (string?)e["data"]!["fingerprint"] == "sdk.platform_quota"), Is.True, "told as an error event");
            await krtu.ShutdownAsync();

            var server2 = new FakeServer();
            var refusing = new FakeCloud { Refuse = true };
            var events2 = new List<JObject>();
            var slots2 = new Dictionary<string, Slot> { ["save-1"] = new Slot { Text = "A", Gen = 1 } };
            var krtu2 = await Start(server2, refusing, slots2, events2);
            await krtu2.SyncPlatformFilesAsync();
            Assert.That(refusing.Writes.Count, Is.EqualTo(1), "a write Steam refused is not tried again in the session");
            Assert.That(slots2["save-1"].Text, Is.EqualTo("A"), "the server's copy is untouched");
            await krtu2.FlushAsync();
            Assert.That(events2.Any(e => (string?)e["data"]!["fingerprint"] == "sdk.platform_rejected"), Is.True);
            await krtu2.ShutdownAsync();
        }

        [Test]
        public async Task NothingIsMirroredWithASteamAccountThePlayerHasNotAllowed_OrWithSteamCloudOff()
        {
            var server = new FakeServer();
            var cloud = new FakeCloud();
            var slots = new Dictionary<string, Slot> { ["save-1"] = new Slot { Text = "A", Gen = 1 } };
            var krtu = await Start(server, cloud, slots, link: "PENDING_CONSENT");
            Assert.That(cloud.Writes, Is.Empty);
            Assert.That(server.Count("GET", "/v1/apps/buried/files"), Is.EqualTo(0), "not even read");
            await krtu.ShutdownAsync();

            var server2 = new FakeServer();
            var off = new FakeCloud { On = false };
            var krtu2 = await Start(server2, off, new Dictionary<string, Slot> { ["save-1"] = new Slot { Text = "A", Gen = 1 } });
            Assert.That(off.Writes, Is.Empty, "the player turned Steam Cloud off");
            await krtu2.ShutdownAsync();
        }
    }
}
