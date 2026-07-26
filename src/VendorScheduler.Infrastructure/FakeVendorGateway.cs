using VendorScheduler.Core.Contracts;
using VendorScheduler.Core.Domain;

namespace VendorScheduler.Infrastructure;

/// <summary>
/// Stand-in for the vendor API: one call, one answer, no retry logic. Retry policy lives in
/// <c>ResilientVendorGateway</c>, so it is not lost when this class is replaced by a real client.
/// </summary>
/// <remarks>
/// Capacity truth lives in <see cref="InMemoryVendorDirectory"/> — the simulated vendor side — and a
/// reservation consumes one slot there, so repeated assignments visibly fill a vendor up until it
/// starts rejecting. The dependency is fake-to-fake, inside Infrastructure; Core never sees it.
/// This fake still never times out or fails transiently; fault simulation is a deferred follow-up.
/// </remarks>
public sealed class FakeVendorGateway(InMemoryVendorDirectory vendorDirectory) : IVendorGateway
{
    public Task<VendorReservation> ReserveCapacityAsync(
        VendorPartner vendor,
        TranslationJob job,
        CancellationToken cancellationToken = default)
    {
        if (vendorDirectory.TryReserveSlot(vendor.VendorId))
        {
            return Task.FromResult(VendorReservation.Reserved());
        }

        return Task.FromResult(VendorReservation.Rejected(
            VendorRejectionReason.NoCapacity,
            $"{vendor.Name} is at full capacity"));
    }
}
