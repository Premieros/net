# V9.3.3 beta: trial session integrity and automatic native WFP tests

**Experimental research build, NOT suitable for paying customers.** It still
uses two-minute dynamic WFP IPv4 forwarding filters which disappear if the
service stops. Do not turn it into a production gateway based on CI tests.

## Improvements

* Native Windows CI smoke now exercises the complete test-only admission
  state machine on synthetic NONEXISTENT interface indices:
  - unconfirmed trial rejects code-based IP grants,
  - confirmed trial grants Phone A and Phone B separately,
  - revoking Phone A preserves only Phone B's permit,
  - stopping the trial clears all dynamic WFP permissions,
  - expired trial refuses admission.
* Fixed an actual native WFP defect found by those tests: the gateway used
  a globally fixed FWPM sublayer GUID. Two overlapping WFP gate instances
  generated `FWP_E_ALREADY_EXISTS (0x80320009)`. Each dynamic gate now
  creates its own sublayer GUID.
* New voucher source IPv4 normalization: the Windows HTTP listener can
  surface IPv4 clients as IPv4-mapped IPv6 addresses. The portal now uses
  the equivalent canonical IPv4 for reservations/authorization. Native
  IPv6 and unspecified/broadcast sources are not eligible.
* Transactional voucher reservation now refuses a second active or
  pending voucher on the same canonical IPv4. This prevents two voucher
  records unintentionally sharing one WFP IP permit, where revoking one
  could disconnect the other.
* The administrative GUI continues to include a one-click read-only
  WFP status display; no PowerShell commands are needed for routine rule
  inspection.

## Automated versus real-hardware proof

Tests do not send client packets. A passing Windows runner proves
integration with native WFP management APIs and code-grant state changes,
NOT that a permit restores Internet on an actual phone using Windows ICS
and an external TP-Link bridge AP.

**Still not implemented:** fail-closed service crash/reboot protection;
IPv6 forwarding control; packet-level proof of grant/revocation; MAC or
802.1X device authentication; IP spoof prevention; speed and data quota
enforcement; HTTPS portal, secure credentials, captive portal automation,
or deployment on a supported fully verified Windows build.

On the tested topology the user had Windows Wi-Fi 2 upstream,
Ethernet 2 192.168.137.1 downstream, and a TL-WR840N in AP mode. The
applicable manual test was explicitly deferred to avoid unnecessarily
burdening the user.
