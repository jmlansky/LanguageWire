using System.Collections.Concurrent;
using VendorScheduler.Core.Contracts;
using VendorScheduler.Core.Domain;

namespace VendorScheduler.Infrastructure;

/// <summary>
/// Vendor roster with live, mutable load — the in-memory stand-in for the vendor management service
/// that would own this data in the real system. Load starts at the seeded values, grows as
/// reservations land, and can be set directly through the test-support endpoints so failover and
/// exhaustion scenarios are reproducible from Swagger.
/// </summary>
public sealed class InMemoryVendorDirectory : IVendorDirectory
{
    private sealed class VendorState
    {
        public required string Name { get; init; }
        public required IReadOnlyCollection<string> SupportedPairs { get; init; }
        public required decimal CostScore { get; init; }
        public required decimal QualityScore { get; init; }
        public int MaxCapacity;
        public int CurrentLoad;
    }

    private readonly ConcurrentDictionary<Guid, VendorState> _vendors = new();

    public InMemoryVendorDirectory()
    {
        Seed(Guid.Parse("11111111-1111-1111-1111-111111111111"), "VendorA",
            ["en->de", "en->fr"], maxCapacity: 100, currentLoad: 35, costScore: 0.75m, qualityScore: 0.90m);
        Seed(Guid.Parse("22222222-2222-2222-2222-222222222222"), "VendorB",
            ["en->de", "de->en"], maxCapacity: 120, currentLoad: 50, costScore: 0.70m, qualityScore: 0.85m);
    }

    public Task<IReadOnlyCollection<VendorPartner>> GetVendorsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyCollection<VendorPartner> snapshot = _vendors
            .Select(entry => Snapshot(entry.Key, entry.Value))
            .ToList();

        return Task.FromResult(snapshot);
    }

    /// <summary>
    /// Atomically takes one slot of the vendor's capacity. This is the "vendor side" of a
    /// reservation: the gateway asks, and a full vendor says no.
    /// </summary>
    public bool TryReserveSlot(Guid vendorId)
    {
        if (!_vendors.TryGetValue(vendorId, out var state))
        {
            return false;
        }

        lock (state)
        {
            if (state.CurrentLoad >= state.MaxCapacity)
            {
                return false;
            }

            state.CurrentLoad++;
            return true;
        }
    }

    /// <summary>Test support: overwrite a vendor's load and capacity to stage a scenario.</summary>
    public bool TrySetState(Guid vendorId, int currentLoad, int maxCapacity)
    {
        if (!_vendors.TryGetValue(vendorId, out var state))
        {
            return false;
        }

        lock (state)
        {
            state.CurrentLoad = Math.Max(0, currentLoad);
            state.MaxCapacity = Math.Max(1, maxCapacity);
        }

        return true;
    }

    private void Seed(
        Guid vendorId,
        string name,
        string[] pairs,
        int maxCapacity,
        int currentLoad,
        decimal costScore,
        decimal qualityScore)
        => _vendors[vendorId] = new VendorState
        {
            Name = name,
            SupportedPairs = pairs,
            MaxCapacity = maxCapacity,
            CurrentLoad = currentLoad,
            CostScore = costScore,
            QualityScore = qualityScore
        };

    private static VendorPartner Snapshot(Guid vendorId, VendorState state)
    {
        lock (state)
        {
            return new VendorPartner
            {
                VendorId = vendorId,
                Name = state.Name,
                SupportedPairs = state.SupportedPairs,
                MaxCapacity = state.MaxCapacity,
                CurrentLoad = state.CurrentLoad,
                CostScore = state.CostScore,
                QualityScore = state.QualityScore
            };
        }
    }
}
