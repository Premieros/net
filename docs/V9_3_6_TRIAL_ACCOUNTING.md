# V9.3.6 Beta — Honest voucher state and trial-loss reconciliation

**Experimental development build only. Not ready for paid Wi-Fi customers.**

## Why this was needed

Previously, a successful Windows WFP filter-management API call was recorded
as `Connected=true` in the client database, even though the source phone's
external Internet reachability had not been proved. This could create
misleading "online customer" status after a test voucher and could leave
stale reservations or trial session records after the two-minute WFP gate
stopped, crashed, or was quarantined.

## What changed

- For two-minute `ITimeLimitedTrialAdmissionController` grants, a successful
  WFP grant is now explicitly saved as
  `SessionStatus=trial-rule-installed-unverified`,
  `NetworkRuleInstalled=true`, `Connected=false`,
  and `TrafficVerified=false`. A rule and real phone connectivity are
  distinct facts. Regular/nontrial network admissions retain their previous
  behavior.
- The voucher-reservation logic recognizes an unverified trial rule as an
  active authorization **for conflict prevention**, so a second phone number
  or voucher cannot reuse the same source IPv4 grant.
- Expiry scheduling still revokes this unverified rule even though the
  phone was never marked `Connected`. A confirmed WFP revoke clears
  `NetworkRuleInstalled`.
- `ExperimentalTrialSessionRecovery.MarkUncontrolled` transactionally
  closes trial-permitted, revocation-required and interrupted pending
  sessions when the short-lived policy disappears. Pending reservations
  become `trial-aborted-before-authorization`; formerly permitted devices
  become `trial-ended-uncontrolled`. The operation is idempotent and
  deliberately does not change ordinary nontrial sessions or silently
  claim their Internet was blocked.
- Read-only loopback status includes `trialSessionAccounting` with the
  number of rule-installed-but-unverified, pending and uncontrolled-ended
  experimental session records. The one-click Windows admin diagnostics
  displays these counts; no PowerShell required.
- Integration regression tests cover state transitions, independent expiry,
  failure reconciliation, duplicate voucher prevention, and the explicit
  non-commercial safety flags in the running Windows Gateway API.

## Remaining architectural blockers

The current WFP filters belong to a dynamic session and **disappear if the
Gateway service stops, fails or Windows restarts**. It is fail-open, not
acceptable for selling access. These changes only prevent misleading
DATABASE reporting and stale reservations; they do NOT make the network
fail closed or verify Internet reachability. Windows ICS + TP-Link actual
client packet tests, IPv4/IPv6 persistent safety, source spoof prevention,
data quota/rate controls, secure portal and device identity are still needed.

No manual user testing is requested for this CI-tested programming change.
