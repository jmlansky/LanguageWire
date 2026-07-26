using VendorScheduler.Core.Contracts;
using VendorScheduler.Core.Domain;

namespace VendorScheduler.Core.Services;

public sealed class AssignmentEngine(
    IVendorGateway vendorGateway,
    IIdempotencyStore idempotencyStore,
    IAssignmentEventPublisher eventPublisher) : IAssignmentEngine
{
    public async Task<AssignmentResult> AssignAsync(
        TranslationJob job,
        IReadOnlyCollection<VendorPartner> vendors,
        CancellationToken cancellationToken = default)
    {
        var idempotencyKey = $"assign:{job.JobId}";
        var started = await idempotencyStore.TryStartAsync(idempotencyKey, cancellationToken);
        if (!started)
        {
            return new AssignmentResult
            {
                Success = false,
                JobId = job.JobId,
                VendorId = null,
                Reason = "Duplicate request detected",
                IdempotencyKey = idempotencyKey
            };
        }

        try
        {
            var candidate = vendors
                .Where(v => v.CanHandle(job.SourceLanguage, job.TargetLanguage))
                .OrderBy(v => v.CurrentLoad / (double)Math.Max(1, v.MaxCapacity))
                .ThenBy(v => v.CostScore)
                .ThenByDescending(v => v.QualityScore)
                .FirstOrDefault();

            if (candidate is null)
            {
                return new AssignmentResult
                {
                    Success = false,
                    JobId = job.JobId,
                    VendorId = null,
                    Reason = "No matching vendor",
                    IdempotencyKey = idempotencyKey
                };
            }

            var reserved = await vendorGateway.ReserveCapacityAsync(candidate, job, cancellationToken);
            if (!reserved)
            {
                return new AssignmentResult
                {
                    Success = false,
                    JobId = job.JobId,
                    VendorId = null,
                    Reason = "Vendor reservation failed",
                    IdempotencyKey = idempotencyKey
                };
            }

            var result = new AssignmentResult
            {
                Success = true,
                JobId = job.JobId,
                VendorId = candidate.VendorId,
                Reason = "Assigned",
                IdempotencyKey = idempotencyKey
            };

            await eventPublisher.PublishAssignedAsync(result, cancellationToken);
            return result;
        }
        finally
        {
            await idempotencyStore.CompleteAsync(idempotencyKey, cancellationToken);
        }
    }
}
