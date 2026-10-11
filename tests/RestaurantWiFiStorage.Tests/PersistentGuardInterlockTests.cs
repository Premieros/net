using RestaurantWiFiNetworking;

internal static class PersistentGuardInterlockTests
{
    public static int Run()
    {
        var count = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAIL: guard interlock " + name);
            Console.WriteLine("PASS: guard interlock " + name);
            count++;
        }
        var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        var good = new PersistentGuardInterlock.Evidence(true, true, true, true,
            true, 22, 11, "192.168.137.0/24", "generation-1", now);
        PersistentGuardInterlock.Decision Decide(PersistentGuardInterlock.Evidence? e,
            int down = 22, int up = 11, string cidr = "192.168.137.0/24",
            string generation = "generation-1", DateTimeOffset? clock = null) =>
            PersistentGuardInterlock.Evaluate(e, down, up, cidr, generation,
                clock ?? now, TimeSpan.FromSeconds(30));

        Check(!Decide(null).AllowAdmission, "missing evidence denies");
        Check(Decide(good).AllowAdmission, "complete matching evidence permits at policy boundary");
        Check(!Decide(good, down: 0).AllowAdmission, "invalid interface index denies");
        Check(!Decide(good, down: 11).AllowAdmission, "same interfaces deny");
        Check(Decide(good, up: 12).Reason == "topology-changed", "uplink rebind denies");
        Check(Decide(good, cidr: "192.168.138.0/24").Reason == "topology-changed",
            "downstream network readdress denies");
        Check(Decide(good, generation: "generation-2").Reason == "policy-generation-mismatch",
            "policy replacement invalidates old verification");
        Check(Decide(good, clock: now.AddSeconds(31)).Reason == "guard-evidence-stale",
            "expired verification denies");
        Check(Decide(good, clock: now.AddSeconds(-1)).Reason == "guard-evidence-stale",
            "future-dated verification denies");
        Check(Decide(good with { PersistentV4DenyVerified = false }).Reason ==
            "persistent-ipv4-deny-unverified", "missing IPv4 baseline denies");
        Check(Decide(good with { PersistentV6DenyVerified = false }).Reason ==
            "persistent-ipv6-deny-unverified", "IPv6 bypass risk denies");
        Check(Decide(good with { RebootSurvivalVerified = false }).Reason ==
            "reboot-survival-unverified", "dynamic-only policy denies");
        Check(Decide(good with { ForwardingBlockTrafficVerified = false }).Reason ==
            "packet-block-unverified", "successful filter installation is not packet proof");
        Check(Decide(good with { IdentityBindingVerified = false }).Reason ==
            "device-identity-unverified", "IP spoofing uncertainty denies");
        var selection = new PersistentGuardTopology.Selection(11, 22,
            "192.168.137.1", "255.255.255.0", "10.0.0.50");
        var valid = PersistentGuardTopology.Validate(selection);
        Check(valid.SafeToStage && valid.DownstreamCidr == "192.168.137.0/24",
            "selected private downstream network is correctly scoped");
        Check(!PersistentGuardTopology.Validate(null).SafeToStage,
            "no selected topology is rejected");
        Check(!PersistentGuardTopology.Validate(selection with { UpstreamIndex = 22 }).SafeToStage,
            "same WAN and AP indices rejected");
        Check(!PersistentGuardTopology.Validate(selection with { DownstreamMask = "255.0.255.0" }).SafeToStage,
            "noncontiguous downstream mask rejected");
        Check(!PersistentGuardTopology.Validate(selection with { DownstreamAddress = "8.8.8.8" }).SafeToStage,
            "public downstream scope rejected");
        Check(!PersistentGuardTopology.Validate(selection with { UpstreamAddress = "192.168.137.22" }).SafeToStage,
            "overlapping uplink subnet rejected");
        foreach (var invalidWan in new[] { "0.0.0.0", "127.0.0.1",
                     "169.254.20.1", "224.0.0.1", "255.255.255.255" })
        {
            Check(PersistentGuardTopology.Validate(selection with
                { UpstreamAddress = invalidWan }).Reason == "upstream-address-not-routable",
                "reserved WAN address rejected: " + invalidWan);
        }

        Check(!PersistentGuardTopology.Validate(selection with { DownstreamAddress = "192.168.137.0" }).SafeToStage,
            "downstream network address rejected");
        Check(!PersistentGuardTopology.Validate(selection with { DownstreamAddress = "192.168.137.255" }).SafeToStage,
            "downstream broadcast address rejected");
        var requiredSteps = PersistentGuardDeploymentPlan.Steps.ToArray();
        var deploy = new PersistentGuardDeploymentPlan.ExecutionState(
            requiredSteps, true, true, true);
        Check(PersistentGuardDeploymentPlan.Evaluate(deploy).CanEnableAdmission,
            "fully verified deployment plan is eligible for independent evidence gate");
        Check(!PersistentGuardDeploymentPlan.Evaluate(null).CanEnableAdmission,
            "unknown persistent deployment denies admission");
        Check(!PersistentGuardDeploymentPlan.Evaluate(deploy with { ExplicitAdminConsent = false }).CanEnableAdmission,
            "persistent deployment requires explicit admin authorization");
        Check(!PersistentGuardDeploymentPlan.Evaluate(deploy with { TransactionCommitted = false }).CanEnableAdmission,
            "partially staged WFP denies admission");
        Check(!PersistentGuardDeploymentPlan.Evaluate(deploy with { VerifiedOutsideInstaller = false }).CanEnableAdmission,
            "installer success alone cannot verify protection");
        foreach (var step in requiredSteps)
        {
            var incomplete = deploy with
            {
                Completed = requiredSteps.Where(x => x != step).ToArray()
            };
            Check(!PersistentGuardDeploymentPlan.Evaluate(incomplete).CanEnableAdmission,
                "missing guard deployment step denies: " + step);
        }
        var readiness = ProductionAdmissionReadiness.Evaluate(selection, deploy, good,
            "generation-1", now);
        Check(readiness.Ready, "production readiness requires all three independent gates");
        Check(!ProductionAdmissionReadiness.Evaluate(null, deploy, good,
            "generation-1", now).Ready, "missing topology keeps production closed");
        Check(!ProductionAdmissionReadiness.Evaluate(selection, null, good,
            "generation-1", now).Ready, "missing deployment keeps production closed");
        Check(!ProductionAdmissionReadiness.Evaluate(selection, deploy, null,
            "generation-1", now).Ready, "missing WFP evidence keeps production closed");
        Check(!ProductionAdmissionReadiness.Evaluate(selection, deploy,
            good with { PersistentV6DenyVerified = false },
            "generation-1", now).Ready, "IPv6 gap keeps production closed");
        Check(!ProductionAdmissionReadiness.Evaluate(selection, deploy, good,
            "generation-2", now).Ready, "generation drift keeps production closed");
        Check(!ProductionAdmissionReadiness.Evaluate(selection, deploy, good,
            "generation-1", now.AddMinutes(1)).Ready,
            "stale observed policy keeps production closed");
        var fourDenyRules = (from family in Enum.GetValues<PersistentForwardPolicy.Family>()
            from direction in Enum.GetValues<PersistentForwardPolicy.Direction>()
            select new PersistentForwardPolicy.Rule(family, direction,
                direction == PersistentForwardPolicy.Direction.DownstreamToUpstream ? 22 : 11,
                direction == PersistentForwardPolicy.Direction.DownstreamToUpstream ? 11 : 22,
                true, true, true)).ToArray();
        Check(PersistentForwardPolicy.Validate(fourDenyRules, 11, 22).Complete,
            "complete IPv4 and IPv6 forward baseline can pass pure preflight");
        Check(!PersistentForwardPolicy.Validate(null, 11, 22).Complete,
            "unknown baseline fails closed");
        foreach (var missing in fourDenyRules)
        {
            Check(!PersistentForwardPolicy.Validate(
                    fourDenyRules.Where(x => x != missing), 11, 22).Complete,
                "missing forward deny fails closed: " + missing.Family + " " + missing.Direction);
        }
        Check(!PersistentForwardPolicy.Validate(fourDenyRules.Concat(
            new[] { fourDenyRules[0] }), 11, 22).Complete,
            "duplicate baseline rule rejected");
        Check(!PersistentForwardPolicy.Validate(fourDenyRules.Select(
            (r, i) => i == 0 ? r with { Persistent = false } : r), 11, 22).Complete,
            "dynamic WFP rule cannot stand in for durable baseline");
        Check(!PersistentForwardPolicy.Validate(fourDenyRules.Select(
            (r, i) => i == 0 ? r with { IndependentlyVerified = false } : r), 11, 22).Complete,
            "filter add alone cannot count as verified block");
        Check(!PersistentForwardPolicy.Validate(fourDenyRules.Select(
            (r, i) => i == 0 ? r with { SourceInterfaceIndex = 99 } : r), 11, 22).Complete,
            "unexpected forwarding interface rejected");
        return count;
    }
}
