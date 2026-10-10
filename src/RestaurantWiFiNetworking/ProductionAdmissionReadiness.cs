namespace RestaurantWiFiNetworking;

/// <summary>
/// Production admission gate: independently requires a safe interface selection,
/// completed durable-policy deployment, and fresh measured guard evidence.
/// This class has no WFP side effects and cannot activate the legacy trial.
/// </summary>
public static class ProductionAdmissionReadiness
{
    public sealed record Result(bool Ready, string Reason);

    public static Result Evaluate(
        PersistentGuardTopology.Selection? topology,
        PersistentGuardDeploymentPlan.ExecutionState? deployment,
        PersistentGuardInterlock.Evidence? evidence,
        string expectedPolicyGeneration,
        DateTimeOffset nowUtc)
    {
        var selected = PersistentGuardTopology.Validate(topology);
        if (!selected.SafeToStage || selected.DownstreamCidr is null)
            return new Result(false, "topology:" + selected.Reason);

        var staged = PersistentGuardDeploymentPlan.Evaluate(deployment);
        if (!staged.CanEnableAdmission)
            return new Result(false, "deployment:" + staged.Reason);

        var guard = PersistentGuardInterlock.Evaluate(evidence,
            topology!.DownstreamIndex, topology.UpstreamIndex,
            selected.DownstreamCidr, expectedPolicyGeneration,
            nowUtc, TimeSpan.FromSeconds(30));

        return guard.AllowAdmission
            ? new Result(true, "all-production-guard-prerequisites-verified")
            : new Result(false, "guard:" + guard.Reason);
    }
}
