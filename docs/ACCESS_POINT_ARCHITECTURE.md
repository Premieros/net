# Access Point topology (Windows PC or router) — design decision

The product should support **access points** (APs) hosted by a Windows PC or provided by a router / dedicated AP.
The AP broadcasts Wi-Fi. **The gateway** forwards packets to the internet. These can be different devices.

## Supported target topologies

### A. Windows PC is the internet gateway; PC provides the AP
```text
Internet/Uplink -> Windows PC (gateway / NAT + policy enforcement) -> Windows Wi-Fi AP -> clients
```
Windows must actually forward the client packets and a verified packet-filtering mechanism must be installed.
A hosted AP/Windows sharing mode is a transport detail, not sufficient enforcement by itself.

### B. Windows PC is the gateway; external router is an AP (bridge mode)
```text
Internet/Uplink -> Windows PC (gateway + policy enforcement) -> Ethernet -> bridged AP -> Wi-Fi clients
```
The AP must bridge client traffic rather than route around Windows. Configure DHCP and default gateway
to point at the intended managed path. Treat guest VLAN and IPv6 bypass as explicit test cases.

### C. Router is the gateway and provides/controls the AP
```text
Internet -> router (gateway + per-client policy enforcement) -> Wi-Fi clients
                |
                +-- Windows management app + Gateway service over secure management interface
```
If router is gateway, Windows cannot control its clients' forwarded traffic just because it is on the same LAN.
Implement a **model-specific router integration**: supported router API, captive portal, RADIUS or vendor policy interface.
Without a verified enforcement interface, this topology is monitoring/management only, **not access control**.

## Architecture contracts
- **NetworkTopology:** AP origin, internet gateway, and verified enforcement capability.
- **Admission policy:** deny by default until an enforcement provider explicitly confirms access.
- **WindowsGatewayController:** planned packet-path/WFP implementation; Windows versions and NICs need bench tests.
- **RouterController:** planned vendor-specific integrations. A generic home router is not assumed controllable.
- **Session store:** accepted codes and logical sessions are not proof that a device can reach the internet.
- **Metering / shaping / expiry:** must be performed by the effective gateway, not by the AP icon in the UI.

## Configuration and security
Discover network adapters, client subnet, internet gateway, DHCP, DNS and AP mode at setup.
Before enabling portal mode confirm that the access point network cannot bypass the selected enforcement gateway.
Keep management API separate from untrusted clients. Use a secure local privileged service for state writes,
plus authenticated management transport to an external router when applicable.

## Acceptance tests on physical equipment
1. Verify the client's actual default gateway and outbound path.
2. Confirm internet is denied to unauthenticated clients (IPv4/IPv6 and DNS).
3. Confirm explicit grant, revoke, expiry and quota enforcement work on that path.
4. Test reconnection, DHCP IP change, MAC randomization, reboot, fallback and two clients reusing one code.
5. Verify service failure cannot silently permit anonymous internet.
6. Confirm rate limits independently for upload/download and compare with observed network counters.

## Current status
Both target AP origins are represented in the architecture. Implementation of **actual network enforcement**
for either mode is not yet shipped. Windows Forms + HTTP listener + SQLite alone cannot do this.
