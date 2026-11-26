# Changelog

All notable changes to `com.krtuworks.sdk`. Versions follow semantic versioning; a breaking change is marked **Breaking**.

## Unreleased

## 1.0.8 - 2026-11-26

### Fixed
- A failure of the player's token no longer drops what is queued. Before, a refresh the server refused with `app_suspended` (HTTP 400) made the events, bug-report and save queues treat their own item as refused and delete it. Errors raised while getting or refreshing the token are now told apart from a refusal of the body, and every queue keeps its item and backs off.
- What is queued waits for a refusal that can lift. `app_suspended`, `account_merged`, `account_merging`, `sanctioned`, `account_deletion_pending`, `switched_off` and `module_disabled` keep the event batch, the bug report with its screenshot, the save (a merged account's save stays for `AdoptWaitingSaveAsync` / `DiscardWaitingSaveAsync`) and the progress op, and the queue waits for `Retry-After`, otherwise 15 minutes (the network coming back tries at once). Only a refusal of the body itself drops it. Before, a save refused with one of these codes was deleted.
- A 401 `account_deleted` signs the player out (`IdentityChange` `signed_out`, `Reason` `account_deleted`) and drops that player's queues; before, the queues retried forever.
- A bug report's screenshot answered 409 `app_transferring` or `account_merging` is kept and sent again; before it was deleted although the report was accepted. A stored screenshot that cannot be read back costs only the screenshot, and a failure inside one queue no longer stops the others in the same flush.
- A 200 answer for the remote config that is not a JSON object (a captive portal's page) is a failed fetch: the kept copy stays and nothing is stored. Before, it replaced the kept config with an empty one, which switched every module the owner had turned off back on.
- A failed session open is tried again (after the server's wait, else a growing wait up to 5 minutes, and at once when the network returns); before, the run went on with no session. An open still running for the previous player no longer hands the next player a session of nothing.
- Settings saves (`SaveSettingsAsync`) are written at most once per slot every 30 seconds, the newest content wins, and what waits is written when the app pauses or ends. A save the server rate-limited (`too_many_writes`) is sent again when the limit passes instead of at the next save or launch.
- A save sent again after a lost answer that the server rejected as a conflict now carries the slot's current version in `Details["current"]`, so the choice screen can open.
- `ui.SaveAsync` without a base version writes without a check; before it failed with `file_conflict` on any slot that already had a save. The save choice screen marks the newer save by the server's clock, not the device's.
- The platform mirror (Steam Cloud) works for the player it started for: when another player signs in meanwhile, nothing of that sync is read or written for them.
- A player waiting to be deleted sees "This account is waiting to be deleted" with the account page, not a retry loop.
- A returning Steam player on a new PC signs in without the age screen: the server decides, and the age screen shows only when it says the Steam account is new. A Steam ticket that was got but never sent (the proof or the credentials failed first) is cancelled.
- A guest's age answer the server refuses for good (`too_young`, an invalid date) is no longer kept to be sent at every launch, and accepting newer terms updates a kept request.
- An open level attempt belongs to the player who started it: after a player change `LevelComplete` and `LevelFail` tell nothing under the new player's name.
- Retry-After given as an HTTP date, the age limit of queued events and the screens' "newer" marks use the server's clock, not the device's. An event made while the device clock was wrong is no longer aged out.
- Events the store refused to write are kept in memory for the next write instead of being lost.
- The desktop sign-in's loopback receiver lets go of a connection that sends nothing (a browser's idle pre-connect) after 5 seconds, and cancelling closes it, so the real request behind it is served.
- A sealed secret file store used by several threads on its first use no longer reads one of its two keys before the other exists.
- An iOS Keychain item that could not be deleted is reported (`secret_storage_unavailable`) instead of leaving a token that comes back at the next launch. An Android Keystore key that is permanently unusable is replaced by a new one instead of leaving secret storage unavailable for good.
- `KrtuworksUnity.InitializeAsync` no longer keeps a failed start for good (a missing config), and with Enter Play Mode's domain reload switched off each Play session starts clean instead of returning the last session's client.
- A player who signed out in another process is told about it (`IdentityChange` `signed_out`) when the SDK next needs their token. Sign-in records nobody came back to are removed when another sign-in starts.

### Added
- `IdentityChange` types `merging` and `merging_ended`: told once when the server starts answering a player's calls with 409 `account_merging`, and once when the first call is answered after it. Nothing else changes while it lasts.
- `firstLaunch: "online"` in `krtuworks-config.json`: when there is no player and no kept config and the server cannot be reached at the first launch, `KrtuworksClient.State` is `first_launch_offline` (`StateFirstLaunchOffline`) until a config fetch succeeds; the ready-made screens show a connect screen with Try again. Without the option the first launch stays offline-first (`State` is `ready`).
- `KrtuworksClient.SaveFileAsync(slot, bytes)` saves a SAVE or PROFILE slot without a check against the slot's current version.
- `IPlatformTicketCancel`: a platform adapter that can take back a ticket the SDK got and never sent (the Steam adapter does, with `CancelAuthTicket`).

## 1.0.7 - 2026-11-25

### Fixed
- A bug report carries only its own player's recent events, log lines and frame rate. Before, after a switch to another account in the same run, the new player's report took the previous player's events and log lines too. What was recorded before anyone signed in goes to the first player, like the events queue.
- A different player signed in by another tab or instance sharing the same storage is now taken up like a sign-in here: the identity change is reported and a session opens for that player. Before, the SDK switched to that player silently. A plain token refresh by another tab stays silent.
- The events and log lines a bug report attaches are now limited in bytes, not characters, so a report in Chinese, Japanese or other multi-byte text stays under the server's size limit. Before, such a report could be refused as too large and dropped.

## 1.0.6 - 2026-11-25

### Fixed
- The save screens answer only for the player who opened them. Before, when the player signed out or another account signed in while the "which save to keep" screen was open, the choice and the save could reach the new account. Now `SaveAsync`, `ChooseSaveAsync` and `ResolveMergedSavesAsync` end with `context_changed` and nothing is saved, and the Steam Cloud screen is dropped: the copy waits for its own player.
- On Android, a key kept unsealed because the Android Keystore failed once stayed unsealed for good. Now every launch seals the same key again once the Keystore works, and the saved sign-in stays. Before Android 6.0 there is no Keystore and the key stays as it was.

## 1.0.5 - 2026-11-25

### Added
- Ready-made screens (`KrtuworksUnity.CreateUi()`, UI Toolkit, Unity 2022.3 and later): `await ui.EnsurePlayerAsync()` shows the age and terms screen before a new guest, a screen for players under 13 (the game goes on offline and the screen is not shown again until their 13th birthday), the screen for newer terms and an error screen with Try again and Keep playing. `ui.SignInAsync()` signs in the way the platform does: on a computer the system browser with a "Finish signing in in your browser" screen (Open the page again, Cancel), on iOS and Android the system's own sheet, on a console a code screen with the address, the QR of the complete address, a countdown and New code; backing out is a cancelled result, not an error. `ui.SaveAsync(slot, bytes, baseVersion, name)` shows both saves when another device saved first and does what the player picks (`DescribeSave` adds the game's own chapter, play time or picture to each card), and `ResolveMergedSavesAsync()` asks about the slots two merged accounts both hold. With Steam, a Steam Cloud copy that differs from the server's shows the two saves too (`PlatformSaveConflicts`). A guest gets the "link a Krtuworks ID" prompt in a corner when the SDK says it is time. `<KrtuworksSignInButton>` and `<KrtuworksAccountChip>` are a sign-in button and an account chip with its menu, for UXML or code. Every screen works with a gamepad and a keyboard, and consoles show the controller hints. Colours, corners and the backdrop are USS custom properties (`--kw-accent` ...) on `.kw-root` that a game's own style sheet overrides, every sentence can be replaced (`Texts`), and a game that draws its own screens implements `IUiHost` and gets the same models from `KrtuworksUi`. A WebGL build offers no sign-in (guests play).

### Fixed
- `KrtuworksUnity.SignInAsync` on a console tried to open the system browser, which consoles do not have. It now throws `NotSupportedException` that points to `StartDeviceLinkAsync` and the ready-made screens.
- An Xbox One build (`RuntimePlatform.XboxOne`) was reported as WINDOWS; it is now XBOX.

## 1.0.4 - 2026-11-22

### Fixed
- A late `account_merged` answer for a player who already left no longer signs out the player who plays now. Before, when a call made for one account was answered after the player had switched to another, the new player's tokens were dropped and the app was told of a merge that was not theirs. Now only the merged player is signed out; its queues still wait for the merge check.
- A session reports only its own time in use. Before, a session opened after another one (a new player, a session the server closed after 30 minutes) started from the whole launch's time, so its first heartbeat could claim an hour of play the session never had. `SESSION_MINUTES` in rich presence counts the session too.

## 1.0.3 - 2026-11-22

### Fixed
- A player's call is sent only with that player's token. Before, a call refused as expired while the player changed (a sign-in in another tab, a switch of accounts) could be sent again with the new player's token, and a queued save, stat, event or bug report could go out under whoever was signed in when it was sent. Now each call carries the player it was made for, and when the token in hand is another player's nothing is sent (`context_changed`); the work waits for its own player.
- Moving waiting progress to another account (the first sign-in after playing without one, a merge) adds nothing twice when it is cut short (the app closed between its two writes) and run again. The events queue move is safe the same way.
- One waiting save per slot: a newer save takes the place of an older one not sent yet (the older one's call ends with `save_replaced`), and the saves of a slot go in the order they were made, so an older copy never lands after a newer one. Before, every save made offline waited on its own and they went in no set order.

## 1.0.2 - 2026-11-15

### Changed
- No change in this package: the SDK packages share one version, and 1.0.2 fixes the Unreal plugins.

## 1.0.1 - 2026-11-15

### Fixed
- Player builds for every platform but iOS failed to compile (`UnityEngine.iOS` does not exist there); only the iOS player now marks the SDK's folder as not backed up.

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
