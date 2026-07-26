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
        var idempotencyKey = BuildKey(job.JobId);

        // Otra request ganó la clave y sigue en vuelo: no ejecutamos nada, el cliente reintenta.
        var started = await idempotencyStore.TryStartAsync(idempotencyKey, cancellationToken);
        if (!started)
        {
            return Failed(job.JobId, idempotencyKey, "Assignment already in progress");
        }

        try
        {
            var result = await ExecuteAsync(job, vendors, idempotencyKey, cancellationToken);

            // Solo un resultado exitoso se vuelve definitivo. Un fallo deja el job reintentable.
            if (!result.Success)
            {
                return result;
            }

            // Persistimos antes de publicar: si el publish falla, la asignación no se pierde.
            await idempotencyStore.SaveCompletedAsync(idempotencyKey, result, cancellationToken);
            await eventPublisher.PublishAssignedAsync(result, cancellationToken);
            return result;
        }
        finally
        {
            await idempotencyStore.ReleaseInFlightAsync(idempotencyKey, cancellationToken);
        }
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
            return Failed(job.JobId, idempotencyKey, "No matching vendor");
        }

        var reserved = await vendorGateway.ReserveCapacityAsync(candidate, job, cancellationToken);
        if (!reserved)
        {
            return Failed(job.JobId, idempotencyKey, "Vendor reservation failed");
        }

        return new AssignmentResult
        {
            Success = true,
            JobId = job.JobId,
            VendorId = candidate.VendorId,
            Reason = "Assigned",
            IdempotencyKey = idempotencyKey
        };
    }

    private static VendorPartner? SelectVendor(TranslationJob job, IReadOnlyCollection<VendorPartner> vendors)
        => vendors
            .Where(v => v.CanHandle(job.SourceLanguage, job.TargetLanguage))
            .OrderBy(v => v.CurrentLoad / (double)Math.Max(1, v.MaxCapacity))
            .ThenBy(v => v.CostScore)
            .ThenByDescending(v => v.QualityScore)
            .FirstOrDefault();

    private static string BuildKey(Guid jobId) => $"assign:{jobId}";

    private static AssignmentResult Failed(Guid jobId, string idempotencyKey, string reason)
        => new()
        {
            Success = false,
            JobId = jobId,
            VendorId = null,
            Reason = reason,
            IdempotencyKey = idempotencyKey
        };
}
