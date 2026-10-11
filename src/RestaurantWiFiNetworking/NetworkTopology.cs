using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RestaurantWiFiNetworking;

/// <summary>
/// Internet ALWAYS enters the Windows PC from an upstream router.
/// Client connectivity leaves the PC through either a Windows-hosted Wi-Fi hotspot
/// or a separate access point in bridge (AP) mode.
/// </summary>
public enum ClientAccessMode
{
    WindowsHostedHotspot,
    ExternalAccessPointBridge
}

public enum EnforcementBackend
{
    WindowsPacketFilter
}

/// <param name="UpstreamAdapterId">Windows NIC connected to the internet-supplying router.</param>
/// <param name="DownstreamAdapterId">Different NIC that leads to clients (AP or hotspot).</param>
/// <param name="ClientsUseWindowsAsGateway">
/// Expected client default gateway points at the Windows PC, not directly at the upstream router.
/// This configuration flag is NOT a verified packet-path test.
/// </param>
public sealed record WindowsSharingTopology(
    string UpstreamAdapterId,
    string DownstreamAdapterId,
    ClientAccessMode AccessMode,
    bool ClientsUseWindowsAsGateway);

public sealed record AdapterSnapshot(
    string Id, string Name, bool IsUp, bool HasIpv4Address, bool HasIpv4DefaultGateway,
    NetworkInterfaceType InterfaceType = NetworkInterfaceType.Ethernet,
    string? Ipv4Address = null, string? Ipv4Gateway = null, int? Ipv4PrefixLength = null);

public sealed record TopologyAssessment(
    EnforcementBackend Backend,
    bool ConfigurationConsistent,
    bool NetworkAccessEnforcementReady,
    string Explanation);

public static class WindowsAdapterDiscovery
{
    /// <summary>Read-only adapter discovery. Does not enable internet sharing or firewall rules.</summary>
    public static IReadOnlyList<AdapterSnapshot> Discover() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Select(n =>
            {
                var ip = n.GetIPProperties();
                return new AdapterSnapshot(
                    n.Id, n.Name,
                    n.OperationalStatus == OperationalStatus.Up,
                    ip.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork),
                    ip.GatewayAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                        && !System.Net.IPAddress.Any.Equals(a.Address)),
                    n.NetworkInterfaceType,
                    ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString(),
                    ip.GatewayAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                        && !System.Net.IPAddress.Any.Equals(a.Address))?.Address.ToString(),
                    ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.PrefixLength);
            })
            .ToArray();
}

public static class TopologyValidator
{
    public static TopologyAssessment Assess(WindowsSharingTopology topology,
        IEnumerable<AdapterSnapshot> adapters)
    {
        TopologyAssessment Invalid(string why) =>
            new(EnforcementBackend.WindowsPacketFilter, false, false, why);

        if (string.IsNullOrWhiteSpace(topology.UpstreamAdapterId) ||
            string.IsNullOrWhiteSpace(topology.DownstreamAdapterId))
            return Invalid("Select both router-facing (upstream) and client-facing (downstream) Windows adapters.");
        if (string.Equals(topology.UpstreamAdapterId, topology.DownstreamAdapterId, StringComparison.OrdinalIgnoreCase))
            return Invalid("Select distinct upstream and downstream adapters. A shared Wi-Fi radio needs an explicitly verified virtual adapter.");
        var available = adapters.ToArray();
        var upstream = available.FirstOrDefault(a =>
            string.Equals(a.Id, topology.UpstreamAdapterId, StringComparison.OrdinalIgnoreCase));
        var downstream = available.FirstOrDefault(a =>
            string.Equals(a.Id, topology.DownstreamAdapterId, StringComparison.OrdinalIgnoreCase));
        if (upstream is null || downstream is null)
            return Invalid("One or both configured Windows network adapters were not found.");
        if (upstream.InterfaceType is not (NetworkInterfaceType.Ethernet or
            NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or
            NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.Wireless80211))
            return Invalid("Router uplink must be Ethernet or Wi-Fi.");
        if (!upstream.IsUp || !upstream.HasIpv4Address || !upstream.HasIpv4DefaultGateway)
            return Invalid("Router-facing Ethernet/Wi-Fi adapter must be connected with an IPv4 address and default gateway.");
        if (topology.AccessMode == ClientAccessMode.ExternalAccessPointBridge &&
            downstream.InterfaceType is not (NetworkInterfaceType.Ethernet or
                NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or
                NetworkInterfaceType.FastEthernetT))
            return Invalid("An external access point must be connected to a downstream wired Ethernet adapter.");
        if (!downstream.IsUp || !downstream.HasIpv4Address)
            return Invalid("Client-facing adapter must be active and have its own IPv4 address.");
        if (!topology.ClientsUseWindowsAsGateway)
            return Invalid("Clients must route through the Windows PC; direct router routing bypasses admission control.");
        return new(EnforcementBackend.WindowsPacketFilter, true, false,
            "Adapter configuration is plausible for router -> Windows -> AP/hotspot. " +
            "Routing, NAT/ICS, client gateway and packet filtering remain unverified. " +
            "No network access control is installed.");
    }
}

public sealed record ClientIdentity(string IpAddress, string? HardwareAddress = null);
public sealed record AdmissionResult(bool Enforced, string Reason);

/// <summary>
/// A network grant only succeeds if the Windows gateway truly enforces it.
/// Creating a session row in SQLite does not grant internet access.
/// </summary>
public interface INetworkAdmissionController
{
    /// <summary>True only if packet-level policy is installed and its behavior was verified.</summary>
    bool IsEnforcementReady { get; }
    ValueTask<AdmissionResult> GrantAsync(ClientIdentity client, DateTimeOffset expiresAt,
        CancellationToken token = default);
    ValueTask<AdmissionResult> RevokeAsync(ClientIdentity client,
        CancellationToken token = default);
}

/// <summary>
/// Non-production WFP field experiment: limit session lifetime to the trial
/// window. This is NOT a persistent or fail-closed admission backend.
/// </summary>
public interface ITimeLimitedTrialAdmissionController : INetworkAdmissionController
{
    DateTimeOffset TrialEndsAt { get; }
}

/// <summary>Explicit safe placeholder until a packet filter is installed and tested.</summary>
public sealed class UnconfiguredAdmissionController : INetworkAdmissionController
{
    public bool IsEnforcementReady => false;

    public ValueTask<AdmissionResult> GrantAsync(ClientIdentity client, DateTimeOffset expiresAt,
        CancellationToken token = default) =>
        ValueTask.FromResult(new AdmissionResult(false, "Windows gateway admission control is not configured."));

    public ValueTask<AdmissionResult> RevokeAsync(ClientIdentity client,
        CancellationToken token = default) =>
        ValueTask.FromResult(new AdmissionResult(false, "No verified network filter is installed."));
}
