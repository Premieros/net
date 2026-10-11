# V9.3 experimental IPv4 code trial — **NOT FOR PAYING CUSTOMERS**

## Scope

This is a two-minute, explicitly opt-in Windows Filtering Platform (WFP)
**IPv4-only** forwarding experiment. It reuses the selected router-facing
Wi-Fi/Ethernet adapter and selected customer Hotspot/AP adapter. The dynamic
WFP session installs a per-interface-pair default deny, then optionally a
higher-weight source-IPv4 permit for a code redeemed through the local portal.

A Windows service STOP, CRASH or trial expiration removes the dynamic rules:
**at that point all clients may have Internet without codes**. Windows ICS/NAT,
DHCP, IPv6 paths, device identity (IP spoofing or address reuse), data quotas,
speed shaping, multi-device limits and automatic captive portal redirect are
**NOT** enforced. Do NOT sell codes or use production customers in this test.

## Preconditions (one isolated test phone, no paying clients)

- Keep Mobile Hotspot enabled and confirm the phone receives internet from the
  PC. Turn **mobile data OFF** on the test phone to prevent false positives.
- In the administrator app choose the router-facing adapter as *upstream*, and
  the active **different** Wi-Fi Direct virtual Hotspot adapter as *downstream*.
  Never select the same adapter twice.
- The private downstream IPv4 subnet must differ from upstream and the
  downstream must not have an Internet default gateway.
- Do not run this test on a network where losing Internet for two minutes is
  unacceptable. Do not remotely administer the test via a downstream client.
- Create a disposable code with an inexpensive test group first. A successful
  test consumes one code use; the temporary permission expires no later than
  two minutes after the trial starts.

## Test

1. In Settings > Network Setup, click **اختبار حجب IPv4 لمدة دقيقتين** and
   explicitly approve the warning. The filter is never installed at startup
   unless the operator starts a valid trial.
2. Within seconds, try an external IPv4 site on the phone **without a code**.
   External IPv4 traffic should fail. The local portal
   `http://192.168.137.1:8088/` should still open on the same phone.
   If external browsing still works, stop immediately and report **FAIL**.
3. Only if both checks pass, click **تأكيد الحجب من الهاتف** in the Windows
   app. This records a manual observation for the *current process only*;
   service restarts invalidate earlier confirmations.
4. Submit the disposable code at the portal on the phone. If a temporary WFP
   permit is installed and the SQLite transaction commits, the phone should
   gain external IPv4 Internet during the remainder of the trial.
   If it fails, preserve logs and do not issue real codes.
5. Test again without a code from a second isolated phone, if available;
   verify it stays blocked while the coded phone can browse.
6. Wait for trial expiry or click **إيقاف اختبار الحجب**. The app removes the
   temporary WFP rules and marks experimental sessions as ended. Confirm the
   phone returns to normal Internet access (which demonstrates precisely why
   this is **not** fail-closed protection).

## Diagnostics

- Administrator service state: `Get-Service RestaurantWiFiGateway`
- From the Windows PC: `http://127.0.0.1:8765/status/`
- Status field `admissionReady` always remains `false` for **production**
  admission. `ipv4CodeTrialReady` is true only for a manually confirmed,
  currently active test. `experimentalWfp` reports state and expiry.
- Report the phone's local IP, whether an external IPv4 address opens before /
  after code redemption, whether the portal opens, and the Gateway status
  **without including actual password, phone numbers or customer codes**.

## Production gate (not implemented)

Before production, implement persistent fail-closed admission (including
service crash and reboot behavior), IPv4/IPv6 bypass prevention, authenticated
client identity, HTTPS portal and automatic redirect where appropriate,
measured real per-client traffic counters, quotas, shaping, client disconnect
and revocation, as well as repeatable on-hardware multi-client tests.
