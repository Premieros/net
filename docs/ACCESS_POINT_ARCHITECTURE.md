# Internet distribution architecture — router -> Windows PC -> access point / hotspot

## Confirmed hardware input

The upstream internet connection is explicitly **wired Ethernet (LAN cable)** from the router into
the Windows PC. In the WinForms Settings page choose **توصيل الشبكة** to inspect available adapters,
select this router-facing Ethernet NIC, and choose an independent downstream NIC.

- For an **external access point**, use a **second Ethernet port**, such as an additional PCIe or USB-to-Ethernet adapter, and connect its cable to the AP operating in bridge mode.
- For a **PC hotspot**, use a compatible Wi-Fi adapter as the downstream interface; do not assume the physical Wi-Fi radio/virtual adapter is available before the hotspot is enabled.
- The diagnostic dialog stores adapter choices in SQLite but **does not** enable Windows ICS/NAT, DHCP, captive portal filtering, or bandwidth control.
- Recheck adapter IDs and physical cabling after moving USB adapters or changing network hardware.

## Confirmed requirement

The **router supplies internet to the Windows computer**. The **computer** is responsible for forwarding
and eventually controlling internet access for clients. Clients connect by one of two methods.

### Mode 1: External access point connected to the computer

```text
Internet -> upstream router
                 |
        (Ethernet or Wi-Fi)
                 |
         Windows computer
         [NIC A: upstream router]
         [routing / NAT / policy gateway — to be implemented/verified]
         [NIC B: downstream LAN]
                 |
          Ethernet cable
                 |
       Access Point (AP/bridge mode)
                 |
          Wi-Fi clients
```

The downstream AP is **not directly connected to the upstream router**.
The AP must operate in bridge/AP mode (no competing NAT/DHCP server) and client default gateways must
point to the Windows downstream interface. There must not be a second internet uplink bypassing Windows.
Windows requires separate *logical* upstream and downstream interfaces, potentially a USB Ethernet adapter
if the PC lacks a second network port.

### Mode 2: Hotspot emitted by the computer

```text
Internet -> upstream router
                 |
         Windows computer
         [NIC A: upstream router]
         [routing / NAT / policy gateway — to be implemented/verified]
         [NIC B: Windows-hosted Wi-Fi hotspot]
                 |
          Wi-Fi clients
```

A compatible Windows Wi-Fi adapter/driver and an operational Windows sharing method are required.
Some hardware supports shared physical radios through virtual adapters, but this must be verified on
the specific Windows version and Wi-Fi hardware.

## What the program needs to manage

- **Upstream NIC:** detects the interface receiving internet and its router gateway.
- **Downstream NIC:** detects the separate client-facing interface (Ethernet AP or Wi-Fi hotspot).
- **Connectivity routing:** check that clients actually use Windows as their internet gateway, that
  Windows NAT/forwarding is set up, and that no alternative IPv4/IPv6 paths bypass enforcement.
- **Captive-portal admission:** unauthenticated clients must not have internet. On valid redemption,
  a verified Windows packet enforcement component should grant precisely that client session.
- **Session policy:** disconnect at expiry, revoke on administrator request, account for usage and
  enforce upload/download limits; packet admission and traffic shaping require a working network layer.
- **Deployment security:** the Windows service should exclusively own the SQLite database and expose
  an authenticated, access-controlled local administration interface to WinForms.

## What is *not* required

A router-specific control API (MikroTik, TP-Link, etc.) is **not** required for this target topology.
The upstream router just provides ordinary internet. Router policy integration would only be needed
if the clients' packets bypassed Windows; that is outside the confirmed design.

## First-boot verification

1. Identify **two distinct Windows network interfaces**: router-facing upstream and client-facing downstream.
2. Confirm the upstream adapter has a working router default gateway.
3. Confirm that downstream clients obtain IP/DNS/gateway in a separate network served through Windows.
4. Verify an unauthenticated client's packets cannot bypass the intended enforcement layer, including IPv6.
5. Validate real client disconnect, allowed sessions, code reuse, time expiration, quotas and bandwidth caps
   on the actual router, PC adapters, AP and Windows version.
6. Confirm service failure and reboot do not accidentally leave clients unrestricted.

## Implementation status

A typed topology model, read-only adapter discovery and tests are present in
`RestaurantWiFiNetworking`. They validate possible wiring but **do not** turn on routing/NAT,
configure Windows sharing, create a captive portal, firewall rules or bandwidth shaping.
The current HTTP portal stores logical code redemptions; the real network admission backend
and real-hardware testing are still required.
