namespace RestaurantWiFiNetworking;

/// <summary>
/// Pure preflight for a persistent dual-stack WFP baseline. No native calls.
/// Prevents activating a policy with a single direction/family omitted.
/// </summary>
public static class PersistentForwardPolicy
{
    public enum Family { Ipv4, Ipv6 }
    public enum Direction { DownstreamToUpstream, UpstreamToDownstream }

    public sealed record Rule(Family Family, Direction Direction,
        int SourceInterfaceIndex, int DestinationInterfaceIndex,
        bool Persistent, bool Block, bool IndependentlyVerified);

    public sealed record Decision(bool Complete, string Reason);

    public static Decision Validate(IEnumerable<Rule>? rules, int wanIndex, int apIndex)
    {
        if (rules is null) return new(false, "rules-missing");
        if (wanIndex <= 0 || apIndex <= 0 || wanIndex == apIndex)
            return new(false, "invalid-interface-pair");

        var all = rules.ToArray();
        foreach (var rule in all)
        {
            if (!Enum.IsDefined(rule.Family) || !Enum.IsDefined(rule.Direction))
                return new(false, "unsupported-rule");
            if (!rule.Persistent || !rule.Block || !rule.IndependentlyVerified)
                return new(false, "unverified-or-nonblocking-rule");
            var expectedSource = rule.Direction == Direction.DownstreamToUpstream ? apIndex : wanIndex;
            var expectedDestination = rule.Direction == Direction.DownstreamToUpstream ? wanIndex : apIndex;
            if (rule.SourceInterfaceIndex != expectedSource ||
                rule.DestinationInterfaceIndex != expectedDestination)
                return new(false, "wrong-interface-scope");
        }

        foreach (var family in Enum.GetValues<Family>())
            foreach (var direction in Enum.GetValues<Direction>())
                if (all.Count(rule => rule.Family == family && rule.Direction == direction) != 1)
                    return new(false, "missing-or-duplicate-" + family + "-" + direction);

        return new(true, "four-dual-stack-directions-verified");
    }
}
