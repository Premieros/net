using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using RestaurantWiFiStorage;
using RestaurantWiFiNetworking;

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
    // AP location is independent of which device is the internet gateway.
    var pcAp = TopologyValidator.Assess(new AccessPointTopology(
        AccessPointOrigin.WindowsComputer, InternetGateway.WindowsComputer,
        WindowsForwardsClientTraffic: true, RouterHasSupportedIntegration: false));
    Check(pcAp.Backend == EnforcementBackend.WindowsPacketFilter && !pcAp.NetworkAccessEnforcementReady,
        "Windows-origin AP requires a real packet filtering provider");
    var bridgeAp = TopologyValidator.Assess(new AccessPointTopology(
        AccessPointOrigin.ExternalAccessPoint, InternetGateway.WindowsComputer,
        WindowsForwardsClientTraffic: true, RouterHasSupportedIntegration: false));
    Check(bridgeAp.Backend == EnforcementBackend.WindowsPacketFilter && !bridgeAp.NetworkAccessEnforcementReady,
        "Bridge AP behind Windows uses Windows gateway enforcement path");
    var routerAp = TopologyValidator.Assess(new AccessPointTopology(
        AccessPointOrigin.ExternalAccessPoint, InternetGateway.Router,
        WindowsForwardsClientTraffic: false, RouterHasSupportedIntegration: true));
    Check(routerAp.Backend == EnforcementBackend.RouterIntegration && !routerAp.NetworkAccessEnforcementReady,
        "Router acting as gateway needs a tested vendor-specific integration");
    var bypass = TopologyValidator.Assess(new AccessPointTopology(
        AccessPointOrigin.ExternalAccessPoint, InternetGateway.WindowsComputer,
        WindowsForwardsClientTraffic: false, RouterHasSupportedIntegration: false));
    Check(!bypass.NetworkAccessEnforcementReady && bypass.Explanation.Contains("does not traverse Windows"),
        "AP bypassing Windows cannot be controlled by Windows-only policy");
    var unconfigured = new UnconfiguredAdmissionController();
    var grant = await unconfigured.GrantAsync(new ClientIdentity("192.0.2.10"), DateTimeOffset.UtcNow.AddMinutes(10));
    Check(!grant.Enforced, "Unconfigured controller never confirms internet access");

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
