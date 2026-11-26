#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Krtuworks.Internal;

namespace Krtuworks
{
    /// <summary>PASSED, FAILED or SKIPPED.</summary>
    public sealed class ValidationCheck
    {
        public string Key = "";
        public string Title = "";
        public string Status = "";
        /// <summary>Why it failed or was skipped.</summary>
        public string? Detail;
    }

    public sealed class ValidationReport
    {
        /// <summary>No check failed.</summary>
        public bool Ok;
        public string Build = "";
        public List<ValidationCheck> Checks = new List<ValidationCheck>();
    }

    /// <summary>
    /// The Krtuworks validator, the rules of krtuworks-sdk-js/src/validator.ts: one call
    /// checks the app's setup on this machine, then runs a TEST round against the servers the way the build's
    /// release report counts it (the server's release report): a guest signs in, a session opens, sends
    /// a heartbeat and ends, an event batch, a save written and read back, a progress operation, a test bug
    /// report, the remote config read. Every call names the build, so this run turns its report green.
    ///
    /// The run uses the options' own stores: on this machine the same TEST guest comes back run after run (new
    /// guests are limited per address and day). A new guest is an adult that accepts the current terms. The
    /// save goes to the app's first save slot in TEST (`saveSlot` names another) as a new version; a run right
    /// after another waits out the slot's write limit (one write per 10 seconds) and saves again.
    /// </summary>
    public static class KrtuworksValidator
    {
        static readonly Regex AppId = new Regex("^app_[0-9a-z]{20}$");
        static readonly Regex Slug = new Regex("^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$");
        static readonly Regex BuildLabel = new Regex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$");
        static readonly string[] Types = { "GAME", "MOBILE_APP", "WEB_APP", "DESKTOP_APP" };
        static readonly string[] Platforms = { "STEAM", "EPIC", "XBOX", "PLAYSTATION", "NINTENDO", "IOS", "ANDROID", "WEB", "WINDOWS", "MACOS", "LINUX" };
        /// <summary>Consoles sign in with a code shown on the TV: the device client of each environment.</summary>
        static readonly string[] Consoles = { "XBOX", "PLAYSTATION", "NINTENDO" };
        const int Sends = 5;
        /// <summary>The server takes one write per slot per 10 s; the validator sits out a wait of at most WriteWaitSeconds.</summary>
        const double WriteLimitSeconds = 10;
        const double WriteWaitSeconds = 15;
        /// <summary>How many half seconds the save sent again by the SDK is waited for.</summary>
        const int SentAgainChecks = 20;

        /// <summary>The event the validator sends: the first family whose module is on.</summary>
        static readonly (string Module, string Type, object Data)[] Probes =
        {
            ("ui", "screen_view", new { screen = "krtuworks-validator" }),
            ("performance", "performance", new { fps = 60 }),
            ("settings", "settings", new { language = "en" }),
            ("errors", "error", new { kind = "other", message = "Krtuworks validator", handled = true }),
            ("economy", "balance", new { currency = "validator", amount = 0 }),
            ("items", "interaction", new { kind = "other", name = "krtuworks-validator" }),
            ("progression", "progression", new { action = "checkpoint", level = "krtuworks-validator", checkpoint = "validator" }),
            ("combat", "damage", new { amount = 0 }),
        };

        static readonly (string Key, string Title)[] Titles =
        {
            ("config", "krtuworks-config.json names the app and its sign-in clients"),
            ("build", "The build is a version label the servers take"),
            ("platform", "The platform and its services are set"),
            ("sign_in", "Signed in as a TEST player"),
            ("session", "Opened a session, sent a heartbeat and ended it"),
            ("events", "An event batch was accepted"),
            ("remote_config", "Read the remote config"),
            ("cloud_save", "Wrote a save and read it back"),
            ("progress", "Sent a progress operation"),
            ("bug_report", "Sent a test bug report"),
            ("sentry", "A test error reached Sentry"),
        };
        static readonly string[] Run = { "sign_in", "session", "events", "remote_config", "cloud_save", "progress", "bug_report" };

        static ValidationCheck Check(string key, string status, string? detail = null) =>
            new ValidationCheck { Key = key, Title = Titles.First(t => t.Key == key).Title, Status = status, Detail = detail };

        /// <summary>What is wrong with krtuworks-config.json for this platform (a web build reads the web client), if anything.</summary>
        public static List<string> ConfigProblems(KrtuworksAppConfig? config, string platform, bool web)
        {
            if (config == null) return new List<string> { "the config is missing" };
            var problems = new List<string>();
            if (!AppId.IsMatch(config.AppId ?? "")) problems.Add("appId is not an app_ id");
            if (!Slug.IsMatch(config.Slug ?? "")) problems.Add("slug is not the app's address name");
            if (config.Type != null && !Types.Contains(config.Type)) problems.Add($"type is one of {string.Join(", ", Types)}");
            var wanted = new List<(string Field, string Kind)> { web ? ("webClientId", "web") : ("clientId", "native") };
            if (Consoles.Contains(platform)) wanted.Add(("deviceClientId", "device"));
            foreach (var env in new[] { "TEST", "LIVE" })
            {
                config.Environments.TryGetValue(env, out var clients);
                foreach (var (field, kind) in wanted)
                {
                    var id = field == "webClientId" ? clients?.WebClientId : field == "deviceClientId" ? clients?.DeviceClientId : clients?.ClientId;
                    var want = $"{config.Slug}.{env.ToLowerInvariant()}.{kind}";
                    if (string.IsNullOrEmpty(id)) problems.Add($"environments.{env} has no {field}");
                    else if (id != want) problems.Add($"environments.{env}.{field} is {id}, not {want}");
                }
            }
            return problems;
        }

        /// <summary>What is wrong with the platform, if anything: a known one, and Steam with its Steamworks services.</summary>
        public static List<string> PlatformProblems(string platform, bool hasPlatformServices)
        {
            var problems = new List<string>();
            if (!Platforms.Contains(platform)) problems.Add($"platform is one of {string.Join(", ", Platforms)}");
            if (platform == "STEAM" && !hasPlatformServices) problems.Add("STEAM needs the Steamworks services (PlatformServices) for its ticket and achievements");
            return problems;
        }

        static string Why(Exception err) => err is KrtuworksException k ? $"{k.Code}: {k.Message}" : err.Message;

        /// <summary>
        /// Sends until nothing waits (another send may be running when this starts) and adds up what came back,
        /// with what still waits at the end: the game's own SDK on the same store may have sent the validator's
        /// item first, so "left this device unrefused" counts as sent.
        /// </summary>
        static async Task<(SendResult Sent, (int Events, int Progress, int BugReports) Left)> SendAll(KrtuworksClient krtu)
        {
            var total = new SendResult();
            var left = (Events: 0, Progress: 0, BugReports: 0);
            for (var round = 0; round < Sends; round += 1)
            {
                var r = await krtu.FlushAsync();
                total.Progress.Applied += r.Progress.Applied;
                total.Progress.StoppedBy = r.Progress.StoppedBy;
                total.Bugs.Sent += r.Bugs.Sent;
                total.Bugs.Dropped.AddRange(r.Bugs.Dropped);
                total.Bugs.StoppedBy = r.Bugs.StoppedBy;
                total.Events.Sent += r.Events.Sent;
                total.Events.Rejected.AddRange(r.Events.Rejected);
                total.Events.StoppedBy = r.Events.StoppedBy;
                left = await krtu.WaitingAsync();
                if (left.Events == 0 && left.Progress == 0 && left.BugReports == 0) break;
                if (r.Progress.StoppedBy != null || r.Bugs.StoppedBy != null || r.Events.StoppedBy != null) break;
            }
            return (total, left);
        }

        /// <summary>A send's verdict: sent, or left this device with no refusal (null); otherwise why it was not.</summary>
        internal static string? SentOrWhy(int sent, int waiting, string? stoppedBy) =>
            sent > 0 || (waiting == 0 && stoppedBy == null) ? null : stoppedBy ?? "not sent";

        /// <summary>
        /// Checks this app's setup and runs a TEST round against the servers; never throws. `options` are the
        /// game's own (its config, platform, build, stores, transport); the run is always in TEST. `web` is a
        /// WebGL build (it signs in with the web client).
        /// </summary>
        public static async Task<ValidationReport> ValidateAsync(KrtuworksOptions options, bool web = false, string? saveSlot = null, string? birthDate = null, string? acceptedTerms = null)
        {
            var build = options.Build ?? "";
            var results = new Dictionary<string, ValidationCheck>();
            ValidationReport Report()
            {
                var checks = Titles.Select(t => results.TryGetValue(t.Key, out var c) ? c : Check(t.Key, "SKIPPED", "Not reached.")).ToList();
                return new ValidationReport { Ok = checks.All(c => c.Status != "FAILED"), Build = build, Checks = checks };
            }
            var config = ConfigProblems(options.Config, options.Platform, web);
            results["config"] = Check("config", config.Count > 0 ? "FAILED" : "PASSED", config.Count > 0 ? string.Join("; ", config) : null);
            var buildOk = BuildLabel.IsMatch(build);
            results["build"] = Check("build", buildOk ? "PASSED" : "FAILED", buildOk ? null : "Build is the app version, like 1.4.2 or 1.4.2+381 (letters, digits, . _ + -, at most 64)");
            var platform = PlatformProblems(options.Platform, options.PlatformServices != null);
            results["platform"] = Check("platform", platform.Count > 0 ? "FAILED" : "PASSED", platform.Count > 0 ? string.Join("; ", platform) : null);
            results["sentry"] = Check("sentry", "SKIPPED", "Sentry is not set up yet.");
            if (config.Count > 0 || !buildOk)
            {
                foreach (var key in Run) results[key] = Check(key, "SKIPPED", "Fix the config and the build first.");
                return Report();
            }

            KrtuworksClient? krtu = null;
            try
            {
                krtu = await KrtuworksClient.InitializeAsync(new KrtuworksOptions
                {
                    Config = options.Config,
                    Environment = "TEST",
                    Platform = options.Platform,
                    Build = build,
                    ClientId = web ? options.Config.Environments["TEST"].WebClientId : options.ClientId,
                    Store = options.Store,
                    Secrets = options.Secrets,
                    Http = options.Http,
                    PlatformServices = options.PlatformServices,
                    Device = options.Device,
                    Locale = options.Locale,
                    Sessions = false,
                    IdOrigin = options.IdOrigin,
                    DevOrigin = options.DevOrigin,
                    Delay = options.Delay,
                });
                var k = krtu;
                try
                {
                    if (k.Player == null) await k.SignInAsGuestAsync(birthDate ?? "2000-01-01", acceptedTerms ?? KrtuworksClient.CurrentTerms);
                    await k.ReadyAsync();
                    results["sign_in"] = Check("sign_in", "PASSED");
                }
                catch (Exception err)
                {
                    results["sign_in"] = Check("sign_in", "FAILED", Why(err));
                    foreach (var key in Run.Skip(1)) results[key] = Check(key, "SKIPPED", "No TEST player could sign in.");
                    return Report();
                }

                async Task Step(string key, Func<Task<ValidationCheck?>> body)
                {
                    try { results[key] = await body() ?? Check(key, "PASSED"); }
                    catch (Exception err) { results[key] = Check(key, "FAILED", Why(err)); }
                }

                // The modules the owner left on in TEST; the read that counts for the report comes last, once the build is known there.
                try { await k.RefreshConfigAsync(); }
                catch (KrtuworksException) { /* the kept copy tells the modules */ }
                var modules = k.ModuleStates();
                bool On(string module) => modules.TryGetValue(module, out var on) && on;

                await Step("session", async () =>
                {
                    if (!On("sessions")) return Check("session", "SKIPPED", "The sessions module is off in TEST.");
                    await k.SessionRoundAsync();
                    return null;
                });
                await Step("events", async () =>
                {
                    var index = Array.FindIndex(Probes, p => On(p.Module));
                    if (index < 0) return Check("events", "SKIPPED", "Every telemetry module the validator can use is off in TEST.");
                    if (!k.Track(Probes[index].Type, Probes[index].Data)) return Check("events", "FAILED", "The event was not kept: statistics are off for this player, or the owner switched this build off.");
                    var (all, left) = await SendAll(k);
                    var sent = all.Events;
                    if (sent.Rejected.Count > 0) return Check("events", "FAILED", string.Join(", ", sent.Rejected.Select(r => r.Code)));
                    var not = SentOrWhy(sent.Sent, left.Events, sent.StoppedBy);
                    return not == null ? null : Check("events", "FAILED", not);
                });
                await Step("cloud_save", async () =>
                {
                    if (!On("cloudSave")) return Check("cloud_save", "SKIPPED", "The cloudSave module is off in TEST.");
                    var usable = (await k.ListFilesAsync()).Slots.Where(s => !s.Retired && s.Kind != "DEVICE_SETTINGS").ToList();
                    var slot = saveSlot != null ? usable.FirstOrDefault(s => s.Key == saveSlot) : usable.FirstOrDefault();
                    if (slot == null) return saveSlot != null ? Check("cloud_save", "FAILED", $"The app has no slot {saveSlot} in TEST.") : Check("cloud_save", "SKIPPED", "The app has no save slot in TEST.");
                    var bytes = Encoding.UTF8.GetBytes($"krtuworks-validator {DateTime.UtcNow:O}");
                    var conditional = slot.Kind == "SAVE" || slot.Kind == "PROFILE";
                    try
                    {
                        if (conditional) await k.SaveFileAsync(slot.Key, bytes, slot.File?.Id);
                        else await k.SaveSettingsAsync(slot.Key, bytes);
                    }
                    catch (KrtuworksException err) when (err.Code == "too_many_writes" && (err.RetryAfter ?? WriteLimitSeconds) <= WriteWaitSeconds)
                    {
                        // A slot takes one write per 10 seconds: a run right after another waits. The SDK sends the same save again by itself
                        // when the limit passes (same upload id, same bytes, an older save of this slot replaced); the read back below tells.
                        Func<TimeSpan, CancellationToken, Task> delay = options.Delay ?? Task.Delay;
                        await delay(TimeSpan.FromSeconds((err.RetryAfter ?? WriteLimitSeconds) + 0.25), CancellationToken.None);
                        var player = k.Player?.Subject;
                        for (var i = 0; i < SentAgainChecks && player != null && (await k.WaitingSavesAsync(player)).Any(w => w.Slot == slot.Key); i++)
                            await delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
                    }
                    var back = await k.ReadFileAsync(slot.Key);
                    return back != null && back.Bytes.SequenceEqual(bytes) ? null : Check("cloud_save", "FAILED", $"The save read back from {slot.Key} is not the one written.");
                });
                await Step("progress", async () =>
                {
                    if (!On("achievements")) return Check("progress", "SKIPPED", "The achievements module is off in TEST.");
                    var achievement = (await k.GetProgressAsync()).Achievements.FirstOrDefault(a => !a.Retired);
                    if (achievement == null) return Check("progress", "SKIPPED", "The app has no achievement in TEST.");
                    var before = k.ProgressRefusals.Count;
                    await k.UnlockAchievementAsync(achievement.Key);
                    var (all, left) = await SendAll(k);
                    var refused = k.ProgressRefusals.Skip(before).FirstOrDefault(r => r.Key == achievement.Key);
                    if (refused != null) return Check("progress", "FAILED", refused.Code);
                    var not = SentOrWhy(all.Progress.Applied, left.Progress, all.Progress.StoppedBy);
                    return not == null ? null : Check("progress", "FAILED", not);
                });
                await Step("bug_report", async () =>
                {
                    if (!On("bugReports")) return Check("bug_report", "SKIPPED", "The bugReports module is off in TEST.");
                    var reportId = await k.ReportBugAsync(new BugReportInput { Category = "OTHER", Description = "A test report from the Krtuworks validator." });
                    var (all, left) = await SendAll(k);
                    var dropped = all.Bugs.Dropped.FirstOrDefault(d => d.ReportId == reportId);
                    if (dropped != null) return Check("bug_report", "FAILED", dropped.Code);
                    var not = SentOrWhy(all.Bugs.Sent, left.BugReports, all.Bugs.StoppedBy);
                    return not == null ? null : Check("bug_report", "FAILED", not);
                });
                await Step("remote_config", async () =>
                {
                    await k.RefreshConfigAsync();
                    return null;
                });
            }
            catch (Exception err)
            {
                foreach (var key in Run) if (!results.ContainsKey(key)) results[key] = Check(key, "FAILED", Why(err));
            }
            finally
            {
                if (krtu != null)
                {
                    try { await krtu.ShutdownAsync(); }
                    catch (Exception) { /* the report is what matters */ }
                }
            }
            return Report();
        }
    }
}
