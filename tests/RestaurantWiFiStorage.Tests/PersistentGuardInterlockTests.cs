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
        Check(!PersistentGuardTopology.Validate(selection with { DownstreamAddress = "192.168.137.0" }).SafeToStage,
            "downstream network address rejected");
        Check(!PersistentGuardTopology.Validate(selection with { DownstreamAddress = "192.168.137.255" }).SafeToStage,
            "downstream broadcast address rejected");
        return count;
    }
}
