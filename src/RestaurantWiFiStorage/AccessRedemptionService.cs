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
            // An exception does not prove that the native grant was never
            // installed. Compensate using a fresh token even when the client
            // request was cancelled, then retain a visible revocation record
            // if the network backend cannot confirm cleanup.
            await CompensateUnknownGrantAsync(reservation.Id, request.Ip);
            return new(false, "تعذر التأكد من إذن الشبكة؛ لم يتم استهلاك الكود.");
        }

        if (!grant.Enforced)
        {
            await CompensateUnknownGrantAsync(reservation.Id, request.Ip);
            return new(false, "لم يتم تأكيد السماح بالإنترنت؛ الكود لا يزال متاحًا.");
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
                // Trial WFP API success proves a rule was committed, NOT that
                // the customer's phone can reach the Internet.
                if (trialUntil.HasValue)
                {
                    pending["Connected"] = false;
                    pending["NetworkRuleInstalled"] = true;
                    pending["TrafficVerified"] = false;
                    pending["SessionStatus"] = "trial-rule-installed-unverified";
                }
                else
                {
                    pending["Connected"] = true;
                    pending["SessionStatus"] = "network-authorized";
                }
                return true;
            });
            if (committed) return new(true, reservation.GroupName, reservation.Minutes);
        }
        catch
        {
            // Continue to compensate the network grant. Never report authorization success.
        }

        // Database failure after a native grant requires the same compensation.
        await CompensateUnknownGrantAsync(reservation.Id, request.Ip);
        return new(false, "تعذر إتمام الجلسة؛ يلزم فحص الشبكة.");
    }

    async ValueTask CompensateUnknownGrantAsync(string id, string ip)
    {
        bool revoked = false;
        try
        {
            // Do not forward a cancelled HTTP request token into cleanup.
            revoked = (await admission.RevokeAsync(
                new ClientIdentity(ip), CancellationToken.None)).Enforced;
        }
        catch { /* Retain revocation-required for reconciliation. */ }

        if (revoked)
        {
            ReleaseReservation(id);
            return;
        }

        store.Update(root =>
        {
            var pending = (root["Clients"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(c => c["ReservationId"]?.GetValue<string>() == id);
            if (pending is null) return false;
            pending["SessionStatus"] = "revocation-required";
            pending["NetworkRuleInstalled"] = true; // conservative, not proof
            pending["Connected"] = false;
            return true;
        });
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
             c["SessionStatus"]?.GetValue<string>() is "pending-network-authorization" or
                 "revocation-required" or "trial-rule-installed-unverified")))
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
                 "pending-network-authorization" or "revocation-required" or
                 "trial-rule-installed-unverified")))
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

        // Limit simultaneous devices PER VOUCHER, not per entire package.
        // Count pending authorization too: reservations occur in one SQLite
        // transaction, preventing concurrent requests bypassing the cap.
        var maximumDevices = group["MaxDevices"]?.GetValue<int>() ?? 1;
        if (maximumDevices <= 0)
            return new(false, "إعداد عدد الأجهزة للباقة غير صالح.");
        var occupiedDevices = clients.OfType<JsonObject>().Count(c =>
            string.Equals(c["AccessCode"]?.GetValue<string>(), request.Code,
                StringComparison.OrdinalIgnoreCase) &&
            (c["SessionStatus"]?.GetValue<string>() is "pending-network-authorization"
                or "revocation-required" or "trial-rule-installed-unverified" ||
             (c["Connected"]?.GetValue<bool>() == true &&
              DateTimeOffset.TryParse(c["SessionExpiresAt"]?.GetValue<string>(),
                  out var activeUntil) && activeUntil > now)));
        if (occupiedDevices >= maximumDevices)
            return new(false, "اكتمل عدد الأجهزة المتزامنة المسموح به لهذا الكود.");

        // This is a per-voucher/per-phone historical use cap. A failed
        // reservation is excluded; expired completed sessions still count.
        var maxPerDevice = group["MaxUsesPerDevice"]?.GetValue<int>() ?? 0;
        if (maxPerDevice < 0)
            return new(false, "إعداد استخدامات الجهاز غير صالح.");
        if (maxPerDevice > 0)
        {
            var priorUses = clients.OfType<JsonObject>().Count(c =>
                string.Equals(c["AccessCode"]?.GetValue<string>(), request.Code,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c["Phone"]?.GetValue<string>(), request.Phone,
                    StringComparison.OrdinalIgnoreCase) &&
                (c["Connected"]?.GetValue<bool>() == true ||
                 c["NetworkRuleInstalled"]?.GetValue<bool>() == true ||
                 c["SessionStatus"]?.GetValue<string>() is
                     "expired" or "network-authorized" or
                     "revocation-required" or "trial-rule-installed-unverified"));
            if (priorUses >= maxPerDevice)
                return new(false, "بلغ هذا الهاتف الحد المسموح لاستخدام الكود.");
        }

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
            newClient["NetworkRuleInstalled"] = false;
            newClient["TrafficVerified"] = false;
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
