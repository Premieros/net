using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RestaurantWiFiStorage;

public sealed record AdminRequest(string Operation, JsonObject? Payload = null);
public sealed record AdminResponse(bool Success, string? Data = null, string? Error = null);

/// <summary>
/// Explicit command whitelist. Only the privileged service may execute these writes.
/// The desktop is allowed to request operations but never submits replacement state JSON.
/// </summary>
public static class AdminStateCommands
{
    public static AdminResponse Execute(StateStore store, AdminRequest request)
    {
        if (request.Operation == "read") return new(true, store.Read());
        if (request.Operation == "initialize")
            return store.Update(root =>
            {
                var groups = root["Groups"] as JsonArray;
                if (groups is null) { groups = new JsonArray(); root["Groups"] = groups; }
                if (groups.Count != 0) return new AdminResponse(true);
                foreach (var (name, kind, minutes, quota, down, up, devices, uses, block) in new[]
                {
                    ("العملاء", "customer", 60, 1024, 5, 2, 1, 2, true),
                    ("الموظفين", "employee", 720, 4096, 10, 5, 1, 20, false),
                    ("المديرين", "manager", 1440, 0, 0, 0, 2, 100, false)
                })
                    groups.Add(new JsonObject
                    {
                        ["Id"] = Guid.NewGuid().ToString(), ["Name"] = name, ["Kind"] = kind,
                        ["Minutes"] = minutes, ["QuotaMb"] = quota, ["DownloadMbps"] = down,
                        ["UploadMbps"] = up, ["MaxDevices"] = devices, ["MaxUsesPerDevice"] = uses,
                        ["BlockVideo"] = block, ["Enabled"] = true
                    });
                return new AdminResponse(true);
            });

        if (request.Operation == "confirm_wfp_trial_block_observed")
            return store.Update(root =>
            {
                var network = root["Network"] as JsonObject ??
                    throw new ArgumentException("No IPv4 trial is configured.");
                if (!DateTimeOffset.TryParse(
                    network["ExperimentalWfpTrialUntilUtc"]?.GetValue<string>(), out var until) ||
                    until <= DateTimeOffset.UtcNow.AddSeconds(15))
                    throw new ArgumentException("Trial is not active.");
                network["ExperimentalWfpTrialBlockingObserved"] = true;
                return new AdminResponse(true);
            });

        if (request.Operation == "stop_wfp_trial")
            return store.Update(root =>
            {
                if (root["Network"] is JsonObject network)
                    network["ExperimentalWfpTrialUntilUtc"] = "";
                    network["ExperimentalWfpTrialBlockingObserved"] = false;
                return new AdminResponse(true);
            });

        var payload = request.Payload ?? throw new ArgumentException("Missing command parameters.");
        return store.Update(root =>
        {
            switch (request.Operation)
            {
                case "add_group":
                {
                    var group = (payload["Group"] as JsonObject) ?? throw new ArgumentException("Group is required.");
                    var name = group["Name"]?.GetValue<string>()?.Trim() ?? "";
                    if (name.Length is < 1 or > 100) throw new ArgumentException("Invalid group name.");
                    if (group["Minutes"]?.GetValue<int>() is not int minutes || minutes <= 0)
                        throw new ArgumentException("Invalid duration.");
                    if (group["QuotaMb"]?.GetValue<int>() is not int quota || quota < 0 ||
                        group["MaxDevices"]?.GetValue<int>() is not int devices || devices < 1)
                        throw new ArgumentException("Invalid quota or device limit.");
                    if (group["DownloadMbps"]?.GetValue<decimal>() < 0 ||
                        group["UploadMbps"]?.GetValue<decimal>() < 0)
                        throw new ArgumentException("Invalid rate limit.");
                    var groups = root["Groups"] as JsonArray ?? new JsonArray();
                    if (groups.OfType<JsonObject>().Any(g =>
                        string.Equals(g["Name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException("Group already exists.");
                    if (root["Groups"] is null) root["Groups"] = groups;
                    groups.Add(group.DeepClone());
                    return new AdminResponse(true);
                }
                case "add_codes":
                {
                    var groupId = payload["GroupId"]?.GetValue<string>() ?? "";
                    var toAdd = payload["Codes"] as JsonArray ??
                        throw new ArgumentException("Codes array required.");
                    if (toAdd.Count is < 1 or > 500)
                        throw new ArgumentException("Invalid code count.");
                    var groups = root["Groups"] as JsonArray ?? new JsonArray();
                    if (!groups.OfType<JsonObject>().Any(g =>
                        string.Equals(g["Id"]?.GetValue<string>(), groupId, StringComparison.OrdinalIgnoreCase) &&
                        g["Enabled"]?.GetValue<bool>() == true))
                        throw new ArgumentException("Selected group is disabled or missing.");
                    var existing = root["Codes"] as JsonArray ?? new JsonArray();
                    var seen = new HashSet<string>(existing.OfType<JsonObject>()
                        .Select(c => c["Code"]?.GetValue<string>() ?? ""), StringComparer.OrdinalIgnoreCase);
                    foreach (var node in toAdd)
                    {
                        var item = node as JsonObject ?? throw new ArgumentException("Invalid code.");
                        var code = item["Code"]?.GetValue<string>() ?? "";
                        if (!Regex.IsMatch(code, @"^\d{4,12}$") || !seen.Add(code) ||
                            !string.Equals(item["GroupId"]?.GetValue<string>(), groupId, StringComparison.OrdinalIgnoreCase) ||
                            item["MaxUses"]?.GetValue<int>() is not int maxUses || maxUses < 1)
                            throw new ArgumentException("Invalid, duplicate, or mismatched access code.");
                    }
                    if (root["Codes"] is null) root["Codes"] = existing;
                    foreach (var item in toAdd) existing.Add(item!.DeepClone());
                    return new AdminResponse(true);
                }
                case "set_password":
                {
                    var salt = payload["Salt"]?.GetValue<string>() ?? "";
                    var hash = payload["Hash"]?.GetValue<string>() ?? "";
                    if (Convert.FromBase64String(salt).Length != 16 ||
                        Convert.FromBase64String(hash).Length != 32)
                        throw new ArgumentException("Invalid password credential data.");
                    root["PasswordSalt"] = salt;
                    root["PasswordHash"] = hash;
                    return new AdminResponse(true);
                }
                case "set_name":
                {
                    var name = payload["Name"]?.GetValue<string>()?.Trim() ?? "";
                    if (name.Length is < 1 or > 100) throw new ArgumentException("Invalid restaurant name.");
                    root["RestaurantName"] = name;
                    return new AdminResponse(true);
                }
                case "set_network":
                {
                    var network = payload["Network"] as JsonObject ??
                        throw new ArgumentException("Network object required.");
                    var upstream = network["UpstreamAdapterId"]?.GetValue<string>() ?? "";
                    var downstream = network["DownstreamAdapterId"]?.GetValue<string>() ?? "";
                    var mode = network["AccessMode"]?.GetValue<int>() ?? -1;
                    if (upstream.Length is < 1 or > 256 || downstream.Length is < 1 or > 256 ||
                        upstream.Equals(downstream, StringComparison.OrdinalIgnoreCase) || mode is not (0 or 1))
                        throw new ArgumentException("Invalid adapter selection.");
                    var trialUntil = network["ExperimentalWfpTrialUntilUtc"]?.GetValue<string>() ?? "";
                    if (trialUntil.Length > 0)
                    {
                        if (!DateTimeOffset.TryParse(trialUntil, out var expires) ||
                            expires <= DateTimeOffset.UtcNow ||
                            expires > DateTimeOffset.UtcNow.AddMinutes(2).AddSeconds(15))
                            throw new ArgumentException("WFP trial must be a short-lived future timestamp (up to two minutes).");
                    }
                    var clean = (JsonObject)network.DeepClone();
                    clean["ExperimentalWfpTrialBlockingObserved"] = false;
                    root["Network"] = clean;
                    return new AdminResponse(true);
                }
                default: throw new ArgumentException("Unsupported administrative operation.");
            }
        });
    }
}
