# Restaurant WiFi Control

Windows .NET 8 restaurant network-management prototype with a WinForms administration app,
a local Gateway service, access codes, and SQLite-backed logical sessions.

## Intended network modes
- **Computer-provided Access Point:** Windows acts as the internet gateway and hosts the client-facing AP.
- **Router / dedicated Access Point:** The AP can operate as a bridge behind a Windows gateway, or a compatible router can be the internet gateway and enforce policy through a vendor integration.

An Access Point provides Wi-Fi connectivity; it does not automatically provide a programmable
access-control interface. Read [AP network architecture](docs/ACCESS_POINT_ARCHITECTURE.md)
for traffic paths and safety checks.

**Important:** The current portal records codes/sessions but does not actually deny/allow internet
packets, disconnect clients, enforce bandwidth, or meter quotas. Neither Windows nor router
enforcement backends are implemented. Do not deploy this as a protected captive portal yet.

## Projects
- `src/RestaurantWiFiControl`: WinForms management UI
- `src/RestaurantWiFiGateway`: Windows portal and logical session handling
- `src/RestaurantWiFiStorage`: SQLite state transactions and legacy JSON migration
- `src/RestaurantWiFiNetworking`: AP topology and admission-controller contract (design scaffold)
- `tests/RestaurantWiFiStorage.Tests`: storage, concurrency, expiry and topology integration tests

CI builds the Windows installer and runs the integration test console project.
Legacy `v9-data.json` is backed up before its first import into SQLite. See
[Phase 1 notes](docs/PHASE1_FOUNDATION.md) for migration cautions and security issues.
