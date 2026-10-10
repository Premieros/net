namespace RestaurantWiFiGateway;

/// <summary>
/// A failure in a manually confirmed WFP test invalidates that entire test
/// window. Do not automatically turn code authorization back on after a
/// transient WFP, topology or storage error using an earlier observation.
/// Only an explicitly restarted trial with a NEW deadline may resume.
/// This protects application authorization state; it does NOT make a
/// dynamic WFP session fail-closed when the gateway is stopped.
/// </summary>
internal sealed class TrialFailureLatch
{
    DateTimeOffset? rejectedTrialDeadline;

    public bool Reject(DateTimeOffset deadline)
    {
        if (rejectedTrialDeadline == deadline) return false;
        rejectedTrialDeadline = deadline;
        return true;
    }

    public bool IsRejected(DateTimeOffset deadline) =>
        rejectedTrialDeadline == deadline;

    public DateTimeOffset? RejectedDeadline => rejectedTrialDeadline;
}
