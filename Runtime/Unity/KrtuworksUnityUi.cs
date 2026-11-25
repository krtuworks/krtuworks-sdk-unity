#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Krtuworks.Unity
{
    /// <summary>What a game may set on the ready-made screens before <see cref="KrtuworksUnity.CreateUi"/>; everything has a default.</summary>
    public sealed class KrtuworksUnityUiOptions
    {
        /// <summary>The game's own sentences (any subset, any language); the English ones fill the rest. Keys are in <see cref="UiTexts.Defaults"/>.</summary>
        public IReadOnlyDictionary<string, string>? Texts;
        /// <summary>The language tag for dates and times; by default the system language.</summary>
        public string? Locale;
        /// <summary>Plays as a guest when nobody signs in (default true); false starts the sign-in instead.</summary>
        public bool Guests = true;
        /// <summary>Shows the "link a Krtuworks ID" prompt when the SDK says it is time (default true).</summary>
        public bool LinkReminder = true;
        /// <summary>Shows the two saves when the platform's cloud holds a copy that differs from the server's (default true).</summary>
        public bool PlatformSaveConflicts = true;
        /// <summary>How the player signs in; by default from the platform: consoles a code, iOS and Android the system sheet, WebGL none, the rest the system browser.</summary>
        public UiSignInMode? SignInMode;
        /// <summary>Draws the controller hints row ("A Select  B Back"); by default on consoles only.</summary>
        public bool? ControllerHints;
        /// <summary>The game's style sheets, added after the package's: override the <c>--kw-*</c> properties of <c>.kw-root</c> in them.</summary>
        public IReadOnlyList<StyleSheet>? StyleSheets;
        /// <summary>Reads a save's bytes into what its card shows (chapter, play time, a picture).</summary>
        public Func<string, byte[], SaveSummary?>? DescribeSave;
        /// <summary>The ready-made screens' panel sorts above the game's own UI by this much.</summary>
        public float SortingOrder = 10000f;
    }

    public static partial class KrtuworksUnity
    {
        static KrtuworksUi? ui;

        /// <summary>The ready-made screens made by <see cref="CreateUi"/> (null before).</summary>
        public static KrtuworksUi? Ui => ui;

        /// <summary>The ready-made screens were made again: <see cref="KrtuworksSignInButton"/> and <see cref="KrtuworksAccountChip"/> follow.</summary>
        internal static event Action<KrtuworksUi?>? UiChanged;

        /// <summary>
        /// The ready-made screens: <c>await KrtuworksUnity.CreateUi().EnsurePlayerAsync()</c> before the game starts, then
        /// <see cref="KrtuworksSignInButton"/> and <see cref="KrtuworksAccountChip"/> wherever a UI document wants them. Call it after
        /// <see cref="InitializeAsync"/>; a second call replaces the first.
        /// </summary>
        public static KrtuworksUi CreateUi(KrtuworksUnityUiOptions? options = null)
        {
            var client = Client ?? throw new InvalidOperationException("Call KrtuworksUnity.InitializeAsync first");
            var o = options ?? new KrtuworksUnityUiOptions();
            var texts = new UiTexts(o.Texts);
            var console = IsConsole(Application.platform);
            // A console has no browser to open.
            Action<string>? open = console ? (Action<string>?)null : Application.OpenURL;
            var mode = o.SignInMode ?? ModeFor(Application.platform);
            var host = new KrtuworksUiHost(texts, o.ControllerHints ?? console, open, o.StyleSheets, o.SortingOrder);
            var created = new KrtuworksUi(client, host, new KrtuworksUiOptions
            {
                Texts = texts,
                Locale = o.Locale ?? Locale() ?? "en",
                Guests = o.Guests,
                LinkReminder = o.LinkReminder,
                PlatformSaveConflicts = o.PlatformSaveConflicts,
                Mode = mode,
                OpenUrl = open,
                Authenticate = mode == UiSignInMode.App ? MobileSignIn.AuthenticateAsync : null,
                DeviceName = Clip(SystemInfo.deviceModel, 60),
                DescribeSave = o.DescribeSave,
            });
            ui?.Dispose();
            ui = created;
            UiChanged?.Invoke(created);
            return created;
        }

        static UiSignInMode ModeFor(RuntimePlatform platform)
        {
            if (IsConsole(platform)) return UiSignInMode.Device;
            switch (platform)
            {
                case RuntimePlatform.IPhonePlayer:
                case RuntimePlatform.Android:
                    return UiSignInMode.App;
                case RuntimePlatform.WebGLPlayer:
                    return UiSignInMode.None;
                default:
                    return UiSignInMode.Browser;
            }
        }
    }
}
