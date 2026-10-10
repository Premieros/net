using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace RestaurantWiFiNetworking;

/// <summary>
/// Builds NON-OVERLAPPING IPv4 source ranges for a temporary WFP forward gate.
/// At WFP a BLOCK filter can veto a PERMIT filter in another arbitration step,
/// even when the permit has a higher weight. Never overlap default deny with
/// the address we intend to allow.
/// This pure policy planner does not install WFP rules or prove traffic passes.
/// </summary>
public static class Ipv4TrialRulePlanner
{
    public const int MaxTrialPermits = 8;

    public readonly record struct Ipv4Prefix(uint Network, int Length)
    {
        public uint Mask => Length switch
        {
            0 => 0u,
            >= 1 and <= 32 => uint.MaxValue << (32 - Length),
            _ => throw new ArgumentOutOfRangeException(nameof(Length))
        };

        public bool Contains(IPAddress ip) =>
            ip.AddressFamily == AddressFamily.InterNetwork &&
            (ToNetworkUInt32(ip) & Mask) == Network;

        public override string ToString() =>
            new IPAddress(new byte[]
            {
                (byte)(Network >> 24), (byte)(Network >> 16),
                (byte)(Network >> 8), (byte)Network
            }) + "/" + Length;
    }

    public static uint ToNetworkUInt32(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("IPv4 address required.", nameof(ip));
        var bytes = ip.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) | bytes[3];
    }

    /// <summary>
    /// Partitions 0.0.0.0/0 EXCEPT every permitted single IPv4 host into
    /// disjoint CIDR ranges. Thus NONE of the WFP BLOCK rules matches any
    /// permitted host, and EVERY other IPv4 address matches one deny rule.
    /// </summary>
    public static IReadOnlyList<Ipv4Prefix> DenyAllExcept(IEnumerable<IPAddress> permitted)
    {
        ArgumentNullException.ThrowIfNull(permitted);
        var allowed = permitted.Select(ToNetworkUInt32).Distinct().Order().ToArray();
        if (allowed.Length > MaxTrialPermits)
            throw new ArgumentException("Too many simultaneous test devices.", nameof(permitted));

        var prefixes = new List<Ipv4Prefix>();
        ulong first = 0;
        foreach (var address in allowed)
        {
            if ((ulong)address > first)
                AppendRange(prefixes, first, (ulong)address - 1);
            first = (ulong)address + 1;
        }
        if (first <= uint.MaxValue)
            AppendRange(prefixes, first, uint.MaxValue);
        return prefixes;
    }

    static void AppendRange(List<Ipv4Prefix> result, ulong start, ulong last)
    {
        while (start <= last)
        {
            var trailingZeros = BitOperations.TrailingZeroCount((uint)start);
            var availableBits = BitOperations.Log2(last - start + 1);
            var hostBits = Math.Min(trailingZeros, availableBits);
            result.Add(new Ipv4Prefix((uint)start, 32 - hostBits));
            start += 1UL << hostBits;
        }
    }
}
