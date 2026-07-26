using VendorScheduler.Core.Contracts;
using VendorScheduler.Core.Domain;

namespace VendorScheduler.Infrastructure;

/// <summary>
/// Stand-in for the vendor API: one call, one answer, no retry logic. The retry policy that used to
/// live here now sits in <c>ResilientVendorGateway</c>, so it is not lost when this class is replaced
/// by a real vendor client.
/// </summary>
/// <remarks>
/// This fake always answers cleanly — it never times out, fails or replies partially. Fault
/// simulation is deliberately deferred to a follow-up step; until then the resilience behaviour is
/// covered by the unit tests, not by running the API.
/// </remarks>
public sealed class FakeVendorGateway : IVendorGateway
{
    public Task<VendorReservation> ReserveCapacityAsync(
        VendorPartner vendor,
        TranslationJob job,
        CancellationToken cancellationToken = default)
    {
        if (vendor.CurrentLoad >= vendor.MaxCapacity)
        {
            return Task.FromResult(VendorReservation.Rejected(
                VendorRejectionReason.NoCapacity,
                $"{vendor.Name} is at {vendor.CurrentLoad}/{vendor.MaxCapacity}"));
        }

        return Task.FromResult(VendorReservation.Reserved());
    }
}
