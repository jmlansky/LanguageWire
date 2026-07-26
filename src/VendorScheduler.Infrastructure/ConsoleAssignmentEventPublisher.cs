using VendorScheduler.Core.Contracts;
using VendorScheduler.Core.Domain;

namespace VendorScheduler.Infrastructure;

public sealed class ConsoleAssignmentEventPublisher : IAssignmentEventPublisher
{
    public Task PublishAssignedAsync(AssignmentResult result, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"ASSIGNMENT_EVENT jobId={result.JobId} vendorId={result.VendorId} success={result.Success} key={result.IdempotencyKey}");
        return Task.CompletedTask;
    }
}
