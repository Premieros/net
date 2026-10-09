namespace RestaurantWiFiNetworking;

public enum AccessPointOrigin
{
    WindowsComputer,
    ExternalAccessPoint
}

public enum InternetGateway
{
    WindowsComputer,
    Router
}

public enum EnforcementBackend
{
    WindowsPacketFilter,
    RouterIntegration
}

public sealed record AccessPointTopology(
    AccessPointOrigin AccessPoint,
    InternetGateway Gateway,
    bool WindowsForwardsClientTraffic,
    bool RouterHasSupportedIntegration);

public sealed record TopologyAssessment(
    EnforcementBackend Backend,
    bool NetworkAccessEnforcementReady,
    string Explanation);

public static class TopologyValidator
{
    public static TopologyAssessment Assess(AccessPointTopology topology)
    {
        if (topology.Gateway == InternetGateway.WindowsComputer)
        {
            if (!topology.WindowsForwardsClientTraffic)
                return new(EnforcementBackend.WindowsPacketFilter, false,
                    "Client traffic does not traverse Windows. A Windows-only policy cannot control this network.");
            return new(EnforcementBackend.WindowsPacketFilter, false,
                "Windows gateway topology is possible, but no verified Windows packet filtering provider has been installed.");
        }

        if (!topology.RouterHasSupportedIntegration)
            return new(EnforcementBackend.RouterIntegration, false,
                "Router is the internet gateway; its hardware/API must support network policy enforcement.");
        return new(EnforcementBackend.RouterIntegration, false,
            "Router is a candidate for a policy integration, but a vendor-specific controller must be implemented and tested.");
    }
}

public sealed record ClientIdentity(string IpAddress, string? HardwareAddress = null);
public sealed record AdmissionResult(bool Enforced, string Reason);

/// <summary>
/// Enforced=true is valid only when an actual network gateway confirms a deny/allow rule.
/// Recording a database session never counts as enforcement.
/// </summary>
public interface INetworkAdmissionController
{
    ValueTask<AdmissionResult> GrantAsync(ClientIdentity client, DateTimeOffset expiresAt, CancellationToken token = default);
    ValueTask<AdmissionResult> RevokeAsync(ClientIdentity client, CancellationToken token = default);
}

/// <summary>Explicit safe placeholder: never reports a network grant as successful.</summary>
public sealed class UnconfiguredAdmissionController : INetworkAdmissionController
{
    public ValueTask<AdmissionResult> GrantAsync(ClientIdentity client, DateTimeOffset expiresAt, CancellationToken token = default) =>
        ValueTask.FromResult(new AdmissionResult(false, "Network enforcement provider has not been configured."));

    public ValueTask<AdmissionResult> RevokeAsync(ClientIdentity client, CancellationToken token = default) =>
        ValueTask.FromResult(new AdmissionResult(false, "No active enforcement provider is installed."));
}
