#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Krtuworks.Internal;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Krtuworks.Tests
{
    /// <summary>The ready-made screens' decisions (the JS package's behaviour tests, ported) plus the native sign-ins: browser, code, phone, none.</summary>
    public class UiTests
    {
        static readonly DateTimeOffset Today = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        /// <summary>The screens' waits, short: the SDK's own (a second between code polls, 20 seconds of reminder) would stall the tests.</summary>
        static Task Fast(TimeSpan span, CancellationToken cancel) => Task.Delay(TimeSpan.FromMilliseconds(Math.Min(span.TotalMilliseconds, 20)), cancel);

        sealed class FakeDialog : IUiDialog
        {
            internal readonly UiAnswerQueue Answers = new UiAnswerQueue();
            internal readonly List<UiScreen> Updates = new List<UiScreen>();
            internal bool Closed;

            public Task<UiAnswer> NextAnswerAsync() => Answers.NextAsync();
            public void Update(UiScreen screen) => Updates.Add(screen);

            public void Close()
            {
                Closed = true;
                Answers.Close();
            }
        }

        sealed class FakeReminder : IUiReminder
        {
            readonly TaskCompletionSource<UiReminderAnswer> answer = new TaskCompletionSource<UiReminderAnswer>();
            internal bool Closed;

            public Task<UiReminderAnswer> Answer => answer.Task;
            internal void Answered(UiReminderAnswer a) => answer.TrySetResult(a);

            public void Close() => Closed = true;
        }

        /// <summary>A host that answers each screen with what the script says (null: the test answers later) and records what it was shown.</summary>
        sealed class ScriptedHost : IUiHost
        {
            readonly Func<UiScreen, UiAnswer?> script;
            internal readonly List<UiScreen> Shown = new List<UiScreen>();
            internal readonly List<FakeDialog> Dialogs = new List<FakeDialog>();
            internal readonly List<FakeReminder> Reminders = new List<FakeReminder>();
            internal UiReminderAnswer? ReminderAnswer;

            internal ScriptedHost(Func<UiScreen, UiAnswer?> script) => this.script = script;

            public IUiDialog Show(UiScreen screen)
            {
                Shown.Add(screen);
                var dialog = new FakeDialog();
                Dialogs.Add(dialog);
                var answer = script(screen);
                if (answer != null) dialog.Answers.Post(answer);
                return dialog;
            }

            public IUiReminder Remind()
            {
                var reminder = new FakeReminder();
                Reminders.Add(reminder);
                if (ReminderAnswer.HasValue) reminder.Answered(ReminderAnswer.Value);
                return reminder;
            }

            internal IEnumerable<UiScreenKind> Kinds => Shown.Select(s => s.Kind);
        }

        /// <summary>The next scripted reply for each screen in turn; a screen with none is a failure.</summary>
        static ScriptedHost Script(params Func<UiScreen, UiAnswer>[] replies)
        {
            var queue = new Queue<Func<UiScreen, UiAnswer>>(replies);
            return new ScriptedHost(screen =>
            {
                if (queue.Count == 0) throw new InvalidOperationException($"no reply scripted for the {screen.Kind} screen");
                return queue.Dequeue()(screen);
            });
        }

        /// <summary>Answers an error screen with Keep playing and leaves every other screen to the test.</summary>
        static ScriptedHost Manual() => new ScriptedHost(screen => screen is ErrorScreen ? UiAnswer.Continue : null);

        static Func<UiScreen, UiAnswer> Birth(int year, int month, int day, bool accepted = true) => _ => UiAnswer.Birth(year, month, day, accepted);
        static Func<UiScreen, UiAnswer> Say(UiAnswer answer) => _ => answer;

        static KrtuworksUiOptions Opts(MemoryStore? memory = null, string locale = "en", DateTimeOffset? now = null, UiSignInMode mode = UiSignInMode.Browser, Action<string>? open = null, Action<KrtuworksUiOptions>? extra = null)
        {
            var options = new KrtuworksUiOptions
            {
                Locale = locale,
                Now = () => now ?? Today,
                Delay = Fast,
                Memory = memory,
                Mode = mode,
                OpenUrl = mode == UiSignInMode.Browser ? open ?? (_ => { }) : open,
                PlatformSaveConflicts = false,
            };
            extra?.Invoke(options);
            return options;
        }

        static async Task<KrtuworksClient> Start(FakeServer server, MemoryStore? store = null, JObject? config = null)
        {
            server.ConfigRoute(config ?? new JObject());
            return await KrtuworksClient.InitializeAsync(server.Options(store, extra: o => o.Delay = Fast));
        }

        static IEnumerable<FakeCall> GuestGrants(FakeServer server) => server.Calls.Where(c => c.Path == "/oidc/token" && c.Field("grant_type") == Identity.DeviceKeyGrant);

        static async Task Until(Func<bool> condition, string what)
        {
            for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(10);
            Assert.That(condition(), what);
        }

        static Dictionary<string, string> QueryOf(string url) =>
            new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

        // ── The age screen and the guest ─────────────────────────────────────

        [Test]
        public async Task ANewGuestAnswersTheAgeScreenOnce_ControlTheNextLaunchOnThisDeviceShowsNothing()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            var store = new MemoryStore();
            var krtu = await Start(server, store);
            var first = Script(Birth(2000, 1, 1));
            var result = await new KrtuworksUi(krtu, first, Opts(locale: "en-GB")).EnsurePlayerAsync();
            Assert.That(result.Outcome, Is.EqualTo(UiOutcome.SignedIn));
            Assert.That(first.Kinds, Is.EqualTo(new[] { UiScreenKind.Age }));
            var age = (AgeScreen)first.Shown[0];
            Assert.That(age.Order, Is.EqualTo(DateOrder.Dmy), "en-GB writes the day first");
            Assert.That(age.Years[0], Is.EqualTo(2026));
            Assert.That(age.SignIn, Is.True, "the browser sign-in is offered on the age screen");
            Assert.That(age.TermsUrl, Is.EqualTo("https://id.krtuworks.com/legal/terms/latest"));
            Assert.That(GuestGrants(server).Last().Field("birth_date"), Is.EqualTo("2000-01-01"));
            Assert.That(GuestGrants(server).Last().Field("accepted_terms"), Is.EqualTo(KrtuworksClient.CurrentTerms));
            await krtu.ShutdownAsync();

            var again = await Start(server, store);
            var second = Script();
            Assert.That((await new KrtuworksUi(again, second, Opts()).EnsurePlayerAsync()).Outcome, Is.EqualTo(UiOutcome.SignedIn));
            Assert.That(second.Shown, Is.Empty, "the device key is known: no age screen");
            await again.ShutdownAsync();
        }

        [Test]
        public async Task UnderThirteenPlaysOffline_NothingGoesToTheServerAndTheAnswerHoldsUntilThe13thBirthday_ControlAfterItTheAgeScreenComesBack()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            var krtu = await Start(server);
            var memory = new MemoryStore();
            var child = Script(Birth(2016, 3, 14), Say(UiAnswer.Offline));
            var result = await new KrtuworksUi(krtu, child, Opts(memory)).EnsurePlayerAsync();
            Assert.That((result.Outcome, result.Reason), Is.EqualTo((UiOutcome.Offline, OfflineReason.Underage)));
            Assert.That(child.Kinds, Is.EqualTo(new[] { UiScreenKind.Age, UiScreenKind.Underage }));
            Assert.That(GuestGrants(server), Is.Empty, "no guest asked for");
            Assert.That(await memory.GetAsync("krtu:ui:underageUntil"), Is.EqualTo("\"2029-03-14\""));

            var later = Script();
            var held = await new KrtuworksUi(krtu, later, Opts(memory, now: new DateTimeOffset(2028, 12, 31, 12, 0, 0, TimeSpan.Zero))).EnsurePlayerAsync();
            Assert.That((held.Outcome, held.Reason), Is.EqualTo((UiOutcome.Offline, OfflineReason.Underage)));
            Assert.That(later.Shown, Is.Empty, "not asked again, so the date cannot be changed");

            server.GuestRoutes();
            var grown = Script(Birth(2016, 3, 14));
            var adult = await new KrtuworksUi(krtu, grown, Opts(memory, now: new DateTimeOffset(2029, 3, 14, 12, 0, 0, TimeSpan.Zero))).EnsurePlayerAsync();
            Assert.That(adult.Outcome, Is.EqualTo(UiOutcome.SignedIn));
            Assert.That(grown.Kinds, Is.EqualTo(new[] { UiScreenKind.Age }));
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task AnImpossibleDateAndUntickedTermsAskAgainWithTheReason_ControlTheCorrectedAnswerGoesThrough()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            var krtu = await Start(server);
            var host = Script(Birth(2000, 2, 30), Birth(2000, 2, 10, false), Birth(2000, 2, 10));
            Assert.That((await new KrtuworksUi(krtu, host, Opts()).EnsurePlayerAsync()).Outcome, Is.EqualTo(UiOutcome.SignedIn));
            Assert.That(host.Shown.Select(s => s is AgeScreen a ? a.Problem : AgeProblem.None), Is.EqualTo(new[] { AgeProblem.None, AgeProblem.Invalid, AgeProblem.Accept }));
            Assert.That(GuestGrants(server).Count(), Is.EqualTo(1), "only the good answer was sent");
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task NewerTermsOnTheServerAreShownAndAcceptedAsTheServerNamesThem_ControlAServerThatKeepsRefusingEndsOnAnErrorNotALoop()
        {
            foreach (var accepts in new[] { true, false })
            {
                var server = new FakeServer();
                server.GuestRoutes();
                server.On("POST", "/oidc/token", call =>
                {
                    if (accepts && call.Field("accepted_terms") == "TERMS:v2 PRIVACY:v1") return FakeServer.Ok(FakeServer.TokenAnswer("pw_guest_aaaaaaaaaaaa"));
                    return FakeServer.OAuthError("terms_required", "Accept the current terms: TERMS:v2 PRIVACY:v1.");
                });
                var krtu = await Start(server);
                var replies = new List<Func<UiScreen, UiAnswer>> { Birth(2000, 1, 1), Say(UiAnswer.Accept) };
                if (!accepts) replies.Add(Say(UiAnswer.Continue));
                var host = Script(replies.ToArray());
                var result = await new KrtuworksUi(krtu, host, Opts()).EnsurePlayerAsync();
                Assert.That(host.Kinds, Is.EqualTo(accepts ? new[] { UiScreenKind.Age, UiScreenKind.Terms } : new[] { UiScreenKind.Age, UiScreenKind.Terms, UiScreenKind.Error }));
                Assert.That(result.Outcome, Is.EqualTo(accepts ? UiOutcome.SignedIn : UiOutcome.Offline));
                Assert.That(GuestGrants(server).Select(c => c.Field("accepted_terms")), Is.EqualTo(new[] { KrtuworksClient.CurrentTerms, "TERMS:v2 PRIVACY:v1" }));
                await krtu.ShutdownAsync();
            }
        }

        [Test]
        public async Task NoConnectionTryAgainAsksOnceMore_ControlKeepPlayingGoesOnOffline()
        {
            foreach (var retry in new[] { true, false })
            {
                var server = new FakeServer();
                server.GuestRoutes();
                var krtu = await Start(server);
                server.Offline.Add("/oidc/device-key/challenge");
                var host = Script(Birth(2000, 1, 1), screen =>
                {
                    Assert.That(screen is ErrorScreen error && error.Retry, "the error offers Try again");
                    server.Offline.Remove("/oidc/device-key/challenge");
                    return retry ? UiAnswer.Retry : UiAnswer.Continue;
                });
                var result = await new KrtuworksUi(krtu, host, Opts()).EnsurePlayerAsync();
                Assert.That(result.Outcome, Is.EqualTo(retry ? UiOutcome.SignedIn : UiOutcome.Offline));
                await krtu.ShutdownAsync();
            }
        }

        [Test]
        public async Task TheServerSayingUnder13WhenThisDeviceThinks13_IsRememberedAtLeastUntilTomorrow()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            server.On("POST", "/oidc/token", _ => FakeServer.OAuthError("too_young", "Krtuworks ID is for people aged 13 or older."));
            var krtu = await Start(server);
            var memory = new MemoryStore();
            var host = Script(Birth(2013, 6, 1), Say(UiAnswer.Offline));
            var result = await new KrtuworksUi(krtu, host, Opts(memory)).EnsurePlayerAsync();
            Assert.That((result.Outcome, result.Reason), Is.EqualTo((UiOutcome.Offline, OfflineReason.Underage)));
            Assert.That(host.Kinds, Is.EqualTo(new[] { UiScreenKind.Age, UiScreenKind.Underage }));
            Assert.That(await memory.GetAsync("krtu:ui:underageUntil"), Is.EqualTo("\"2026-06-02\""));
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task MaintenanceStopsBeforeAnyGuestIsAskedForAndShowsTheOwnersMessage_ControlNoGateTheAgeScreen()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            var krtu = await Start(server, config: new JObject { ["maintenance"] = new JObject { ["enabled"] = true, ["message"] = "Back at 18:00 UTC." } });
            await krtu.RefreshConfigAsync();
            var host = Script(Say(UiAnswer.Continue));
            var result = await new KrtuworksUi(krtu, host, Opts()).EnsurePlayerAsync();
            Assert.That((result.Outcome, result.Reason), Is.EqualTo((UiOutcome.Offline, OfflineReason.Unavailable)));
            var error = (ErrorScreen)host.Shown.Single();
            Assert.That((error.Message, error.Retry), Is.EqualTo(("Back at 18:00 UTC.", false)));
            Assert.That(GuestGrants(server), Is.Empty);
            await krtu.ShutdownAsync();

            var open = new FakeServer();
            open.GuestRoutes();
            var again = await Start(open, config: new JObject { ["maintenance"] = new JObject { ["enabled"] = false, ["message"] = "Back at 18:00 UTC." } });
            await again.RefreshConfigAsync();
            var control = Script(Birth(2000, 1, 1));
            Assert.That((await new KrtuworksUi(again, control, Opts()).EnsurePlayerAsync()).Outcome, Is.EqualTo(UiOutcome.SignedIn));
            Assert.That(control.Kinds, Is.EqualTo(new[] { UiScreenKind.Age }));
            await again.ShutdownAsync();
        }

        [Test]
        public void ErrorSentences_BanWithReasonAndEnd_BanWithout_NoConnection_TooManyGuests_TheGamesOwnSentenceWins()
        {
            var plain = new UiTexts();
            var ban = ErrorScreen.From(new KrtuworksException("sanctioned", "x", 403, null, new JObject { ["reason"] = "Cheating in ranked games.", ["endsAt"] = "2026-07-01T00:00:00Z" }), plain, "en-GB");
            Assert.That((ban.Message, ban.Retry), Is.EqualTo(("You can't play this game online: Cheating in ranked games. This ends on 1 July 2026.", false)));
            Assert.That(ErrorScreen.From(new KrtuworksException("sanctioned", "x", 403), plain, "en").Message, Is.EqualTo("You can't play this game online."));
            var offline = ErrorScreen.From(new KrtuworksException("network_error", "x"), plain, "en");
            Assert.That((offline.Message, offline.Retry), Is.EqualTo((plain.Get("error.offline"), true)));
            Assert.That(ErrorScreen.From(new KrtuworksException("guest_capacity", "x", 503), plain, "en").Message, Is.EqualTo(plain.Get("error.busy")));
            Assert.That(ErrorScreen.From(new Exception("boom"), new UiTexts(new Dictionary<string, string> { ["error.generic"] = "Bir şey ters gitti." }), "tr").Message, Is.EqualTo("Bir şey ters gitti."));
        }

        // ── Saves ────────────────────────────────────────────────────────────

        [Test]
        public async Task AnotherDeviceSavedFirstBothSavesAreShownTheNewerMarked_KeepingTheOtherHandsItsBytesBack_ControlKeepingThisOneSavesOverIt()
        {
            foreach (var keep in new[] { 1, 0 })
            {
                var server = new FakeServer();
                server.GuestRoutes();
                var theirs = new JObject
                {
                    ["id"] = "fil_bbbbbbbbbbbbbbbbbbbb", ["slot"] = "save-1", ["device"] = null, ["generation"] = 4, ["sizeBytes"] = 3, ["sha256"] = "",
                    ["platform"] = "STEAM", ["build"] = "1.0.0", ["savedAt"] = "2026-06-01T11:00:00Z", ["fromMerge"] = false,
                };
                var bases = new List<string?>();
                server.On("GET", "/v1/apps/buried/files/save-1", _ =>
                {
                    var reply = new Reply { Body = new byte[] { 9, 9, 9 } };
                    reply.Headers["etag"] = "\"fil_bbbbbbbbbbbbbbbbbbbb\"";
                    reply.Headers["x-krtu-generation"] = "4";
                    reply.Headers["x-krtu-saved-at"] = "2026-06-01T11:00:00Z";
                    return reply;
                });
                server.On("PUT", "/v1/apps/buried/files/save-1", call =>
                {
                    bases.Add(call.Header("if-match"));
                    if (call.Header("if-match") != "fil_bbbbbbbbbbbbbbbbbbbb")
                        return new Reply { Status = 409, Json = new JObject { ["error"] = new JObject { ["code"] = "file_conflict", ["message"] = "x" }, ["current"] = theirs } };
                    var saved = (JObject)theirs.DeepClone();
                    saved["id"] = "fil_cccccccccccccccccccc";
                    saved["generation"] = 5;
                    return new Reply { Status = 201, Json = new JObject { ["file"] = saved } };
                });
                var krtu = await Start(server);
                await krtu.SignInAsGuestAsync("2000-01-01", KrtuworksClient.CurrentTerms);
                await krtu.ReadyAsync();
                var host = Script(Say(UiAnswer.Keep(keep)));
                var ui = new KrtuworksUi(krtu, host, Opts(locale: "en-GB", extra: o => o.DescribeSave = (_, bytes) => new SaveSummary { Title = bytes[0] == 9 ? "Chapter 2" : "Chapter 3" }));
                var result = await ui.SaveAsync("save-1", new byte[] { 1, 2 }, "fil_aaaaaaaaaaaaaaaaaaaa", "Slot 1");
                var screen = (ConflictScreen)host.Shown.Single();
                Assert.That(screen.Slot, Is.EqualTo("Slot 1"));
                Assert.That(screen.Cards.Select(c => (c.Label, c.Newer, c.Summary?.Title, c.When)), Is.EqualTo(new[]
                {
                    ("This device", true, (string?)"Chapter 3", "Today, 12:00"),
                    ("Another device · Steam", false, (string?)"Chapter 2", "Today, 11:00"),
                }));
                if (keep == 1)
                {
                    Assert.That(result.Kept, Is.EqualTo(SaveKept.There));
                    Assert.That(result.Bytes, Is.EqualTo(new byte[] { 9, 9, 9 }));
                    Assert.That(bases.Count, Is.EqualTo(1), "nothing saved over the other device");
                }
                else
                {
                    Assert.That(result.Kept, Is.EqualTo(SaveKept.Here));
                    Assert.That(bases.Last(), Is.EqualTo("fil_bbbbbbbbbbbbbbbbbbbb"), "saved over the version the player saw");
                }
                await krtu.ShutdownAsync();
            }
        }

        [Test]
        public async Task ThePlayerSignsOutWhileTheConflictScreenIsOpen_NothingIsReturnedOrSavedAndTheCallEndsContextChanged_ControlTheSamePlayerGetsTheChosenBytes()
        {
            foreach (var changes in new[] { true, false })
            {
                var server = new FakeServer();
                server.GuestRoutes();
                server.On("POST", "/oidc/token/revocation", _ => new Reply { Status = 200 });
                var theirs = new JObject
                {
                    ["id"] = "fil_bbbbbbbbbbbbbbbbbbbb", ["slot"] = "save-1", ["device"] = null, ["generation"] = 4, ["sizeBytes"] = 3, ["sha256"] = "",
                    ["platform"] = "STEAM", ["build"] = "1.0.0", ["savedAt"] = "2026-06-01T11:00:00Z", ["fromMerge"] = false,
                };
                var writes = 0;
                server.On("GET", "/v1/apps/buried/files/save-1", _ =>
                {
                    var reply = new Reply { Body = new byte[] { 9, 9, 9 } };
                    reply.Headers["etag"] = "\"fil_bbbbbbbbbbbbbbbbbbbb\"";
                    reply.Headers["x-krtu-generation"] = "4";
                    reply.Headers["x-krtu-saved-at"] = "2026-06-01T11:00:00Z";
                    return reply;
                });
                server.On("PUT", "/v1/apps/buried/files/save-1", _ =>
                {
                    writes++;
                    return new Reply { Status = 201, Json = new JObject { ["file"] = theirs } };
                });
                var krtu = await Start(server);
                await krtu.SignInAsGuestAsync("2000-01-01", KrtuworksClient.CurrentTerms);
                await krtu.ReadyAsync();
                var host = Manual();
                var ui = new KrtuworksUi(krtu, host, Opts());
                var run = ui.ChooseSaveAsync("save-1", new byte[] { 1, 2 }, theirs.ToObject<FileVersion>()!);
                while (host.Shown.Count == 0) await Task.Delay(5);
                if (changes) await krtu.SignOutAsync();
                host.Dialogs[0].Answers.Post(UiAnswer.Keep(1));
                if (changes)
                {
                    var err = await AsyncAssert.ThrowsAsync<KrtuworksException>(() => run);
                    Assert.That(err.Code, Is.EqualTo("context_changed"));
                    Assert.That(writes, Is.EqualTo(0), "nothing saved for the other player");
                }
                else
                {
                    var result = await run;
                    Assert.That(result.Kept, Is.EqualTo(SaveKept.There));
                    Assert.That(result.Bytes, Is.EqualTo(new byte[] { 9, 9, 9 }));
                }
                await krtu.ShutdownAsync();
            }
        }

        [Test]
        public async Task AMergedAccountsSlotsAskWhichToKeep_ControlASlotWithNoWaitingVersionAsksNothing()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            string Id(string slot, string tag) => $"fil_{slot}{tag}".PadRight(24, '0');
            JObject Version(string slot, string tag, string savedAt) => new JObject
            {
                ["id"] = Id(slot, tag), ["slot"] = slot, ["device"] = null, ["generation"] = 1, ["sizeBytes"] = 4, ["sha256"] = "", ["platform"] = "WINDOWS", ["build"] = "1.0.0", ["savedAt"] = savedAt, ["fromMerge"] = tag == "m",
            };
            server.On("GET", "/v1/apps/buried/files", _ => FakeServer.Ok(new JObject
            {
                ["slots"] = new JArray
                {
                    new JObject { ["key"] = "save-1", ["kind"] = "SAVE", ["name"] = "Slot 1", ["file"] = Version("save1", "c", "2026-06-01T10:00:00Z"), ["conflict"] = Version("save1", "m", "2026-06-01T11:00:00Z"), ["devices"] = new JArray() },
                    new JObject { ["key"] = "save-2", ["kind"] = "SAVE", ["name"] = "Slot 2", ["file"] = Version("save2", "c", "2026-06-01T10:00:00Z"), ["conflict"] = null, ["devices"] = new JArray() },
                },
                ["limits"] = new JObject(),
            }));
            server.On("GET", "/v1/apps/buried/files/save-1", _ =>
            {
                var reply = new Reply { Body = Encoding.UTF8.GetBytes("curr") };
                reply.Headers["etag"] = $"\"{Id("save1", "c")}\"";
                return reply;
            });
            var resolves = new List<JObject>();
            server.On("POST", "/v1/apps/buried/files/save-1/resolve", call =>
            {
                resolves.Add(call.Json!);
                return FakeServer.Ok(new JObject { ["file"] = Version("save1", "r", "2026-06-01T12:00:00Z") });
            });
            var krtu = await Start(server);
            await krtu.SignInAsGuestAsync("2000-01-01", KrtuworksClient.CurrentTerms);
            await krtu.ReadyAsync();
            var host = Script(Say(UiAnswer.Keep(1)));
            var resolved = await new KrtuworksUi(krtu, host, Opts()).ResolveMergedSavesAsync();
            var screen = (ConflictScreen)host.Shown.Single();
            Assert.That(screen.Slot, Is.EqualTo("Slot 1"));
            Assert.That(screen.Cards.Select(c => (c.Label, c.Newer)), Is.EqualTo(new[] { ("Current save", false), ("From your other account", true) }));
            Assert.That(resolved.Select(r => r.Slot), Is.EqualTo(new[] { "save-1" }), "control: the slot with no waiting version was not asked about");
            Assert.That(((string?)resolves.Single()["keep"], (string?)resolves.Single()["current"]), Is.EqualTo((Id("save1", "m"), Id("save1", "c"))), "the merged version was kept over the current one");
            await krtu.ShutdownAsync();
        }

        // ── The link reminder ────────────────────────────────────────────────

        [Test]
        public async Task AGuestsLinkReminderShowsTheCornerPrompt_ControlAnAccountIsNeverReminded()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            server.On("POST", "/oidc/token/revocation", _ => new Reply { Status = 200 });
            var krtu = await Start(server);
            await krtu.SignInAsGuestAsync("2000-01-01", KrtuworksClient.CurrentTerms);
            var host = Script();
            host.ReminderAnswer = UiReminderAnswer.Later;
            using var ui = new KrtuworksUi(krtu, host, Opts());
            await ui.RemindAsync();
            Assert.That(host.Reminders.Count, Is.EqualTo(1));
            Assert.That(host.Reminders[0].Closed, Is.True);
            await krtu.SignOutAsync();
            await ui.RemindAsync();
            Assert.That(host.Reminders.Count, Is.EqualTo(1), "nobody is a guest now");
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task AnUnansweredReminderClosesByItselfAfterItsTime_LinkStartsTheSignIn_ControlLaterDoesNot()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            var krtu = await Start(server);
            await krtu.SignInAsGuestAsync("2000-01-01", KrtuworksClient.CurrentTerms);
            var asked = 0;
            Task<string> Authenticate(string url, string scheme, CancellationToken cancel)
            {
                asked++;
                throw new OperationCanceledException();
            }
            var host = Script();
            using var ui = new KrtuworksUi(krtu, host, Opts(mode: UiSignInMode.App, extra: o => o.Authenticate = Authenticate));
            await ui.RemindAsync();
            Assert.That(host.Reminders.Single().Closed, Is.True, "nobody answered: it closed on its own");
            Assert.That(asked, Is.Zero);

            host.ReminderAnswer = UiReminderAnswer.Later;
            await ui.RemindAsync();
            Assert.That(asked, Is.Zero, "Later starts nothing");

            host.ReminderAnswer = UiReminderAnswer.Link;
            await ui.RemindAsync();
            Assert.That(asked, Is.EqualTo(1), "Link starts the sign-in");
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task TheSdksLinkReminderShowsThePromptThroughTheController_ControlSwitchedOffNothingShows()
        {
            foreach (var on in new[] { true, false })
            {
                var server = new FakeServer();
                server.GuestRoutes();
                server.ApplyProgress();
                server.AcceptEvents(new List<JObject>());
                var krtu = await Start(server, config: new JObject { ["linkReminder"] = new JObject { ["everyDays"] = 3 } });
                await krtu.RefreshConfigAsync();
                await krtu.SignInAsGuestAsync("2000-01-01", KrtuworksClient.CurrentTerms);
                await krtu.ReadyAsync();
                var host = Script();
                host.ReminderAnswer = UiReminderAnswer.Later;
                using var ui = new KrtuworksUi(krtu, host, Opts(extra: o => o.LinkReminder = on));
                await krtu.UnlockAchievementAsync("first_win");
                await Until(() => !on || host.Reminders.Count == 1, "the prompt was shown");
                Assert.That(host.Reminders.Count, Is.EqualTo(on ? 1 : 0));
                await krtu.ShutdownAsync();
            }
        }

        // ── Browser ──────────────────────────────────────────────────────────

        static void BrowserRoutes(FakeServer server, Func<string?> opened)
        {
            server.On("POST", "/oidc/token", _ => FakeServer.Ok(FakeServer.TokenAnswer("pw_acct_cccccccccccc", new JObject { ["nonce"] = QueryOf(opened()!)["nonce"], ["preferred_username"] = "player_one" })));
        }

        [Test]
        public async Task BrowserSignIn_TheWaitingScreenShowsWhenTheAddressIsHandedOver_ReopenOpensTheSameAddressAndKeepsWaiting_TheReturnClosesIt()
        {
            var server = new FakeServer();
            var krtu = await Start(server);
            var opened = new List<string>();
            BrowserRoutes(server, () => opened.FirstOrDefault());
            var host = Manual();
            var ui = new KrtuworksUi(krtu, host, Opts(open: opened.Add));
            var signIn = ui.SignInAsync();
            await Until(() => host.Dialogs.Count == 1, "the waiting screen was shown");
            var waiting = (WaitingScreen)host.Shown[0];
            Assert.That(opened.Count, Is.EqualTo(1), "the browser was opened once");
            Assert.That(waiting.Url, Is.EqualTo(opened[0]), "the screen holds the address that was opened");

            host.Dialogs[0].Answers.Post(UiAnswer.Reopen);
            await Until(() => opened.Count == 2, "the page was opened again");
            Assert.That(opened[1], Is.EqualTo(opened[0]), "the same address, so the same sign-in");
            Assert.That(host.Dialogs[0].Closed, Is.False, "still waiting");
            Assert.That(signIn.IsCompleted, Is.False);

            var q = QueryOf(opened[0]);
            var redirect = new Uri(q["redirect_uri"]);
            Assert.That(await IdentityTests.Get(redirect.Port, $"/callback?code=c&state={q["state"]}&iss={Uri.EscapeDataString("https://id.krtuworks.com/oidc")}"), Does.StartWith("HTTP/1.1 200"));
            var result = await signIn;
            Assert.That(result.Outcome, Is.EqualTo(UiOutcome.SignedIn));
            Assert.That(result.Player!.Username, Is.EqualTo("player_one"));
            Assert.That(host.Dialogs[0].Closed, Is.True, "success closes the waiting screen");
            Assert.That(host.Shown.Count, Is.EqualTo(1), "no error screen");
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task BrowserSignIn_CancelEndsItAsCancelledWithNoErrorScreenAndTheSameCallWorksAgain()
        {
            var server = new FakeServer();
            var krtu = await Start(server);
            var opened = new List<string>();
            BrowserRoutes(server, () => opened.LastOrDefault());
            var host = Manual();
            var ui = new KrtuworksUi(krtu, host, Opts(open: opened.Add));
            var signIn = ui.SignInAsync();
            await Until(() => host.Dialogs.Count == 1, "the waiting screen was shown");
            host.Dialogs[0].Answers.Post(UiAnswer.Cancel);
            var result = await signIn;
            Assert.That(result.Outcome, Is.EqualTo(UiOutcome.Cancelled));
            Assert.That(host.Dialogs[0].Closed, Is.True);
            Assert.That(host.Shown.Count, Is.EqualTo(1), "no error screen");
            Assert.That(krtu.Player, Is.Null, "the player stays who they were");

            var again = ui.SignInAsync();
            await Until(() => host.Dialogs.Count == 2, "control: a new sign-in shows the waiting screen again");
            var q = QueryOf(opened.Last());
            await IdentityTests.Get(new Uri(q["redirect_uri"]).Port, $"/callback?code=c&state={q["state"]}");
            Assert.That((await again).Outcome, Is.EqualTo(UiOutcome.SignedIn));
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task BrowserSignIn_AFailureClosesTheWaitingScreenAndShowsTheErrorScreen_ControlTheErrorOffersNoTryAgain()
        {
            var server = new FakeServer();
            var krtu = await Start(server);
            var opened = new List<string>();
            var host = Manual();
            var ui = new KrtuworksUi(krtu, host, Opts(open: opened.Add));
            var signIn = ui.SignInAsync();
            await Until(() => host.Dialogs.Count == 1, "the waiting screen was shown");
            var q = QueryOf(opened[0]);
            await IdentityTests.Get(new Uri(q["redirect_uri"]).Port, "/callback?code=c&state=someone-elses");
            var result = await signIn;
            Assert.That((result.Outcome, result.Reason), Is.EqualTo((UiOutcome.Offline, OfflineReason.Unavailable)));
            Assert.That(host.Dialogs[0].Closed, Is.True);
            var error = (ErrorScreen)host.Shown[1];
            Assert.That(error.Retry, Is.False, "keep playing is the only way on");
            Assert.That(krtu.Player, Is.Null);
            await krtu.ShutdownAsync();
        }

        // ── Phone ────────────────────────────────────────────────────────────

        [Test]
        public async Task AppSignIn_ThePlayerCancellingTheSystemSheetIsCancelledWithNoScreen_ControlTheAddressItComesBackToSignsIn()
        {
            var server = new FakeServer();
            var krtu = await Start(server);
            string? asked = null;
            server.On("POST", "/oidc/token", _ => FakeServer.Ok(FakeServer.TokenAnswer("pw_phone_aaaaaaaaaaaa", new JObject { ["nonce"] = QueryOf(asked!)["nonce"] })));
            var cancel = true;
            Task<string> Authenticate(string url, string scheme, CancellationToken token)
            {
                asked = url;
                if (cancel) return Task.FromCanceled<string>(new CancellationToken(true));
                return Task.FromResult($"{scheme}:/callback?code=c&state={QueryOf(url)["state"]}");
            }
            var host = Script();
            var ui = new KrtuworksUi(krtu, host, Opts(mode: UiSignInMode.App, extra: o => o.Authenticate = Authenticate));
            Assert.That((await ui.SignInAsync()).Outcome, Is.EqualTo(UiOutcome.Cancelled));
            Assert.That(host.Shown, Is.Empty, "the system's own sheet is on top: no screen of ours");
            cancel = false;
            var result = await ui.SignInAsync();
            Assert.That((result.Outcome, result.Player!.Subject), Is.EqualTo((UiOutcome.SignedIn, "pw_phone_aaaaaaaaaaaa")));
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task TheAgeScreensSignInRunsTheSignIn_CancelledShowsTheAgeScreenAgain_ControlItsSuccessEndsTheQuestion()
        {
            foreach (var cancel in new[] { true, false })
            {
                var server = new FakeServer();
                server.GuestRoutes();
                var krtu = await Start(server);
                string? asked = null;
                server.On("POST", "/oidc/token", call => call.Field("grant_type") == Identity.DeviceKeyGrant
                    ? FakeServer.Ok(FakeServer.TokenAnswer("pw_guest_aaaaaaaaaaaa"))
                    : FakeServer.Ok(FakeServer.TokenAnswer("pw_phone_aaaaaaaaaaaa", new JObject { ["nonce"] = QueryOf(asked!)["nonce"] })));
                Task<string> Authenticate(string url, string scheme, CancellationToken token)
                {
                    asked = url;
                    return cancel ? Task.FromCanceled<string>(new CancellationToken(true)) : Task.FromResult($"{scheme}:/callback?code=c&state={QueryOf(url)["state"]}");
                }
                var host = cancel ? Script(Say(UiAnswer.SignIn), Birth(2000, 1, 1)) : Script(Say(UiAnswer.SignIn));
                var result = await new KrtuworksUi(krtu, host, Opts(mode: UiSignInMode.App, extra: o => o.Authenticate = Authenticate)).EnsurePlayerAsync();
                Assert.That(result.Outcome, Is.EqualTo(UiOutcome.SignedIn));
                Assert.That(host.Kinds, Is.EqualTo(cancel ? new[] { UiScreenKind.Age, UiScreenKind.Age } : new[] { UiScreenKind.Age }));
                Assert.That(result.Player!.Subject, Is.EqualTo(cancel ? "pw_guest_aaaaaaaaaaaa" : "pw_phone_aaaaaaaaaaaa"));
                await krtu.ShutdownAsync();
            }
        }

        // ── Console ──────────────────────────────────────────────────────────

        const string Complete = "https://id.krtuworks.com/link?code=ABCD-EFGH-JK";

        sealed class DeviceServer
        {
            internal readonly FakeServer Server = new FakeServer();
            internal Func<Reply> Poll = () => FakeServer.OAuthError("authorization_pending", "");
            internal int Codes;
            internal readonly List<string?> Names = new List<string?>();

            internal DeviceServer()
            {
                Server.On("POST", "/v1/device/authorize", call =>
                {
                    Names.Add(call.Field("device_name"));
                    var n = ++Codes;
                    var code = n == 1 ? "ABCD-EFGH-JK" : "WXYZ-2345-67";
                    return FakeServer.Ok(new JObject
                    {
                        ["device_code"] = $"dc-{n}", ["user_code"] = code, ["verification_uri"] = "https://id.krtuworks.com/link",
                        ["verification_uri_complete"] = $"https://id.krtuworks.com/link?code={code}", ["expires_in"] = 600, ["interval"] = 1,
                    });
                });
                Server.On("POST", "/v1/device/token", _ => Poll());
            }
        }

        static bool SameModules(bool[][] a, bool[][] b) => a.Length == b.Length && a.Zip(b, (x, y) => x.SequenceEqual(y)).All(same => same);

        [Test]
        public async Task ConsoleSignIn_ShowsTheCodeTheAddressAndTheQrOfTheCompleteAddress_ApprovalSignsInAndClosesIt()
        {
            var device = new DeviceServer();
            var krtu = await Start(device.Server);
            var host = Manual();
            var ui = new KrtuworksUi(krtu, host, Opts(mode: UiSignInMode.Device, extra: o => o.DeviceName = "Living room"));
            var signIn = ui.SignInAsync();
            await Until(() => host.Dialogs.Count == 1, "the code screen was shown");
            var screen = (DeviceScreen)host.Shown[0];
            Assert.That((screen.Code, screen.Uri, screen.UriComplete, screen.Expired), Is.EqualTo(("ABCD-EFGH-JK", "https://id.krtuworks.com/link", Complete, false)));
            Assert.That(screen.ExpiresAtMs, Is.EqualTo(Today.ToUnixTimeMilliseconds() + 600_000));
            Assert.That(SameModules(screen.Qr, QrCode.Modules(Complete)), Is.True, "the QR holds the complete address");
            Assert.That(SameModules(screen.Qr, QrCode.Modules("https://id.krtuworks.com/link")), Is.False, "control: not the bare address");
            Assert.That(device.Names, Is.EqualTo(new[] { "Living room" }));
            Assert.That(signIn.IsCompleted, Is.False, "waiting for the approval");

            device.Poll = () => FakeServer.Ok(FakeServer.TokenAnswer("pw_console_aaaaaaaa", new JObject { ["preferred_username"] = "player_one" }));
            var result = await signIn;
            Assert.That((result.Outcome, result.Player!.Subject), Is.EqualTo((UiOutcome.SignedIn, "pw_console_aaaaaaaa")));
            Assert.That(host.Dialogs[0].Closed, Is.True);
            Assert.That(host.Shown.Count, Is.EqualTo(1));
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task ConsoleSignIn_NewCodeUpdatesTheSameOpenScreen_AnExpiredCodeShowsTheExpiredState_CancelEndsItWithNoErrorScreen()
        {
            var device = new DeviceServer();
            var krtu = await Start(device.Server);
            var host = Manual();
            var ui = new KrtuworksUi(krtu, host, Opts(mode: UiSignInMode.Device));
            var signIn = ui.SignInAsync();
            await Until(() => host.Dialogs.Count == 1, "the code screen was shown");
            var dialog = host.Dialogs[0];

            dialog.Answers.Post(UiAnswer.NewCode);
            await Until(() => dialog.Updates.Count == 1, "the screen was updated with a new code");
            var fresh = (DeviceScreen)dialog.Updates[0];
            Assert.That((fresh.Code, fresh.Expired), Is.EqualTo(("WXYZ-2345-67", false)));
            Assert.That(host.Dialogs.Count, Is.EqualTo(1), "the same screen: nothing was closed and opened again");
            Assert.That(dialog.Closed, Is.False);

            device.Poll = () => FakeServer.OAuthError("expired_token", "");
            await Until(() => dialog.Updates.Count == 2, "the screen shows the expired state");
            var expired = (DeviceScreen)dialog.Updates[1];
            Assert.That((expired.Expired, expired.Code), Is.EqualTo((true, "WXYZ-2345-67")), "the same code, now expired");
            Assert.That(signIn.IsCompleted, Is.False, "an expired code waits for New code or Cancel");

            device.Poll = () => FakeServer.OAuthError("authorization_pending", "");
            dialog.Answers.Post(UiAnswer.NewCode);
            await Until(() => dialog.Updates.Count == 3, "a new code after the expiry");
            Assert.That(((DeviceScreen)dialog.Updates[2]).Expired, Is.False, "control: the expired state is gone with the new code");
            Assert.That(device.Codes, Is.EqualTo(3));

            dialog.Answers.Post(UiAnswer.Cancel);
            var result = await signIn;
            Assert.That(result.Outcome, Is.EqualTo(UiOutcome.Cancelled));
            Assert.That(dialog.Closed, Is.True);
            Assert.That(host.Shown.Count, Is.EqualTo(1), "no error screen");
            Assert.That(krtu.Player, Is.Null);
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task ConsoleSignIn_ACodeThatEndsAnotherWayShowsTheErrorScreen_ControlTheCodeScreenWasClosed()
        {
            var device = new DeviceServer();
            device.Poll = () => FakeServer.OAuthError("access_denied", "The player said no.");
            var krtu = await Start(device.Server);
            var host = Manual();
            var result = await new KrtuworksUi(krtu, host, Opts(mode: UiSignInMode.Device)).SignInAsync();
            Assert.That((result.Outcome, result.Reason), Is.EqualTo((UiOutcome.Offline, OfflineReason.Unavailable)));
            Assert.That(host.Dialogs[0].Closed, Is.True);
            Assert.That(host.Shown[1], Is.TypeOf<ErrorScreen>());
            await krtu.ShutdownAsync();
        }

        // ── No sign-in here (WebGL) ──────────────────────────────────────────

        [Test]
        public async Task NoneMode_SignInIsUnavailableWithNoScreenAndTheAgeScreenHidesItsSignIn_ControlAnotherModeOffersIt()
        {
            var server = new FakeServer();
            server.GuestRoutes();
            var krtu = await Start(server);
            var host = Script(Birth(2000, 1, 1));
            var ui = new KrtuworksUi(krtu, host, Opts(mode: UiSignInMode.None));
            var denied = await ui.SignInAsync();
            Assert.That((denied.Outcome, denied.Reason), Is.EqualTo((UiOutcome.Offline, OfflineReason.Unavailable)));
            Assert.That(host.Shown, Is.Empty);
            Assert.That(ui.SignInOffered, Is.False);
            Assert.That((await ui.EnsurePlayerAsync()).Outcome, Is.EqualTo(UiOutcome.SignedIn), "guests play");
            Assert.That(((AgeScreen)host.Shown.Single()).SignIn, Is.False);
            Assert.That(new KrtuworksUi(krtu, Script(), Opts(mode: UiSignInMode.Browser)).SignInOffered, Is.True);
            await krtu.ShutdownAsync();
        }

        [Test]
        public async Task ABrowserOrAppSignInWithoutItsOpenerIsRefusedAtOnce_ControlTheRightOptionsBuild()
        {
            var server = new FakeServer();
            var krtu = await Start(server);
            Assert.Throws<ArgumentException>(() => new KrtuworksUi(krtu, Script(), new KrtuworksUiOptions { Mode = UiSignInMode.Browser }));
            Assert.Throws<ArgumentException>(() => new KrtuworksUi(krtu, Script(), new KrtuworksUiOptions { Mode = UiSignInMode.App }));
            using var ok = new KrtuworksUi(krtu, Script(), new KrtuworksUiOptions { Mode = UiSignInMode.Device });
            Assert.That(ok.Mode, Is.EqualTo(UiSignInMode.Device));
            await krtu.ShutdownAsync();
        }

        // ── The platform's cloud ─────────────────────────────────────────────

        [Test]
        public async Task APlatformCloudConflictShowsCurrentSaveAndTheCloudCopy_KeepingTheCloudsPutsItOnTheServer_ControlKeepingTheCurrentOneLeavesTheServer()
        {
            foreach (var keep in new[] { 1, 0 })
            {
                var server = new FakeServer();
                var cloud = new CloudMirrorTests.FakeCloud();
                cloud.Put("save-1", "steam");
                var slots = new Dictionary<string, CloudMirrorTests.Slot> { ["save-1"] = new CloudMirrorTests.Slot { Text = "server", Gen = 3 } };
                var host = Script(Say(UiAnswer.Keep(keep)));
                KrtuworksUi? ui = null;
                var krtu = await CloudMirrorTests.Start(server, cloud, slots, beforeSignIn: c =>
                    ui = new KrtuworksUi(c, host, Opts(extra: o =>
                    {
                        o.PlatformSaveConflicts = true;
                        o.DescribeSave = (_, bytes) => new SaveSummary { Title = Encoding.UTF8.GetString(bytes) };
                    })));
                await Until(() => host.Shown.Count == 1, "the conflict was shown");
                var screen = (ConflictScreen)host.Shown[0];
                Assert.That(screen.Cards.Select(c => (c.Label, c.Summary?.Title)), Is.EqualTo(new[] { ("Current save", (string?)"server"), ("Steam Cloud", (string?)"steam") }));
                await Until(() => keep == 1 ? slots["save-1"].Text == "steam" : cloud.Text("save-1") == "server", "the choice was applied");
                Assert.That(slots["save-1"].Text, Is.EqualTo(keep == 1 ? "steam" : "server"));
                ui!.Dispose();
                await krtu.ShutdownAsync();
            }
        }

        [Test]
        public async Task ThePlayerSignsOutWhileTheCloudConflictScreenIsOpen_NothingIsResolved_ControlTheSamePlayersChoiceIsResolved()
        {
            foreach (var changes in new[] { true, false })
            {
                var server = new FakeServer();
                server.GuestRoutes();
                server.On("POST", "/oidc/token/revocation", _ => new Reply { Status = 200 });
                server.On("GET", "/v1/apps/buried/files/save-1", _ =>
                {
                    var reply = new Reply { Body = new byte[] { 1, 2, 3 } };
                    reply.Headers["etag"] = "\"fil_server00000000000000\"";
                    return reply;
                });
                var resolves = 0;
                server.On("POST", "/v1/apps/buried/files/save-1/resolve", _ =>
                {
                    resolves++;
                    return new Reply { Json = new JObject { ["file"] = new JObject { ["id"] = "fil_cloud000000000000000", ["slot"] = "save-1", ["generation"] = 4, ["sizeBytes"] = 4, ["savedAt"] = "2026-06-01T11:30:00Z" } } };
                });
                var krtu = await Start(server);
                await krtu.SignInAsGuestAsync("2000-01-01", KrtuworksClient.CurrentTerms);
                await krtu.ReadyAsync();
                var host = Manual();
                var ui = new KrtuworksUi(krtu, host, Opts());
                var conflict = new PlatformSaveConflict
                {
                    Slot = "save-1",
                    Server = new FileVersion { Id = "fil_server00000000000000", Slot = "save-1", Generation = 3, SizeBytes = 3, SavedAt = "2026-06-01T10:00:00Z" },
                    Platform = new FileVersion { Id = "fil_cloud000000000000000", Slot = "save-1", Generation = 4, SizeBytes = 4, SavedAt = "2026-06-01T11:30:00Z", PlatformCloud = "STEAM" },
                    PlatformData = new byte[] { 4, 5, 6, 7 },
                };
                var run = ui.ResolvePlatformConflictAsync(conflict, krtu.Player?.Subject);
                while (host.Shown.Count == 0) await Task.Delay(5);
                if (changes) await krtu.SignOutAsync();
                host.Dialogs[0].Answers.Post(UiAnswer.Keep(1));
                await run;
                Assert.That(resolves, Is.EqualTo(changes ? 0 : 1), changes ? "the copy keeps waiting for its own player" : "the same player's choice is resolved");
                await krtu.ShutdownAsync();
            }
        }

        [Test]
        public async Task APlatformCloudConflictIsLeftToTheGameWhenTheOptionIsOff_ControlTheGameWasToldAndNoScreenShowed()
        {
            var server = new FakeServer();
            var cloud = new CloudMirrorTests.FakeCloud();
            cloud.Put("save-1", "steam");
            var slots = new Dictionary<string, CloudMirrorTests.Slot> { ["save-1"] = new CloudMirrorTests.Slot { Text = "server", Gen = 3 } };
            var host = Script();
            var told = new List<PlatformSaveConflict>();
            var krtu = await CloudMirrorTests.Start(server, cloud, slots, told: told, beforeSignIn: c => new KrtuworksUi(c, host, Opts()));
            Assert.That(told.Count, Is.EqualTo(1), "the SDK told the game");
            await Task.Delay(100);
            Assert.That(host.Shown, Is.Empty, "and the screens stayed out of it");
            await krtu.ShutdownAsync();
        }

        // ── Formats and texts ────────────────────────────────────────────────

        [Test]
        public void Formats_DateOrderByPlace_TheAgeRule_The13thBirthday_SentencesWithMarks()
        {
            Assert.That(UiFormat.DateOrderFor("en-US"), Is.EqualTo(DateOrder.Mdy));
            Assert.That(UiFormat.DateOrderFor("ja"), Is.EqualTo(DateOrder.Ymd));
            Assert.That(UiFormat.DateOrderFor("zh-Hant-TW"), Is.EqualTo(DateOrder.Ymd));
            Assert.That(UiFormat.DateOrderFor("tr-TR"), Is.EqualTo(DateOrder.Dmy));
            Assert.That(UiFormat.CheckBirth(2013, 6, 1, Today), Is.EqualTo(BirthCheck.Ok), "exactly 13 today");
            Assert.That(UiFormat.CheckBirth(2013, 6, 2, Today), Is.EqualTo(BirthCheck.Underage), "control: 13 tomorrow");
            Assert.That(UiFormat.CheckBirth(2027, 1, 1, Today), Is.EqualTo(BirthCheck.Invalid), "not born yet");
            Assert.That(UiFormat.CheckBirth(1900, 1, 1, Today), Is.EqualTo(BirthCheck.Invalid), "more than 120 years");
            Assert.That(UiFormat.CheckBirth(2000, 2, 30, Today), Is.EqualTo(BirthCheck.Invalid), "no 30 February");
            Assert.That(UiFormat.CheckBirth(0, 0, 0, Today), Is.EqualTo(BirthCheck.Invalid), "nothing chosen");
            Assert.That(UiFormat.TurnsThirteen("2012-02-29"), Is.EqualTo("2025-03-01"));
            Assert.That(UiFormat.TurnsThirteen("2012-02-28"), Is.EqualTo("2025-02-28"), "control: any other day is its own");
            var marks = new UiTexts().Segments("age.accept");
            Assert.That(marks.Select(s => s.Text ?? "{" + s.Mark + "}"), Is.EqualTo(new[] { "I accept the ", "{terms}", " and the ", "{privacy}", "." }));
        }

        [Test]
        public void EngineImage_AProfilePictureIsAskedForAsPng_AnyOtherAddressStaysAsItIs()
        {
            var sha = new string('a', 64);
            Assert.That(UiFormat.EngineImage($"https://id.krtuworks.com/avatars/{sha}.webp"), Is.EqualTo($"https://id.krtuworks.com/avatars/{sha}.png"));
            Assert.That(UiFormat.EngineImage($"https://id.krtuworks.com/avatars/{sha}-128.webp"), Is.EqualTo($"https://id.krtuworks.com/avatars/{sha}-128.png"));
            foreach (var other in new[]
            {
                $"https://id.krtuworks.com/avatars/{sha}.png",
                $"https://id.krtuworks.com/avatars/{sha}.webp?x=1",
                $"https://id.krtuworks.com/files/avatars/{sha}.webp",
                $"https://id.krtuworks.com/avatars/{new string('A', 64)}.webp",
                $"http://id.krtuworks.com/avatars/{sha}.webp",
                "https://cdn.example.com/save-thumb.webp",
            })
            {
                Assert.That(UiFormat.EngineImage(other), Is.EqualTo(other));
            }
        }

        [Test]
        public void Formats_TheCountdownIsMinutesAndSecondsRoundedUp_FileSizes_SavedWhen()
        {
            Assert.That(UiFormat.Countdown(600_000), Is.EqualTo("10:00"));
            Assert.That(UiFormat.Countdown(59_001), Is.EqualTo("1:00"));
            Assert.That(UiFormat.Countdown(9_000), Is.EqualTo("0:09"));
            Assert.That(UiFormat.Countdown(1), Is.EqualTo("0:01"), "a started second still counts");
            Assert.That(UiFormat.Countdown(0), Is.EqualTo("0:00"));
            Assert.That(UiFormat.Countdown(-5000), Is.EqualTo("0:00"), "never negative");
            Assert.That(UiFormat.FileSize(500), Is.EqualTo("500 B"));
            Assert.That(UiFormat.FileSize(12 * 1024), Is.EqualTo("12 KB"));
            Assert.That(UiFormat.FileSize(1_468_006), Is.EqualTo("1.4 MB"));
            var texts = new UiTexts();
            Assert.That(UiFormat.SavedWhen("2026-06-01T09:15:00Z", Today, "en-GB", texts), Is.EqualTo("Today, 09:15"));
            Assert.That(UiFormat.SavedWhen("2026-05-31T23:15:00Z", Today, "en-GB", texts), Is.EqualTo("Yesterday, 23:15"));
            Assert.That(UiFormat.SavedWhen("2026-05-20T23:15:00Z", Today, "en-GB", texts), Does.Not.StartWith("Today").And.Not.StartWith("Yesterday"), "control: older is a date");
            Assert.That(UiFormat.SavedWhen("not a date", Today, "en-GB", texts), Is.Empty);
        }

        [Test]
        public void Texts_TheGamesSentenceWinsAndTheEnglishOneFillsTheRest_MarksAreFilledAndUnknownOnesStay()
        {
            var texts = new UiTexts(new Dictionary<string, string> { ["conflict.title"] = "Slot {slot} has two saves", ["age.title"] = "Doğum tarihin?" });
            Assert.That(texts.Get("age.title"), Is.EqualTo("Doğum tarihin?"));
            Assert.That(texts.Get("age.day"), Is.EqualTo("Day"), "control: not overridden");
            Assert.That(texts.Get("conflict.title", ("slot", "A")), Is.EqualTo("Slot A has two saves"));
            Assert.That(texts.Get("device.go"), Is.EqualTo("Go to {uri}"), "a mark nobody filled stays");
            Assert.That(texts.Get("device.waiting", ("time", UiFormat.Countdown(125_000))), Is.EqualTo("Waiting · code works for 2:05"));
            Assert.That(texts.Get("conflict.cloud", ("platform", "Steam")), Is.EqualTo("Steam Cloud"));
            Assert.That(new[] { "hint.select", "hint.back", "hint.selectButton", "hint.backButton", "waiting.title", "waiting.body", "waiting.reopen", "waiting.cancel", "device.title", "device.enter", "device.expired", "device.newCode", "device.cancel" }.All(texts.Has), Is.True, "the native keys exist");
        }

        [Test]
        public async Task TheAnswerQueueHandsAnswersOutInOrderAndAClosedOneAnswersClosed()
        {
            var queue = new UiAnswerQueue();
            var waiting = queue.NextAsync();
            Assert.That(waiting.IsCompleted, Is.False);
            queue.Post(UiAnswer.Reopen);
            queue.Post(UiAnswer.Cancel);
            Assert.That((await waiting).Kind, Is.EqualTo(UiAnswerKind.Reopen));
            Assert.That((await queue.NextAsync()).Kind, Is.EqualTo(UiAnswerKind.Cancel), "an answer given before anyone asked is kept");
            var pending = queue.NextAsync();
            queue.Close();
            Assert.That((await pending).Kind, Is.EqualTo(UiAnswerKind.Closed), "closing frees a waiter");
            queue.Post(UiAnswer.Reopen);
            Assert.That((await queue.NextAsync()).Kind, Is.EqualTo(UiAnswerKind.Closed), "control: nothing is taken after the close");
        }
    }
}
