#nullable enable
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Krtuworks
{
    /// <summary>One piece of a sentence: plain text, or the place of a <c>{name}</c> mark.</summary>
    public sealed class UiSegment
    {
        /// <summary>The text, or null at a mark.</summary>
        public string? Text { get; }
        /// <summary>The mark's name (`terms`), or null in plain text.</summary>
        public string? Mark { get; }

        public UiSegment(string? text, string? mark)
        {
            Text = text;
            Mark = mark;
        }
    }

    /// <summary>
    /// Every sentence the ready-made screens show, in English. A game passes its own (any subset, any language)
    /// as the overrides; <c>{name}</c> marks a value filled in at show time.
    /// </summary>
    public sealed class UiTexts
    {
        static readonly Regex Mark = new Regex("\\{([a-zA-Z]+)\\}");

        public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
        {
            ["age.title"] = "When were you born?",
            ["age.day"] = "Day",
            ["age.month"] = "Month",
            ["age.year"] = "Year",
            ["age.accept"] = "I accept the {terms} and the {privacy}.",
            ["age.termsLink"] = "Terms of Service",
            ["age.privacyLink"] = "Privacy Notice",
            ["age.continue"] = "Continue",
            ["age.signIn"] = "Sign in with Krtuworks ID",
            ["age.invalid"] = "Enter your date of birth.",
            ["age.acceptNeeded"] = "Accept the terms to continue.",
            ["month.1"] = "January",
            ["month.2"] = "February",
            ["month.3"] = "March",
            ["month.4"] = "April",
            ["month.5"] = "May",
            ["month.6"] = "June",
            ["month.7"] = "July",
            ["month.8"] = "August",
            ["month.9"] = "September",
            ["month.10"] = "October",
            ["month.11"] = "November",
            ["month.12"] = "December",
            ["terms.title"] = "Our terms changed",
            ["terms.body"] = "Accept the current {terms} and {privacy} to keep playing online.",
            ["terms.accept"] = "Accept",
            ["underage.title"] = "Online play needs you to be 13",
            ["underage.body"] = "You can keep playing on this device. Progress stays here and isn't saved online.",
            ["underage.play"] = "Play offline",
            ["signIn.button"] = "Sign in with Krtuworks ID",
            ["account.guest"] = "Guest",
            ["account.label"] = "Krtuworks ID",
            ["account.manage"] = "Manage account",
            ["account.link"] = "Link a Krtuworks ID",
            ["account.signOut"] = "Sign out",
            ["conflict.title"] = "Two saves for {slot}",
            ["conflict.body"] = "Pick the one to keep. The other stays in your save history.",
            ["conflict.here"] = "This device",
            ["conflict.there"] = "Another device",
            ["conflict.current"] = "Current save",
            ["conflict.merged"] = "From your other account",
            ["conflict.cloud"] = "{platform} Cloud",
            ["conflict.newer"] = "Newer",
            ["conflict.keep"] = "Keep this save",
            ["reminder.title"] = "Keep your progress safe",
            ["reminder.body"] = "Link a Krtuworks ID so a new phone or a reinstall doesn't lose it.",
            ["reminder.later"] = "Later",
            ["reminder.link"] = "Link",
            ["error.title"] = "Couldn't sign in",
            ["error.offline"] = "No connection to Krtuworks. Your game keeps going; we'll try again when you're back online.",
            ["error.cancelled"] = "Sign-in was cancelled.",
            ["error.banned"] = "You can't play this game online.",
            ["error.deletionPending"] = "This account is waiting to be deleted. Open your account page to cancel the deletion or download your data.",
            ["error.bannedReason"] = "You can't play this game online: {reason}",
            ["error.bannedUntil"] = "This ends on {date}.",
            ["error.maintenance"] = "Online features are down for maintenance.",
            ["error.update"] = "A new version is out. Update the game to keep playing online.",
            ["error.busy"] = "Too many new players right now. Try again in a moment.",
            ["error.generic"] = "Something went wrong. Try again in a moment.",
            ["error.retry"] = "Try again",
            ["error.continue"] = "Keep playing",
            ["waiting.title"] = "Finish signing in in your browser",
            ["waiting.body"] = "We opened id.krtuworks.com. Come back here when it says you're done.",
            ["waiting.reopen"] = "Open the page again",
            ["waiting.cancel"] = "Cancel",
            ["device.title"] = "Sign in on your phone",
            ["device.go"] = "Go to {uri}",
            ["device.enter"] = "Enter this code",
            ["device.waiting"] = "Waiting · code works for {time}",
            ["device.expired"] = "This code expired.",
            ["device.newCode"] = "New code",
            ["device.cancel"] = "Cancel",
            ["hint.select"] = "Select",
            ["hint.back"] = "Back",
            ["hint.selectButton"] = "A",
            ["hint.backButton"] = "B",
            ["time.today"] = "Today, {time}",
            ["time.yesterday"] = "Yesterday, {time}",
            ["platform.STEAM"] = "Steam",
            ["platform.EPIC"] = "Epic Games",
            ["platform.WINDOWS"] = "Windows",
            ["platform.MACOS"] = "Mac",
            ["platform.LINUX"] = "Linux",
            ["platform.IOS"] = "iPhone or iPad",
            ["platform.ANDROID"] = "Android",
            ["platform.WEB"] = "Browser",
            ["platform.XBOX"] = "Xbox",
            ["platform.PLAYSTATION"] = "PlayStation",
            ["platform.NINTENDO"] = "Nintendo Switch",
        };

        readonly IReadOnlyDictionary<string, string>? overrides;

        public UiTexts(IReadOnlyDictionary<string, string>? overrides = null)
        {
            this.overrides = overrides;
        }

        /// <summary>Whether the screens have a sentence for <paramref name="key"/> (the game's own or the English one).</summary>
        public bool Has(string key) => (overrides != null && overrides.ContainsKey(key)) || Defaults.ContainsKey(key);

        string Template(string key)
        {
            if (overrides != null && overrides.TryGetValue(key, out var own)) return own;
            return Defaults.TryGetValue(key, out var text) ? text : key;
        }

        /// <summary>The game's sentence for <paramref name="key"/>, else the English one, with each <c>{name}</c> filled from <paramref name="vars"/>.</summary>
        public string Get(string key, params (string Name, string Value)[] vars) =>
            Mark.Replace(Template(key), found =>
            {
                foreach (var v in vars)
                {
                    if (v.Name == found.Groups[1].Value) return v.Value;
                }
                return found.Value;
            });

        /// <summary>
        /// The sentence split around its <c>{name}</c> marks, for a screen that puts something other than text there
        /// (the terms sentence's two links).
        /// </summary>
        public IReadOnlyList<UiSegment> Segments(string key)
        {
            var template = Template(key);
            var parts = new List<UiSegment>();
            var at = 0;
            foreach (Match found in Mark.Matches(template))
            {
                if (found.Index > at) parts.Add(new UiSegment(template.Substring(at, found.Index - at), null));
                parts.Add(new UiSegment(null, found.Groups[1].Value));
                at = found.Index + found.Length;
            }
            if (at < template.Length) parts.Add(new UiSegment(template.Substring(at), null));
            return parts;
        }
    }
}
