namespace VendorScheduler.Core.Domain;

public sealed class AssignmentResult
{
    public required bool Success { get; init; }
    public required Guid JobId { get; init; }
    public Guid? VendorId { get; init; }
    public required string Reason { get; init; }
    public required string IdempotencyKey { get; init; }
}
