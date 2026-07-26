using VendorScheduler.Core.Domain;

namespace VendorScheduler.Core.Contracts;

public interface IVendorGateway
{
    Task<bool> ReserveCapacityAsync(VendorPartner vendor, TranslationJob job, CancellationToken cancellationToken = default);
}
