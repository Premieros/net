using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using RestaurantWiFiStorage;
using RestaurantWiFiNetworking;
using System.Net.NetworkInformation;

var count = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
    count++;
}
var dir = Path.Combine(Path.GetTempPath(), "wifi-storage-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    const string legacy = """{"RestaurantName":"Test Cafe","PasswordSalt":"old-salt","Groups":[],"Codes":[{"Code":"ABCD","Uses":0,"MaxUses":1}],"Clients":[{"Phone":"123456","SessionExpiresAt":"2027-01-01T00:00:00Z","AccessCode":"ABCD"}],"FutureField":{"test":true}}""";
    File.WriteAllText(Path.Combine(dir, "v9-data.json"), legacy);
    var store = new StateStore(dir);
    var imported = JsonNode.Parse(store.Read())!.AsObject();
    Check(imported["RestaurantName"]!.GetValue<string>() == "Test Cafe", "Legacy data migration");
    Check(imported["Clients"]![0]!["SessionExpiresAt"]!.GetValue<string>() == "2027-01-01T00:00:00Z", "Session metadata preservation");
    Check(imported["FutureField"]!["test"]!.GetValue<bool>(), "Unknown field preservation");
    Check(File.Exists(Path.Combine(dir, "v9-data.json.pre-sqlite.bak")), "Legacy JSON backup created");
    var successful = new ConcurrentBag<bool>();
    await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() =>
    {
        var another = new StateStore(dir);
        var redeemed = another.Update(root =>
        {
            var code = root["Codes"]![0]!;
            var uses = code["Uses"]!.GetValue<int>();
            if (uses >= code["MaxUses"]!.GetValue<int>()) return false;
            code["Uses"] = uses + 1;
            return true;
        });
        successful.Add(redeemed);
    })));
    Check(successful.Count(x => x) == 1, "Concurrent single-use code redeemed exactly once");
    Check(JsonNode.Parse(store.Read())!["Codes"]![0]!["Uses"]!.GetValue<int>() == 1, "Usage counter committed once");
    Check(JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "v9-data.json")))!["Codes"]![0]!["Uses"]!.GetValue<int>() == 0, "Legacy JSON not mutated after migration");
    var sessionRoot = JsonNode.Parse("""{"Clients":[{"Connected":true,"SessionExpiresAt":"2026-01-01T00:00:00Z"},{"Connected":true,"SessionExpiresAt":"2028-01-01T00:00:00Z"},{"Connected":false,"SessionExpiresAt":"2026-01-01T00:00:00Z"}]}""")!.AsObject();
    Check(SessionLifecycle.Expire(sessionRoot, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)) == 1, "Only elapsed active sessions expire");
    Check(sessionRoot["Clients"]![0]!["SessionStatus"]!.GetValue<string>() == "expired", "Expired session records lifecycle status");
    Check(sessionRoot["Clients"]![1]!["Connected"]!.GetValue<bool>(), "Unexpired sessions remain logically active");
    Check(SessionLifecycle.Expire(sessionRoot, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)) == 0, "Expiry is idempotent");
    // Router is exclusively the uplink; Windows is the gateway for both downstream modes.
    var adapters = new[]
    {
        new AdapterSnapshot("router-uplink", "Ethernet Internet", true, true, true),
        new AdapterSnapshot("client-lan", "Ethernet AP", true, true, false),
        new AdapterSnapshot("client-wifi", "Windows WiFi hotspot", true, true, false,
            NetworkInterfaceType.Wireless80211),
        new AdapterSnapshot("router-over-wifi", "WiFi router uplink", true, true, true,
            NetworkInterfaceType.Wireless80211)
    };
    var ap = TopologyValidator.Assess(new WindowsSharingTopology(
        "router-uplink", "client-lan", ClientAccessMode.ExternalAccessPointBridge,
        ClientsUseWindowsAsGateway: true), adapters);
    Check(ap.ConfigurationConsistent && !ap.NetworkAccessEnforcementReady,
        "Router -> Windows -> bridged AP is correctly recognized without claiming enforced access");
    var hotspot = TopologyValidator.Assess(new WindowsSharingTopology(
        "router-uplink", "client-wifi", ClientAccessMode.WindowsHostedHotspot,
        ClientsUseWindowsAsGateway: true), adapters);
    Check(hotspot.ConfigurationConsistent && !hotspot.NetworkAccessEnforcementReady,
        "Router -> Windows -> PC hotspot is correctly recognized without claiming enforced access");
    var sameNic = TopologyValidator.Assess(new WindowsSharingTopology(
        "router-uplink", "router-uplink", ClientAccessMode.WindowsHostedHotspot, true), adapters);
    Check(!sameNic.ConfigurationConsistent, "The same adapter cannot be selected on both sides");
    var bypass = TopologyValidator.Assess(new WindowsSharingTopology(
        "router-uplink", "client-lan", ClientAccessMode.ExternalAccessPointBridge, false), adapters);
    Check(!bypass.ConfigurationConsistent && !bypass.NetworkAccessEnforcementReady,
        "Bypass routes directly through the router are rejected");
    var upstreamDown = TopologyValidator.Assess(new WindowsSharingTopology(
        "missing", "client-lan", ClientAccessMode.ExternalAccessPointBridge, true), adapters);
    Check(!upstreamDown.ConfigurationConsistent, "Missing uplink adapter is rejected");
    var noGateway = TopologyValidator.Assess(new WindowsSharingTopology(
        "client-lan", "client-wifi", ClientAccessMode.ExternalAccessPointBridge, true), adapters);
    Check(!noGateway.ConfigurationConsistent, "Router-facing NIC must have a gateway");
    var wifiUplink = TopologyValidator.Assess(new WindowsSharingTopology(
        "router-over-wifi", "client-lan", ClientAccessMode.ExternalAccessPointBridge, true), adapters);
    Check(!wifiUplink.ConfigurationConsistent && wifiUplink.Explanation.Contains("wired Ethernet"),
        "Internet uplink requires an Ethernet cable from the router");
    var wifiBridgeOutput = TopologyValidator.Assess(new WindowsSharingTopology(
        "router-uplink", "client-wifi", ClientAccessMode.ExternalAccessPointBridge, true), adapters);
    Check(!wifiBridgeOutput.ConfigurationConsistent, "External AP requires a wired Ethernet downlink");

    var win10 = WindowsCompatibility.Assess(true, 19045);
    Check(win10.Family == WindowsEditionFamily.Windows10 && win10.TargetBuildRecognized
        && !win10.NetworkEnforcementVerified, "Windows 10 recognized without claiming network policy is active");
    var win11 = WindowsCompatibility.Assess(true, 22631);
    Check(win11.Family == WindowsEditionFamily.Windows11 && win11.TargetBuildRecognized
        && !win11.NetworkEnforcementVerified, "Windows 11 recognized without claiming network policy is active");
    var olderWin10 = WindowsCompatibility.Assess(true, 18363);
    Check(olderWin10.Family == WindowsEditionFamily.Windows10 && !olderWin10.TargetBuildRecognized,
        "Older Windows 10 builds are flagged for upgrade");
    var nonWindows = WindowsCompatibility.Assess(false, 0);
    Check(!nonWindows.TargetBuildRecognized, "Non-Windows machines are not deployment targets");
    var unconfigured = new UnconfiguredAdmissionController();
    var grant = await unconfigured.GrantAsync(new ClientIdentity("192.0.2.10"), DateTimeOffset.UtcNow.AddMinutes(10));
    Check(!grant.Enforced, "Unconfigured controller never confirms internet access");

    // Privileged state operations execute a fixed whitelist under SQLite transactions.
    var adminDirectory = Path.Combine(dir, "admin-commands");
    var adminStore = new StateStore(adminDirectory);
    Check(AdminStateCommands.Execute(adminStore, new AdminRequest("initialize")).Success,
        "Privileged service initializes default groups");
    var adminSnapshot = JsonNode.Parse(AdminStateCommands.Execute(adminStore,
        new AdminRequest("read")).Data!)!.AsObject();
    Check(adminSnapshot["Groups"]!.AsArray().Count == 3, "Default groups initialized over admin commands");
    var groupId = Guid.NewGuid().ToString();
    var groupNode = new JsonObject
    {
        ["Id"] = groupId, ["Name"] = "Special", ["Kind"] = "customer", ["Minutes"] = 30,
        ["QuotaMb"] = 1024, ["DownloadMbps"] = 3.0m, ["UploadMbps"] = 2.0m,
        ["MaxDevices"] = 1, ["MaxUsesPerDevice"] = 1, ["Enabled"] = true
    };
    Check(AdminStateCommands.Execute(adminStore, new AdminRequest("add_group",
        new JsonObject { ["Group"] = groupNode.DeepClone() })).Success,
        "Service can add an approved group");
    var submittedCode = new JsonObject
    {
        ["Id"] = Guid.NewGuid().ToString(), ["GroupId"] = groupId,
        ["Code"] = "123456", ["MaxUses"] = 1, ["Uses"] = 0, ["Enabled"] = true
    };
    var codeRequest = new AdminRequest("add_codes", new JsonObject
    {
        ["GroupId"] = groupId, ["Codes"] = new JsonArray(submittedCode.DeepClone())
    });
    Check(AdminStateCommands.Execute(adminStore, codeRequest).Success,
        "Service can add a code to an enabled group");
    bool duplicateRejected = false;
    try { AdminStateCommands.Execute(adminStore, codeRequest); }
    catch (ArgumentException) { duplicateRejected = true; }
    Check(duplicateRejected, "Service rejects duplicate code without committing changes");
    bool replacementRejected = false;
    try { AdminStateCommands.Execute(adminStore, new AdminRequest("replace_all",
        new JsonObject { ["RestaurantName"] = "Attacker" })); }
    catch (ArgumentException) { replacementRejected = true; }
    Check(replacementRejected, "Arbitrary full-state replacement command is forbidden");
    var currentAdmin = JsonNode.Parse(adminStore.Read())!;
    Check(currentAdmin["Codes"]!.AsArray().Count == 1 && currentAdmin["RestaurantName"] is null,
        "Rejected administrative operations leave prior data intact");
    var restarted = new StateStore(dir);
    Check(JsonNode.Parse(restarted.Read())!["Codes"]![0]!["Uses"]!.GetValue<int>() == 1, "Persistent state after restart");
    var invalidDir = Path.Combine(dir, "invalid");
    Directory.CreateDirectory(invalidDir);
    File.WriteAllText(Path.Combine(invalidDir, "v9-data.json"), "{broken");
    bool failed = false;
    try { _ = new StateStore(invalidDir); } catch (System.Text.Json.JsonException) { failed = true; }
    Check(failed, "Malformed legacy JSON fails rather than silently resetting data");
    Console.WriteLine($"All {count} storage integration tests passed.");
}
finally
{
    try { Directory.Delete(dir, recursive: true); } catch { }
}
