using System.Text.Json.Nodes;
using RestaurantWiFiNetworking;

namespace RestaurantWiFiStorage;

public sealed record RedemptionRequest(string Name, string Phone, string Code, string Ip, string Device);
public sealed record RedemptionResult(bool Success, string Message, int Minutes = 0);

/// <summary>
/// Transactional reservation -> enforce network rule -> commit usage.
/// A code is NOT consumed when no verified network backend is available.
/// The real Windows packet filter remains to be implemented.
/// </summary>
public sealed class AccessRedemptionService
{
    readonly StateStore store;
    readonly INetworkAdmissionController admission;

    public AccessRedemptionService(StateStore store, INetworkAdmissionController admission)
    {
        this.store = store;
        this.admission = admission;
    }

    sealed record Reservation(bool Valid, string Message, string Id = "",
        string GroupName = "", int Minutes = 0, DateTimeOffset Expires = default);

    public async ValueTask<RedemptionResult> RedeemAsync(
        RedemptionRequest request, CancellationToken token = default)
    {
        if (request.Name.Length is < 2 or > 100 || request.Phone.Length is < 5 or > 30 ||
            request.Code.Length is < 4 or > 32 || request.Device.Length > 512 ||
            !ClientIpv4Source.TryNormalize(request.Ip, out var sourceIp))
            return new(false, "راجع الاسم والهاتف والكود وعنوان IPv4.");
        // Store and compare only canonical IPv4 values; mapped forms must not
        // create a second voucher record for the same Windows client address.
        request = request with { Ip = sourceIp };

        // Mandatory: do not read or update codes unless a verified packet filter exists.
        if (!admission.IsEnforcementReady)
            return new(false, "التحكم بالشبكة غير جاهز؛ لم يتم استهلاك الكود.");

        var now = DateTimeOffset.UtcNow;
        var trialUntil = (admission as ITimeLimitedTrialAdmissionController)?.TrialEndsAt;
        var reservation = store.Update(root => Reserve(root, request, now, trialUntil));
        if (!reservation.Valid) return new(false, reservation.Message);

        AdmissionResult grant;
        try
        {
            grant = await admission.GrantAsync(
                new ClientIdentity(request.Ip), reservation.Expires, token);
        }
        catch
        {
            ReleaseReservation(reservation.Id);
            return new(false, "تعذر تطبيق إذن الشبكة؛ لم يتم استهلاك الكود.");
        }

        if (!grant.Enforced)
        {
            ReleaseReservation(reservation.Id);
            return new(false, "لم يتم السماح بالإنترنت؛ الكود لا يزال متاحًا.");
        }

        try
        {
            var committed = store.Update(root =>
            {
                var clients = root["Clients"] as JsonArray;
                var pending = clients?.OfType<JsonObject>().FirstOrDefault(c =>
                    c["ReservationId"]?.GetValue<string>() == reservation.Id &&
                    c["SessionStatus"]?.GetValue<string>() == "pending-network-authorization");
                if (pending is null) return false;

                var code = (root["Codes"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(c =>
                    string.Equals(c["Code"]?.GetValue<string>(), request.Code, StringComparison.OrdinalIgnoreCase));
                if (code is null || code["Enabled"]?.GetValue<bool>() != true ||
                    (code["Uses"]?.GetValue<int>() ?? 0) >= (code["MaxUses"]?.GetValue<int>() ?? 1))
                    return false;

                code["Uses"] = (code["Uses"]?.GetValue<int>() ?? 0) + 1;
                pending["Connected"] = true;
                pending["SessionStatus"] = "network-authorized";
                return true;
            });
            if (committed) return new(true, reservation.GroupName, reservation.Minutes);
        }
        catch
        {
            // Continue to compensate the network grant. Never report authorization success.
        }

        // Database commit failed after the network grant: revoke the rule, preserving
        // a visible error record if the physical revoke itself cannot be confirmed.
        var revoked = false;
        try
        {
            revoked = (await admission.RevokeAsync(new ClientIdentity(request.Ip), token)).Enforced;
        }
        catch { /* A failed revoke requires administrator attention. */ }
        if (revoked) ReleaseReservation(reservation.Id);
        else store.Update(root =>
        {
            var pending = (root["Clients"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(c => c["ReservationId"]?.GetValue<string>() == reservation.Id);
            if (pending is not null) pending["SessionStatus"] = "revocation-required";
            return true;
        });
        return new(false, "تعذر إتمام الجلسة؛ يلزم فحص الشبكة.");
    }

    Reservation Reserve(JsonObject root, RedemptionRequest request, DateTimeOffset now,
        DateTimeOffset? trialUntil)
    {
        SessionLifecycle.Expire(root, now);
        var clients = root["Clients"] as JsonArray;
        if (clients is null) { clients = new JsonArray(); root["Clients"] = clients; }
        // Stale reservation cleanup is safe only for sessions never granted network access.
        foreach (var stale in clients.OfType<JsonObject>().Where(c =>
            c["SessionStatus"]?.GetValue<string>() == "pending-network-authorization" &&
            DateTimeOffset.TryParse(c["ReservationDeadline"]?.GetValue<string>(), out var deadline) &&
            deadline < now).ToArray()) clients.Remove(stale);

        if (clients.OfType<JsonObject>().Any(c =>
            string.Equals(c["Phone"]?.GetValue<string>(), request.Phone, StringComparison.OrdinalIgnoreCase) &&
            ((c["Connected"]?.GetValue<bool>() == true &&
              DateTimeOffset.TryParse(c["SessionExpiresAt"]?.GetValue<string>(), out var end) && end > now) ||
             c["SessionStatus"]?.GetValue<string>() is "pending-network-authorization" or "revocation-required")))
            return new(false, "هذا الهاتف لديه جلسة نشطة أو قيد التفعيل.");

        // The IPv4 trial filters traffic by source IP, not by phone number
        // or MAC address. Two simultaneously active vouchers on the SAME
        // source IP would share one network permit; revoking either voucher
        // could revoke the other. Reject the second reservation transactionally.
        // This is a conflict check only, NOT a solution to IP spoofing.
        if (clients.OfType<JsonObject>().Any(c =>
            string.Equals(c["Ip"]?.GetValue<string>(), request.Ip, StringComparison.OrdinalIgnoreCase) &&
            ((c["Connected"]?.GetValue<bool>() == true &&
              DateTimeOffset.TryParse(c["SessionExpiresAt"]?.GetValue<string>(), out var until) &&
              until > now) ||
             c["SessionStatus"]?.GetValue<string>() is
                 "pending-network-authorization" or "revocation-required")))
            return new(false, "عنوان IP لهذا الجهاز لديه جلسة نشطة أو قيد التفعيل؛ لم يتم استخدام الكود.");

        var code = (root["Codes"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(c =>
            string.Equals(c["Code"]?.GetValue<string>(), request.Code, StringComparison.OrdinalIgnoreCase) &&
            c["Enabled"]?.GetValue<bool>() == true);
        if (code is null) return new(false, "الكود غير صحيح أو معطل.");
        var pendingCount = clients.OfType<JsonObject>().Count(c =>
            string.Equals(c["AccessCode"]?.GetValue<string>(), request.Code, StringComparison.OrdinalIgnoreCase) &&
            c["SessionStatus"]?.GetValue<string>() is "pending-network-authorization" or "revocation-required");
        if ((code["Uses"]?.GetValue<int>() ?? 0) + pendingCount >=
            (code["MaxUses"]?.GetValue<int>() ?? 1))
            return new(false, "الكود انتهى استخدامه أو قيد التفعيل.");

        var groupId = code["GroupId"]?.GetValue<string>();
        var group = (root["Groups"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(g =>
            string.Equals(g["Id"]?.GetValue<string>(), groupId, StringComparison.OrdinalIgnoreCase) &&
            g["Enabled"]?.GetValue<bool>() == true);
        var minutes = group?["Minutes"]?.GetValue<int>() ?? 0;
        if (group is null || minutes <= 0)
            return new(false, "الباقة غير متاحة.");

        var id = Guid.NewGuid().ToString("N");
        var expiry = now.AddMinutes(minutes);
        if (trialUntil.HasValue && trialUntil.Value < expiry) expiry = trialUntil.Value;
        if (expiry <= now.AddSeconds(10))
            return new(false, "انتهت فترة اختبار IPv4؛ لم يتم استخدام الكود.");
        var grantedMinutes = trialUntil.HasValue
            ? (int)Math.Max(1, Math.Ceiling((expiry - now).TotalMinutes)) : minutes;
        var newClient = new JsonObject
        {
            ["ReservationId"] = id, ["ReservationDeadline"] = now.AddMinutes(2).ToString("O"),
            ["Name"] = request.Name, ["Phone"] = request.Phone,
            ["Device"] = request.Device, ["Ip"] = request.Ip, ["Mac"] = "",
            ["Group"] = group["Name"]?.GetValue<string>() ?? "",
            ["UsedMb"] = 0, ["Connected"] = false,
            ["SessionStatus"] = "pending-network-authorization",
            ["SessionStartedAt"] = now.ToString("O"), ["SessionExpiresAt"] = expiry.ToString("O"),
            ["AccessCode"] = request.Code
        };
        if (trialUntil.HasValue)
        {
            newClient["ExperimentalIpv4Trial"] = true;
            newClient["TrialExpiresAt"] = expiry.ToString("O");
        }
        clients.Add(newClient);
        return new(true, "", id, group["Name"]?.GetValue<string>() ?? "", grantedMinutes, expiry);
    }

    void ReleaseReservation(string id)
    {
        store.Update(root =>
        {
            var clients = root["Clients"] as JsonArray;
            var reserved = clients?.OfType<JsonObject>().FirstOrDefault(c =>
                c["ReservationId"]?.GetValue<string>() == id &&
                c["SessionStatus"]?.GetValue<string>() == "pending-network-authorization");
            if (reserved is not null) clients!.Remove(reserved);
            return true;
        });
    }
}
