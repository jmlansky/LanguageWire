namespace VendorScheduler.Core.Domain;

/// <summary>
/// Outcome of a single reservation attempt against a vendor.
/// </summary>
public enum VendorReservationStatus
{
    /// <summary>Capacity is held for the job.</summary>
    Reserved,

    /// <summary>The vendor answered and declined. Retrying the same vendor changes nothing.</summary>
    Rejected,

    /// <summary>The call failed before the vendor could act on it. Safe to retry.</summary>
    TransientFailure,

    /// <summary>
    /// The vendor may or may not have reserved: a timeout, or an answer we could not interpret.
    /// Retrying risks a double reservation unless the vendor deduplicates on the job id, so this is
    /// tracked apart from <see cref="TransientFailure"/> even though both are retried.
    /// </summary>
    Uncertain
}

public enum VendorRejectionReason
{
    None,
    NoCapacity,
    PairNotSupported,
    QuotaExceeded,
    Unknown
}

public sealed class VendorReservation
{
    public required VendorReservationStatus Status { get; init; }
    public VendorRejectionReason RejectionReason { get; init; } = VendorRejectionReason.None;

    /// <summary>Free-text context for logs only. Never used to make decisions.</summary>
    public string? Detail { get; init; }

    public bool IsReserved => Status == VendorReservationStatus.Reserved;

    /// <summary>A rejection is the vendor's final answer, so it is never retried.</summary>
    public bool IsRetryable => Status is VendorReservationStatus.TransientFailure or VendorReservationStatus.Uncertain;

    public static VendorReservation Reserved()
        => new() { Status = VendorReservationStatus.Reserved };

    public static VendorReservation Rejected(VendorRejectionReason reason, string? detail = null)
        => new() { Status = VendorReservationStatus.Rejected, RejectionReason = reason, Detail = detail };

    public static VendorReservation TransientFailure(string? detail = null)
        => new() { Status = VendorReservationStatus.TransientFailure, Detail = detail };

    public static VendorReservation Uncertain(string? detail = null)
        => new() { Status = VendorReservationStatus.Uncertain, Detail = detail };
}
