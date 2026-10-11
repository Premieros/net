namespace RestaurantWiFiNetworking;

public enum WindowsEditionFamily
{
    Windows10,
    Windows11,
    Other
}

public sealed record WindowsCompatibilityAssessment(
    WindowsEditionFamily Family,
    int Build,
    bool TargetBuildRecognized,
    bool NetworkEnforcementVerified,
    string Message);

/// <summary>
/// Read-only compatibility hint, not a Windows ICS / firewall / hotspot capability probe.
/// Build numbers alone cannot prove that the device supports internet sharing or filtering.
/// </summary>
public static class WindowsCompatibility
{
    public static WindowsCompatibilityAssessment InspectCurrent() =>
        Assess(OperatingSystem.IsWindows(), Environment.OSVersion.Version.Build);

    public static WindowsCompatibilityAssessment Assess(bool isWindows, int build)
    {
        if (!isWindows || build < 10240)
            return new(WindowsEditionFamily.Other, build, false, false,
                "This network setup is designed for Windows 10 and Windows 11.");

        if (build >= 22000)
            return new(WindowsEditionFamily.Windows11, build, true, false,
                "Windows 11 detected. Update to a serviced Windows release. " +
                "Adapter presence alone does not verify ICS, NAT, Wi-Fi hotspot, or policy enforcement.");

        if (build >= 19045)
            return new(WindowsEditionFamily.Windows10, build, true, false,
                "Windows 10 (22H2-era build) detected. Standard Windows 10 support ended " +
                "October 14, 2025; confirm applicable Extended Security Updates or LTSC servicing. " +
                "ICS, NAT, hotspot and admission policy still require device testing.");

        return new(WindowsEditionFamily.Windows10, build, false, false,
            "An older Windows 10 build was detected. Update before testing this application. " +
            "No network traffic enforcement has been verified.");
    }
}
