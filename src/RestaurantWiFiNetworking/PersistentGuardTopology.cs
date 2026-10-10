using System.Net;
using System.Net.Sockets;

namespace RestaurantWiFiNetworking;

/// <summary>
/// Validates an explicitly selected interface pair before any future persistent
/// forwarding policy is staged. No machine/network side effects.
/// </summary>
public static class PersistentGuardTopology
{
    public sealed record Selection(int UpstreamIndex, int DownstreamIndex,
        string DownstreamAddress, string DownstreamMask, string UpstreamAddress);

    public sealed record Validation(bool SafeToStage, string Reason, string? DownstreamCidr);

    public static Validation Validate(Selection? selection)
    {
        if (selection is null) return Reject("selection-missing");
        if (selection.UpstreamIndex <= 0 || selection.DownstreamIndex <= 0 ||
            selection.UpstreamIndex == selection.DownstreamIndex)
            return Reject("invalid-interface-pair");
        if (!IPAddress.TryParse(selection.DownstreamAddress, out var downstream) ||
            downstream.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.TryParse(selection.UpstreamAddress, out var upstream) ||
            upstream.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.TryParse(selection.DownstreamMask, out var mask) ||
            mask.AddressFamily != AddressFamily.InterNetwork)
            return Reject("invalid-ipv4");
        var bytes = downstream.GetAddressBytes();
        var upstreamBytes = upstream.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        uint maskValue = Ipv4TrialRulePlanner.ToNetworkUInt32(mask);
        var prefix = 0;
        var zeroSeen = false;
        for (var i = 31; i >= 0; i--)
        {
            var bit = (maskValue & (1u << i)) != 0;
            if (!bit) zeroSeen = true;
            else if (zeroSeen) return Reject("noncontiguous-mask");
            else prefix++;
        }
        if (prefix < 16 || prefix > 30) return Reject("unsupported-subnet");
        if (bytes[0] != 10 && !(bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) &&
            !(bytes[0] == 192 && bytes[1] == 168))
            return Reject("downstream-not-private");
        var addr = Ipv4TrialRulePlanner.ToNetworkUInt32(downstream);
        var wan = Ipv4TrialRulePlanner.ToNetworkUInt32(upstream);
        var network = addr & maskValue;
        if (addr == network || addr == (network | ~maskValue))
            return Reject("downstream-host-invalid");
        if ((wan & maskValue) == network) return Reject("upstream-overlaps-downstream");
        var cidr = new IPAddress(new byte[]
        {
            (byte)(network >> 24), (byte)(network >> 16),
            (byte)(network >> 8), (byte)network
        }) + "/" + prefix;
        return new Validation(true, "validated-for-isolated-staging-only", cidr);
    }

    static Validation Reject(string reason) => new(false, reason, null);
}
