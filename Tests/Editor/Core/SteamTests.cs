#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Krtuworks.Tests
{
    public class SteamTests
    {
        sealed class FakeBackend : ISteamBackend
        {
            internal bool Running = true;
            internal uint NextHandle = 7;
            internal readonly List<string> Requested = new List<string>();
            internal readonly List<(string Name, bool Achieved, bool CallWorks)> Achievements = new List<(string, bool, bool)>();
            internal readonly HashSet<string> Refused = new HashSet<string>();
            internal readonly List<string> Calls = new List<string>();

            public bool IsSteamRunning() => Running;

            public uint RequestWebApiTicket(string identity)
            {
                Requested.Add(identity);
                var handle = NextHandle;
                NextHandle = 0;
                return handle;
            }

            public void CancelAuthTicket(uint handle) => Calls.Add("cancel:" + handle);

            public uint GetNumAchievements() => (uint)Achievements.Count;

            public string GetAchievementName(uint index) => Achievements[(int)index].Name;

            public bool TryGetAchievement(string name, out bool achieved)
            {
                var found = Achievements.First(a => a.Name == name);
                achieved = found.Achieved;
                return found.CallWorks;
            }

            public bool SetAchievement(string name)
            {
                Calls.Add("set:" + name);
                return !Refused.Contains(name);
            }

            public bool ClearAchievement(string name)
            {
                Calls.Add("clear:" + name);
                return !Refused.Contains(name);
            }

            internal readonly Dictionary<string, int> Ints = new Dictionary<string, int>();
            internal readonly Dictionary<string, float> Floats = new Dictionary<string, float>();

            public bool TryGetStatInt(string name, out int value) => Ints.TryGetValue(name, out value);

            public bool TryGetStatFloat(string name, out float value) => Floats.TryGetValue(name, out value);

            public bool SetStatInt(string name, int value)
            {
                Calls.Add($"setInt:{name}={value}");
                if (!Ints.ContainsKey(name)) return false;
                Ints[name] = value;
                return true;
            }

            public bool SetStatFloat(string name, float value)
            {
                Calls.Add($"setFloat:{name}={value}");
                if (!Floats.ContainsKey(name)) return false;
                Floats[name] = value;
                return true;
            }

            public void StoreStats() => Calls.Add("store");

            internal bool CloudOn = true;
            internal readonly Dictionary<string, (byte[] Data, long At)> Cloud = new Dictionary<string, (byte[], long)>();
            internal readonly HashSet<string> Unreadable = new HashSet<string>();
            internal ulong? Free = 1_000_000;
            internal bool WriteWorks = true;

            public bool IsCloudEnabled() => CloudOn;

            public bool FileExists(string name) => Cloud.ContainsKey(name);

            public byte[]? FileRead(string name) => Unreadable.Contains(name) || !Cloud.TryGetValue(name, out var f) ? null : f.Data;

            public long FileTimestamp(string name) => Cloud.TryGetValue(name, out var f) ? f.At : 0;

            public bool FileWrite(string name, byte[] data)
            {
                Calls.Add($"write:{name}:{data.Length}");
                if (!WriteWorks) return false;
                Cloud[name] = (data, 1_800_000_000);
                return true;
            }

            public bool TryGetQuota(out ulong total, out ulong available)
            {
                total = 10_000_000;
                available = Free ?? 0;
                return Free.HasValue;
            }

            internal readonly Dictionary<string, ulong> Boards = new Dictionary<string, ulong>();
            /// <summary>A board's rows by handle: the local player's row; a null value is a download Steam failed.</summary>
            internal readonly Dictionary<ulong, PlatformLeaderboardScore?> OwnRows = new Dictionary<ulong, PlatformLeaderboardScore?>();

            public Task<ulong?> FindLeaderboardAsync(string name)
            {
                Calls.Add("find:" + name);
                return Task.FromResult(Boards.TryGetValue(name, out var handle) ? (ulong?)handle : null);
            }

            public Task<PlatformLeaderboardScore?> DownloadOwnEntryAsync(ulong leaderboard)
            {
                Calls.Add("download:" + leaderboard);
                return Task.FromResult(OwnRows.TryGetValue(leaderboard, out var row) ? row : null);
            }

            public bool SetRichPresence(string key, string value)
            {
                Calls.Add($"presence:{key}={value}");
                return !Refused.Contains(key);
            }

            public void ClearRichPresence() => Calls.Add("presence:clear");

            internal readonly HashSet<uint> Subscribed = new HashSet<uint>();

            public bool IsSubscribedApp(uint appId)
            {
                Calls.Add("subscribed:" + appId);
                return Subscribed.Contains(appId);
            }
        }

        [Test]
        public void TheSteamClientsOwnershipAnswerIsAskedPerAppId_ControlNothingIsAskedOfASteamThatIsNotRunningOrOfAnIdThatIsNotAnAppId()
        {
            var steam = new FakeBackend();
            steam.Subscribed.Add(481);
            var sync = new SteamAchievementSync(steam);
            Assert.That(sync.OwnsProduct("481"), Is.True);
            Assert.That(sync.OwnsProduct("482"), Is.False, "Steam says the DLC is not owned");
            Assert.That(sync.OwnsProduct("not-an-id"), Is.Null);
            Assert.That(steam.Calls, Is.EqualTo(new[] { "subscribed:481", "subscribed:482" }));
            var down = new FakeBackend { Running = false };
            down.Subscribed.Add(481);
            Assert.That(new SteamAchievementSync(down).OwnsProduct("481"), Is.Null, "a Steam that is not running cannot tell");
            Assert.That(down.Calls, Is.Empty);
        }

        [Test]
        public void RichPresenceGoesToSteamAsItIs_ControlNothingIsAskedOfASteamThatIsNotRunning()
        {
            var steam = new FakeBackend();
            var sync = new SteamAchievementSync(steam);
            sync.ClearRichPresence();
            Assert.That(sync.SetRichPresence("steam_display", "#Presence_Menu"), Is.True);
            steam.Refused.Add("MAP");
            Assert.That(sync.SetRichPresence("MAP", "dust"), Is.False, "Steam's refusal is told");
            Assert.That(steam.Calls, Is.EqualTo(new[] { "presence:clear", "presence:steam_display=#Presence_Menu", "presence:MAP=dust" }));
            var down = new FakeBackend { Running = false };
            var idle = new SteamAchievementSync(down);
            idle.ClearRichPresence();
            Assert.That(idle.SetRichPresence("steam_display", "#Presence_Menu"), Is.False);
            Assert.That(down.Calls, Is.Empty, "a Steam that is not running is not written to");
        }

        [Test]
        public async Task ATicketIsAskedForTheKrtuworksIdentityAndAnsweredInHex()
        {
            var steam = new FakeBackend();
            var sync = new SteamAchievementSync(steam);
            var pending = sync.GetWebApiTicketAsync();
            sync.OnTicketAnswer(7, new byte[] { 0x00, 0xab, 0x0f, 0xff, 0x99 }, 4);
            Assert.That(await pending, Is.EqualTo("00ab0fff"), "lower-case hex of the first `length` bytes");
            Assert.That(steam.Requested, Is.EqualTo(new[] { "krtuworks-id" }));
            Assert.That(steam.Calls, Is.Empty, "control: a ticket that is used is not cancelled");
        }

        [Test]
        public async Task ATicketNeverSentIsCancelledByItsText_ControlAnUnknownOrAlreadyCancelledOneIsNot()
        {
            var steam = new FakeBackend();
            var sync = new SteamAchievementSync(steam);
            var pending = sync.GetWebApiTicketAsync();
            sync.OnTicketAnswer(7, new byte[] { 0xab, 0xcd }, 2);
            var ticket = await pending;
            sync.CancelWebApiTicket("not-a-ticket-we-gave");
            Assert.That(steam.Calls, Is.Empty, "control: a ticket this class never handed out is not Steam's to cancel");
            sync.CancelWebApiTicket(ticket!);
            sync.CancelWebApiTicket(ticket!);
            Assert.That(steam.Calls, Is.EqualTo(new[] { "cancel:7" }), "cancelled once, by the handle it was issued under");
        }

        [Test]
        public async Task NoTicketWhenSteamIsNotRunning_ControlNothingIsAskedOfSteam()
        {
            var steam = new FakeBackend { Running = false };
            Assert.That(await new SteamAchievementSync(steam).GetWebApiTicketAsync(), Is.Null);
            Assert.That(steam.Requested, Is.Empty, "a Steam that is not running is not asked for a ticket");
        }

        [Test]
        public async Task NoTicketWhenSteamRefusesToStartOne()
        {
            var steam = new FakeBackend { NextHandle = 0 };
            Assert.That(await new SteamAchievementSync(steam, 5000).GetWebApiTicketAsync(), Is.Null, "an invalid handle returns at once, it is not waited for");
            Assert.That(steam.Calls, Is.Empty, "control: no ticket was started, nothing to cancel");
        }

        [Test]
        public async Task NoTicketWhenSteamsAnswerIsNotOk()
        {
            var steam = new FakeBackend();
            var sync = new SteamAchievementSync(steam);
            var pending = sync.GetWebApiTicketAsync();
            sync.OnTicketAnswer(7, null, 0);
            Assert.That(await pending, Is.Null);
            Assert.That(steam.Calls, Is.EqualTo(new[] { "cancel:7" }), "a refused ticket is cancelled");
        }

        [Test]
        public async Task NoTicketWhenSteamAnswersOkWithNoBytes()
        {
            var steam = new FakeBackend();
            var sync = new SteamAchievementSync(steam);
            var pending = sync.GetWebApiTicketAsync();
            sync.OnTicketAnswer(7, new byte[] { 0x01 }, 0);
            Assert.That(await pending, Is.Null, "an empty ticket is Steam's refusal, as in the Unreal core");
            Assert.That(steam.Calls, Is.EqualTo(new[] { "cancel:7" }));
        }

        [Test]
        public async Task NoTicketWhenNoAnswerComes_AndALateAnswerIsDropped()
        {
            var steam = new FakeBackend();
            var sync = new SteamAchievementSync(steam, 30);
            Assert.That(await sync.GetWebApiTicketAsync(), Is.Null, "gives up after the wait");
            Assert.That(steam.Calls, Is.EqualTo(new[] { "cancel:7" }), "the ticket nobody waited for is cancelled");
            Assert.DoesNotThrow(() => sync.OnTicketAnswer(7, new byte[] { 1 }, 1), "an answer nobody waits for is dropped");
        }

        [Test]
        public async Task AnAnswerForAnotherTicketIsIgnored_ControlOwnAnswerStillArrives()
        {
            var sync = new SteamAchievementSync(new FakeBackend());
            var pending = sync.GetWebApiTicketAsync();
            sync.OnTicketAnswer(8, new byte[] { 0xee }, 1);
            Assert.That(pending.IsCompleted, Is.False, "another handle's answer does not complete this ticket");
            sync.OnTicketAnswer(7, new byte[] { 0x01 }, 1);
            Assert.That(await pending, Is.EqualTo("01"));
        }

        [Test]
        public async Task TwoTicketsAskedTogetherEachGetTheirOwnAnswer()
        {
            var steam = new FakeBackend();
            var sync = new SteamAchievementSync(steam);
            var first = sync.GetWebApiTicketAsync();
            steam.NextHandle = 9;
            var second = sync.GetWebApiTicketAsync();
            sync.OnTicketAnswer(9, new byte[] { 0x22 }, 1);
            sync.OnTicketAnswer(7, new byte[] { 0x11 }, 1);
            Assert.That(await first, Is.EqualTo("11"));
            Assert.That(await second, Is.EqualTo("22"));
        }

        [Test]
        public async Task OnlyTheAchievementsSteamReportsAchievedAreListed()
        {
            var steam = new FakeBackend();
            steam.Achievements.Add(("ACH_A", true, true));
            steam.Achievements.Add(("ACH_B", false, true));
            steam.Achievements.Add(("ACH_C", true, true));
            steam.Achievements.Add(("ACH_D", true, false));
            var unlocked = await new SteamAchievementSync(steam).UnlockedAchievementsAsync();
            Assert.That(unlocked, Is.EqualTo(new[] { "ACH_A", "ACH_C" }), "not-achieved is left out; control: a failed read is left out even when it says achieved");
        }

        [Test]
        public async Task NoAchievementsMeansAnEmptyList()
        {
            Assert.That(await new SteamAchievementSync(new FakeBackend()).UnlockedAchievementsAsync(), Is.Empty);
        }

        [Test]
        public async Task UnlockSetsTheAchievementAndStoresOnlyWhenSteamTookIt()
        {
            var steam = new FakeBackend();
            steam.Refused.Add("ACH_NO");
            var sync = new SteamAchievementSync(steam);
            await sync.UnlockAchievementAsync("ACH_A");
            await sync.UnlockAchievementAsync("ACH_NO");
            await sync.UnlockAchievementAsync("ACH_B");
            Assert.That(steam.Calls, Is.EqualTo(new[] { "set:ACH_A", "store", "set:ACH_NO", "set:ACH_B", "store" }), "StoreStats right after each accepted set, none after a refused one");
        }

        [Test]
        public async Task ClearTakesTheAchievementBackAndStoresOnlyWhenSteamTookIt()
        {
            var steam = new FakeBackend();
            steam.Refused.Add("ACH_NO");
            var sync = new SteamAchievementSync(steam);
            await sync.ClearAchievementAsync("ACH_A");
            await sync.ClearAchievementAsync("ACH_NO");
            Assert.That(steam.Calls, Is.EqualTo(new[] { "clear:ACH_A", "store", "clear:ACH_NO" }), "control: a set is never made by a clear");
        }

        [Test]
        public async Task StatsAreReadAndSetByTheirSteamType_AStatSteamDoesNotHoldIsNull_ControlASetIsNotStoredByItself()
        {
            var steam = new FakeBackend();
            steam.Ints["STAT_KILLS"] = 3;
            steam.Floats["STAT_BEST"] = 1.5f;
            var sync = new SteamAchievementSync(steam);
            Assert.That(await sync.GetStatAsync("STAT_KILLS", "INT"), Is.EqualTo(3));
            Assert.That(await sync.GetStatAsync("STAT_BEST", "FLOAT"), Is.EqualTo(1.5));
            Assert.That(await sync.GetStatAsync("STAT_KILLS", "FLOAT"), Is.Null, "another type is not held");
            Assert.That(await sync.GetStatAsync("STAT_NONE", "INT"), Is.Null);
            Assert.That(await sync.SetStatAsync("STAT_KILLS", "INT", 9), Is.True);
            Assert.That(await sync.SetStatAsync("STAT_BEST", "FLOAT", 2.25), Is.True);
            Assert.That(await sync.SetStatAsync("STAT_NONE", "INT", 1), Is.False, "Steam refused a stat it does not hold");
            Assert.That(steam.Calls, Is.EqualTo(new[] { "setInt:STAT_KILLS=9", "setFloat:STAT_BEST=2.25", "setInt:STAT_NONE=1" }), "no StoreStats with a set: the SDK stores every 2 minutes");
            await sync.StoreStatsAsync();
            Assert.That(steam.Calls.Last(), Is.EqualTo("store"));
        }

        [Test]
        public async Task SteamCloudFilesAreReadWithTheirTimeAndWritten_AnUnreadableFileThrowsInsteadOfReadingAsAbsent()
        {
            var steam = new FakeBackend();
            steam.Cloud["krtuworks-save-1.bin"] = (new byte[] { 1, 2, 3 }, 1_700_000_000);
            var cloud = new SteamAchievementSync(steam);
            Assert.That(cloud.CloudEnabled(), Is.True);
            var file = await cloud.ReadFileAsync("krtuworks-save-1.bin");
            Assert.That(file!.Data, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(file.WrittenAt, Is.EqualTo(1_700_000_000L));
            Assert.That(await cloud.ReadFileAsync("krtuworks-none.bin"), Is.Null, "control: no file is null");
            steam.Unreadable.Add("krtuworks-save-1.bin");
            var err = await AsyncAssert.ThrowsAsync<KrtuworksException>(() => cloud.ReadFileAsync("krtuworks-save-1.bin"));
            Assert.That((err!.Code, err.Retryable), Is.EqualTo(("platform_read_failed", true)), "a file Steam lists but cannot read is never taken as absent (another PC's newer save would be written over)");
            Assert.That(await cloud.WriteFileAsync("krtuworks-save-2.bin", new byte[] { 9 }), Is.True);
            Assert.That(await cloud.AvailableBytesAsync(), Is.EqualTo(1_000_000));
            steam.Free = null;
            Assert.That(await cloud.AvailableBytesAsync(), Is.Null, "Steam could not tell");
            steam.CloudOn = false;
            Assert.That(cloud.CloudEnabled(), Is.False, "the player or the game turned Steam Cloud off");
            steam.CloudOn = true;
            steam.Running = false;
            Assert.That(cloud.CloudEnabled(), Is.False, "Steam not running");
        }

        [Test]
        public async Task TheOwnRowOfASteamLeaderboardIsRead_NoRowIsAnAnswerToo_ABoardSteamDoesNotHaveOrCannotDownloadIsNot_NothingIsEverUploaded()
        {
            var steam = new FakeBackend();
            steam.Boards["fastest_run"] = 77;
            steam.OwnRows[77] = new PlatformLeaderboardScore { Present = true, Score = 91234 };
            steam.Boards["empty_run"] = 78;
            steam.OwnRows[78] = new PlatformLeaderboardScore { Present = false };
            steam.Boards["broken_run"] = 79;
            var sync = new SteamAchievementSync(steam);

            var own = await sync.ReadOwnScoreAsync("fastest_run");
            Assert.That((own!.Present, own.Score), Is.EqualTo((true, 91234L)));
            Assert.That((await sync.ReadOwnScoreAsync("empty_run"))!.Present, Is.False, "the player has no row there");
            Assert.That(await sync.ReadOwnScoreAsync("no_such_board"), Is.Null, "Steam has no such board");
            Assert.That(await sync.ReadOwnScoreAsync("broken_run"), Is.Null, "Steam failed the download");
            Assert.That(steam.Calls, Is.EqualTo(new[] { "find:fastest_run", "download:77", "find:empty_run", "download:78", "find:no_such_board", "find:broken_run", "download:79" }),
                "Steam was only asked to find a board and to hand over the player's own row: no upload call exists, none was made");
            steam.Calls.Clear();
            steam.Running = false;
            Assert.That(await sync.ReadOwnScoreAsync("fastest_run"), Is.Null, "control: Steam not running reads nothing");
            Assert.That(steam.Calls, Is.Empty);
        }
    }
}
