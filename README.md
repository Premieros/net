# Restaurant WiFi Control

Native Windows captive-portal and network-gate application for restaurant networks.

## V10 Network Gate

- Detects the active internet-facing WAN interface.
- Detects private IPv4 interfaces used as Access Point / hotspot egress lines.
- Applies a Windows Filtering Platform (WFP) default-deny rule at the IPv4 forwarding layer from AP interfaces to WAN.
- A client receives internet forwarding permission only after a valid access code is accepted.
- Authorization is tied to the client IPv4 address and expires with the local session.
- The management dashboard shows the detected WAN line, AP lines, and gate state.

Data remains local in `%ProgramData%\Restaurant WiFi Control\v9-data.json`.
