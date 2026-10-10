using System.Text.Json.Nodes;

namespace RestaurantWiFiStorage;

/// <summary>
/// Reconciles the database after experimental WFP policy disappears.
/// A dynamic rule is owned by a process: a service crash/stop/expiry can
/// remove it without proving anyone's traffic is blocked. Never leave
/// trial clients labeled as connected or keep a stale pending reservation.
/// This changes only state; it does NOT restore fail-closed network access.
/// </summary>
public static class ExperimentalTrialSessionRecovery
{
    public static int MarkUncontrolled(JsonObject root, DateTimeOffset now)
    {
        if (root["Clients"] is not JsonArray clients) return 0;
        var updated = 0;
        foreach (var item in clients.OfType<JsonObject>())
        {
            if (item["ExperimentalIpv4Trial"]?.GetValue<bool>() != true)
                continue;

            var status = item["SessionStatus"]?.GetValue<string>() ?? "";
            var pending = status == "pending-network-authorization";
            var rulePreviouslyInstalled =
                status is "trial-rule-installed-unverified" or
                    "network-authorized" or "revocation-required" or
                    "revocation-failed";
            var looksConnected = item["Connected"]?.GetValue<bool>() == true;
            if (!pending && !rulePreviouslyInstalled && !looksConnected)
                continue;

            item["Connected"] = false;
            item["NetworkRuleInstalled"] = false;
            item["TrafficVerified"] = false;
            item["TrialEndedAtUtc"] = now.ToString("O");
            item["SessionStatus"] = pending
                ? "trial-aborted-before-authorization"
                : "trial-ended-uncontrolled";
            updated++;
        }
        return updated;
    }

    public static object Summary(JsonObject? root)
    {
        var clients = (root?["Clients"] as JsonArray)?.OfType<JsonObject>()
            .Where(c => c["ExperimentalIpv4Trial"]?.GetValue<bool>() == true)
            .ToArray() ?? Array.Empty<JsonObject>();
        return new
        {
            ruleInstalledUnverified = clients.Count(c =>
                c["SessionStatus"]?.GetValue<string>() == "trial-rule-installed-unverified"),
            pendingReservations = clients.Count(c =>
                c["SessionStatus"]?.GetValue<string>() == "pending-network-authorization"),
            endedUncontrolled = clients.Count(c =>
                c["SessionStatus"]?.GetValue<string>() == "trial-ended-uncontrolled"),
            phoneInternetReachabilityVerified = false
        };
    }
}
