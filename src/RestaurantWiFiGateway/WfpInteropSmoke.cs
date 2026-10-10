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

        Console.WriteLine("PASS: WFP native ABI smoke used only nonexistent synthetic interface indices.");
        Console.WriteLine("NOTE: Does not establish client Internet reachability, fail-closed safety, or paid Wi-Fi readiness.");
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("WFP interop smoke: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
