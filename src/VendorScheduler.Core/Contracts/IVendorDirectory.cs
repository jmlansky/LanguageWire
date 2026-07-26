using VendorScheduler.Core.Domain;

namespace VendorScheduler.Core.Contracts;

public interface IVendorDirectory
{
    Task<IReadOnlyCollection<VendorPartner>> GetVendorsAsync(CancellationToken cancellationToken = default);
}
