using VendorScheduler.Core.Domain;

namespace VendorScheduler.Core.Contracts;

/// <summary>
/// A single reservation attempt against a vendor. Implementations talk to the vendor and report what
/// happened; they do not retry. Retry policy lives in <c>ResilientVendorGateway</c>, which decorates
/// this interface, so it survives swapping the implementation for a real vendor client.
/// </summary>
public interface IVendorGateway
{
    Task<VendorReservation> ReserveCapacityAsync(
        VendorPartner vendor,
        TranslationJob job,
        CancellationToken cancellationToken = default);
}
