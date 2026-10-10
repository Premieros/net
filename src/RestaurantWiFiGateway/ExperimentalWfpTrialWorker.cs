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
    readonly TrialAdmissionController admission;
    readonly StateStore store = new();
    readonly DateTimeOffset workerStartedUtc = DateTimeOffset.UtcNow;
    readonly TrialFailureLatch failureLatch = new();
    DateTimeOffset? inspectingTrialEnd;

    public ExperimentalWfpTrialWorker(WfpTrialStatus status, TrialAdmissionController admission)
    {
        this.status = status;
        this.admission = admission;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    InspectAndApply();
                }
                catch (Exception error)
                {
                    // Never retry the SAME test window after a WFP/storage
                    // error with its previous manual-confirmation flag.
                    if (inspectingTrialEnd is { } currentEnd)
                        failureLatch.Reject(currentEnd);
                    try { admission.Stop(); }
                    catch (Exception stopError) { Console.Error.WriteLine(stopError); }
                    try { MarkTrialEnded(); }
                    catch (Exception cleanupError) { Console.Error.WriteLine(cleanupError); }
                    status.Set("trial-quarantined",
                        "Trial stopped after a gateway error. Start a NEW trial and " +
                        "confirm the blocked phone again; prior confirmation is invalid. " +
                        error.GetType().Name + ": " + error.Message);
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            admission.Stop();
            MarkTrialEnded();
            status.Set("off", "Experimental WFP rules cleared on service shutdown.");
        }
    }

    void InspectAndApply()
    {
        inspectingTrialEnd = null;
        var network = JsonNode.Parse(store.Read())?["Network"] as JsonObject;
        var endText = network?["ExperimentalWfpTrialUntilUtc"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(endText) ||
            !DateTimeOffset.TryParse(endText, out var end) ||
            end <= DateTimeOffset.UtcNow)
        {
            admission.Stop();
            MarkTrialEnded();
            status.Set("off", "WFP test is not active (or its two-minute window has expired).");
            return;
        }

        inspectingTrialEnd = end;
        if (failureLatch.IsRejected(end))
        {
            // Dynamic WFP filters have already been removed by RejectTrial.
            // The only way out is a NEW administrator-started trial.
            status.Set("trial-quarantined",
                "This IPv4 trial window was invalidated by a prior error. " +
                "Start a new two-minute test to re-enable experimental codes.", end);
            return;
        }

        var upstreamId = network?["UpstreamAdapterId"]?.GetValue<string>() ?? "";
        var downstreamId = network?["DownstreamAdapterId"]?.GetValue<string>() ?? "";
        var accessMode = network?["AccessMode"]?.GetValue<int>() ?? -1;
        if (accessMode is not (0 or 1))
        {
            RejectTrial(end, "invalid-network", "Select a supported downstream AP/hotspot mode.");
            return;
        }

        var wiring = new WindowsSharingTopology(upstreamId, downstreamId,
            (ClientAccessMode)accessMode, ClientsUseWindowsAsGateway: true);
        var adapters = WindowsAdapterDiscovery.Discover();
        var report = GatewayPreflight.Check(wiring, adapters);
        if (!report.WiringAppearsValid)
        {
            RejectTrial(end, "invalid-network", string.Join("; ", report.Issues));
            return;
        }

        var nics = NetworkInterface.GetAllNetworkInterfaces();
        var uplink = nics.FirstOrDefault(n =>
            string.Equals(n.Id, upstreamId, StringComparison.OrdinalIgnoreCase));
        var downlink = nics.FirstOrDefault(n =>
            string.Equals(n.Id, downstreamId, StringComparison.OrdinalIgnoreCase));
        if (uplink is null || downlink is null)
        {
            RejectTrial(end, "missing-adapter", "Selected network interface has disappeared.");
            return;
        }

        // IPv4-only test cannot secure an IPv6-capable downstream interface.
        var allClientAddresses = downlink.GetIPProperties().UnicastAddresses;
        if (allClientAddresses.Any(a =>
            a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
            !a.Address.IsIPv6LinkLocal))
        {
            RejectTrial(end, "ipv6-risk", "Downstream has non-link-local IPv6; refusing IPv4-only network test.");
            return;
        }

        var upstreamIndex = uplink.GetIPProperties().GetIPv4Properties()?.Index;
        var downstreamIndex = downlink.GetIPProperties().GetIPv4Properties()?.Index;
        var discovered = NetworkDiscovery.Discover();
        if (upstreamIndex is null || downstreamIndex is null ||
            discovered.Wan?.InterfaceIndex != upstreamIndex ||
            !discovered.AccessPoints.Any(a => a.InterfaceIndex == downstreamIndex))
        {
            RejectTrial(end, "route-mismatch", "Windows default internet route or downstream AP does not match selected adapters.");
            return;
        }

        // Exact selected interface pair only. Manual field verification is
        // required before the trial permits code activation.
        var onlySelectedClientAp = discovered.AccessPoints.Single(a => a.InterfaceIndex == downstreamIndex);
        // A dynamic WFP session vanishes when the process exits. A confirmation
        // from a prior process is never valid evidence of today's packet policy.
        var observedAt = network?["ExperimentalWfpTrialBlockingObservedAtUtc"]?.GetValue<string>();
        var confirmed = network?["ExperimentalWfpTrialBlockingObserved"]?.GetValue<bool>() == true &&
            DateTimeOffset.TryParse(observedAt, out var observed) && observed > workerStartedUtc;
        if (!confirmed) MarkTrialEnded();
        admission.Apply(new NetworkTopologySnapshot
        {
            Wan = discovered.Wan,
            AccessPoints = new List<NetworkAdapterSnapshot> { onlySelectedClientAp }
        }, end, confirmed);

        var ready = admission.IsEnforcementReady;
        status.Set(ready ? "ipv4-code-trial-active" : "ipv4-default-deny-trial-active",
            ready
                ? "Operator confirmed blocked IPv4 forwarding; temporary per-IP code grants enabled. " +
                  "NOT production protection; check traffic from the test phone."
                : "Experimental IPv4 forwarding deny installed for the selected Hotspot. " +
                  "Verify that an uncoded phone cannot browse externally, then confirm in the admin dialog. " +
                  "IPv6 and service-stop bypass remain unverified.",
            end);
    }

    void RejectTrial(DateTimeOffset deadline, string reason, string explanation)
    {
        failureLatch.Reject(deadline);
        admission.Stop();
        MarkTrialEnded();
        status.Set(reason, explanation +
            " Prior deny confirmation is invalid; start a NEW trial.", deadline);
    }

    // These records must not remain "Connected" when the dynamic WFP policy
    // vanishes on trial stop/expiry or service failure. This is a UI/database
    // status correction; it does NOT assert that real Internet is blocked.
    void MarkTrialEnded()
    {
        // Handle both previously committed experimental rules and requests
        // interrupted while still reserving a disposable test voucher.
        store.Update(root =>
            ExperimentalTrialSessionRecovery.MarkUncontrolled(root, DateTimeOffset.UtcNow));
    }

}
