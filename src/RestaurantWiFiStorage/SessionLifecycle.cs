using System.Globalization;
using System.Text.Json.Nodes;

namespace RestaurantWiFiStorage;

/// <summary>Logical session expiry only. Network access enforcement must be performed by an admission controller.</summary>
public static class SessionLifecycle
{
    public static int Expire(JsonObject root, DateTimeOffset now)
    {
        if (root["Clients"] is not JsonArray clients) return 0;
        int expired = 0;
        foreach (var item in clients)
        {
            if (item is not JsonObject client) continue;
            var status = client["SessionStatus"]?.GetValue<string>();
            // Experimental IPv4 "rule installed" sessions are deliberately
            // NOT marked Connected (packet reachability was not verified),
            // but must still be revoked at the configured deadline.
            var experimentalRule = status == "trial-rule-installed-unverified" &&
                client["ExperimentalIpv4Trial"]?.GetValue<bool>() == true &&
                client["NetworkRuleInstalled"]?.GetValue<bool>() == true;
            if (client["Connected"]?.GetValue<bool>() != true && !experimentalRule)
                continue;
            var raw = client["SessionExpiresAt"]?.GetValue<string>();
            if (raw is null || !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var until) || until > now)
                continue;
            if (status is "network-authorized" or "revocation-required" or
                "trial-rule-installed-unverified")
            {
                // Never mark physically authorized sessions disconnected before
                // an enforcement backend confirms the rule was revoked.
                if (status == "revocation-required") continue;
                client["SessionStatus"] = "revocation-required";
            }
            else
            {
                client["Connected"] = false;
                client["SessionStatus"] = "expired";
            }
            expired++;
        }
        return expired;
    }
}
