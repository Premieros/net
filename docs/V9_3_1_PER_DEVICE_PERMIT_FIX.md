# V9.3.1 test-only IPv4 per-device permit correction

**This build is not production Wi-Fi access control. Do not use customer vouchers.**

## Observed on TL-WR840N

Windows 11 ICS/NAT: router -> Wi-Fi 2 -> Windows -> Ethernet 2
(192.168.137.1) -> TP-Link TL-WR840N in AP mode -> two phones.
External IPv4 stopped for both phones during manual WFP test; portal
`http://192.168.137.1:8088/` remained accessible. The phone entering a
code could receive the success page but had no external Internet, and
the other phone sometimes appeared to have Internet. When the test was
stopped, both phones regained Internet.

## Root cause hypothesis and implementation

In V9.3, an IPFORWARD_V4 BLOCK for the entire AP->WAN path continued to
match packets from permitted source IPv4 addresses. Adding a second,
higher-weight PERMIT does not reliably override an overlapping WFP BLOCK.

V9.3.1 replaces the overlapping blanket BLOCK with a partition of IPv4
CIDR prefixes which **exclude** each authorized IP. Every source IP
other than the permitted addresses is still covered by a BLOCK filter,
while the allowed IP matches no deny prefix and an explicit PERMIT
filter. The swap occurs in one WFP transaction so it does not
temporarily expose every client during the rules update. The first
(no-permitted-client) policy remains a one-filter default deny.

The prefix planner is pure .NET and covered by regression tests for
two concurrent phones, full local /24 coverage, other public/private
IPv4 sources, IPv4 boundaries, and a maximum of eight test permits.

This fixes a **logical policy overlap**; source-address matching on
the actual Windows ICS forward path is still unverified until tested
on the user's hardware. In particular, successful
`FwpmFilterAdd0` does not mean HTTP traffic is working on a phone.

## Verify with two phones (only an isolated test network)

1. Stop any old trial, confirm both phones browse the Internet normally
   with mobile data and VPN off. Check both Wi-Fi IPv4 addresses are
   **different** in 192.168.137.0/24. Keep phone A's and B's addresses.
2. Prepare a disposable test code. Start a new two-minute trial with
   `Wi-Fi 2` upstream and `Ethernet 2` downstream.
3. While WFP is active, verify BOTH phones fail to browse an **external
   IPv4-only** test address but BOTH can still open the local portal at
   `http://192.168.137.1:8088/`.
4. Confirm the observed deny in the admin app and enter the code on
   **phone A only**.
5. IMMEDIATELY check the loopback-only administrator status from PC:
   `Invoke-RestMethod http://127.0.0.1:8765/status/ | ConvertTo-Json -Depth 6`.
   `ipv4TrialPolicy.authorizedClientIps` should contain phone A's actual
   IPv4 only, and `installedFilterCount` should be nonzero. This shows
   what source address the portal actually granted, not traffic success.
6. BEFORE two minutes elapse, test a NEW external IPv4 connection from
   both phones simultaneously. Phone A should browse; phone B should
   remain blocked. Verify the WFP trial state remains active. If phone
   B also browses, or A is blocked, STOP the trial and report both
   addresses and status. Do not enter more codes.
7. Click Stop trial or let the deadline expire. BOTH phones should
   regain normal Internet since dynamic WFP filters disappear. This is
   an intentional fail-open behavior **not suitable for paid access**.

Potential additional causes if A stays blocked: NAT/source-address
change at the IPFORWARD_V4 inspection layer, mismatched phone IP,
WFP filter match type/value marshalling incompatibility, or
competing firewall filters. No blanket firewall disable is needed.

## Remaining mandatory production work

Persistent fail-closed deny across service crashes/reboot, IPv6
coverage, anti-IP-spoofing and device identity, validated packet-path
behavior, actual byte quotas/speed shaping, revocation, secure portal,
and reliable captive-portal discovery. None are implied by this test.
