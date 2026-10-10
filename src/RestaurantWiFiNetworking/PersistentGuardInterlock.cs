using System.Net;
using System.Net.Sockets;

namespace RestaurantWiFiNetworking;

/// <summary>
/// Pure, side-effect-free admission interlock for a future persistent WFP guard.
/// It NEVER installs filters. A successful WFP API call alone is not proof of
/// installed, surviving, correctly scoped or effective fail-closed protection.
/// </summary>
public static class PersistentGuardInterlock
{
    public sealed record Evidence(
        bool PersistentV4DenyVerified,
        bool PersistentV6DenyVerified,
        bool RebootSurvivalVerified,
        bool ForwardingBlockTrafficVerified,
        bool IdentityBindingVerified,
        int DownstreamInterfaceIndex,
        int UpstreamInterfaceIndex,
        string DownstreamNetworkCidr,
        string PolicyGeneration,
        DateTimeOffset CheckedAtUtc);

    public sealed record Decision(bool AllowAdmission, string Reason);

    public static Decision Evaluate(Evidence? evidence, int downstreamIndex,
        int upstreamIndex, string expectedCidr, string expectedGeneration,
        DateTimeOffset nowUtc, TimeSpan maxEvidenceAge)
    {
        if (evidence is null) return Deny("guard-evidence-missing");
        if (downstreamIndex <= 0 || upstreamIndex <= 0 ||
            downstreamIndex == upstreamIndex)
            return Deny("invalid-interface-pair");
        if (maxEvidenceAge <= TimeSpan.Zero || evidence.CheckedAtUtc > nowUtc ||
            nowUtc - evidence.CheckedAtUtc > maxEvidenceAge)
            return Deny("guard-evidence-stale");
        if (string.IsNullOrWhiteSpace(expectedGeneration) ||
            !StringComparer.Ordinal.Equals(evidence.PolicyGeneration, expectedGeneration))
            return Deny("policy-generation-mismatch");
        if (evidence.DownstreamInterfaceIndex != downstreamIndex ||
            evidence.UpstreamInterfaceIndex != upstreamIndex ||
            !StringComparer.Ordinal.Equals(evidence.DownstreamNetworkCidr, expectedCidr))
            return Deny("topology-changed");
        if (!evidence.PersistentV4DenyVerified) return Deny("persistent-ipv4-deny-unverified");
        if (!evidence.PersistentV6DenyVerified) return Deny("persistent-ipv6-deny-unverified");
        if (!evidence.RebootSurvivalVerified) return Deny("reboot-survival-unverified");
        if (!evidence.ForwardingBlockTrafficVerified) return Deny("packet-block-unverified");
        if (!evidence.IdentityBindingVerified) return Deny("device-identity-unverified");
        return new Decision(true, "guard-evidence-valid");
    }

    static Decision Deny(string reason) => new(false, reason);
}
