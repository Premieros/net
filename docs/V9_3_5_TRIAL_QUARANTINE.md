# V9.3.5 experimental WFP trial quarantine

**Not production-ready.** This is an operator-approved, temporary IPv4-only
WFP experiment on Windows 11 ICS. Phone-specific connectivity was not proven
on the real TP-Link TL-WR840N access point.

## Changes

* Once a WFP trial fails on a network configuration error, unexpected IPv6
  address, routing mismatch, disappearing adapter or worker exception, the
  entire **trial deadline** is quarantined. The worker does not silently
  reinstall permissions under the same operator-confirmed block.
* WFP grant/revocation or filter transaction failures also invalidate manual
  confirmation in the admission controller. Recovery requires a NEW short
  trial and a new manual observed deny.
* A local administrator status endpoint now reports explicit production
  protection capabilities: permanent fail-closed deny, IPv6 enforcement,
  anti-source-IP-spoofing, trustworthy device identity, byte quotas,
  rate limits and actual client phone traffic verification are all **false**.
  A code-ready experimental IPv4 status is never commercial readiness.
* The GUI's existing one-click status button shows WFP diagnostics and
  non-commercial readiness without PowerShell commands.
* Windows CI checks the actual gateway local status endpoint plus native
  WFP code-grant/revocation tests on deliberately **nonexistent synthetic
  adapter indexes**, keeping live networks unchanged. Regression tests
  cover quarantine on the same deadline.

## Limitations and next architecture work

Current WFP rules are dynamic: when the gateway process stops, its
security filters are removed and clients may regain Internet **without any
paid code**. This is not fail-closed and must not be deployed for selling
Wi-Fi codes. A future separate trusted guard/service and persistent-deny
lifecycle, IPv4/IPv6 packet-path tests, identity anti-spoofing and per-client
quota/shaping remain required, in addition to HTTPS/captive portal changes.

No operator manual testing or network changes are needed for this update.
