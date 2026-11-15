# Krtuworks SDK for Unity: instructions for coding agents

`com.krtuworks.sdk` connects a Unity game or app (Unity 2022.3 LTS or later, Unity 6 included) to Krtuworks: Krtuworks ID sign-in (guest, desktop browser, phone, console code, Steam), sessions, events, stats and achievements, cloud saves, remote config and A/B tests.

## Before you start
- Check the project folder. A `*.uproject` file means Unreal: use the Krtuworks Unreal plugin instead. `ProjectSettings/ProjectVersion.txt` means Unity: this package.
- Get the app's `krtuworks-config.json` from the partner console (the app's Credentials page) or from the Krtuworks MCP tool `get_setup`. Never write it by hand and never guess an id in it. It holds no secret, so it may be committed.

## Required steps
1. Add the package `com.krtuworks.sdk`: in Package Manager, Add package from git URL, `https://github.com/krtuworks/krtuworks-sdk-unity.git#<version>`, or run `openupm add com.krtuworks.sdk@<version>` (`<version>` is the SDK version to use). It brings `com.unity.nuget.newtonsoft-json` 3.2.1.
2. Save the config as `Assets/Resources/krtuworks-config.json`. A build stops when the file is missing or its `appId` or `slug` is empty.
3. Call `await KrtuworksUnity.InitializeAsync()` once when the game starts (namespace `Krtuworks.Unity`), then use `KrtuworksUnity.Client` anywhere.
   - The Editor and development builds use TEST, release builds LIVE. Test players and their data never reach LIVE. Set `KrtuworksUnityOptions.Environment` only to override this.
   - The build label is `Application.version`: give each released build its own version.
4. Sign the player in:
   - A guest needs no screen: `await KrtuworksUnity.Client.SignInAsGuestAsync(birthDate)` once, after the game's age screen (birth date `YYYY-MM-DD`, 13 or older, with the Krtuworks ID Terms and Privacy Notice). The same device signs in again with no input.
   - `await KrtuworksUnity.SignInAsync()` opens Krtuworks ID the platform's way (desktop: system browser and a loopback return; iOS and Android: the system's sign-in session and the app's own scheme `com.krtuworks.<slug>:/callback`). A guest who signs in keeps their progress.
   - Consoles: `StartDeviceLinkAsync()`, show its `UserCode` and `VerificationUri`, then `FinishDeviceLinkAsync(link)`.
   - WebGL has no sign-in screen in Unity: use the JavaScript package on the page. Guests work.
5. For Steam: add Steamworks.NET (the UPM package switches the adapter on by itself; with a `.unitypackage` add the scripting define `KRTU_STEAMWORKS`), start Steam and call `SteamAPI.RunCallbacks()` every frame as usual, and pass the adapter:
   ```csharp
   await KrtuworksUnity.InitializeAsync(new KrtuworksUnityOptions { PlatformServices = new Krtuworks.Steamworks.SteamworksPlatformServices() });
   ```
   While Steam sign-in is not yet open for the game (Krtuworks answers `steam_not_configured`), the SDK falls back to a device guest by itself. Do not treat it as an error.

## Rules
- Do not use `ConfigureAwait(false)` around SDK calls: Unity's HTTP runs on the main thread.
- Events: `Track(type, data)` with a ready-made family (`screen_view`, `economy`, `item`, `performance`, `settings`, `error` …) or a type defined on the console. It returns `false` when nothing was kept (the module is off, statistics are switched off, or the player opted out); that is not an error.
- Stats and achievements: `IncrementStatAsync`, `SetStatAsync`, `UnlockAchievementAsync`. They are kept on the device until the server has them and are never counted twice: do not add your own retry. With Steam, achievements also go to Steam, and ones support took back are cleared there.
- Leaderboards: `SubmitScoreAsync(key, value, details)` (a whole number below 10^15, details 1 to 64 bytes) is kept on the device until the server has it; the board decides what it does (KEEP_BEST keeps the better score, FORCE_UPDATE takes it, a score outside the board's range is dropped). Read with `GetLeaderboardsAsync` (own score and rank) and `GetLeaderboardEntriesAsync(key, "global" | "around", limit, offset)`; a row without a username shows as `DisplayName` "Player". Reads fail with `module_disabled` while the leaderboards module is off; scores wait. With Steam the player's own Steam row is imported once automatically; never write scores to Steam yourself.
- Steam stats: with Steam, stats mirror the server. The SDK sets the Steam stat to the server's value; never write Steam stats yourself. A stat is mirrored only when its Steam API name is set on the partner console.
- Rich presence (Steam, games only): `await SetPresenceAsync("in_level", new Dictionary<string, object> { ["MAP"] = "dust" })` shows one of the states the app declares on the console (key, display token, parameter names); the SDK checks it, fills LEVEL (the level in play) and SESSION_MINUTES itself when you give none, and writes Steam. It never throws for the reasons in `PresenceResult.Reason`: `no_platform`, `not_game`, `module_disabled`, `hidden` (the player hides their presence; the state is kept and shows when they allow it), `unknown_state`, `param_missing` (a declared parameter has no value), `states_unknown` (the states were never read on this device; try again later) and `platform_rejected`. `ClearPresence()` clears it; `SetPresenceGroup(groupId, size)` sets the party shown with every state (null clears it). Give the player a switch: `SetPresenceVisibleAsync(bool)` (current choice in `PresenceVisible`). Never write Steam rich presence yourself; the SDK clears and rewrites all its keys.
- DLC ownership (games only): `KrtuworksUnity.Client.OwnsDlc("soundtrack")` takes the DLC's product key from the console and answers at once. The server's answer for this launch wins; when it could not check, the Steam client's own answer (the Steamworks adapter asks `SteamApps.BIsSubscribedApp` for the DLC's AppID); else the last server answer kept on the device. `KrtuworksUnity.Client.Entitlements` lists the licences (`Sku` `base` first; `Status` `ACTIVE`, `REVOKED` or null; `Checked` for what the server confirmed this launch) and `await KrtuworksUnity.Client.RefreshEntitlementsAsync()` asks the server now. Do not call `BIsSubscribedApp` or keep your own ownership flags.
- Steam accounts: progress mirrors only with Steam accounts the player allowed. The first Steam account a game sees with a Krtuworks ID is used without asking. Subscribe to `PlatformLinkNotice` and show your own dialog for its `State`: `PENDING_CONSENT` (a second Steam account: "Also show your progress on this Steam account?", then `AnswerPlatformConsentAsync(allow)`), `LIMIT` (a game uses at most 2 Steam accounts; the player removes one at id.krtuworks.com/apps), `MISMATCH` (this Steam account belongs to another Krtuworks ID; nothing is mirrored to it). Do not sync achievements yourself around it.
- Levels and deaths (`LevelStartAsync`, `LevelComplete`, `Death` …) are for games only; in an app they do nothing.
- Cloud saves: `SaveFileAsync(slot, bytes, baseVersion)` and `ReadFileAsync(slot)`. A conflict (`file_conflict`) carries the current version: let the player choose, do not overwrite.
- Steam Cloud: with the Steam adapter, the slots the console marks "mirror to Steam Cloud" are copied there as `krtuworks-<slot>.bin` by the SDK itself; keep Steam Auto-Cloud away from those files and do not write them yourself. When Steam's copy and the server's differ and nothing tells which is newer, `PlatformSaveConflict` fires: show both to the player and call `ResolveFileAsync(slot, keep, conflict.Server.Id)` with the chosen version's id. Nothing in that slot moves until then. The SDK lines Steam Cloud up by itself when a mirrored session starts and after each save of a mirrored slot; call `SyncPlatformFilesAsync()` to do it on demand (for example after the player changes the Steam Cloud choice). It never throws, and does nothing unless the session's Steam account is mirrored for this player and the cloudSave module is on.
- The SDK keeps its data under `Application.persistentDataPath/krtuworks` and its secrets in the platform's secure storage. Do not move, copy or back up these files: a copied secret is refused on another device, and the SDK starts as a new device.
- Never put a server key or any other Krtuworks secret into game code. This package needs none.

## Verify
In Play mode or a development build:
```csharp
var report = await KrtuworksUnity.ValidateAsync();
```
It checks the config, the build label and the platform, then makes a TEST round (sign-in, session, event, remote config, cloud save, progress, bug report) and logs each check to the Console. `report.Ok` must be `true`. Each check is `PASSED`, `FAILED` (with `Detail`) or `SKIPPED` (a module that is off, an app with no save slots or no achievements). A build whose report is not green is not released in LIVE.
