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
        var idempotencyKey = AssignmentKeys.For(job.JobId);

        // Fast path: the job was already assigned, so replay the stored result untouched.
        var stored = await TryReplayAsync(idempotencyKey, cancellationToken);
        if (stored is not null)
        {
            return stored;
        }

        var started = await idempotencyStore.TryStartAsync(idempotencyKey, cancellationToken);
        if (!started)
        {
            return await ResolveLostRaceAsync(job.JobId, idempotencyKey, cancellationToken);
        }

        try
        {
            var result = await ExecuteAsync(job, vendors, idempotencyKey, cancellationToken);

            // Only a successful result becomes final. A failure leaves the job retryable.
            if (!result.Success)
            {
                return result;
            }

            // Persist before publishing: if the publisher throws, the assignment is not lost.
            await idempotencyStore.SaveCompletedAsync(idempotencyKey, result, cancellationToken);
            await eventPublisher.PublishAssignedAsync(result, cancellationToken);
            return result;
        }
        finally
        {
            await idempotencyStore.ReleaseInFlightAsync(idempotencyKey, cancellationToken);
        }
    }

    /// <summary>
    /// Another request owns the key. It may have completed between our replay check and our attempt
    /// to start, so we look once more before declaring the assignment in progress.
    /// </summary>
    private async Task<AssignmentResult> ResolveLostRaceAsync(
        Guid jobId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var stored = await TryReplayAsync(idempotencyKey, cancellationToken);
        if (stored is not null)
        {
            return stored;
        }

        return AssignmentResult.Failed(jobId, idempotencyKey, AssignmentOutcome.AlreadyInProgress);
    }

    private async Task<AssignmentResult?> TryReplayAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        var stored = await idempotencyStore.TryGetCompletedAsync(idempotencyKey, cancellationToken);
        return stored?.AsReplay();
    }

    private async Task<AssignmentResult> ExecuteAsync(
        TranslationJob job,
        IReadOnlyCollection<VendorPartner> vendors,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var candidate = SelectVendor(job, vendors);
        if (candidate is null)
        {
            return AssignmentResult.Failed(job.JobId, idempotencyKey, AssignmentOutcome.NoMatchingVendor);
        }

        var reserved = await vendorGateway.ReserveCapacityAsync(candidate, job, cancellationToken);
        if (!reserved)
        {
            return AssignmentResult.Failed(job.JobId, idempotencyKey, AssignmentOutcome.VendorReservationFailed);
        }

        return AssignmentResult.Assigned(job.JobId, candidate.VendorId, idempotencyKey);
    }

    private static VendorPartner? SelectVendor(TranslationJob job, IReadOnlyCollection<VendorPartner> vendors)
        => vendors
            .Where(v => v.CanHandle(job.SourceLanguage, job.TargetLanguage))
            .OrderBy(v => v.CurrentLoad / (double)Math.Max(1, v.MaxCapacity))
            .ThenBy(v => v.CostScore)
            .ThenByDescending(v => v.QualityScore)
            .FirstOrDefault();
}
