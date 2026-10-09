# Session revocation reconciliation

The session expiry process now distinguishes logical expiration from physical network rule removal.
If a session is marked `network-authorized` and its time expires, the service records
`revocation-required` **without** reporting the client disconnected. A periodic reconciliation
task requests actual rule removal from `INetworkAdmissionController`; only a confirmed revoke
transitions it to `expired` / `Connected=false`. An unavailable backend leaves the case flagged
for review rather than silently indicating safety. Mock admission/revocation tests cover this.

An unconfigured Windows network controller remains the deployed default, so no physical packet
access is granted or revoked yet. Real Windows forwarding, packet filtering, usage metering and
rate limiting still need implementation and on-device verification.

---

# Next networking foundation batch — network preflight and transactional authorization

- The Windows administration dialog now reports IPv4 subnet/prefix information, warns if
  uplink and downstream networks overlap, and flags a downstream default gateway that may bypass
  the Windows PC. A passing preflight means **plausible wiring only**, not that NAT or packet filtering works.
- The Gateway's portal now **fails closed** with HTTP 503 whenever there is no verified packet-level
  admission provider. It does not consume customers' codes or mark them connected.
- A new `AccessRedemptionService` reserves a code under a SQLite transaction, requests real
  network authorization from `INetworkAdmissionController`, and increments `Uses` only after the
  controller reports that the rule was applied. A refused grant releases the reservation; a failed
  commit attempts a compensating revoke. Mock-provider tests exercise allow, deny, and concurrent use.
- The management dashboard distinguishes a merely recorded session from a logically authorized one.
  It still cannot independently measure actual packet reachability.
- The Windows installer attempts to stop the service **before** replacing its executable, preserves
  the service registration across upgrades, and aborts if an installed service cannot be stopped.
  Installer execution, upgrades, named pipe permissions and actual Windows traffic still need on-device tests.

**The network filter is intentionally unconfigured.** No WFP admission provider, NAT/ICS setup,
per-client rate control, quota meter, DHCP/captive portal redirect or verified fail-closed
network behavior has been implemented yet. Never interpret successful code redemption tests using
a mock controller as real internet sharing capability.

---

# Privileged service and named-pipe administration — in review

The Gateway Windows service now owns the SQLite data store. On service startup it attempts to
restrict the ProgramData directory to **SYSTEM** and local **Administrators** before opening SQLite.
If access control setup fails the service startup should fail rather than continue with a writable
public database. This ACL setup must be tested on Windows 10 and Windows 11, including upgrades from
existing installations.

The WinForms desktop is now requested to run elevated via its Windows manifest. It connects to a
**local named pipe** with a Windows ACL limited to SYSTEM and the local Administrators group and
requests explicitly allowlisted commands. It no longer directly opens the SQLite database.

Administrative commands implemented: read, initialize, add_group, add_codes, set_password,
set_name, set_network. Arbitrary state replacement is not allowed. The public HTTP portal does not
host these commands. The service is still the owner of code redemption and session expiration.

## Security and deployment cautions

- **Windows elevation is required** to administer the system. Every permitted local Administrator
  can issue administrative commands; more granular app accounts and tamper-resistant login
  controls are not implemented.
- This is a security architecture milestone, **not** an approved production release. Named pipe
  ACLs, inherited filesystem permissions, installer service lifecycle, and upgrade migration
  must be verified on physical Windows 10 / 11 installations.
- Keep both WinForms and Gateway on the same revision; do not run the legacy JSON desktop
  against the service-owned SQLite database.
- The database does not encode physical networking rules. Captive portal interception,
  forwarding/NAT/ICS, client deny/allow, per-client speed shaping and quota enforcement remain
  **unimplemented**.
- Confirm network path Router (LAN Ethernet) -> Windows PC -> downstream Ethernet AP
  or Windows-hosted Wi-Fi hotspot before implementing firewall admission.

---

# Access Point requirement update

This project targets **Access Points sourced from the Windows computer or from an external router/AP**, not just the Windows Mobile Hotspot feature.
The network must be classified by **which device routes internet packets**, not by which device broadcasts Wi-Fi.
See [Access Point Architecture](ACCESS_POINT_ARCHITECTURE.md).
No real gateway enforcement has been implemented for either mode.

---

## Additional batch: session lifecycle and tests
- Integration test runner passes migration, legacy backup, metadata preservation, concurrent single-use redemption, persistence, corrupted JSON rejection and logical session expiration/idempotence (subject to current CI).
- Gateway periodically marks expired logical sessions as disconnected in SQLite. This does **not** stop physical network packets.
- Portal no longer claims that internet access was actually granted: gateway code redemption only registers a session.
- **Remaining blockers before production:** privileged service-only data ownership with authenticated IPC; managed hotspot admission and traffic inspection/shaping; real Windows Hotspot test bench. Do not rely on the current ProgramData ACL for security.

# Phase 1 SQLite integration (work in progress)

The WinForms desktop app and Gateway now use a common `StateStore` based on Microsoft.Data.Sqlite.
On first initialization, the previous `v9-data.json` is imported into `wifi-state.db` with `v9-data.json.pre-sqlite.bak` as a backup. The original JSON remains untouched. Each mutation uses a database transaction, and each redemption checks the latest usage count inside that transaction. Treat the SQLite file as authoritative after migration; do not keep running older versions of the application against the JSON file.

## Deployment precautions
- Stop the Windows Gateway service and close the desktop app before first installation/migration. Back up ProgramData/Restaurant WiFi Control.
- Do not roll back to a prior build without explicit data export/conversion, because the previous JSON version will not reflect new SQLite activity.
- Build and test Windows release, migration, concurrent redemptions and installer upgrades before merging this branch.
- SQLite coordination protects logical state, not actual network access. Windows Hotspot traffic interception and traffic accounting are not implemented.
- HTTP portal and personally identifiable data still need a comprehensive production security review.

## Current limitations
- The desktop app and gateway still serialize the logical state as a single JSON document inside SQLite; this is an interim foundation, not normalized relational schema.
- The gateway's in-memory IP throttle resets on restart and needs an operational rate-limit strategy.
- Running mixed old/new versions concurrently is unsupported.
- Recovery procedures, database file ACLs, session expiry enforcement and tests remain work to do.

# Phase 1 foundation: Windows Hotspot

## Completed in this change
- Serialize activation requests inside the Windows Gateway service to avoid simultaneous code-use increments within one service process.
- Limit portal form requests to 4 KiB, require form-url-encoded content, and restrict POST to /activate.
- Reject disabled or missing access groups and prevent expired sessions from blocking a new activation for the same phone.
- Atomically replace the gateway and desktop JSON data file to prevent readers observing a partially written JSON document.
- Preserve gateway-owned ClientRecord fields (e.g. SessionStartedAt, SessionExpiresAt, AccessCode) in the desktop app using JsonExtensionData.
- Enable Windows installer CI builds for pull requests.

## Not yet solved
- **Cross-process lost updates remain possible**: Windows UI and Gateway can still both read an old snapshot, then overwrite each other's changes. Do not treat atomic replacement as a transaction.
- JSON stores personal data in plaintext. Storage permissions and a migration to SQLite with explicit transactions and a shared data-access layer are next.
- Sessions remain logical records; no actual Windows Hotspot enforcement, quota metering, DNS redirection or physical disconnect exists.
- The portal uses HTTP and is not ready for production exposure. Secure networking depends on the final Windows Hotspot gateway architecture.
- Authentication rate limiting and data-retention controls still need implementation.

## Windows Hotspot design constraint
Client traffic must traverse a controllable Windows network path before quotas, client isolation, and allow/deny rules can be enforced. Merely running HttpListener on Windows does not make the service a network gateway. Validate whether Windows Mobile Hotspot/ICS permits the required interception and enforcement on the target Windows version before selecting WFP drivers or a dedicated routed configuration.

## Next milestone
1. Shared data-access project and transactional SQLite migration preserving all existing data, including unknown session fields.
2. Explicit session state machine: issued -> active -> expired/revoked; code redemption in one transaction.
3. Unit tests for concurrent redemption, migration and expiry; CI gates.
4. Windows Hotspot traffic-path proof of concept, without claiming enforcement before real network tests.
