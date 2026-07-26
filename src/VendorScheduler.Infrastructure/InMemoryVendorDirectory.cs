using VendorScheduler.Core.Contracts;
using VendorScheduler.Core.Domain;

namespace VendorScheduler.Infrastructure;

/// <summary>
/// Fixed vendor roster. Replaces the list that used to be built inline in the HTTP handler, so the
/// endpoint no longer owns domain data and the roster can be served and swapped independently.
/// </summary>
public sealed class InMemoryVendorDirectory : IVendorDirectory
{
    private static readonly IReadOnlyCollection<VendorPartner> Roster =
    [
        new()
        {
            VendorId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "VendorA",
            SupportedPairs = ["en->de", "en->fr"],
            MaxCapacity = 100,
            CurrentLoad = 35,
            CostScore = 0.75m,
            QualityScore = 0.90m
        },
        new()
        {
            VendorId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Name = "VendorB",
            SupportedPairs = ["en->de", "de->en"],
            MaxCapacity = 120,
            CurrentLoad = 50,
            CostScore = 0.70m,
            QualityScore = 0.85m
        }
    ];

    public Task<IReadOnlyCollection<VendorPartner>> GetVendorsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Roster);
}
