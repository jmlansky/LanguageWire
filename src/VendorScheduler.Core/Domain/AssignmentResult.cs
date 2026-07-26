namespace VendorScheduler.Core.Domain;

public sealed class AssignmentResult
{
    public required Guid JobId { get; init; }
    public required AssignmentOutcome Outcome { get; init; }
    public required string IdempotencyKey { get; init; }
    public Guid? VendorId { get; init; }

    /// <summary>
    /// True when this response replays a previously stored assignment instead of executing a new
    /// one. The assignment payload itself is identical to the original; this flag only tells the
    /// caller which of the two happened.
    /// </summary>
    public bool IsReplay { get; init; }

    public bool Success => Outcome == AssignmentOutcome.Assigned;

    public string Reason => Outcome.ToReason();

    public static AssignmentResult Assigned(Guid jobId, Guid vendorId, string idempotencyKey)
        => new()
        {
            JobId = jobId,
            Outcome = AssignmentOutcome.Assigned,
            VendorId = vendorId,
            IdempotencyKey = idempotencyKey
        };

    public static AssignmentResult Failed(Guid jobId, string idempotencyKey, AssignmentOutcome outcome)
        => new()
        {
            JobId = jobId,
            Outcome = outcome,
            VendorId = null,
            IdempotencyKey = idempotencyKey
        };

    /// <summary>
    /// Copy of a stored result, marked as a replay.
    /// </summary>
    public AssignmentResult AsReplay()
        => new()
        {
            JobId = JobId,
            Outcome = Outcome,
            VendorId = VendorId,
            IdempotencyKey = IdempotencyKey,
            IsReplay = true
        };
}
