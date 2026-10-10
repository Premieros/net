namespace RestaurantWiFiNetworking;

/// <summary>
/// Ordered, side-effect-free contract for staging a durable WFP policy.
/// This is NOT a filter installer. The executor must atomically commit
/// IPv4 + IPv6 baseline deny before any authorization becomes eligible.
/// </summary>
public static class PersistentGuardDeploymentPlan
{
    public enum Step
    {
        VerifyTopology,
        RegisterPersistentProvider,
        RegisterPersistentSublayer,
        StageIpv4ForwardDeny,
        StageIpv6ForwardDeny,
        CommitTransaction,
        VerifyCommittedFilters,
        VerifyRestartSurvival,
        VerifyPacketBlocking,
        VerifyIdentityBinding,
        EnableAdmission
    }

    public static IReadOnlyList<Step> Steps { get; } =
        Array.AsReadOnly(new[]
        {
            Step.VerifyTopology,
            Step.RegisterPersistentProvider,
            Step.RegisterPersistentSublayer,
            Step.StageIpv4ForwardDeny,
            Step.StageIpv6ForwardDeny,
            Step.CommitTransaction,
            Step.VerifyCommittedFilters,
            Step.VerifyRestartSurvival,
            Step.VerifyPacketBlocking,
            Step.VerifyIdentityBinding
        });

    public sealed record ExecutionState(
        IReadOnlyCollection<Step> Completed,
        bool TransactionCommitted,
        bool VerifiedOutsideInstaller,
        bool ExplicitAdminConsent);

    public sealed record Decision(bool CanEnableAdmission, string Reason);

    public static Decision Evaluate(ExecutionState? state)
    {
        if (state is null) return Deny("deployment-state-missing");
        if (!state.ExplicitAdminConsent) return Deny("admin-consent-required");
        if (!state.TransactionCommitted) return Deny("deny-transaction-not-committed");
        if (!state.VerifiedOutsideInstaller) return Deny("independent-verification-missing");
        if (state.Completed is null) return Deny("deployment-steps-missing");
        var complete = new HashSet<Step>(state.Completed);
        foreach (var step in Steps)
        {
            if (!Enum.IsDefined(step)) return Deny("invalid-plan");
            if (!complete.Contains(step)) return Deny("missing-" + step);
        }
        return new Decision(true, "ready-for-guard-evidence-evaluation");
    }

    static Decision Deny(string reason) => new(false, reason);
}
