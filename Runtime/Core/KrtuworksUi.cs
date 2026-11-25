#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Krtuworks.Internal;
using Newtonsoft.Json.Linq;

namespace Krtuworks
{
    /// <summary>What a game may set on the ready-made screens; everything has a default.</summary>
    public sealed class KrtuworksUiOptions
    {
        /// <summary>The game's own sentences (any subset, any language); the English ones fill the rest.</summary>
        public UiTexts? Texts;
        /// <summary>The device's language tag, for the date order and times. Default <c>en</c>.</summary>
        public string? Locale;
        /// <summary>Plays as a guest when nobody signs in (default true); false starts the sign-in instead.</summary>
        public bool Guests = true;
        /// <summary>Shows the "link a Krtuworks ID" prompt when the SDK says it is time (default true).</summary>
        public bool LinkReminder = true;
        /// <summary>Shows the two saves when the platform's cloud holds a copy that differs from the server's (default true).</summary>
        public bool PlatformSaveConflicts = true;
        /// <summary>How the player signs in on this platform.</summary>
        public UiSignInMode Mode = UiSignInMode.Browser;
        /// <summary>Opens an address in the system browser; null where there is none (consoles). Needed for <see cref="UiSignInMode.Browser"/>.</summary>
        public Action<string>? OpenUrl;
        /// <summary>The platform's authentication session (<c>url, scheme, cancel</c> to the address it came back to). Needed for <see cref="UiSignInMode.App"/>.</summary>
        public Func<string, string, CancellationToken, Task<string>>? Authenticate;
        /// <summary>The name the player sees for this device when approving a code (<see cref="UiSignInMode.Device"/>).</summary>
        public string? DeviceName;
        /// <summary>Reads a save's bytes into what its card shows (chapter, play time, a picture).</summary>
        public Func<string, byte[], SaveSummary?>? DescribeSave;
        /// <summary>Where the screens keep their small values; by default the SDK's own store.</summary>
        public IKrtuStore? Memory;
        public Func<DateTimeOffset>? Now;
        /// <summary>How the screens wait; by default the SDK's own wait.</summary>
        public Func<TimeSpan, CancellationToken, Task>? Delay;
    }

    /// <summary>
    /// The ready-made screens' decisions: which one to show, what it says, what the SDK does with the answer.
    /// A <see cref="IUiHost"/> draws the screens (the Unity layer's host by default).
    /// </summary>
    public sealed class KrtuworksUi : IDisposable
    {
        const string UnderageKey = "krtu:ui:underageUntil";
        static readonly TimeSpan ReminderShows = TimeSpan.FromSeconds(20);

        readonly KrtuworksClient client;
        readonly IUiHost host;
        readonly KrtuworksUiOptions options;
        readonly IKrtuStore memory;
        readonly Func<DateTimeOffset> now;
        readonly Func<TimeSpan, CancellationToken, Task> delay;
        readonly SingleFlight<UiResult> signingIn = new SingleFlight<UiResult>();
        readonly object platformGate = new object();
        Task platformQueue = Task.CompletedTask;
        bool reminding;

        public KrtuworksUi(KrtuworksClient client, IUiHost host, KrtuworksUiOptions? options = null)
        {
            this.client = client;
            this.host = host;
            this.options = options ?? new KrtuworksUiOptions();
            if (this.options.Mode == UiSignInMode.Browser && this.options.OpenUrl == null) throw new ArgumentException("The browser sign-in needs OpenUrl", nameof(options));
            if (this.options.Mode == UiSignInMode.App && this.options.Authenticate == null) throw new ArgumentException("The app sign-in needs Authenticate", nameof(options));
            Texts = this.options.Texts ?? new UiTexts();
            Locale = this.options.Locale ?? "en";
            memory = this.options.Memory ?? client.Store;
            now = this.options.Now ?? (() => DateTimeOffset.Now);
            delay = this.options.Delay ?? client.Delay;
            if (this.options.LinkReminder) client.LinkReminder += OnLinkReminder;
            if (this.options.PlatformSaveConflicts) client.PlatformSaveConflict += OnPlatformSaveConflict;
        }

        public KrtuworksClient Client => client;
        public UiTexts Texts { get; }
        public string Locale { get; }
        public UiSignInMode Mode => options.Mode;
        /// <summary>False in a WebGL build: the sign-in button and the age screen's sign-in are not shown.</summary>
        public bool SignInOffered => options.Mode != UiSignInMode.None;
        public string TermsUrl => client.IdOrigin + "/legal/terms/latest";
        public string PrivacyUrl => client.IdOrigin + "/legal/privacy/latest";
        public string AccountUrl => client.IdOrigin + "/";
        /// <summary>Whether an address can be opened here (not on a console).</summary>
        public bool CanOpenUrl => options.OpenUrl != null;

        public string T(string key, params (string Name, string Value)[] vars) => Texts.Get(key, vars);

        /// <summary>Opens the account site in the system browser.</summary>
        public void OpenAccountPage() => options.OpenUrl?.Invoke(AccountUrl);

        /// <summary>
        /// Makes sure someone is playing: the signed-in player, the guest on this device, or a new guest after the age
        /// screen. Never throws: without an online player the result says so and the game keeps going offline.
        /// </summary>
        public async Task<UiResult> EnsurePlayerAsync()
        {
            if (client.Player != null) return UiResult.SignedIn(client.Player);
            var blocked = GateProblem();
            if (blocked != null) return await FailOfflineAsync(blocked);
            var until = Json.Read<string>(await memory.GetAsync(UnderageKey));
            if (until != null && string.CompareOrdinal(Today(0), until) < 0) return UiResult.Offline(OfflineReason.Underage);
            if (!options.Guests) return Settled(await SignInAsync());
            string? birthDate = null;
            string? acceptedTerms = null;
            for (;;)
            {
                try
                {
                    return UiResult.SignedIn(await client.SignInAsGuestAsync(birthDate, acceptedTerms));
                }
                catch (Exception err)
                {
                    var failure = err as KrtuworksException;
                    var code = failure?.Code ?? "";
                    if (code == "birth_date_required" || code == "invalid_birth_date")
                    {
                        var age = await AskAgeAsync(code == "invalid_birth_date" ? AgeProblem.Invalid : AgeProblem.None);
                        if (age.SignIn)
                        {
                            var signedIn = await SignInAsync();
                            if (signedIn.Outcome != UiOutcome.Cancelled) return signedIn;
                            continue;
                        }
                        if (age.BirthDate == null) return UiResult.Offline(OfflineReason.Underage);
                        birthDate = age.BirthDate;
                        acceptedTerms = KrtuworksClient.CurrentTerms;
                        continue;
                    }
                    if (code == "too_young")
                    {
                        if (birthDate != null) await RememberUnderageAsync(birthDate);
                        await Once(new UnderageScreen());
                        return UiResult.Offline(OfflineReason.Underage);
                    }
                    var asked = failure != null && failure.Details["terms"]?.Type == JTokenType.String ? (string?)failure.Details["terms"] : null;
                    if (code == "terms_required" && asked != null && asked != acceptedTerms)
                    {
                        await Once(new TermsScreen { TermsUrl = TermsUrl, PrivacyUrl = PrivacyUrl });
                        acceptedTerms = asked;
                        continue;
                    }
                    var answer = await Once(ErrorScreen.From(err, Texts, Locale));
                    if (answer.Kind != UiAnswerKind.Retry) return UiResult.Offline(OfflineReason.Unavailable);
                }
            }
        }

        /// <summary>A sign-in that was cancelled leaves nobody playing online here: the game goes on offline.</summary>
        static UiResult Settled(UiResult result) => result.Outcome == UiOutcome.Cancelled ? UiResult.Offline(OfflineReason.Unavailable) : result;

        /// <summary>
        /// Signs the player in the way this platform does and shows what the player needs meanwhile: the waiting
        /// screen (browser), nothing (the system's own sheet on a phone), the code (console). Backing out is
        /// <see cref="UiOutcome.Cancelled"/> (no error screen); a failure shows the error screen and the game goes on offline.
        /// </summary>
        public Task<UiResult> SignInAsync(CancellationToken cancel = default) => signingIn.Run(() => RunSignInAsync(cancel));

        async Task<UiResult> RunSignInAsync(CancellationToken cancel)
        {
            if (options.Mode == UiSignInMode.None) return UiResult.Offline(OfflineReason.Unavailable);
            try
            {
                Player player;
                switch (options.Mode)
                {
                    case UiSignInMode.Browser:
                        player = await BrowserSignInAsync(cancel);
                        break;
                    case UiSignInMode.App:
                        player = await client.SignInWithAppAsync(options.Authenticate!, cancel);
                        break;
                    default:
                        player = await DeviceSignInAsync(cancel);
                        break;
                }
                return UiResult.SignedIn(player);
            }
            catch (OperationCanceledException)
            {
                return UiResult.Cancelled;
            }
            catch (Exception err)
            {
                return await FailOfflineAsync(ErrorScreen.From(err, Texts, Locale));
            }
        }

        async Task<Player> BrowserSignInAsync(CancellationToken cancel)
        {
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            IUiDialog? waiting = null;
            try
            {
                return await client.SignInWithBrowserAsync(address =>
                {
                    options.OpenUrl!(address);
                    waiting = host.Show(new WaitingScreen { Url = address });
                    _ = Pump(waiting, address, cancelled);
                }, cancelled.Token);
            }
            finally
            {
                waiting?.Close();
            }
        }

        /// <summary>The waiting screen's answers: Open the page again opens the same address and keeps waiting, Cancel stops the sign-in.</summary>
        async Task Pump(IUiDialog waiting, string address, CancellationTokenSource cancelled)
        {
            for (;;)
            {
                var answer = await waiting.NextAnswerAsync();
                if (answer.Kind == UiAnswerKind.Reopen) options.OpenUrl!(address);
                else if (answer.Kind == UiAnswerKind.Cancel)
                {
                    try { cancelled.Cancel(); }
                    catch (ObjectDisposedException) { /* the sign-in already ended */ }
                    return;
                }
                else if (answer.Kind == UiAnswerKind.Closed) return;
            }
        }

        /// <summary>
        /// The code screen: one screen for as many codes as it takes. A new code (asked for, after one expired) updates the
        /// same screen; the SDK's wait for approval runs beside it and ends it.
        /// </summary>
        async Task<Player> DeviceSignInAsync(CancellationToken cancel)
        {
            IUiDialog? dialog = null;
            try
            {
                for (;;)
                {
                    var link = await client.StartDeviceLinkAsync(options.DeviceName);
                    var screen = new DeviceScreen
                    {
                        Code = link.UserCode,
                        Uri = link.VerificationUri,
                        UriComplete = link.VerificationUriComplete,
                        Qr = QrCode.Modules(link.VerificationUriComplete),
                        ExpiresAtMs = now().ToUnixTimeMilliseconds() + link.ExpiresIn * 1000L,
                    };
                    if (dialog == null) dialog = host.Show(screen);
                    else dialog.Update(screen);
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    Task<Player>? finish = client.FinishDeviceLinkAsync(link, attempt.Token);
                    var answerTask = dialog.NextAnswerAsync();
                    var again = false;
                    while (!again)
                    {
                        Task first = finish == null ? answerTask : await Task.WhenAny(finish, answerTask);
                        if (finish != null && first == finish)
                        {
                            try
                            {
                                return await finish;
                            }
                            catch (KrtuworksException err) when (err.Code == "expired_token")
                            {
                                screen = screen.AsExpired();
                                dialog.Update(screen);
                                finish = null;
                                continue;
                            }
                        }
                        var answer = await answerTask;
                        if (answer.Kind == UiAnswerKind.NewCode || answer.Kind == UiAnswerKind.Cancel || answer.Kind == UiAnswerKind.Closed)
                        {
                            attempt.Cancel();
                            if (finish != null)
                            {
                                // The approval may have arrived at this very moment: that sign-in stands.
                                try { return await finish; }
                                catch (Exception) { /* cancelled, or the code ended: nothing was signed in */ }
                            }
                            if (answer.Kind != UiAnswerKind.NewCode) throw new OperationCanceledException(cancel);
                            again = true;
                        }
                        else answerTask = dialog.NextAnswerAsync();
                    }
                }
            }
            finally
            {
                dialog?.Close();
            }
        }

        public Task SignOutAsync() => client.SignOutAsync();

        // ── Saves ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Saves <paramref name="bytes"/> to <paramref name="slot"/> over <paramref name="baseVersion"/>; when another device saved first, shows both
        /// saves and does what the player picks. <see cref="SaveKept.There"/> hands back the other save's bytes for the game to load.
        /// </summary>
        public async Task<SaveChoice> SaveAsync(string slot, byte[] bytes, string? baseVersion = null, string? name = null)
        {
            try
            {
                return new SaveChoice(SaveKept.Here, await client.SaveFileAsync(slot, bytes, baseVersion));
            }
            catch (KrtuworksException err) when (err.Code == "file_conflict" && CurrentOf(err) != null)
            {
                return await ChooseSaveAsync(slot, bytes, CurrentOf(err)!, name);
            }
        }

        static FileVersion? CurrentOf(KrtuworksException err) => (err.Details["current"] as JObject)?.ToObject<FileVersion>();

        /// <summary>Two saves of <paramref name="slot"/>: this device's <paramref name="bytes"/> and the server's <paramref name="current"/>.</summary>
        public async Task<SaveChoice> ChooseSaveAsync(string slot, byte[] bytes, FileVersion current, string? name = null)
        {
            for (;;)
            {
                var read = await ReadSlotAsync(slot);
                if (read?.VersionId == null) return new SaveChoice(SaveKept.Here, await client.SaveFileAsync(slot, bytes, null));
                var there = new FileVersion
                {
                    Id = read.VersionId,
                    Slot = current.Slot,
                    Device = current.Device,
                    Generation = read.Generation,
                    SizeBytes = read.Bytes.Length,
                    Sha256 = current.Sha256,
                    Platform = current.Platform,
                    Build = current.Build,
                    SavedAt = read.SavedAt.Length > 0 ? read.SavedAt : current.SavedAt,
                    FromMerge = current.FromMerge,
                    PlatformCloud = current.PlatformCloud,
                };
                var moment = now();
                var hereCard = Card(Texts.Get("conflict.here"), moment.ToString("o", CultureInfo.InvariantCulture), bytes.Length, slot, bytes);
                var thereLabel = there.Platform != null ? $"{Texts.Get("conflict.there")} · {PlatformName(there.Platform)}" : Texts.Get("conflict.there");
                var thereCard = Card(thereLabel, there.SavedAt, there.SizeBytes, slot, read.Bytes);
                hereCard.Newer = IsNewer(moment, there.SavedAt, true);
                thereCard.Newer = !hereCard.Newer;
                var answer = await Once(new ConflictScreen { Slot = name ?? slot, Cards = new[] { hereCard, thereCard } });
                if (answer.Kind == UiAnswerKind.Keep && answer.Card == 1) return new SaveChoice(SaveKept.There, there, read.Bytes);
                try
                {
                    return new SaveChoice(SaveKept.Here, await client.SaveFileAsync(slot, bytes, there.Id));
                }
                catch (KrtuworksException err) when (err.Code == "file_conflict" && CurrentOf(err) != null)
                {
                    current = CurrentOf(err)!;
                }
            }
        }

        /// <summary>After two accounts were merged: every slot holding two versions asks the player which to keep.</summary>
        public async Task<List<ResolvedSave>> ResolveMergedSavesAsync()
        {
            var list = await client.ListFilesAsync();
            var resolved = new List<ResolvedSave>();
            foreach (var slot in list.Slots)
            {
                // The platform cloud's waiting copy has its own prompt (PlatformSaveConflict).
                if (slot.Conflict == null || slot.File == null || slot.Conflict.PlatformCloud != null) continue;
                var read = await ReadSlotAsync(slot.Key);
                var currentCard = Card(Texts.Get("conflict.current"), slot.File.SavedAt, slot.File.SizeBytes, slot.Key, read?.Bytes);
                var mergedCard = Card(Texts.Get("conflict.merged"), slot.Conflict.SavedAt, slot.Conflict.SizeBytes, slot.Key, null);
                mergedCard.Newer = IsNewer(slot.Conflict.SavedAt, slot.File.SavedAt);
                currentCard.Newer = !mergedCard.Newer;
                var answer = await Once(new ConflictScreen { Slot = string.IsNullOrEmpty(slot.Name) ? slot.Key : slot.Name, Cards = new[] { currentCard, mergedCard } });
                var keep = answer.Kind == UiAnswerKind.Keep && answer.Card == 1 ? slot.Conflict : slot.File;
                resolved.Add(new ResolvedSave { Slot = slot.Key, Kept = await client.ResolveFileAsync(slot.Key, keep.Id, slot.File.Id) });
            }
            return resolved;
        }

        /// <summary>The slot as the server has it; null when it has no version.</summary>
        async Task<FileRead?> ReadSlotAsync(string slot)
        {
            try
            {
                return await client.ReadFileAsync(slot);
            }
            catch (KrtuworksException err) when (err.Status == 404)
            {
                return null;
            }
        }

        static bool IsNewer(string later, string earlier) =>
            DateTimeOffset.TryParse(later, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var a)
            && DateTimeOffset.TryParse(earlier, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var b)
            && a > b;

        /// <summary>Whether <paramref name="moment"/> is no earlier than <paramref name="other"/> when <paramref name="orEqual"/>, else later.</summary>
        static bool IsNewer(DateTimeOffset moment, string other, bool orEqual) =>
            DateTimeOffset.TryParse(other, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) && (orEqual ? moment >= parsed : moment > parsed);

        SaveCard Card(string label, string savedAt, long sizeBytes, string slot, byte[]? bytes)
        {
            SaveSummary? summary = null;
            if (bytes != null && options.DescribeSave != null)
            {
                try { summary = options.DescribeSave(slot, bytes); }
                catch (Exception) { summary = null; }
            }
            return new SaveCard { Label = label, When = UiFormat.SavedWhen(savedAt, now(), Locale, Texts), Size = UiFormat.FileSize(sizeBytes), Summary = summary };
        }

        public string PlatformName(string platform) => Texts.Has($"platform.{platform}") ? Texts.Get($"platform.{platform}") : platform;

        // ── The platform's cloud ──────────────────────────────────────────────

        void OnPlatformSaveConflict(PlatformSaveConflict conflict)
        {
            lock (platformGate) platformQueue = QueuePlatformConflict(platformQueue, conflict);
        }

        /// <summary>One at a time: the next waits for the player's choice on the one before.</summary>
        async Task QueuePlatformConflict(Task previous, PlatformSaveConflict conflict)
        {
            await previous;
            await Background(() => ResolvePlatformConflictAsync(conflict), "ui_platform_conflict");
        }

        internal async Task ResolvePlatformConflictAsync(PlatformSaveConflict conflict)
        {
            var read = await ReadSlotAsync(conflict.Slot);
            var platform = conflict.Platform.PlatformCloud ?? conflict.Platform.Platform ?? "";
            var currentCard = Card(Texts.Get("conflict.current"), conflict.Server.SavedAt, conflict.Server.SizeBytes, conflict.Slot, read?.Bytes);
            var cloudCard = Card(Texts.Get("conflict.cloud", ("platform", PlatformName(platform))), conflict.Platform.SavedAt, conflict.PlatformData?.Length ?? conflict.Platform.SizeBytes, conflict.Slot, conflict.PlatformData);
            cloudCard.Newer = IsNewer(conflict.Platform.SavedAt, conflict.Server.SavedAt);
            currentCard.Newer = !cloudCard.Newer;
            var answer = await Once(new ConflictScreen { Slot = conflict.Slot, Cards = new[] { currentCard, cloudCard } });
            var keep = answer.Kind == UiAnswerKind.Keep && answer.Card == 1 ? conflict.Platform : conflict.Server;
            await client.ResolveFileAsync(conflict.Slot, keep.Id, conflict.Server.Id);
        }

        // ── The link reminder ────────────────────────────────────────────────

        void OnLinkReminder(string reason) => _ = Background(RemindAsync, "ui_link_reminder");

        /// <summary>Shows the "link a Krtuworks ID" prompt for 20 seconds; Link starts the sign-in. One at a time, and only for a guest.</summary>
        public async Task RemindAsync()
        {
            if (reminding || !SignInOffered || client.Player?.Kind != PlayerKind.Guest) return;
            reminding = true;
            var prompt = host.Remind();
            using var shown = new CancellationTokenSource();
            try
            {
                var answer = prompt.Answer;
                var timeout = delay(ReminderShows, shown.Token);
                var first = await Task.WhenAny(answer, timeout);
                if (first == answer && await answer == UiReminderAnswer.Link) await SignInAsync();
            }
            finally
            {
                shown.Cancel();
                prompt.Close();
                reminding = false;
            }
        }

        async Task Background(Func<Task> work, string fingerprint)
        {
            try { await work(); }
            catch (Exception err) { client.Error(err, "exception", $"sdk.{fingerprint}"); }
        }

        // ── Pieces ───────────────────────────────────────────────────────────

        ErrorScreen? GateProblem()
        {
            var gate = client.GetGate();
            if (gate.Maintenance) return new ErrorScreen { Message = string.IsNullOrEmpty(gate.MaintenanceMessage) ? Texts.Get("error.maintenance") : gate.MaintenanceMessage! };
            if (gate.UpdateRequired) return new ErrorScreen { Message = Texts.Get("error.update") };
            return null;
        }

        async Task<UiResult> FailOfflineAsync(ErrorScreen screen)
        {
            await Once(new ErrorScreen { Message = screen.Message, Retry = false });
            return UiResult.Offline(OfflineReason.Unavailable);
        }

        sealed class AgeAnswer
        {
            internal bool SignIn;
            /// <summary>The birth date to send; null when the player is under 13.</summary>
            internal string? BirthDate;
        }

        /// <summary>The age screen until it has a real answer: a birth date, the sign-in, or "under 13" (remembered).</summary>
        async Task<AgeAnswer> AskAgeAsync(AgeProblem first)
        {
            var problem = first;
            for (;;)
            {
                var moment = now();
                var answer = await Once(new AgeScreen
                {
                    Order = UiFormat.DateOrderFor(Locale),
                    Years = UiFormat.BirthYears(moment),
                    TermsUrl = TermsUrl,
                    PrivacyUrl = PrivacyUrl,
                    SignIn = SignInOffered,
                    Problem = problem,
                });
                if (answer.Kind == UiAnswerKind.SignIn) return new AgeAnswer { SignIn = true };
                if (answer.Kind != UiAnswerKind.Birth) continue;
                var check = UiFormat.CheckBirth(answer.Year, answer.Month, answer.Day, moment);
                if (check == BirthCheck.Invalid)
                {
                    problem = AgeProblem.Invalid;
                    continue;
                }
                var birthDate = UiFormat.IsoDate(answer.Year, answer.Month, answer.Day);
                if (check == BirthCheck.Underage)
                {
                    await RememberUnderageAsync(birthDate);
                    await Once(new UnderageScreen());
                    return new AgeAnswer();
                }
                if (!answer.Accepted)
                {
                    problem = AgeProblem.Accept;
                    continue;
                }
                return new AgeAnswer { BirthDate = birthDate };
            }
        }

        /// <summary>Until the 13th birthday the age screen is not shown again; at least until tomorrow when the server and this device's clock disagree.</summary>
        async Task RememberUnderageAsync(string birthDate)
        {
            var until = UiFormat.TurnsThirteen(birthDate);
            await Json.Put(memory, UnderageKey, string.CompareOrdinal(until, Today(0)) > 0 ? until : Today(1));
        }

        string Today(int plusDays)
        {
            var day = now().UtcDateTime.AddDays(plusDays);
            return UiFormat.IsoDate(day.Year, day.Month, day.Day);
        }

        async Task<UiAnswer> Once(UiScreen screen)
        {
            var dialog = host.Show(screen);
            try
            {
                return await dialog.NextAnswerAsync();
            }
            finally
            {
                dialog.Close();
            }
        }

        public void Dispose()
        {
            if (options.LinkReminder) client.LinkReminder -= OnLinkReminder;
            if (options.PlatformSaveConflicts) client.PlatformSaveConflict -= OnPlatformSaveConflict;
        }
    }
}
