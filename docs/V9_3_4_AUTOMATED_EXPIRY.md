# V9.3.4 — Automatic test coverage for individual voucher expiry

**Beta development only; not ready for paying Wi-Fi clients.** The Windows
ICS + external TP-Link TL-WR840N topology has not been proven to restore actual
Internet access only for the intended device.

## Implemented

* `TrialAdmissionController` accepts an injectable `.NET TimeProvider`. The
  Windows service uses the real system clock; Windows CI uses a test clock to
  advance ten seconds immediately with no slow manual waiting.
* WFP native synthetic-interface CI now exercises a short-lived eight-second
  voucher. On the next controller reconciliation, the expired host's WFP
  source address is removed while other authorized client IPv4 addresses stay
  active. The underlying WFP engine runs on Windows; the interface indices are
  intentionally nonexistent so no actual external traffic is affected.
* Expired vouchers remain in `revocation-required` until the admission
  controller confirms a revoke. Integration tests verify a simulated
  unsuccessful WFP revoke does not mark the customer disconnected.
* The Gateway's ten-second expiry/revocation maintenance loop now logs and
  retries after a transient error instead of permanently quitting.

## What this does NOT establish

These tests verify the grant/revocation code paths and Windows WFP management
API, **not actual phone packet forwarding or IPv6 safety**. IP addresses are
not robust device identity, and runtime packet counters, real data quotas,
bandwidth throttling, persistent fail-closed rules after service shutdown and
Wi-Fi captive-portal automatic redirect are not implemented.

Keep the PR draft. No more operator PowerShell or phone tests are needed for
these code changes. A final limited real-device verification will eventually
be required before classifying the product as commercial-ready.
