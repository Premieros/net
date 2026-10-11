using System.Net;
using System.Net.Sockets;

namespace RestaurantWiFiNetworking;

/// <summary>
/// Read-only safety inspection for router -> wired Windows uplink -> downstream AP/hotspot.
/// Configuration checks are necessary but NEVER prove internet access enforcement.
/// </summary>
public sealed record GatewayPreflightReport(
    bool WiringAppearsValid,
    bool ActualForwardingVerified,
    bool AdmissionRulesVerified,
    IReadOnlyList<string> Issues);

public static class GatewayPreflight
{
    public static GatewayPreflightReport Check(
        WindowsSharingTopology topology, IEnumerable<AdapterSnapshot> discovered)
    {
        var adapters = discovered.ToArray();
        var issues = new List<string>();
        var assessment = TopologyValidator.Assess(topology, adapters);
        if (!assessment.ConfigurationConsistent) issues.Add(assessment.Explanation);

        var uplink = adapters.FirstOrDefault(n =>
            string.Equals(n.Id, topology.UpstreamAdapterId, StringComparison.OrdinalIgnoreCase));
        var downlink = adapters.FirstOrDefault(n =>
            string.Equals(n.Id, topology.DownstreamAdapterId, StringComparison.OrdinalIgnoreCase));

        if (uplink is not null && downlink is not null && uplink.Id != downlink.Id)
        {
            if (downlink.HasIpv4DefaultGateway)
                issues.Add("Downstream network adapter has its own default gateway; this may bypass Windows routing.");

            if (!TryNetwork(uplink, out var upstreamAddress, out var upstreamPrefix))
                issues.Add("Upstream IPv4 address or subnet prefix is unavailable.");
            if (!TryNetwork(downlink, out var downstreamAddress, out var downstreamPrefix))
                issues.Add("Downstream IPv4 address or subnet prefix is unavailable.");

            if (TryNetwork(uplink, out upstreamAddress, out upstreamPrefix) &&
                TryNetwork(downlink, out downstreamAddress, out downstreamPrefix) &&
                SameNetwork(upstreamAddress, upstreamPrefix, downstreamAddress, downstreamPrefix))
                issues.Add("Upstream and downstream IPv4 networks overlap; isolated client subnet is required.");
        }

        return new(issues.Count == 0, false, false, issues);
    }

    static bool TryNetwork(AdapterSnapshot adapter, out uint address, out int prefix)
    {
        address = 0;
        prefix = adapter.Ipv4PrefixLength ?? 0;
        if (prefix is < 1 or > 30 ||
            !IPAddress.TryParse(adapter.Ipv4Address, out var ip) ||
            ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = ip.GetAddressBytes();
        address = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) |
                  ((uint)bytes[2] << 8) | bytes[3];
        return true;
    }

    static bool SameNetwork(uint first, int firstPrefix, uint second, int secondPrefix)
    {
        // Networks must be disjoint. Even partial range overlap is unsafe.
        var shorter = Math.Min(firstPrefix, secondPrefix);
        var mask = uint.MaxValue << (32 - shorter);
        return (first & mask) == (second & mask);
    }
}
