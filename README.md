# Krtuworks SDK for Unity

`com.krtuworks.sdk` connects a Unity game or app (Unity 2022.3 LTS or later, Unity 6 included) to Krtuworks: Krtuworks ID sign-in (guest, desktop browser, phone, console code, Steam), sessions, events and dashboards, stats, achievements and leaderboards, cloud saves, remote config and A/B tests, bug reports and the release report.

Install it in Package Manager with **Add package from git URL**:

```
https://github.com/krtuworks/krtuworks-sdk-unity.git#1.0.0
```

or from [OpenUPM](https://openupm.com): `openupm add com.krtuworks.sdk`.

Save your app's `krtuworks-config.json` (the app's Credentials page in the [partner console](https://partner.krtuworks.com)) as `Assets/Resources/krtuworks-config.json`, then call `await KrtuworksUnity.InitializeAsync()` once when the game starts.

- Documentation: [partner.krtuworks.com/docs](https://partner.krtuworks.com/docs)
- Coding agents: [`AGENTS.md`](AGENTS.md)
- Changes: [`CHANGELOG.md`](CHANGELOG.md)
- Support: support@krtuworks.com

Licensed under the [Apache License 2.0](LICENSE). The Krtuworks name and logo are not covered by the licence ([`NOTICE`](NOTICE)).
