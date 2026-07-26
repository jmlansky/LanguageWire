using VendorScheduler.Core.Domain;

namespace VendorScheduler.Core.Contracts;

public interface IIdempotencyStore
{
    Task<AssignmentResult?> TryGetCompletedAsync(string idempotencyKey, CancellationToken cancellationToken = default);
    Task<bool> TryStartAsync(string idempotencyKey, CancellationToken cancellationToken = default);
    Task SaveCompletedAsync(string idempotencyKey, AssignmentResult result, CancellationToken cancellationToken = default);
    Task ReleaseInFlightAsync(string idempotencyKey, CancellationToken cancellationToken = default);
}
