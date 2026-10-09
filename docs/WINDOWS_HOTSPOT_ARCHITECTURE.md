# Windows Hotspot control architecture — implementation gate

## Traffic path
Windows Mobile Hotspot / Internet Connection Sharing (ICS) is an OS-managed NAT service, not a programmable captive portal. The HTTP gateway currently only records a redeemed code; that does not block unauthenticated traffic. The installation must not claim to protect clients until packet-path enforcement is established.

## Proposed components
1. **Windows Hotspot Adapter**: Discover actual private hotspot adapter, gateway address, NAT and connectivity. Verify capability on supported Windows builds and handle adapter changes.
2. **Admission Controller**: Track client identity (IP plus authenticated device binding as supported by the networking layer), apply deny-by-default before authorization, install allow rules on valid redemption, revoke upon logout/expiry.
3. **Traffic Metering**: Measure packets per client at an enforcement layer. Account for IPv4/IPv6, reconnects, DHCP churn, and traffic through NAT; distinguish outbound/inbound bytes.
4. **Policy Engine**: Persist session state and implement timeout, quota, bandwidth control, maximum devices and disconnection as independent verified policies.
5. **Portal Discovery**: Determine whether clients can be redirected without OS-specific privileged filtering. Never assume a listener on port 8088 provides captive-portal redirection.

## Candidate mechanisms requiring real hardware tests
- Windows Filtering Platform (WFP) for packet authorization/metering may require a signed driver or supported filtering APIs; simple firewall port rules are not equivalent to per-client shaping.
- ICS configuration and NAT can change between Windows versions. Confirm that the gateway actually traverses a controllable path.
- If Windows Mobile Hotspot cannot offer reliable enforcement, use a deliberately configured Windows routed gateway or a dedicated network appliance. Do not weaken fail-closed semantics to make a demonstration appear to work.

## Security blocking issues
- Current WinForms app writes shared state from a normal user session; the gateway runs as a Windows service. A writable SQLite file under ProgramData can let local unprivileged users manipulate codes if its ACL is broad. **Do not grant Everyone/Users write access to the database.**
- Recommended next change: the privileged Windows service exclusively owns the SQLite database (SYSTEM/Administrators); desktop app communicates through an authenticated, ACL-protected named pipe. Require authorization per operation and log administrative actions.
- Portal is plain HTTP, collects names and phone numbers, and currently has no strong distributed rate limiting. Avoid production deployment without a privacy/security design.
- Do not expose the service's internal admin interface on the hotspot network. Separate local management IPC from untrusted portal endpoints.

## Verification checklist
- Unauthenticated hotspot client has no internet (including IPv6 and DNS tunnels).
- Valid redemption grants only its session; one-time code cannot be redeemed twice.
- Expired, revoked, over-quota and disconnected clients immediately lose connectivity.
- Download and upload limits measured independently and verified by traffic generation.
- Reboot, service failure, adapter reset and IP address changes preserve the intended deny-by-default behavior.
- Local unprivileged user cannot edit SQLite directly or impersonate the management app.

Status: DESIGN ONLY; no Windows packet-level control has been shipped.
