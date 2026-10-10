using System.Net;
using RestaurantWiFiNetworking;

namespace RestaurantWiFiGateway;

/// <summary>
/// Manual, two-minute, IPv4-only customer-code field experiment.
/// Dynamic WFP filters disappear when this service exits. This MUST NOT be
/// used as production default-deny or presented as verified quota/rate control.
/// An operator must first observe that a test phone loses external IPv4 access
/// while the local portal remains reachable, then explicitly confirm.
/// </summary>
internal sealed class TrialAdmissionController : ITimeLimitedTrialAdmissionController, IDisposable
{
    readonly TimeProvider clock;
    public TrialAdmissionController() : this(TimeProvider.System) { }
    internal TrialAdmissionController(TimeProvider provider) => clock = provider;

    readonly object sync = new();
    readonly ExperimentalWfpForwardGate gate = new();
    readonly Dictionary<string, DateTimeOffset> grants = new(StringComparer.Ordinal);
    NetworkTopologySnapshot? topology;
    DateTimeOffset end;
    string pathFingerprint = "";
    bool manuallyObservedBlock;
    bool policyInstalled;
    DateTimeOffset? failedTrialDeadline;

    // A failed WFP transaction invalidates the operator's old observation.
    // No automatic re-arming in the same trial window is permitted.
    void MarkPolicyFailure()
    {
        failedTrialDeadline = end;
        policyInstalled = false;
        manuallyObservedBlock = false;
        grants.Clear();
        gate.Dispose();
    }

    // Read-only diagnostics for the administrator's loopback status endpoint.
    // A committed WFP filter is not proof that the phone has Internet;
    // only a second client/device check establishes that.
    public object Snapshot()
    {
        lock (sync)
        {
            return new
            {
                ruleEngineActive = gate.Active,
                installedFilterCount = gate.InstalledFilterCount,
                authorizedClientIps = grants
                    .Where(pair => pair.Value > clock.GetUtcNow())
                    .Select(pair => pair.Key)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                endsUtc = end == default ? (DateTimeOffset?)null : end,
                operatorConfirmedDeny = manuallyObservedBlock,
                requiresNewTrialAfterPolicyFailure = failedTrialDeadline == end && end != default,
                internetReachabilityVerified = false
            };
        }
    }

    public DateTimeOffset TrialEndsAt
    {
        get { lock (sync) return end; }
    }

    public bool IsEnforcementReady
    {
        get { lock (sync) return Ready(); }
    }

    bool Ready() => policyInstalled && gate.Active && manuallyObservedBlock &&
                    topology?.Wan is not null && clock.GetUtcNow() < end;

    public void Apply(NetworkTopologySnapshot selected, DateTimeOffset endsAt, bool blockObserved)
    {
        lock (sync)
        {
            if (endsAt == failedTrialDeadline)
                throw new InvalidOperationException(
                    "WFP policy update failed in this trial. Start a NEW trial; prior block confirmation is invalid.");
            failedTrialDeadline = null;
            // Invalid or expired reconfiguration must revoke existing grants,
            // not merely throw while the previous WFP trial remains active.
            if (selected.Wan is null || selected.AccessPoints.Count != 1 ||
                endsAt <= clock.GetUtcNow())
            {
                MarkPolicyFailure();
                throw new InvalidOperationException(
                    "Invalid or expired WFP trial topology; previous permits revoked.");
            }
            var wan = selected.Wan;
            var downstream = selected.AccessPoints[0];
            // Reject unsafe topology BEFORE any native WFP policy is changed.
            // This protects the experimental path from accidental subnet overlap,
            // host-wide scope, invalid masks and swapped adapter selections.
            var safety = PersistentGuardTopology.Validate(
                new PersistentGuardTopology.Selection(
                    wan.InterfaceIndex, downstream.InterfaceIndex,
                    downstream.Ipv4, downstream.Mask, wan.Ipv4));
            if (!safety.SafeToStage)
            {
                // A previously active permit must not survive an unsafe rebind.
                MarkPolicyFailure();
                throw new InvalidOperationException(
                    "Unsafe selected WFP topology: " + safety.Reason);
            }
            // A previously authorized IP cannot survive a network address/subnet
            // change or withdrawal of the operator's manual block confirmation.
            var nextPath = wan.InterfaceIndex + ":" + wan.Ipv4 + ":" +
                downstream.InterfaceIndex + ":" + downstream.Ipv4 + ":" + downstream.Mask;
            if (nextPath != pathFingerprint || endsAt != end ||
                (manuallyObservedBlock && !blockObserved))
            {
                grants.Clear();
                pathFingerprint = nextPath;
            }
            end = endsAt;
            topology = selected;
            manuallyObservedBlock = blockObserved;
            policyInstalled = false;

            try
            {
                RefreshFilters();
                policyInstalled = gate.Active;
            }
            catch
            {
                // The WFP filter transaction rolls back on error. Never signal
                // that a failed policy update granted Internet.
                MarkPolicyFailure();
                throw;
            }
        }
    }

    void RefreshFilters()
    {
        var now = clock.GetUtcNow();
        foreach (var key in grants.Where(p => p.Value <= now).Select(p => p.Key).ToArray())
            grants.Remove(key);
        if (topology is null || now >= end)
        {
            gate.Dispose();
            return;
        }
        var addresses = grants.Keys.Select(IPAddress.Parse).ToArray();
        gate.Apply(topology, addresses);
    }

    public ValueTask<AdmissionResult> GrantAsync(ClientIdentity client,
        DateTimeOffset expiresAt, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (!Ready())
                return ValueTask.FromResult(new AdmissionResult(false,
                    "The manually confirmed IPv4 WFP trial is not active."));
            if (!IPAddress.TryParse(client.IpAddress, out var ip) ||
                ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                topology!.AccessPoints.Count != 1 || !topology.AccessPoints[0].Contains(ip) ||
                ip.ToString() == topology.AccessPoints[0].Ipv4)
                return ValueTask.FromResult(new AdmissionResult(false,
                    "Client source IPv4 is not within the selected Hotspot subnet."));
            if (expiresAt <= clock.GetUtcNow())
                return ValueTask.FromResult(new AdmissionResult(false, "Session has expired."));

            var address = ip.ToString();
            var allowedUntil = expiresAt < end ? expiresAt : end;
            var previous = grants.TryGetValue(address, out var old) ? old : (DateTimeOffset?)null;
            grants[address] = allowedUntil;
            try
            {
                RefreshFilters();
                policyInstalled = gate.Active;
                return ValueTask.FromResult(new AdmissionResult(Ready(),
                    Ready() ? "Experimental IPv4 WFP permit committed; verify traffic on a real client." :
                              "WFP policy is no longer active."));
            }
            catch
            {
                if (previous.HasValue) grants[address] = previous.Value;
                else grants.Remove(address);
                MarkPolicyFailure();
                return ValueTask.FromResult(new AdmissionResult(false,
                    "WFP permit update failed; this trial was invalidated."));
            }
        }
    }

    public ValueTask<AdmissionResult> RevokeAsync(ClientIdentity client,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (!Ready()) return ValueTask.FromResult(
                new AdmissionResult(false, "WFP trial is no longer active."));
            if (!IPAddress.TryParse(client.IpAddress, out var ip) ||
                ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return ValueTask.FromResult(new AdmissionResult(false, "Invalid client IPv4."));
            var address = ip.ToString();
            var previous = grants.TryGetValue(address, out var old) ? old : (DateTimeOffset?)null;
            grants.Remove(address);
            try
            {
                RefreshFilters();
                policyInstalled = gate.Active;
                return ValueTask.FromResult(new AdmissionResult(Ready(),
                    "Experimental IPv4 WFP permit removed."));
            }
            catch
            {
                if (previous.HasValue) grants[address] = previous.Value;
                MarkPolicyFailure();
                return ValueTask.FromResult(new AdmissionResult(false, "WFP revoke failed; trial invalidated."));
            }
        }
    }

    public void Stop()
    {
        lock (sync)
        {
            policyInstalled = false;
            manuallyObservedBlock = false;
            topology = null;
            end = default;
            pathFingerprint = "";
            grants.Clear();
            failedTrialDeadline = null;
            gate.Dispose();
        }
    }

    public void Dispose() => Stop();
}
