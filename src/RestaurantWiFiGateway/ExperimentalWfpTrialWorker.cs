using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using RestaurantWiFiNetworking;
using RestaurantWiFiStorage;

namespace RestaurantWiFiGateway;

internal sealed record WfpTrialSnapshot(
    string State, string Message, DateTimeOffset? ExpiresUtc = null);

internal sealed class WfpTrialStatus
{
    WfpTrialSnapshot current = new("off", "Experimental IPv4 filter is disabled.");
    public WfpTrialSnapshot Current => Volatile.Read(ref current);
    public void Set(string state, string message, DateTimeOffset? expires = null) =>
        Volatile.Write(ref current, new WfpTrialSnapshot(state, message, expires));
}

/// <summary>
/// Manual, SHORT-LIVED IPv4 WFP block-only test. Do not confuse with production admission:
/// no packet policy is installed unless an elevated administrator explicitly requests
/// a two-minute trial from the Windows network settings dialog.
/// </summary>
internal sealed class ExperimentalWfpTrialWorker : BackgroundService
{
    readonly WfpTrialStatus status;
    readonly StateStore store = new();

    public ExperimentalWfpTrialWorker(WfpTrialStatus status) => this.status = status;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var gate = new ExperimentalWfpForwardGate();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    InspectAndApply(gate);
                }
                catch (Exception error)
                {
                    gate.Dispose();
                    status.Set("error", "IPv4 trial was stopped: " + error.Message);
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            gate.Dispose();
            status.Set("off", "Experimental WFP rules cleared on service shutdown.");
        }
    }

    void InspectAndApply(ExperimentalWfpForwardGate gate)
    {
        var network = JsonNode.Parse(store.Read())?["Network"] as JsonObject;
        var endText = network?["ExperimentalWfpTrialUntilUtc"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(endText) ||
            !DateTimeOffset.TryParse(endText, out var end) ||
            end <= DateTimeOffset.UtcNow)
        {
            if (gate.Active) gate.Dispose();
            status.Set("off", "WFP test is not active (or its two-minute window has expired).");
            return;
        }

        var upstreamId = network?["UpstreamAdapterId"]?.GetValue<string>() ?? "";
        var downstreamId = network?["DownstreamAdapterId"]?.GetValue<string>() ?? "";
        var accessMode = network?["AccessMode"]?.GetValue<int>() ?? -1;
        if (accessMode is not (0 or 1))
        {
            gate.Dispose();
            status.Set("invalid-network", "Select a supported downstream AP/hotspot mode.");
            return;
        }

        var wiring = new WindowsSharingTopology(upstreamId, downstreamId,
            (ClientAccessMode)accessMode, ClientsUseWindowsAsGateway: true);
        var adapters = WindowsAdapterDiscovery.Discover();
        var report = GatewayPreflight.Check(wiring, adapters);
        if (!report.WiringAppearsValid)
        {
            gate.Dispose();
            status.Set("invalid-network", string.Join("; ", report.Issues));
            return;
        }

        var nics = NetworkInterface.GetAllNetworkInterfaces();
        var uplink = nics.FirstOrDefault(n =>
            string.Equals(n.Id, upstreamId, StringComparison.OrdinalIgnoreCase));
        var downlink = nics.FirstOrDefault(n =>
            string.Equals(n.Id, downstreamId, StringComparison.OrdinalIgnoreCase));
        if (uplink is null || downlink is null)
        {
            gate.Dispose();
            status.Set("missing-adapter", "Selected network interface has disappeared.");
            return;
        }

        // IPv4-only test cannot secure an IPv6-capable downstream interface.
        var allClientAddresses = downlink.GetIPProperties().UnicastAddresses;
        if (allClientAddresses.Any(a =>
            a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
            !a.Address.IsIPv6LinkLocal))
        {
            gate.Dispose();
            status.Set("ipv6-risk", "Downstream has non-link-local IPv6; refusing IPv4-only network test.");
            return;
        }

        var upstreamIndex = uplink.GetIPProperties().GetIPv4Properties()?.Index;
        var downstreamIndex = downlink.GetIPProperties().GetIPv4Properties()?.Index;
        var discovered = NetworkDiscovery.Discover();
        if (upstreamIndex is null || downstreamIndex is null ||
            discovered.Wan?.InterfaceIndex != upstreamIndex ||
            !discovered.AccessPoints.Any(a => a.InterfaceIndex == downstreamIndex))
        {
            gate.Dispose();
            status.Set("route-mismatch", "Windows default internet route or downstream AP does not match selected adapters.");
            return;
        }

        // Exact selected interface pair only. NEVER permit auto-discovery to block
        // an unrelated private-network interface.
        var onlySelectedClientAp = discovered.AccessPoints.Single(a => a.InterfaceIndex == downstreamIndex);
        gate.Apply(new NetworkTopologySnapshot
        {
            Wan = discovered.Wan,
            AccessPoints = new List<NetworkAdapterSnapshot> { onlySelectedClientAp }
        }, Array.Empty<IPAddress>());

        status.Set(gate.Active ? "ipv4-block-trial-active" : "wfp-unavailable",
            gate.Active
                ? "Experimental WFP IPv4 forwarding deny installed for selected AP->LAN route. " +
                  "TEST ON A REAL CLIENT. No IPv6 protection or packet verification."
                : gate.State + " " + gate.LastError, end);
    }
}
