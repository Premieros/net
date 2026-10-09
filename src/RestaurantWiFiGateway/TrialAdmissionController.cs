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
internal sealed class TrialAdmissionController : INetworkAdmissionController, IDisposable
{
    readonly object sync = new();
    readonly ExperimentalWfpForwardGate gate = new();
    readonly Dictionary<string, DateTimeOffset> grants = new(StringComparer.Ordinal);
    NetworkTopologySnapshot? topology;
    DateTimeOffset end;
    string pathFingerprint = "";
    bool manuallyObservedBlock;
    bool policyInstalled;

    public bool IsEnforcementReady
    {
        get { lock (sync) return Ready(); }
    }

    bool Ready() => policyInstalled && gate.Active && manuallyObservedBlock &&
                    topology?.Wan is not null && DateTimeOffset.UtcNow < end;

    public void Apply(NetworkTopologySnapshot selected, DateTimeOffset endsAt, bool blockObserved)
    {
        lock (sync)
        {
            var wan = selected.Wan ?? throw new InvalidOperationException("Missing upstream interface.");
            if (selected.AccessPoints.Count != 1)
                throw new InvalidOperationException("Trial must target one selected Hotspot/AP only.");

            var nextPath = wan.InterfaceIndex + ":" + selected.AccessPoints[0].InterfaceIndex;
            if (nextPath != pathFingerprint || endsAt != end)
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
                policyInstalled = false;
                throw;
            }
        }
    }

    void RefreshFilters()
    {
        var now = DateTimeOffset.UtcNow;
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
                topology!.AccessPoints.Count != 1 || !topology.AccessPoints[0].Contains(ip))
                return ValueTask.FromResult(new AdmissionResult(false,
                    "Client source IPv4 is not within the selected Hotspot subnet."));
            if (expiresAt <= DateTimeOffset.UtcNow)
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
                policyInstalled = false;
                return ValueTask.FromResult(new AdmissionResult(false,
                    "WFP permit could not be committed."));
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
                policyInstalled = false;
                return ValueTask.FromResult(new AdmissionResult(false, "WFP revoke failed."));
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
            gate.Dispose();
        }
    }

    public void Dispose() => Stop();
}
