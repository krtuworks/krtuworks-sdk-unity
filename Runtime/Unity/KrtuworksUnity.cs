#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Krtuworks.Unity
{
    /// <summary>What a game may set before <see cref="KrtuworksUnity.InitializeAsync"/>; everything has a default.</summary>
    public sealed class KrtuworksUnityOptions
    {
        /// <summary>TEST or LIVE; by default TEST in the Editor and development builds, LIVE in release builds.</summary>
        public string? Environment;
        /// <summary>The build label sent with every call; by default <c>Application.version</c>.</summary>
        public string? Build;
        /// <summary>The Steam adapter (Krtuworks.Sdk.Steamworks) or another platform's; the platform is then STEAM.</summary>
        public IPlatformServices? PlatformServices;
        /// <summary>Opens a session and keeps it alive. Default true.</summary>
        public bool Sessions = true;
        /// <summary>The app's krtuworks-config.json; by default the TextAsset <c>Resources/krtuworks-config</c>.</summary>
        public string? ConfigJson;
    }

    /// <summary>
    /// The SDK in Unity: one call makes it ready with the engine's HTTP, a file store under
    /// <c>Application.persistentDataPath</c>, the platform's secret storage and a hidden object that follows
    /// the app's life (pause, quit, network). <c>await KrtuworksUnity.InitializeAsync();</c> then
    /// <see cref="Client"/> anywhere.
    /// </summary>
    public static partial class KrtuworksUnity
    {
        static Task<KrtuworksClient>? starting;

        /// <summary>The SDK once initialized (null before).</summary>
        public static KrtuworksClient? Client { get; private set; }

        public static Task<KrtuworksClient> InitializeAsync(KrtuworksUnityOptions? options = null) => starting ??= Start(options ?? new KrtuworksUnityOptions());

        static bool Web => Application.platform == RuntimePlatform.WebGLPlayer;

        static string? ConfigJson(KrtuworksUnityOptions options) => options.ConfigJson ?? Resources.Load<TextAsset>("krtuworks-config")?.text;

        /// <summary>The SDK's options for this game in an environment: the engine's HTTP, stores under persistentDataPath, the platform's secrets.</summary>
        static KrtuworksOptions OptionsFor(KrtuworksUnityOptions options, KrtuworksAppConfig config, string environment)
        {
            var root = Path.Combine(Application.persistentDataPath, "krtuworks");
#if UNITY_IOS && !UNITY_EDITOR
            // UnityEngine.iOS exists only in an iOS player build: anywhere else this line would not compile
            UnityEngine.iOS.Device.SetNoBackupFlag(root);
#endif
            config.Environments.TryGetValue(environment, out var clients);
            return new KrtuworksOptions
            {
                Config = config,
                Environment = environment,
                Platform = PlatformName(options.PlatformServices),
                Build = options.Build ?? Application.version,
                // A WebGL build is a web page: it signs players in through the app's web client.
                ClientId = Web ? clients?.WebClientId : null,
                Store = new FileStore(Path.Combine(root, "store"), offThread: !Web),
                Secrets = SecretStores.For(root, config.AppId),
                Http = new UnityHttpTransport(),
                PlatformServices = options.PlatformServices,
                Device = DeviceDescription(),
                Locale = Locale(),
                Sessions = options.Sessions,
                Delay = KrtuworksRunner.Delay,
            };
        }

        static async Task<KrtuworksClient> Start(KrtuworksUnityOptions options)
        {
            var configJson = ConfigJson(options) ?? throw new InvalidOperationException(MissingConfig);
            var config = KrtuworksAppConfig.FromJson(configJson);
            var runner = KrtuworksRunner.Create();
            var client = await KrtuworksClient.InitializeAsync(OptionsFor(options, config, options.Environment ?? (Debug.isDebugBuild ? "TEST" : "LIVE")));
            runner.Attach(client);
            Client = client;
            var launchedBy = Application.absoluteURL;
            if (!string.IsNullOrEmpty(launchedBy) && launchedBy.StartsWith(client.AppScheme + ":", StringComparison.Ordinal)) _ = FinishFromLaunch(client, launchedBy);
            return client;
        }

        const string MissingConfig = "Put the app's krtuworks-config.json in a Resources folder (Assets/Resources/krtuworks-config.json)";

        /// <summary>
        /// The Krtuworks validator in one call (Play mode or a development build): checks this build's setup
        /// (krtuworks-config.json, the platform, Steam's services), then runs a TEST round against the servers
        /// with the game's own config, platform, build and stores, and logs each check. The build's release
        /// report on the partner console turns green with it; a red build is not released in LIVE.
        /// </summary>
        public static async Task<ValidationReport> ValidateAsync(KrtuworksUnityOptions? options = null)
        {
            var o = options ?? new KrtuworksUnityOptions();
            KrtuworksRunner.Create();
            var configJson = ConfigJson(o);
            ValidationReport report;
            if (configJson == null)
            {
                Debug.LogError($"Krtuworks validator: {MissingConfig}");
                // A null config is told as missing; nothing is sent.
                report = await KrtuworksValidator.ValidateAsync(new KrtuworksOptions { Config = null!, Platform = PlatformName(o.PlatformServices), Build = o.Build ?? Application.version, PlatformServices = o.PlatformServices }, Web);
            }
            else report = await KrtuworksValidator.ValidateAsync(OptionsFor(o, KrtuworksAppConfig.FromJson(configJson), "TEST"), Web);
            foreach (var check in report.Checks)
            {
                var line = $"Krtuworks validator {report.Build}: {check.Status} {check.Key} ({check.Title}){(check.Detail != null ? $": {check.Detail}" : "")}";
                if (check.Status == "FAILED") Debug.LogError(line);
                else if (check.Status == "SKIPPED") Debug.LogWarning(line);
                else Debug.Log(line);
            }
            Debug.Log(report.Ok ? $"Krtuworks validator: {report.Build} passed." : $"Krtuworks validator: {report.Build} has failed checks.");
            return report;
        }

        /// <summary>The system closed the app while the browser was open; the sign-in's return address started it again.</summary>
        static async Task FinishFromLaunch(KrtuworksClient client, string url)
        {
            try { await client.CompleteSignInAsync(url); }
            catch (KrtuworksException) { /* an old or foreign address: the player signs in again */ }
        }

        /// <summary>
        /// A picture of the screen as the player sees it now (a JPEG, quality 1-100), for a bug report's
        /// <see cref="BugReportInput.Screenshot"/>; take it before opening the report form, and let the player
        /// leave it out.
        /// </summary>
        public static Task<byte[]> CaptureScreenshotAsync(int quality = 75) => KrtuworksRunner.Create().CaptureFrame(Math.Max(1, Math.Min(100, quality)));

        /// <summary>
        /// Signs the player in with Krtuworks ID the way the platform does it: the system browser with a
        /// loopback return on desktops, the platform's authentication session on phones (iOS
        /// ASWebAuthenticationSession; Android the browser and the app's scheme). A console has no browser and throws
        /// <see cref="NotSupportedException"/>: it signs in with a code, through <see cref="KrtuworksClient.StartDeviceLinkAsync"/> or the
        /// ready-made screens (<see cref="CreateUi"/>). A guest playing here is carried along.
        /// </summary>
        public static Task<Player> SignInAsync(CancellationToken cancel = default)
        {
            var client = Client ?? throw new InvalidOperationException("Call KrtuworksUnity.InitializeAsync first");
            switch (Application.platform)
            {
                case RuntimePlatform.IPhonePlayer:
                case RuntimePlatform.Android:
                    return client.SignInWithAppAsync(MobileSignIn.AuthenticateAsync, cancel);
                case RuntimePlatform.WebGLPlayer:
                    throw new NotSupportedException("A WebGL build signs in with the JS SDK (@krtuworks/sdk) on its page; guests play here.");
                case RuntimePlatform.PS4:
                case RuntimePlatform.PS5:
                case RuntimePlatform.XboxOne:
                case RuntimePlatform.GameCoreXboxOne:
                case RuntimePlatform.GameCoreXboxSeries:
                case RuntimePlatform.Switch:
                    throw new NotSupportedException("A console has no web browser: sign in with a code on another device through KrtuworksClient.StartDeviceLinkAsync, or use the ready-made screens (KrtuworksUnity.CreateUi).");
                default:
                    return client.SignInWithBrowserAsync(Application.OpenURL, cancel);
            }
        }

        /// <summary>Consoles have no web browser: they sign in with a code on another device.</summary>
        static bool IsConsole(RuntimePlatform platform)
        {
            switch (platform)
            {
                case RuntimePlatform.PS4:
                case RuntimePlatform.PS5:
                case RuntimePlatform.XboxOne:
                case RuntimePlatform.GameCoreXboxOne:
                case RuntimePlatform.GameCoreXboxSeries:
                case RuntimePlatform.Switch:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>STEAM when a platform adapter is given, otherwise the platform the build runs on.</summary>
        static string PlatformName(IPlatformServices? services)
        {
            if (services != null) return "STEAM";
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                    return "WINDOWS";
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor:
                    return "MACOS";
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxEditor:
                    return "LINUX";
                case RuntimePlatform.IPhonePlayer:
                    return "IOS";
                case RuntimePlatform.Android:
                    return "ANDROID";
                case RuntimePlatform.WebGLPlayer:
                    return "WEB";
                case RuntimePlatform.PS4:
                case RuntimePlatform.PS5:
                    return "PLAYSTATION";
                case RuntimePlatform.XboxOne:
                case RuntimePlatform.GameCoreXboxOne:
                case RuntimePlatform.GameCoreXboxSeries:
                    return "XBOX";
                case RuntimePlatform.Switch:
                    return "NINTENDO";
                default:
                    return "WINDOWS";
            }
        }

        static string? Clip(string? text, int max) => string.IsNullOrWhiteSpace(text) ? null : text!.Trim().Length <= max ? text.Trim() : text.Trim().Substring(0, max);

        static string OsName()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.IPhonePlayer: return "iOS";
                case RuntimePlatform.Android: return "Android";
                case RuntimePlatform.WebGLPlayer: return "Web";
                case RuntimePlatform.PS4:
                case RuntimePlatform.PS5: return "PlayStation";
                case RuntimePlatform.XboxOne:
                case RuntimePlatform.GameCoreXboxOne:
                case RuntimePlatform.GameCoreXboxSeries: return "Xbox";
                case RuntimePlatform.Switch: return "Nintendo Switch";
            }
            switch (SystemInfo.operatingSystemFamily)
            {
                case OperatingSystemFamily.Windows: return "Windows";
                case OperatingSystemFamily.MacOSX: return "macOS";
                case OperatingSystemFamily.Linux: return "Linux";
                default: return "Other";
            }
        }

        static DeviceInfo DeviceDescription() => new DeviceInfo
        {
            Os = OsName(),
            OsVersion = Clip(SystemInfo.operatingSystem, 40),
            Model = Clip(SystemInfo.deviceModel, 120),
            Gpu = Clip(SystemInfo.graphicsDeviceName, 120),
            RamMb = SystemInfo.systemMemorySize > 0 ? SystemInfo.systemMemorySize : (int?)null,
            ScreenWidth = Screen.currentResolution.width > 0 ? Screen.currentResolution.width : (int?)null,
            ScreenHeight = Screen.currentResolution.height > 0 ? Screen.currentResolution.height : (int?)null,
        };

        /// <summary>The player's language as a tag (`tr`, `pt`, `zh-Hans`), from the system language.</summary>
        static string? Locale()
        {
            switch (Application.systemLanguage)
            {
                case SystemLanguage.Afrikaans: return "af";
                case SystemLanguage.Arabic: return "ar";
                case SystemLanguage.Basque: return "eu";
                case SystemLanguage.Belarusian: return "be";
                case SystemLanguage.Bulgarian: return "bg";
                case SystemLanguage.Catalan: return "ca";
                case SystemLanguage.ChineseSimplified: return "zh-Hans";
                case SystemLanguage.ChineseTraditional: return "zh-Hant";
                case SystemLanguage.Chinese: return "zh";
                case SystemLanguage.Czech: return "cs";
                case SystemLanguage.Danish: return "da";
                case SystemLanguage.Dutch: return "nl";
                case SystemLanguage.English: return "en";
                case SystemLanguage.Estonian: return "et";
                case SystemLanguage.Faroese: return "fo";
                case SystemLanguage.Finnish: return "fi";
                case SystemLanguage.French: return "fr";
                case SystemLanguage.German: return "de";
                case SystemLanguage.Greek: return "el";
                case SystemLanguage.Hebrew: return "he";
                case SystemLanguage.Hungarian: return "hu";
                case SystemLanguage.Icelandic: return "is";
                case SystemLanguage.Indonesian: return "id";
                case SystemLanguage.Italian: return "it";
                case SystemLanguage.Japanese: return "ja";
                case SystemLanguage.Korean: return "ko";
                case SystemLanguage.Latvian: return "lv";
                case SystemLanguage.Lithuanian: return "lt";
                case SystemLanguage.Norwegian: return "no";
                case SystemLanguage.Polish: return "pl";
                case SystemLanguage.Portuguese: return "pt";
                case SystemLanguage.Romanian: return "ro";
                case SystemLanguage.Russian: return "ru";
                case SystemLanguage.SerboCroatian: return "sh";
                case SystemLanguage.Slovak: return "sk";
                case SystemLanguage.Slovenian: return "sl";
                case SystemLanguage.Spanish: return "es";
                case SystemLanguage.Swedish: return "sv";
                case SystemLanguage.Thai: return "th";
                case SystemLanguage.Turkish: return "tr";
                case SystemLanguage.Ukrainian: return "uk";
                case SystemLanguage.Vietnamese: return "vi";
                default: return null;
            }
        }
    }
}
