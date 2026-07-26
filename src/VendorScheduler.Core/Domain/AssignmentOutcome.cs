namespace VendorScheduler.Core.Domain;

/// <summary>
/// Closed set of assignment outcomes. Kept as an enum rather than free text so failures can be
/// counted and alerted on per reason without parsing strings.
/// </summary>
public enum AssignmentOutcome
{
    Assigned,
    AlreadyInProgress,
    NoMatchingVendor,
    VendorReservationFailed
}

public static class AssignmentOutcomeExtensions
{
    /// <summary>
    /// Human-readable text for an outcome. Single source of truth, so the message shown to callers
    /// cannot drift away from the category used for metrics.
    /// </summary>
    public static string ToReason(this AssignmentOutcome outcome) => outcome switch
    {
        AssignmentOutcome.Assigned => "Assigned",
        AssignmentOutcome.AlreadyInProgress => "Assignment already in progress",
        AssignmentOutcome.NoMatchingVendor => "No matching vendor",
        AssignmentOutcome.VendorReservationFailed => "Vendor reservation failed",
        _ => outcome.ToString()
    };
}
