# Changelog

All notable changes to `com.krtuworks.sdk`. Versions follow semantic versioning; a breaking change is marked **Breaking**.

## Unreleased

## 1.0.0 - 2026-11-15

First release. Unity 2022.3 LTS or later.

### Added
- `KrtuworksUnity.InitializeAsync` with `Resources/krtuworks-config.json`; TEST in the Editor and development builds, LIVE in release builds; the app's type.
- Guests with a device key, Krtuworks ID sign-in on desktops (system browser, loopback return), phones (iOS ASWebAuthenticationSession, Android browser and the app's scheme) and consoles (a code to enter at id.krtuworks.com/link).
- Secrets in the platform's secure storage: Windows DPAPI, macOS Keychain, iOS Keychain (this device only), Android Keystore with a no-backup folder, Linux and Steam Deck sealed to the machine.
- Steam (Steamworks.NET adapter): the Steam account's player, each session's licence check, achievements both ways with the Steam accounts the player allowed: the session's Steam account (`PlatformLink`), `PlatformLinkNotice` for a second account waiting for the player's answer (`AnswerPlatformConsentAsync`), a third one or another Krtuworks ID's.
- Steam stats mirror the server (`IPlatformStats`): stats with a Steam API name get the server's value, never an increase; the launch's sync imports the player's existing Steam values where the stat's kind allows; values are stored on Steam at most every 2 minutes and on pause or exit. Only with the session's Steam account allowed; achievements unlock on Steam only then too.
- Steam Cloud mirrors the slots the console marks for it (`IPlatformCloud`; SAVE, PROFILE and SETTINGS, never DEVICE_SETTINGS): the server is the record and Steam holds each slot's current version as `krtuworks-<slot>.bin`; whichever side moved alone goes to the other; SETTINGS take the latest; a copy nothing can order waits for the player (`PlatformSaveConflict`, then `ResolveFileAsync`); a slot Steam has no room for is left alone. `SyncPlatformFilesAsync` lines them up on demand. Only with the session's Steam account allowed and Steam Cloud on.
- Leaderboards (the `leaderboards` module): `SubmitScoreAsync(key, value, details)` goes through the same durable progress queue as stats (frozen op id, resent unchanged after a lost answer, kept while the module is off); waiting scores fold once the board's rules are known (KEEP_BEST keeps the better, FORCE_UPDATE the last). `GetLeaderboardsAsync` (own score and rank) and `GetLeaderboardEntriesAsync` (global or around the player). With Steam (`IPlatformLeaderboards`) the player's own Steam row of a board that takes imports is brought in once per board and Steam account as a score; the SDK only reads Steam leaderboards, the server writes them.
- Steam rich presence (`IPlatformPresence`, games only): `SetPresenceAsync(key, parameters)` shows one of the states the app declares (read with an ETag and kept per player), filling LEVEL (the open attempt's level) and SESSION_MINUTES itself and writing them again at each level start and heartbeat; the result says why nothing was shown (`PresenceResult.Reason`) and it never throws for that. `ClearPresence`, `SetPresenceGroup(groupId, size)`, and the player's own switch `SetPresenceVisibleAsync` / `PresenceVisible` (hiding clears Steam at once and keeps the wanted state). Cleared when the player changes.
- DLC ownership: `Entitlements`, `RefreshEntitlementsAsync()` and `OwnsDlc(key)` (the DLC's product key). The server's answer for this launch comes first (its check of the session's Steam ticket); when the server could not check, the Steam client's own answer (`IPlatformOwnership`, Steamworks `SteamApps.BIsSubscribedApp` for the DLC's AppID); without that, the last server answer kept on the device per player and environment.
- Sessions, events, levels and deaths for games, stats and achievements kept on the device until the server has them, cloud saves with conflict handling, remote config, modules, A/B tests, player profile card.
- Bug reports with a screenshot (`CaptureScreenshotAsync`) and the "fixed" notice; profile picture reports.
- A guest's account deletion, its cancellation and data copy from inside the game.
- The validator (`KrtuworksUnity.ValidateAsync`) for the release report.
