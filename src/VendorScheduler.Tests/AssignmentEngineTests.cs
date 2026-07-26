using System.Collections.Concurrent;
using VendorScheduler.Core.Contracts;
using VendorScheduler.Core.Domain;
using VendorScheduler.Core.Services;

namespace VendorScheduler.Tests;

public sealed class AssignmentEngineTests
{
    [Fact]
    public async Task AssignAsync_WithCapableVendor_AssignsAndPublishesOnce()
    {
        var gateway = new StubVendorGateway(reserveResult: true);
        var store = new FakeIdempotencyStore();
        var publisher = new RecordingEventPublisher();
        var engine = new AssignmentEngine(gateway, store, publisher);

        var job = TestData.Job();
        var vendor = TestData.Vendor("VendorA");

        var result = await engine.AssignAsync(job, [vendor]);

        Assert.True(result.Success);
        Assert.Equal(vendor.VendorId, result.VendorId);
        Assert.Equal("Assigned", result.Reason);
        Assert.Equal(1, gateway.CallCount);
        Assert.Single(publisher.Published);

        // Una asignación exitosa queda registrada como definitiva y libera el in-flight.
        Assert.NotNull(await store.TryGetCompletedAsync(result.IdempotencyKey));
        Assert.False(store.IsInFlight(result.IdempotencyKey));
    }
}

internal static class TestData
{
    public static TranslationJob Job(Guid? jobId = null, string source = "en", string target = "de")
        => new()
        {
            JobId = jobId ?? Guid.NewGuid(),
            SourceLanguage = source,
            TargetLanguage = target,
            Priority = "normal",
            DueAtUtc = DateTime.UtcNow.AddDays(1)
        };

    public static VendorPartner Vendor(
        string name,
        string[]? pairs = null,
        int maxCapacity = 100,
        int currentLoad = 10,
        decimal costScore = 0.70m,
        decimal qualityScore = 0.90m)
        => new()
        {
            VendorId = Guid.NewGuid(),
            Name = name,
            SupportedPairs = pairs ?? ["en->de"],
            MaxCapacity = maxCapacity,
            CurrentLoad = currentLoad,
            CostScore = costScore,
            QualityScore = qualityScore
        };
}

internal sealed class StubVendorGateway(bool reserveResult) : IVendorGateway
{
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public Task<bool> ReserveCapacityAsync(VendorPartner vendor, TranslationJob job, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        return Task.FromResult(reserveResult);
    }
}

internal sealed class RecordingEventPublisher : IAssignmentEventPublisher
{
    private readonly ConcurrentQueue<AssignmentResult> _published = new();

    public IReadOnlyCollection<AssignmentResult> Published => _published;

    public Task PublishAssignedAsync(AssignmentResult result, CancellationToken cancellationToken = default)
    {
        _published.Enqueue(result);
        return Task.CompletedTask;
    }
}

internal sealed class FakeIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AssignmentResult> _completed = new(StringComparer.Ordinal);

    public bool IsInFlight(string idempotencyKey) => _inFlight.ContainsKey(idempotencyKey);

    public Task<AssignmentResult?> TryGetCompletedAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        _completed.TryGetValue(idempotencyKey, out var result);
        return Task.FromResult(result);
    }

    public Task<bool> TryStartAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        if (_completed.ContainsKey(idempotencyKey))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(_inFlight.TryAdd(idempotencyKey, 0));
    }

    public Task SaveCompletedAsync(string idempotencyKey, AssignmentResult result, CancellationToken cancellationToken = default)
    {
        _completed[idempotencyKey] = result;
        return Task.CompletedTask;
    }

    public Task ReleaseInFlightAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        _inFlight.TryRemove(idempotencyKey, out _);
        return Task.CompletedTask;
    }
}
