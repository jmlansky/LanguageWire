using VendorScheduler.Core.Contracts;
using VendorScheduler.Core.Domain;

namespace VendorScheduler.Core.Services;

public sealed class AssignmentEngine(
    IVendorGateway vendorGateway,
    IIdempotencyStore idempotencyStore,
    IAssignmentEventPublisher eventPublisher,
    Func<DateTime>? utcNow = null) : IAssignmentEngine
{
    private readonly Func<DateTime> _utcNow = utcNow ?? (static () => DateTime.UtcNow);

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

    /// <summary>
    /// Walks the ranked candidates and takes the best one that actually accepts the job. A vendor
    /// that declines is skipped immediately; one that could not be reached has already exhausted its
    /// retry policy by the time we see it.
    /// </summary>
    private async Task<AssignmentResult> ExecuteAsync(
        TranslationJob job,
        IReadOnlyCollection<VendorPartner> vendors,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var candidates = RankCandidates(job, vendors);
        if (candidates.Count == 0)
        {
            return AssignmentResult.Failed(job.JobId, idempotencyKey, AssignmentOutcome.NoMatchingVendor);
        }

        var anyVendorUnreachable = false;

        foreach (var candidate in candidates)
        {
            var reservation = await vendorGateway.ReserveCapacityAsync(candidate, job, cancellationToken);

            if (reservation.IsReserved)
            {
                return AssignmentResult.Assigned(job.JobId, candidate.VendorId, idempotencyKey);
            }

            // A rejection is a business answer; anything else means we never got one.
            if (reservation.Status != VendorReservationStatus.Rejected)
            {
                anyVendorUnreachable = true;
            }
        }

        // Distinguishing the two keeps "we are out of capacity" from paging the on-call engineer.
        if (anyVendorUnreachable)
        {
            return AssignmentResult.Failed(job.JobId, idempotencyKey, AssignmentOutcome.VendorUnavailable);
        }

        return AssignmentResult.Failed(job.JobId, idempotencyKey, AssignmentOutcome.NoCapacityAvailable);
    }

    /// <summary>
    /// Capable vendors in preference order. Urgency — derived here, at evaluation time — picks the
    /// ranking: urgent work goes to the best vendor (quality first), routine work to the most
    /// convenient one (load, then cost).
    /// </summary>
    private List<VendorPartner> RankCandidates(TranslationJob job, IReadOnlyCollection<VendorPartner> vendors)
    {
        var capable = vendors.Where(v => v.CanHandle(job.SourceLanguage, job.TargetLanguage));

        if (UrgencyPolicy.Evaluate(job, _utcNow()) == JobUrgency.Urgent)
        {
            return capable
                .OrderByDescending(v => v.QualityScore)
                .ThenBy(v => LoadRatio(v))
                .ThenBy(v => v.CostScore)
                .ToList();
        }

        return capable
            .OrderBy(v => LoadRatio(v))
            .ThenBy(v => v.CostScore)
            .ThenByDescending(v => v.QualityScore)
            .ToList();
    }

    private static double LoadRatio(VendorPartner vendor)
        => vendor.CurrentLoad / (double)Math.Max(1, vendor.MaxCapacity);
}
