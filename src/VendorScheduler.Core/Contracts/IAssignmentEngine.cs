using VendorScheduler.Core.Domain;

namespace VendorScheduler.Core.Contracts;

public interface IAssignmentEngine
{
    Task<AssignmentResult> AssignAsync(
        TranslationJob job,
        IReadOnlyCollection<VendorPartner> vendors,
        CancellationToken cancellationToken = default);
}
