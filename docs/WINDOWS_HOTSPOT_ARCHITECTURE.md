# Windows gateway implementation notes (one access-point option)

This document covers only the situation where **Windows is the internet gateway**.
For both PC-generated and router-provided Access Points, see [Access Point Architecture](ACCESS_POINT_ARCHITECTURE.md).

When Windows forwards the packets:
- PC-generated AP can use the Windows gateway path only if packets are actually forwarded through a controllable interface.
- A separate router/AP in **bridge / AP mode** can also use Windows as the gateway.
- If the router itself is the client's default gateway and forwards internet traffic without passing through Windows, Windows packet filtering cannot provide per-client control. See the router integration path in the AP architecture.

## Windows implementation requirements

1. Discover the actual client-facing and upstream NICs, client subnet and default gateway. Confirm the host routes packets.
2. Verify packet enforcement (e.g., Windows Filtering Platform with supported APIs/driver) before granting access. Do not assume a Windows Firewall exception on TCP/8088 affects forwarded client traffic.
3. Apply default-deny policies for unauthenticated clients. Authorize a verified device/session, revoke on expiry/quota, and fail closed where technically possible.
4. Build independent metering and upload/download throttling. Wi-Fi AP association alone is not an internet session.
5. Handle IPv6, NAT, DHCP changes, randomized MAC addresses, bridge configuration, service failure and Windows reboot.
6. Test on the actual supported Windows build and Wi-Fi hardware.

Security blockers:
- Desktop app currently has direct access to SQLite. Restrict storage ownership to privileged service and use authenticated local IPC before deployment.
- Portal is plain HTTP and captures personal information. Its deployment requires network and privacy review.
- Current code handles **logical** activation/expiry only; it does **not** block or shape packets.

**Status:** Windows enforcement backend is not implemented or tested on hardware.
