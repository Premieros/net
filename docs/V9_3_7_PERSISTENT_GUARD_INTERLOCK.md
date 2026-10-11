# Persistent fail-closed guard — first implementation boundary (V9.3.7 development)

This commit does **not** enable permanent WFP filters or claim commercial readiness.

## Proven problem in V9.3.6
`ExperimentalWfpForwardGate` opens `FWPM_SESSION_FLAG_DYNAMIC`. Windows deletes its rules on service/process termination. An IPv4 permit appearing in WFP is not proof of client Internet reachability. The existing trial is intentionally kept unchanged.

## Safe first increment
`PersistentGuardInterlock` is a pure fail-closed decision function. Default is DENY if any evidence is absent, expired, from the wrong interface pair/subnet/policy generation, or if **any** of the following are unverified:
- persistent IPv4 forward deny;
- persistent IPv6 forward deny;
- survival across service and OS restart;
- an actual packet-block test, not only an API return code;
- device identity / IP-spoofing resistance.

Every piece of evidence is required; a successful WFP filter installation never implies successful forward traffic blocking. Evidence is deliberately not synthesized from the old trial or manual operator confirmation. CI validates the decision table without live network changes.

## Required next implementation before activation
1. Introduce a dedicated, privileged, persistently installed WFP provider/sublayer and baseline IPv4 **and IPv6** forward deny, scoped only to a verified downstream->upstream interface pair. Do not use the ephemeral trial sublayer. Avoid broad host-wide firewall changes.
2. Provision deny-before-permit transactionally. Never remove the persistent baseline during voucher lifecycle, service stop, crash, or installer upgrades. Fail safely if registration fails; avoid automatic deployment to the operator's machine.
3. Design per-device exceptions that **cannot be vetoed by the baseline BLOCK** due to WFP arbitration. Validate permit classification with real packet tests, not only synthetic-filter installation.
4. Reconcile durable policy on boot, upgrade, adapter reindex/readdress, WAN switch and rollbacks. Implement authenticated recovery/disable with audit trail to prevent accidental lockout.
5. Generate evidence from independently measured checks; do not report it verified until complete. Test isolated disposable Windows VM with artificial NICs, service termination, reboot and IPv6 traffic before any opt-in physical deployment.
6. Bind address to actual endpoint identity or fail closed on IP spoofing; implement speed/byte controls separately.

Until all steps are finished, `productionSafety.permanentFailClosedDeny` and `admissionReady` must remain false. **Do not** market this version as a working persistent guard or bypass-proof captive portal.
