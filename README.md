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

## Wired router uplink setup (LAN cable, alternative to Wi-Fi test)

1. Plug the internet-providing router into the PC's Ethernet port.
2. For an external access point, connect a **second** PC Ethernet port (USB-to-Ethernet is acceptable) to the AP in bridge/AP mode. Alternatively, use a Windows Wi-Fi adapter for the PC-hosted hotspot.
3. In the Windows administration app, open **الإعدادات → توصيل الشبكة**.
4. Select the router-facing Ethernet adapter, the downstream Ethernet/Wi-Fi adapter, and the client distribution mode. Use **فحص المسار** to review adapter IP and router gateway information.
5. Saving this selection is **diagnostic and configuration only**: it does not enable Internet Connection Sharing, NAT, DHCP, firewall enforcement, captive portal interception, bandwidth shaping or quota enforcement.

The Windows networking backend must still be implemented and verified before using this app to restrict customer internet access.

## V9.2 beta hardware test: router Wi-Fi -> PC -> AP/hotspot

The test PC can receive its internet **over Wi-Fi from the router**; Ethernet upstream is
also supported. On the test PC, choose the real internet-receiving adapter from
**الإعدادات → توصيل الشبكة**. The downstream must be a **different logical network
adapter** with an isolated IPv4 subnet:

- **Wi-Fi in + Ethernet out:** connect a separate PC Ethernet/USB-Ethernet port to an
  external access point configured in bridge/AP mode.
- **Wi-Fi in + PC-hosted Wi-Fi hotspot out:** Windows must expose a distinct, working
  virtual downstream hotspot adapter. A single adapter ID cannot be used on both sides;
  not every Wi-Fi radio/driver supports simultaneous station + hotspot.
- **Ethernet in:** remains supported for the final target network.

This **beta** includes a manual, short-lived experimental Windows Filtering Platform
**IPv4 forwarding block test** imported from the older V10 prototype. It is **OFF by
default**. After configuring and verifying isolated test NICs, select the explicit
two-minute block test in Network Settings; use **إيقاف اختبار الحجب** to end it early.
The temporary block should be tested from a separate client device, not the host PC.
The service reports trial status locally at `http://127.0.0.1:8765/status/`.
It is neither an internet-sharing feature nor a working code-admission backend.

**Safety boundaries:** WFP behavior has not been verified on the target Windows hardware.
The prototype covers IPv4 forwarding only; it neither guarantees IPv6 blocking nor
survives service termination as a protective block. The app does not configure ICS/NAT,
meter traffic or enforce speed and quotas. Do not use this beta to sell Wi-Fi access,
and do not test by interrupting a production network.

## Supported Windows targets

- **Windows 11:** a supported, fully updated Windows 11 release on compatible hardware is the preferred production target.
- **Windows 10:** the app recognizes Windows 10 build 19045 (22H2 era); normal Windows 10 support ended on October 14, 2025. Use Windows 10 only if the installation has an applicable security servicing arrangement (such as ESU or LTSC, where eligible). Earlier builds are flagged for upgrade.
- The network setup screen now displays the detected OS/build and Ethernet adapter diagnostics. The OS check is not a test of Windows Internet Connection Sharing (ICS), NAT, hotspot features or packet filtering.
- Both OS families still require full physical tests with **router Ethernet LAN -> PC -> bridged access point or hosted hotspot**.
- No physical deny/allow, quotas or shaping controller is installed yet. Keep the current branch out of production.

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
