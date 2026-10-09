using System.Net;
using System.Text.Json.Nodes;
using RestaurantWiFiNetworking;

namespace RestaurantWiFiStorage;

/// <summary>
/// Reconciles durable revocation-required sessions with an actual network gateway.
/// A session is marked disconnected only after the controller confirms rule removal.
/// </summary>
public sealed class SessionRevocationService
{
    readonly StateStore store;
    readonly INetworkAdmissionController admission;

    public SessionRevocationService(StateStore store, INetworkAdmissionController admission)
    {
        this.store = store;
        this.admission = admission;
    }

    public async Task<int> ReconcileAsync(CancellationToken token = default)
    {
        if (!admission.IsEnforcementReady) return 0;
        var snapshot = JsonNode.Parse(store.Read())?.AsObject();
        if (snapshot?["Clients"] is not JsonArray clients) return 0;

        var candidates = clients.OfType<JsonObject>()
            .Where(c => c["SessionStatus"]?.GetValue<string>() == "revocation-required")
            .Select(c => (
                ReservationId: c["ReservationId"]?.GetValue<string>(),
                Ip: c["Ip"]?.GetValue<string>()))
            .Where(c => !string.IsNullOrWhiteSpace(c.ReservationId) &&
                IPAddress.TryParse(c.Ip, out _))
            .ToArray();

        var revoked = 0;
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            AdmissionResult outcome;
            try
            {
                outcome = await admission.RevokeAsync(new ClientIdentity(candidate.Ip!), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { continue; }

            if (!outcome.Enforced) continue;
            var committed = store.Update(root =>
            {
                var client = (root["Clients"] as JsonArray)?.OfType<JsonObject>()
                    .FirstOrDefault(c => c["ReservationId"]?.GetValue<string>() == candidate.ReservationId &&
                        c["SessionStatus"]?.GetValue<string>() == "revocation-required");
                if (client is null) return false;
                client["Connected"] = false;
                client["SessionStatus"] = "expired";
                return true;
            });
            if (committed) revoked++;
        }
        return revoked;
    }
}
