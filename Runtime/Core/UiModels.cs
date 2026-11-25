#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Krtuworks
{
    /// <summary>What the game can show about a save, read from its own bytes by <see cref="KrtuworksUiOptions.DescribeSave"/>.</summary>
    public sealed class SaveSummary
    {
        public string? Title;
        public IReadOnlyList<string> Lines = Array.Empty<string>();
        /// <summary>A picture of the save (PNG or JPEG bytes), shown above the lines.</summary>
        public byte[]? Image;
    }

    public sealed class SaveCard
    {
        public string Label = "";
        public bool Newer;
        public string When = "";
        public string Size = "";
        public SaveSummary? Summary;
    }

    public enum UiScreenKind { Age, Terms, Underage, Conflict, Error, Waiting, Device }

    /// <summary>The screens the ready-made components draw; a game that draws its own gets the same models.</summary>
    public abstract class UiScreen
    {
        public abstract UiScreenKind Kind { get; }
    }

    public enum AgeProblem { None, Invalid, Accept }

    public sealed class AgeScreen : UiScreen
    {
        public override UiScreenKind Kind => UiScreenKind.Age;
        public DateOrder Order;
        /// <summary>This year back to 120 years ago, newest first.</summary>
        public IReadOnlyList<int> Years = Array.Empty<int>();
        public string TermsUrl = "";
        public string PrivacyUrl = "";
        /// <summary>Whether "Sign in with Krtuworks ID" is offered (not in a WebGL build).</summary>
        public bool SignIn;
        public AgeProblem Problem;
    }

    public sealed class TermsScreen : UiScreen
    {
        public override UiScreenKind Kind => UiScreenKind.Terms;
        public string TermsUrl = "";
        public string PrivacyUrl = "";
    }

    public sealed class UnderageScreen : UiScreen
    {
        public override UiScreenKind Kind => UiScreenKind.Underage;
    }

    public sealed class ConflictScreen : UiScreen
    {
        public override UiScreenKind Kind => UiScreenKind.Conflict;
        public string Slot = "";
        public SaveCard[] Cards = Array.Empty<SaveCard>();
    }

    public sealed class ErrorScreen : UiScreen
    {
        public override UiScreenKind Kind => UiScreenKind.Error;
        public string Message = "";
        /// <summary>Whether trying again can help (the screen then offers it).</summary>
        public bool Retry;

        /// <summary>The sentence an error screen shows for <paramref name="err"/>, and whether trying again can help.</summary>
        public static ErrorScreen From(Exception err, UiTexts texts, string locale)
        {
            if (!(err is KrtuworksException e)) return new ErrorScreen { Message = texts.Get("error.generic"), Retry = true };
            switch (e.Code)
            {
                case "network_error":
                    return new ErrorScreen { Message = texts.Get("error.offline"), Retry = true };
                case "sanctioned":
                {
                    var reason = e.Details["reason"]?.Type == JTokenType.String ? (string?)e.Details["reason"] : null;
                    var message = !string.IsNullOrEmpty(reason) ? texts.Get("error.bannedReason", ("reason", reason!)) : texts.Get("error.banned");
                    if (e.Details["endsAt"]?.Type == JTokenType.String
                        && DateTimeOffset.TryParse((string?)e.Details["endsAt"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var endsAt))
                        message += " " + texts.Get("error.bannedUntil", ("date", UiFormat.LongDate(endsAt, locale)));
                    return new ErrorScreen { Message = message, Retry = false };
                }
                case "guest_limit":
                case "guest_capacity":
                    return new ErrorScreen { Message = texts.Get("error.busy"), Retry = true };
                case "app_suspended":
                case "maintenance":
                    return new ErrorScreen { Message = texts.Get("error.maintenance"), Retry = true };
                case "access_denied":
                case "cancelled":
                    return new ErrorScreen { Message = texts.Get("error.cancelled"), Retry = true };
                default:
                    return new ErrorScreen { Message = e.Status == 0 ? texts.Get("error.offline") : texts.Get("error.generic"), Retry = true };
            }
        }
    }

    /// <summary>The browser is open on the sign-in page; the game waits for it to come back.</summary>
    public sealed class WaitingScreen : UiScreen
    {
        public override UiScreenKind Kind => UiScreenKind.Waiting;
        public string Url = "";
    }

    /// <summary>A console's or TV's sign-in: a code to enter at an address on another device, or the QR of the complete address.</summary>
    public sealed class DeviceScreen : UiScreen
    {
        public override UiScreenKind Kind => UiScreenKind.Device;
        public string Code = "";
        public string Uri = "";
        /// <summary>The address with the code in it; the QR holds this one.</summary>
        public string UriComplete = "";
        /// <summary>Dark modules of <see cref="UriComplete"/>'s QR code (without the quiet zone).</summary>
        public bool[][] Qr = Array.Empty<bool[]>();
        /// <summary>When the code stops working, in milliseconds on the device's clock (Unix time).</summary>
        public long ExpiresAtMs;
        public bool Expired;

        public DeviceScreen AsExpired() => new DeviceScreen { Code = Code, Uri = Uri, UriComplete = UriComplete, Qr = Qr, ExpiresAtMs = ExpiresAtMs, Expired = true };
    }

    public enum UiAnswerKind { Birth, Accept, SignIn, Offline, Keep, Retry, Continue, Reopen, Cancel, NewCode, Closed }

    public sealed class UiAnswer
    {
        public UiAnswerKind Kind { get; }
        public int Year { get; }
        public int Month { get; }
        public int Day { get; }
        public bool Accepted { get; }
        /// <summary>The card kept, 0 or 1.</summary>
        public int Card { get; }

        UiAnswer(UiAnswerKind kind, int year = 0, int month = 0, int day = 0, bool accepted = false, int card = 0)
        {
            Kind = kind;
            Year = year;
            Month = month;
            Day = day;
            Accepted = accepted;
            Card = card;
        }

        public static UiAnswer Birth(int year, int month, int day, bool accepted) => new UiAnswer(UiAnswerKind.Birth, year, month, day, accepted);
        public static UiAnswer Keep(int card) => new UiAnswer(UiAnswerKind.Keep, card: card);
        public static readonly UiAnswer Accept = new UiAnswer(UiAnswerKind.Accept);
        public static readonly UiAnswer SignIn = new UiAnswer(UiAnswerKind.SignIn);
        public static readonly UiAnswer Offline = new UiAnswer(UiAnswerKind.Offline);
        public static readonly UiAnswer Retry = new UiAnswer(UiAnswerKind.Retry);
        public static readonly UiAnswer Continue = new UiAnswer(UiAnswerKind.Continue);
        public static readonly UiAnswer Reopen = new UiAnswer(UiAnswerKind.Reopen);
        public static readonly UiAnswer Cancel = new UiAnswer(UiAnswerKind.Cancel);
        public static readonly UiAnswer NewCode = new UiAnswer(UiAnswerKind.NewCode);
        /// <summary>What a closed dialog answers, so nobody waits on it for ever.</summary>
        public static readonly UiAnswer Closed = new UiAnswer(UiAnswerKind.Closed);
    }

    /// <summary>
    /// The answers a drawn screen gives, in order. A host posts each answer the player gives; the controller takes
    /// them one by one. After <see cref="Close"/> the answers still waiting are handed out, then <see cref="UiAnswer.Closed"/>.
    /// </summary>
    public sealed class UiAnswerQueue
    {
        readonly object gate = new object();
        readonly Queue<UiAnswer> ready = new Queue<UiAnswer>();
        TaskCompletionSource<UiAnswer>? waiting;
        bool closed;

        public void Post(UiAnswer answer)
        {
            TaskCompletionSource<UiAnswer>? hand;
            lock (gate)
            {
                if (closed) return;
                hand = waiting;
                waiting = null;
                if (hand == null) ready.Enqueue(answer);
            }
            hand?.SetResult(answer);
        }

        public Task<UiAnswer> NextAsync()
        {
            lock (gate)
            {
                if (ready.Count > 0) return Task.FromResult(ready.Dequeue());
                if (closed) return Task.FromResult(UiAnswer.Closed);
                return (waiting ??= new TaskCompletionSource<UiAnswer>()).Task;
            }
        }

        public void Close()
        {
            TaskCompletionSource<UiAnswer>? hand;
            lock (gate)
            {
                closed = true;
                hand = waiting;
                waiting = null;
            }
            hand?.SetResult(UiAnswer.Closed);
        }
    }

    /// <summary>One screen on show.</summary>
    public interface IUiDialog
    {
        /// <summary>The player's next answer on this screen.</summary>
        Task<UiAnswer> NextAnswerAsync();
        /// <summary>Shows <paramref name="screen"/> in place of the one on show, without closing the dialog.</summary>
        void Update(UiScreen screen);
        void Close();
    }

    public enum UiReminderAnswer { Later, Link }

    /// <summary>The "link a Krtuworks ID" prompt in a corner.</summary>
    public interface IUiReminder
    {
        Task<UiReminderAnswer> Answer { get; }
        void Close();
    }

    /// <summary>Draws the screens: the Unity layer's UI Toolkit host by default, or the game's own.</summary>
    public interface IUiHost
    {
        IUiDialog Show(UiScreen screen);
        /// <summary>The "link a Krtuworks ID" prompt in a corner; it never stops the game.</summary>
        IUiReminder Remind();
    }

    public enum UiSignInMode
    {
        /// <summary>The system browser, with a loopback return (Windows, Mac, Linux, Steam Deck).</summary>
        Browser,
        /// <summary>The platform's own sign-in session (iOS, Android).</summary>
        App,
        /// <summary>A code to enter on another device (consoles, TVs).</summary>
        Device,
        /// <summary>No sign-in here (a WebGL build signs in with the JS package on its page).</summary>
        None,
    }

    public enum UiOutcome { SignedIn, Cancelled, Offline }

    public enum OfflineReason { None, Underage, Unavailable }

    /// <summary>
    /// How a sign-in or <see cref="KrtuworksUi.EnsurePlayerAsync"/> ended: someone plays (<see cref="UiOutcome.SignedIn"/>),
    /// the player backed out (<see cref="UiOutcome.Cancelled"/>, a sign-in only), or no online player and the game goes on
    /// offline (<see cref="Reason"/>: under 13, or no connection, a refusal, no sign-in here).
    /// </summary>
    public sealed class UiResult
    {
        public UiOutcome Outcome { get; }
        public Player? Player { get; }
        public OfflineReason Reason { get; }

        UiResult(UiOutcome outcome, Player? player, OfflineReason reason)
        {
            Outcome = outcome;
            Player = player;
            Reason = reason;
        }

        public static UiResult SignedIn(Player player) => new UiResult(UiOutcome.SignedIn, player, OfflineReason.None);
        public static readonly UiResult Cancelled = new UiResult(UiOutcome.Cancelled, null, OfflineReason.None);
        public static UiResult Offline(OfflineReason reason) => new UiResult(UiOutcome.Offline, null, reason);
    }

    public enum SaveKept { Here, There }

    /// <summary>What the player kept when two saves met: this device's (now saved) or the other one's (its bytes are for the game to load).</summary>
    public sealed class SaveChoice
    {
        public SaveKept Kept { get; }
        public FileVersion Version { get; }
        public byte[]? Bytes { get; }

        public SaveChoice(SaveKept kept, FileVersion version, byte[]? bytes = null)
        {
            Kept = kept;
            Version = version;
            Bytes = bytes;
        }
    }

    public sealed class ResolvedSave
    {
        public string Slot = "";
        public FileVersion Kept = new FileVersion();
    }
}
