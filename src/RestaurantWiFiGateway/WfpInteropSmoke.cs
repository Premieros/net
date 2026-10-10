using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RestaurantWiFiGateway;

/// <summary>
/// Actual Windows Filtering Platform API smoke test: no router, AP or phone needed.
/// Uses deliberately nonexistent interface indices, so these filters cannot
/// match real traffic on the build machine. A dynamic WFP session tears them
/// down when the process exits, including after exceptions.
/// Does NOT verify packet delivery through Windows ICS or an external AP.
/// </summary>
internal static class WfpInteropSmoke
{
    public static void Run()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WFP smoke requires Windows.");

        const int fictitiousWan = int.MaxValue - 10;
        const int fictitiousAp = int.MaxValue - 11;
        var indices = NetworkInterface.GetAllNetworkInterfaces()
            .Select(x => x.GetIPProperties().GetIPv4Properties()?.Index ?? -1)
            .ToHashSet();
        if (indices.Contains(fictitiousWan) || indices.Contains(fictitiousAp))
            throw new InvalidOperationException("Reserved synthetic interface index unexpectedly exists.");

        var selected = new NetworkTopologySnapshot
        {
            Wan = new NetworkAdapterSnapshot
            {
                InterfaceIndex = fictitiousWan, Ipv4 = "198.51.100.1",
                Mask = "255.255.255.0", IsUp = true
            },
            AccessPoints = new List<NetworkAdapterSnapshot>
            {
                new()
                {
                    InterfaceIndex = fictitiousAp, Ipv4 = "192.168.137.1",
                    Mask = "255.255.255.0", IsUp = true
                }
            }
        };

        // An old manual phone observation must never be reused after a
        // failed trial. A new deadline represents a fresh operator request.
        var latch = new TrialFailureLatch();
        var failedEnd = DateTimeOffset.UtcNow.AddMinutes(2);
        Assert(!latch.IsRejected(failedEnd), "Fresh trial has no failure latch");
        Assert(latch.Reject(failedEnd) && latch.IsRejected(failedEnd),
            "Failure invalidates this exact two-minute trial window");
        Assert(!latch.Reject(failedEnd) &&
               !latch.IsRejected(failedEnd.AddSeconds(1)),
            "Retrying failed window stays rejected; new trial window can proceed");

        using var gate = new ExperimentalWfpForwardGate();
        var first = IPAddress.Parse("192.168.137.101");
        var second = IPAddress.Parse("192.168.137.102");

        gate.Apply(selected, Array.Empty<IPAddress>());
        Assert(gate.Active && gate.InstalledFilterCount == 1,
            "Native default deny WFP filter installed");

        gate.Apply(selected, new[] { first });
        Assert(gate.Active && gate.InstalledFilterCount > 2,
            "Native non-overlapping IPv4 deny-prefix filters and first client permit installed");

        gate.Apply(selected, new[] { first, second });
        Assert(gate.Active && gate.InstalledFilterCount > 2,
            "Native second client permit installed in one transaction");

        gate.Apply(selected, new[] { second });
        Assert(gate.Active && gate.InstalledFilterCount > 2,
            "Native revocation of first client updated transaction");

        gate.Apply(selected, Array.Empty<IPAddress>());
        Assert(gate.Active && gate.InstalledFilterCount == 1,
            "Native reversion to blanket deny after removing both permits");

        // End-to-end trial admission, using the very same native WFP gate
        // and exact interface pair as production, but only synthetic NICs.
        // This proves that the controller updates its grants and filter set;
        // no packets are sent and no real adapter is affected.
        var clock = new TrialSmokeClock(DateTimeOffset.UtcNow);
        using (var admission = new TrialAdmissionController(clock))
        {
            var end = clock.GetUtcNow().AddMinutes(1);
            admission.Apply(selected, end, blockObserved: false);
            Assert(!admission.IsEnforcementReady,
                "Voucher admission fails closed until administrator confirms the block");
            var blockedBeforeConfirm = admission.GrantAsync(
                new RestaurantWiFiNetworking.ClientIdentity(first.ToString()), end).GetAwaiter().GetResult();
            Assert(!blockedBeforeConfirm.Enforced,
                "Unconfirmed trial does not grant a phone Internet access");

            admission.Apply(selected, end, blockObserved: true);
            Assert(admission.IsEnforcementReady,
                "Confirmed synthetic trial is eligible for disposable voucher admission");

            var illegalGateway = admission.GrantAsync(
                new RestaurantWiFiNetworking.ClientIdentity("192.168.137.1"), end)
                .GetAwaiter().GetResult();
            var illegalWan = admission.GrantAsync(
                new RestaurantWiFiNetworking.ClientIdentity("198.51.100.45"), end)
                .GetAwaiter().GetResult();
            Assert(!illegalGateway.Enforced && !illegalWan.Enforced,
                "Gateway and external IP addresses cannot be granted as clients");

            var grantedA = admission.GrantAsync(
                new RestaurantWiFiNetworking.ClientIdentity(first.ToString()), end).GetAwaiter().GetResult();
            Assert(grantedA.Enforced, "Phone A's individual IP grant is committed by real WFP");

            var grantedB = admission.GrantAsync(
                new RestaurantWiFiNetworking.ClientIdentity(second.ToString()), end).GetAwaiter().GetResult();
            Assert(grantedB.Enforced, "Phone B's individual IP grant coexists with Phone A");

            var snapshot = System.Text.Json.JsonSerializer.SerializeToElement(admission.Snapshot());
            var permitted = snapshot.GetProperty("authorizedClientIps").EnumerateArray()
                .Select(x => x.GetString()).ToArray();
            Assert(permitted.SequenceEqual(new[] { first.ToString(), second.ToString() }) &&
                snapshot.GetProperty("installedFilterCount").GetInt32() > 2,
                "Controller and native WFP agree that exactly two IPs are permitted");

            var shortIp = IPAddress.Parse("192.168.137.103");
            var shortGrant = admission.GrantAsync(
                new RestaurantWiFiNetworking.ClientIdentity(shortIp.ToString()),
                clock.GetUtcNow().AddSeconds(8)).GetAwaiter().GetResult();
            Assert(shortGrant.Enforced, "Eight-second voucher installs one temporary WFP IP permit");
            clock.Advance(TimeSpan.FromSeconds(10));
            admission.Apply(selected, end, blockObserved: true);
            snapshot = System.Text.Json.JsonSerializer.SerializeToElement(admission.Snapshot());
            permitted = snapshot.GetProperty("authorizedClientIps").EnumerateArray()
                .Select(x => x.GetString()).ToArray();
            Assert(permitted.SequenceEqual(new[] { first.ToString(), second.ToString() }),
                "Periodic reconciliation removes only expired source IP, preserving other grants");

            var revokedA = admission.RevokeAsync(
                new RestaurantWiFiNetworking.ClientIdentity(first.ToString())).GetAwaiter().GetResult();
            Assert(revokedA.Enforced, "Phone A can be independently revoked");
            snapshot = System.Text.Json.JsonSerializer.SerializeToElement(admission.Snapshot());
            permitted = snapshot.GetProperty("authorizedClientIps").EnumerateArray()
                .Select(x => x.GetString()).ToArray();
            Assert(permitted.SequenceEqual(new[] { second.ToString() }),
                "Revoking A preserves B and no other source IPv4 authorization");

            admission.Apply(selected, end, blockObserved: false);
            snapshot = System.Text.Json.JsonSerializer.SerializeToElement(admission.Snapshot());
            Assert(!admission.IsEnforcementReady &&
                snapshot.GetProperty("authorizedClientIps").GetArrayLength() == 0 &&
                snapshot.GetProperty("installedFilterCount").GetInt32() == 1,
                "Withdrawal of manual deny verification clears all active per-IP permits");

            admission.Apply(selected, end, blockObserved: true);
            var permittedAgain = admission.GrantAsync(
                new RestaurantWiFiNetworking.ClientIdentity(second.ToString()), end).GetAwaiter().GetResult();
            Assert(permittedAgain.Enforced,
                "New authorization works after fresh confirmation without retaining old grants");

            var readdressed = new NetworkTopologySnapshot
            {
                Wan = selected.Wan,
                AccessPoints = new List<NetworkAdapterSnapshot>
                {
                    new()
                    {
                        InterfaceIndex = fictitiousAp, Ipv4 = "192.168.138.1",
                        Mask = "255.255.255.0", IsUp = true
                    }
                }
            };
            admission.Apply(readdressed, end, blockObserved: true);
            snapshot = System.Text.Json.JsonSerializer.SerializeToElement(admission.Snapshot());
            Assert(snapshot.GetProperty("authorizedClientIps").GetArrayLength() == 0 &&
                   snapshot.GetProperty("installedFilterCount").GetInt32() == 1,
                "Subnet change invalidates previous device grants without unlocking all clients");

            admission.Stop();
            Assert(!admission.IsEnforcementReady &&
                System.Text.Json.JsonSerializer.SerializeToElement(admission.Snapshot())
                    .GetProperty("installedFilterCount").GetInt32() == 0,
                "Stopping the trial removes dynamic WFP rules and clears all permissions");

            // Unsafe adapter selections must be rejected BEFORE touching WFP.
            // Synthetic indices ensure no real host networking is affected.
            var unsafeTopology = new NetworkTopologySnapshot
            {
                Wan = selected.Wan,
                AccessPoints = new List<NetworkAdapterSnapshot>
                {
                    new()
                    {
                        InterfaceIndex = fictitiousAp,
                        Ipv4 = "192.168.137.1",
                        Mask = "255.0.255.0",
                        IsUp = true
                    }
                }
            };
            var unsafeRejected = false;
            try
            {
                admission.Apply(unsafeTopology, clock.GetUtcNow().AddMinutes(1),
                    blockObserved: true);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("Unsafe selected WFP topology",
                    StringComparison.Ordinal))
            {
                unsafeRejected = true;
            }
            Assert(unsafeRejected && !admission.IsEnforcementReady &&
                System.Text.Json.JsonSerializer.SerializeToElement(admission.Snapshot())
                    .GetProperty("installedFilterCount").GetInt32() == 0,
                "Unsafe network mask fails closed without persistent trial permits");

            // The old two-minute trial must not confer new permissions.
            admission.Apply(selected, DateTimeOffset.UtcNow.AddSeconds(-1),
                blockObserved: true);
            Assert(!admission.IsEnforcementReady,
                "Expired trials never return a code-ready state");
        }

        Console.WriteLine("PASS: WFP native ABI smoke used only nonexistent synthetic interface indices.");
        Console.WriteLine("NOTE: Does not establish client Internet reachability, fail-closed safety, or paid Wi-Fi readiness.");
    }

    sealed class TrialSmokeClock : TimeProvider
    {
        DateTimeOffset now;
        public TrialSmokeClock(DateTimeOffset initial) => now = initial;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan interval) => now = now.Add(interval);
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("WFP interop smoke: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
