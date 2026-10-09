using System.Diagnostics;

namespace RestaurantWiFiGateway;

/// <summary>
/// Keep the Gateway SQLite data private to SYSTEM and local Administrators.
/// Do NOT recursively rewrite ACLs on unrelated/legacy user files: old
/// v9-data.json can have a protected DACL from a prior installation, and
/// icacls /T used to crash the Windows service during every startup.
/// </summary>
internal static class GatewayDataSecurity
{
    private static readonly string[] SqliteFileSuffixes =
        ["", "-wal", "-shm", "-journal"];

    public static void Protect()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Gateway data permissions require Windows.");

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Restaurant WiFi Control");

        Directory.CreateDirectory(folder);
        if (IsReparsePoint(folder))
            throw new IOException("Refusing to use a reparse-point Gateway data directory.");

        // First repair any EXISTING database file ACLs, before tightening the
        // directory. Only files owned by the gateway database may be modified.
        // Never touch v9-data.json or its backup: these are immutable migration inputs.
        foreach (var suffix in SqliteFileSuffixes)
        {
            var file = Path.Combine(folder, "wifi-state.db" + suffix);
            if (!File.Exists(file)) continue;
            if (IsReparsePoint(file))
                throw new IOException("Refusing to use a reparse-point Gateway database file.");
            ProtectExistingDatabaseFile(file);
        }

        // New DB and temporary SQLite files inherit access for SYSTEM/Admins only.
        // NO /T flag: recursively applying icacls to old user-owned JSON was the
        // cause of Windows Event 1026 (IOException/Access denied) on user hardware.
        Run(folder, "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F");
        Run(folder, "/grant", "*S-1-5-18:F", "*S-1-5-32-544:F");
        Run(folder, "/inheritance:r");
        Run(folder, "/remove:g", "*S-1-1-0", "*S-1-5-11", "*S-1-5-32-545");

        // SQLite files created previously remain explicitly protected.
        // A legacy file with an inaccessible DACL will now be left untouched
        // until the migration layer specifically needs to read it.
    }

    private static void ProtectExistingDatabaseFile(string file)
    {
        Run(file, "/grant:r", "*S-1-5-18:F", "*S-1-5-32-544:F");
        Run(file, "/inheritance:r");
        Run(file, "/remove:g", "*S-1-1-0", "*S-1-5-11", "*S-1-5-32-545");
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void Run(string target, params string[] arguments)
    {
        var processInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.System), "icacls.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        processInfo.ArgumentList.Add(target);
        foreach (var argument in arguments) processInfo.ArgumentList.Add(argument);

        using var process = Process.Start(processInfo) ??
            throw new IOException("Cannot start Windows ACL tool.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15_000) || process.ExitCode != 0)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new IOException("Cannot protect Gateway SQLite data: " + stderr + stdout);
        }
    }
}
