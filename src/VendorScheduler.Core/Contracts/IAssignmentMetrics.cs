using VendorScheduler.Core.Domain;

namespace VendorScheduler.Core.Contracts;

/// <summary>
/// Counters behind the operational dashboard. Deliberately a narrow interface: the domain reports
/// what happened, and the adapter decides whether that becomes an in-memory counter, an
/// OpenTelemetry instrument, or nothing at all.
/// </summary>
public interface IAssignmentMetrics
{
    /// <summary>One completed assignment request, whatever its outcome.</summary>
    void AssignmentCompleted(AssignmentOutcome outcome, bool isReplay);

    /// <summary>One call to a vendor, classified by how it ended.</summary>
    void VendorAttempt(VendorReservationStatus status);

    /// <summary>A retry was scheduled after a failed attempt.</summary>
    void VendorRetryScheduled();
}

/// <summary>Default used when no metrics adapter is wired, so nothing has to null-check.</summary>
public sealed class NoOpAssignmentMetrics : IAssignmentMetrics
{
    public static readonly NoOpAssignmentMetrics Instance = new();

    public void AssignmentCompleted(AssignmentOutcome outcome, bool isReplay)
    {
    }

    public void VendorAttempt(VendorReservationStatus status)
    {
    }

    public void VendorRetryScheduled()
    {
    }
}
