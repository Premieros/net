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
    var stagedDir = Path.Combine(dir, "installer-secure-migration");
    Directory.CreateDirectory(stagedDir);
    File.WriteAllText(Path.Combine(stagedDir, "v9-data.import.json"), legacy);
    var stagedStore = new StateStore(stagedDir);
    Check(JsonNode.Parse(stagedStore.Read())!["RestaurantName"]!.GetValue<string>() == "Test Cafe",
        "Installer-staged legacy JSON migrates when original is not readable or visible");
    Check(File.Exists(Path.Combine(stagedDir, "v9-data.json.pre-sqlite.bak")),
        "Installer-staged migration preserves a legacy backup");
    Check(JsonNode.Parse(File.ReadAllText(Path.Combine(stagedDir, "v9-data.json.pre-sqlite.bak")))!
        ["FutureField"]!["test"]!.GetValue<bool>(),
        "Installer-staged backup retains unknown legacy metadata");

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
    Check(wifiUplink.ConfigurationConsistent && !wifiUplink.NetworkAccessEnforcementReady,
        "Test PC accepts Wi-Fi router uplink, while packet forwarding remains unverified");
    var wirelessUpstreamWithHotspot = TopologyValidator.Assess(new WindowsSharingTopology(
        "router-over-wifi", "client-wifi", ClientAccessMode.WindowsHostedHotspot, true), adapters);
    Check(wirelessUpstreamWithHotspot.ConfigurationConsistent &&
          !wirelessUpstreamWithHotspot.NetworkAccessEnforcementReady,
        "Wi-Fi uplink plus different virtual Wi-Fi downstream interface is a candidate only");
    var sameWirelessAdapter = TopologyValidator.Assess(new WindowsSharingTopology(
        "router-over-wifi", "router-over-wifi", ClientAccessMode.WindowsHostedHotspot, true), adapters);
    Check(!sameWirelessAdapter.ConfigurationConsistent,
        "One identical Wi-Fi adapter ID cannot be both uplink and hotspot output");

    var wifiBridgeOutput = TopologyValidator.Assess(new WindowsSharingTopology(
        "router-uplink", "client-wifi", ClientAccessMode.ExternalAccessPointBridge, true), adapters);
    Check(!wifiBridgeOutput.ConfigurationConsistent, "External AP requires a wired Ethernet downlink");

    var subnetAdapters = new[]
    {
        new AdapterSnapshot("uplink", "Router LAN", true, true, true,
            NetworkInterfaceType.Ethernet, "192.168.1.10", "192.168.1.1", 24),
        new AdapterSnapshot("out", "Customer AP LAN", true, true, false,
            NetworkInterfaceType.Ethernet, "192.168.50.1", null, 24)
    };
    var wiredPath = new WindowsSharingTopology(
        "uplink", "out", ClientAccessMode.ExternalAccessPointBridge, true);
    var preflight = GatewayPreflight.Check(wiredPath, subnetAdapters);
    Check(preflight.WiringAppearsValid && !preflight.ActualForwardingVerified &&
          !preflight.AdmissionRulesVerified,
        "Disjoint Ethernet subnets pass wiring-only inspection; routing and enforcement remain unverified");
    var overlap = GatewayPreflight.Check(wiredPath, new[]
    {
        subnetAdapters[0],
        subnetAdapters[1] with { Ipv4Address = "192.168.1.80" }
    });
    Check(!overlap.WiringAppearsValid && overlap.Issues.Any(x => x.Contains("overlap")),
        "Overlapping upstream and AP client subnets are blocked");
    var downlinkGateway = GatewayPreflight.Check(wiredPath, new[]
    {
        subnetAdapters[0],
        subnetAdapters[1] with { HasIpv4DefaultGateway = true, Ipv4Gateway = "192.168.50.254" }
    });
    Check(!downlinkGateway.WiringAppearsValid && downlinkGateway.Issues.Any(x => x.Contains("default gateway")),
        "Downstream default gateway is flagged as bypass risk");
    var missingPrefix = GatewayPreflight.Check(wiredPath, new[]
    {
        subnetAdapters[0],
        subnetAdapters[1] with { Ipv4PrefixLength = null }
    });
    Check(!missingPrefix.WiringAppearsValid, "Incomplete subnet data never passes preflight");
    var wifiSubnetAdapters = new[]
    {
        new AdapterSnapshot("wifi-in", "Router Wi-Fi", true, true, true,
            NetworkInterfaceType.Wireless80211, "192.168.1.16", "192.168.1.1", 24),
        new AdapterSnapshot("ap-ethernet-out", "Bridge AP", true, true, false,
            NetworkInterfaceType.Ethernet, "192.168.137.1", null, 24)
    };
    var wifiPath = new WindowsSharingTopology(
        "wifi-in", "ap-ethernet-out", ClientAccessMode.ExternalAccessPointBridge, true);
    var wifiPreflight = GatewayPreflight.Check(wifiPath, wifiSubnetAdapters);
    Check(wifiPreflight.WiringAppearsValid && !wifiPreflight.AdmissionRulesVerified,
        "Wi-Fi internet in -> PC -> wired bridged AP passes read-only wiring preflight");
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

    // Code admission is never enabled merely by a network selection. A real
    // operator must confirm a two-minute WFP block trial on test hardware.
    var trialDirectory = Path.Combine(dir, "ipv4-trial-command-tests");
    var trialStore = new StateStore(trialDirectory);
    var confirmationWithoutTrialRejected = false;
    try
    {
        AdminStateCommands.Execute(trialStore, new AdminRequest("confirm_wfp_trial_block_observed"));
    }
    catch (ArgumentException) { confirmationWithoutTrialRejected = true; }
    Check(confirmationWithoutTrialRejected, "Operator cannot confirm IPv4 deny without an active trial");
    var trialPayload = new JsonObject
    {
        ["Network"] = new JsonObject
        {
            ["UpstreamAdapterId"] = "wan-adapter",
            ["DownstreamAdapterId"] = "hotspot-adapter",
            ["AccessMode"] = 0,
            ["ExperimentalWfpTrialUntilUtc"] = DateTimeOffset.UtcNow.AddSeconds(90).ToString("O"),
            ["ExperimentalWfpTrialBlockingObserved"] = true
        }
    };
    AdminStateCommands.Execute(trialStore, new AdminRequest("set_network", trialPayload));
    Check(JsonNode.Parse(trialStore.Read())!["Network"]!["ExperimentalWfpTrialBlockingObserved"]!
        .GetValue<bool>() == false, "Selecting adapters never self-confirms customer-code blocking");
    AdminStateCommands.Execute(trialStore, new AdminRequest("confirm_wfp_trial_block_observed"));
    Check(JsonNode.Parse(trialStore.Read())!["Network"]!["ExperimentalWfpTrialBlockingObserved"]!
        .GetValue<bool>(), "Manual field observation is explicitly recorded");
    AdminStateCommands.Execute(trialStore, new AdminRequest("stop_wfp_trial"));
    Check(JsonNode.Parse(trialStore.Read())!["Network"]!["ExperimentalWfpTrialBlockingObserved"]!
        .GetValue<bool>() == false, "Trial stop clears manual code-activation confirmation");

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
    var noBackend = new AccessRedemptionService(adminStore, new UnconfiguredAdmissionController());
    var requestForCode = new RedemptionRequest("Alice", "123456789", "123456", "192.0.2.5", "test-device");
    var unavailable = await noBackend.RedeemAsync(requestForCode);
    Check(!unavailable.Success && JsonNode.Parse(adminStore.Read())!["Codes"]![0]!["Uses"]!.GetValue<int>() == 0,
        "Unconfigured network controller does not consume any access code");
    var refusesGrant = new FakeAdmissionController(false);
    var rejected = await new AccessRedemptionService(adminStore, refusesGrant).RedeemAsync(requestForCode);
    Check(!rejected.Success && JsonNode.Parse(adminStore.Read())!["Codes"]![0]!["Uses"]!.GetValue<int>() == 0,
        "Denied network grant releases reservation without consuming code");
    var acceptsGrant = new FakeAdmissionController(true);
    var confirmed = await new AccessRedemptionService(adminStore, acceptsGrant).RedeemAsync(requestForCode);
    Check(confirmed.Success && acceptsGrant.GrantCount == 1 &&
        JsonNode.Parse(adminStore.Read())!["Codes"]![0]!["Uses"]!.GetValue<int>() == 1,
        "Confirmed network grant commits exactly one access code use");
    var authorizedClient = JsonNode.Parse(adminStore.Read())!["Clients"]![0]!;
    Check(authorizedClient["Connected"]!.GetValue<bool>() &&
          authorizedClient["SessionStatus"]!.GetValue<string>() == "network-authorized",
        "Only a confirmed network grant creates an active client");
    var secondAttempt = await new AccessRedemptionService(adminStore, acceptsGrant)
        .RedeemAsync(new RedemptionRequest("Bob", "123456788", "123456", "192.0.2.6", "other-device"));
    Check(!secondAttempt.Success, "Redeeming a one-time code again is rejected");
    var codesAfter = JsonNode.Parse(adminStore.Read())!["Codes"]!.AsArray();
    Check(codesAfter.Count == 1 && codesAfter[0]!["Uses"]!.GetValue<int>() == 1,
        "Denied attempts do not change the committed usage count");

    var concurrentCode = new JsonObject
    {
        ["Id"] = Guid.NewGuid().ToString(), ["GroupId"] = groupId,
        ["Code"] = "654321", ["MaxUses"] = 1, ["Uses"] = 0, ["Enabled"] = true
    };
    AdminStateCommands.Execute(adminStore, new AdminRequest("add_codes", new JsonObject
    {
        ["GroupId"] = groupId, ["Codes"] = new JsonArray(concurrentCode)
    }));
    var concurrentAdmission = new FakeAdmissionController(true);
    var concurrentRedemptions = await Task.WhenAll(Enumerable.Range(0, 24).Select(i =>
        Task.Run(async () => await new AccessRedemptionService(adminStore, concurrentAdmission)
            .RedeemAsync(new RedemptionRequest("Guest", (200000000 + i).ToString(),
                "654321", $"192.0.2.{20 + i}", "device")))));
    Check(concurrentRedemptions.Count(x => x.Success) == 1,
        "24 concurrent attempts redeem a one-use code at most once after network admission");
    var concurrencyState = JsonNode.Parse(adminStore.Read())!.AsObject();
    Check(concurrencyState["Codes"]!.AsArray().OfType<JsonObject>()
        .Single(c => c["Code"]!.GetValue<string>() == "654321")["Uses"]!.GetValue<int>() == 1,
        "Parallel redemption commits exactly one use and keeps database consistent");

    adminStore.Update(root =>
    {
        var alice = (root["Clients"] as JsonArray)!.OfType<JsonObject>()
            .Single(c => c["AccessCode"]?.GetValue<string>() == "123456");
        alice["SessionExpiresAt"] = "2020-01-01T00:00:00Z";
        return true;
    });
    var revokedPending = adminStore.Update(root =>
        SessionLifecycle.Expire(root, DateTimeOffset.UtcNow));
    Check(revokedPending == 1, "Expired network-authorized session enters revocation queue");
    var pendingState = JsonNode.Parse(adminStore.Read())!["Clients"]!.AsArray()
        .OfType<JsonObject>().Single(c => c["AccessCode"]!.GetValue<string>() == "123456");
    Check(pendingState["Connected"]!.GetValue<bool>() &&
        pendingState["SessionStatus"]!.GetValue<string>() == "revocation-required",
        "Expired client is not shown physically disconnected before the network revoke");
    Check(adminStore.Update(root => SessionLifecycle.Expire(root, DateTimeOffset.UtcNow)) == 0,
        "Repeated expiry does not silently clear unrevoked clients");
    var noRevocation = await new SessionRevocationService(adminStore,
        new UnconfiguredAdmissionController()).ReconcileAsync();
    Check(noRevocation == 0 && JsonNode.Parse(adminStore.Read())!["Clients"]!.AsArray()
        .OfType<JsonObject>().Single(c => c["AccessCode"]!.GetValue<string>() == "123456")
        ["Connected"]!.GetValue<bool>(),
        "Missing network provider cannot mark a client disconnected");
    var revocationController = new FakeAdmissionController(true);
    Check(await new SessionRevocationService(adminStore, revocationController).ReconcileAsync() == 1,
        "Confirmed network rule revocation finalizes expired session");
    var finalized = JsonNode.Parse(adminStore.Read())!["Clients"]!.AsArray()
        .OfType<JsonObject>().Single(c => c["AccessCode"]!.GetValue<string>() == "123456");
    Check(!finalized["Connected"]!.GetValue<bool>() &&
        finalized["SessionStatus"]!.GetValue<string>() == "expired",
        "Physically revoked client is marked logically disconnected");

    var validTrial = new JsonObject
    {
        ["UpstreamAdapterId"] = "uplink", ["DownstreamAdapterId"] = "out",
        ["AccessMode"] = 1,
        ["ExperimentalWfpTrialUntilUtc"] = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O")
    };
    Check(AdminStateCommands.Execute(adminStore, new AdminRequest("set_network",
        new JsonObject { ["Network"] = validTrial.DeepClone() })).Success,
        "Explicit, two-minute-maximum experimental WFP test can be scheduled");
    bool overlongTrialRejected = false;
    validTrial["ExperimentalWfpTrialUntilUtc"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
    try
    {
        AdminStateCommands.Execute(adminStore, new AdminRequest("set_network",
            new JsonObject { ["Network"] = validTrial.DeepClone() }));
    }
    catch (ArgumentException) { overlongTrialRejected = true; }
    Check(overlongTrialRejected, "Long-running WFP block attempts are rejected by service");
    var preservedTrialTime = JsonNode.Parse(adminStore.Read())!["Network"]!
        ["ExperimentalWfpTrialUntilUtc"]!.GetValue<string>();
    Check(DateTimeOffset.TryParse(preservedTrialTime, out var preservedExpiry) &&
        preservedExpiry > DateTimeOffset.UtcNow &&
        preservedExpiry < DateTimeOffset.UtcNow.AddMinutes(2),
        "Rejected WFP trial settings leave the previously accepted deadline intact");

    Check(AdminStateCommands.Execute(adminStore,
        new AdminRequest("stop_wfp_trial")).Success,
        "Administrator can cancel WFP trial regardless of adapter availability");
    Check(JsonNode.Parse(adminStore.Read())!["Network"]!["ExperimentalWfpTrialUntilUtc"]!
        .GetValue<string>() == "",
        "Emergency WFP stop clears the scheduled block deadline");

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

sealed class FakeAdmissionController : INetworkAdmissionController
{
    readonly bool allow;
    int count;

    public FakeAdmissionController(bool allow) => this.allow = allow;
    public bool IsEnforcementReady => true;
    public int GrantCount => count;

    public ValueTask<AdmissionResult> GrantAsync(ClientIdentity client,
        DateTimeOffset expiresAt, CancellationToken token = default)
    {
        Interlocked.Increment(ref count);
        return ValueTask.FromResult(new AdmissionResult(allow, allow ? "allowed" : "denied"));
    }

    public ValueTask<AdmissionResult> RevokeAsync(ClientIdentity client,
        CancellationToken token = default) =>
        ValueTask.FromResult(new AdmissionResult(true, "revoked"));
}
