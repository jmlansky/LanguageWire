using VendorScheduler.Core.Domain;

namespace VendorScheduler.Core.Contracts;

public interface IAssignmentEventPublisher
{
    Task PublishAssignedAsync(AssignmentResult result, CancellationToken cancellationToken = default);
}
