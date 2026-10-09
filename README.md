# Restaurant WiFi Control

Windows .NET 8 restaurant internet-management prototype with a WinForms management app,
Windows Gateway service, access codes and SQLite-backed logical sessions.

## Confirmed internet flow

```text
Internet -> Router -> Windows PC (internet gateway) -> clients, by either:
                                               |-> separate Access Point (bridge/AP mode)
                                               |-> Windows Wi-Fi Hotspot
```

The **router only supplies internet to the PC**. The **Windows PC must route all client traffic**
before the application can enforce access policies. For an external AP, connect its Ethernet uplink
to the PC's *downstream* interface, not back to the original router. The PC needs distinct logical
router-facing and client-facing network adapters. A generic router control API is not needed.

See [Internet distribution architecture](docs/ACCESS_POINT_ARCHITECTURE.md) for wiring and validation.

**Important:** The current code validates/records redemption and logically expires sessions, but
does not yet intercept/deny internet packets, enable NAT/ICS, disconnect clients, enforce quotas or
apply per-client speed limits. Do **not** use as a secure captive portal until a verified Windows
network admission backend exists and is tested against the actual hardware.

## Projects
- `src/RestaurantWiFiControl`: WinForms management interface
- `src/RestaurantWiFiGateway`: Windows portal and logical session handling
- `src/RestaurantWiFiStorage`: transactional SQLite state and legacy JSON import
- `src/RestaurantWiFiNetworking`: adapter discovery, router-to-PC-to-client topology validation
  and network admission interface (enforcement backend not yet implemented)
- `tests/RestaurantWiFiStorage.Tests`: storage, migration, concurrency, expiry and topology tests

CI builds the Windows installer and runs integration tests.
Legacy `v9-data.json` is backed up before its first import into SQLite.
See [Phase 1 notes](docs/PHASE1_FOUNDATION.md) for deployment and security caveats.
