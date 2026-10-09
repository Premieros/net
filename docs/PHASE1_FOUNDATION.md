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
