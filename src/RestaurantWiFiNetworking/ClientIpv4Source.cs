using System.Net;
using System.Net.Sockets;

namespace RestaurantWiFiNetworking;

/// <summary>
/// Normalizes IPv4 addresses reported by Windows HTTP stacks, which may
/// represent an IPv4 peer as ::ffff:a.b.c.d. Network authorization is
/// currently IPv4 ONLY. A source IP is not durable proof of device identity.
/// </summary>
public static class ClientIpv4Source
{
    public static bool TryNormalize(string? raw, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(raw) ||
            !IPAddress.TryParse(raw, out var parsed)) return false;
        return TryNormalize(parsed, out normalized);
    }

    public static bool TryNormalize(IPAddress? source, out string normalized)
    {
        normalized = "";
        if (source is null) return false;
        if (source.IsIPv4MappedToIPv6) source = source.MapToIPv4();
        if (source.AddressFamily != AddressFamily.InterNetwork ||
            source.Equals(IPAddress.Any) || source.Equals(IPAddress.Broadcast))
            return false;
        normalized = source.ToString();
        return true;
    }
}
