using System.Diagnostics;

namespace RestaurantWiFiGateway;

/// <summary>
/// Restrict ProgramData data to SYSTEM and local Administrators before creating SQLite.
/// Windows service must run elevated (normally as LocalSystem).
/// </summary>
internal static class GatewayDataSecurity
{
    public static void Protect()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Gateway data permissions require Windows.");
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Restaurant WiFi Control");
        Directory.CreateDirectory(folder);
        var root = new DirectoryInfo(folder);
        var candidates = new List<FileSystemInfo> { root };
        candidates.AddRange(root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories));
        if (candidates.Any(x => (x.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new IOException("Refusing to configure ACLs when data directory contains reparse points.");

        // SID form works on English and non-English Windows installations.
        // Grant first so the service never locks itself out while removing inherited grants.
        Run(folder, "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", "/T", "/Q");
        Run(folder, "/inheritance:r", "/T", "/Q");
        Run(folder, "/remove:g", "*S-1-1-0", "*S-1-5-11", "*S-1-5-32-545", "/T", "/Q");
    }

    static void Run(string folder, params string[] options)
    {
        var processInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "icacls.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        processInfo.ArgumentList.Add(folder);
        foreach (var value in options) processInfo.ArgumentList.Add(value);
        using var process = Process.Start(processInfo) ??
            throw new IOException("Cannot start Windows ACL tool.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15_000) || process.ExitCode != 0)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new IOException("Cannot protect Gateway data directory: " + stderr + stdout);
        }
    }
}
