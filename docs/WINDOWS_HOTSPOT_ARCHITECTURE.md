# Windows internet gateway enforcement — applies to either downstream AP mode

**Confirmed scope:** Internet enters the PC from an ordinary router. The PC redistributes it
through either a separate AP in bridge mode or a Windows-hosted Hotspot. The router itself is
not expected to implement code redemption or bandwidth policy.

See [full router -> PC -> AP/hotspot wiring](ACCESS_POINT_ARCHITECTURE.md).

## Implementation prerequisites

1. Enumerate Windows upstream/downstream NICs, and identify client default gateway and subnet.
2. Configure/verify NAT or Internet Connection Sharing, where supported by the target Windows build.
3. Establish a true default-deny per-client enforcement layer on **forwarded** traffic.
   Windows Filtering Platform may be a candidate, but supported APIs/drivers and privileges must be
   evaluated against the target hardware; opening a firewall port for the portal is not enforcement.
4. Permit a client only after the enforcement provider confirms the actual network rule was applied.
5. Revoke rules after expiry and implement quota metering and per-client upload/download shaping.
6. Prevent alternate paths, especially direct AP-to-router cabling, IPv6 bypass and alternate gateways.
7. Verify correct fail-closed behavior under service restart, reboot, interface reset and loss of state.

## Security boundary

- Gateway Windows service should exclusively own its SQLite state database with restrictive ACLs.
- WinForms must use authenticated, ACL-protected local IPC rather than write to ProgramData directly.
- Captive portal traffic must remain separate from the local admin surface; the current HTTP portal
  should not be used for sensitive credentials or public production deployment.

## Current state

Adapter discovery and topology checking exist. Logical code redemption, sessions and expiry exist.
Real network forwarding setup, Windows packet enforcement, captive-portal redirection,
traffic quotas and bandwidth shaping **are not implemented or hardware-tested**.
