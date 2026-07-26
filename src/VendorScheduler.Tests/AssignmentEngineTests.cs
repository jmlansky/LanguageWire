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
        var gateway = StubVendorGateway.AlwaysReserves();
        var store = new FakeIdempotencyStore();
        var publisher = new RecordingEventPublisher();
        var engine = new AssignmentEngine(gateway, store, publisher);

        var job = TestData.Job();
        var vendor = TestData.Vendor("VendorA");

        var result = await engine.AssignAsync(job, [vendor]);

        Assert.Equal(AssignmentOutcome.Assigned, result.Outcome);
        Assert.True(result.Success);
        Assert.False(result.IsReplay);
        Assert.Equal(vendor.VendorId, result.VendorId);
        Assert.Equal(1, gateway.CallCount);
        Assert.Single(publisher.Published);

        // A successful assignment is recorded as final and releases the in-flight lease.
        Assert.NotNull(await store.TryGetCompletedAsync(result.IdempotencyKey));
        Assert.False(store.IsInFlight(result.IdempotencyKey));
    }

    [Fact]
    public async Task AssignAsync_WithNoCapableVendor_FailsWithoutCallingVendorOrPublishing()
    {
        var gateway = StubVendorGateway.AlwaysReserves();
        var store = new FakeIdempotencyStore();
        var publisher = new RecordingEventPublisher();
        var engine = new AssignmentEngine(gateway, store, publisher);

        var job = TestData.Job(target: "es");
        var vendor = TestData.Vendor("VendorA", pairs: ["en->de"]);

        var result = await engine.AssignAsync(job, [vendor]);

        Assert.Equal(AssignmentOutcome.NoMatchingVendor, result.Outcome);
        Assert.Null(result.VendorId);
        Assert.Equal(0, gateway.CallCount);
        Assert.Empty(publisher.Published);

        // Nothing was persisted, so the job is not poisoned and can be retried later.
        Assert.Null(await store.TryGetCompletedAsync(result.IdempotencyKey));
        Assert.False(store.IsInFlight(result.IdempotencyKey));
    }

    [Fact]
    public async Task AssignAsync_RepeatedAfterCompletion_ReplaysStoredResultWithoutReassigning()
    {
        var gateway = StubVendorGateway.AlwaysReserves();
        var store = new FakeIdempotencyStore();
        var publisher = new RecordingEventPublisher();
        var engine = new AssignmentEngine(gateway, store, publisher);

        var job = TestData.Job();
        var vendor = TestData.Vendor("VendorA");

        var first = await engine.AssignAsync(job, [vendor]);
        var replay = await engine.AssignAsync(job, [vendor]);

        // The replay is the same logical assignment, flagged so the caller can tell them apart.
        Assert.Equal(AssignmentOutcome.Assigned, replay.Outcome);
        Assert.True(replay.Success);
        Assert.True(replay.IsReplay);
        Assert.Equal(first.VendorId, replay.VendorId);
        Assert.Equal(first.IdempotencyKey, replay.IdempotencyKey);

        // The vendor was not called again and the event was not published twice.
        Assert.Equal(1, gateway.CallCount);
        Assert.Single(publisher.Published);
    }

    [Fact]
    public async Task AssignAsync_WhileFirstRequestIsStillInFlight_ReportsAlreadyInProgress()
    {
        var gateway = new ControllableVendorGateway();
        var store = new FakeIdempotencyStore();
        var publisher = new RecordingEventPublisher();
        var engine = new AssignmentEngine(gateway, store, publisher);

        var job = TestData.Job();
        var vendor = TestData.Vendor("VendorA");

        // Hold the first request inside the vendor call so the overlap is deterministic.
        var first = engine.AssignAsync(job, [vendor]);
        await gateway.Entered;

        var duplicate = await engine.AssignAsync(job, [vendor]);

        Assert.Equal(AssignmentOutcome.AlreadyInProgress, duplicate.Outcome);
        Assert.False(duplicate.Success);
        Assert.Null(duplicate.VendorId);

        gateway.Release();
        var firstResult = await first;

        Assert.Equal(AssignmentOutcome.Assigned, firstResult.Outcome);
        Assert.Equal(1, gateway.CallCount);
        Assert.Single(publisher.Published);
    }

    [Fact]
    public async Task AssignAsync_ConcurrentRequestsForSameJob_ProduceExactlyOneAssignment()
    {
        const int concurrentRequests = 32;

        var gateway = StubVendorGateway.AlwaysReserves();
        var store = new FakeIdempotencyStore();
        var publisher = new RecordingEventPublisher();
        var engine = new AssignmentEngine(gateway, store, publisher);

        var job = TestData.Job();
        var vendor = TestData.Vendor("VendorA");

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, concurrentRequests)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                return await engine.AssignAsync(job, [vendor]);
            }))
            .ToArray();

        start.SetResult();
        var results = await Task.WhenAll(requests);

        // The vendor is reserved once and the event is published once, whatever the interleaving.
        Assert.Equal(1, gateway.CallCount);
        Assert.Single(publisher.Published);
        Assert.Equal(1, results.Count(r => r.Success && !r.IsReplay));

        // Every other request either replayed the winner or was told the assignment was in flight.
        Assert.All(results, r => Assert.True(
            r.Outcome is AssignmentOutcome.Assigned or AssignmentOutcome.AlreadyInProgress,
            $"unexpected outcome {r.Outcome}"));

        // Whoever got a vendor got the same one.
        var assignedVendors = results.Where(r => r.Success).Select(r => r.VendorId).Distinct();
        Assert.Single(assignedVendors);
    }

    [Fact]
    public async Task AssignAsync_AfterAFailedAttempt_CanBeRetriedSuccessfully()
    {
        var gateway = StubVendorGateway.Sequence(false, true);
        var store = new FakeIdempotencyStore();
        var publisher = new RecordingEventPublisher();
        var engine = new AssignmentEngine(gateway, store, publisher);

        var job = TestData.Job();
        var vendor = TestData.Vendor("VendorA");

        var failed = await engine.AssignAsync(job, [vendor]);
        Assert.Equal(AssignmentOutcome.VendorReservationFailed, failed.Outcome);
        Assert.Empty(publisher.Published);

        // The failure did not consume the idempotency key, so the retry is a real attempt.
        var retried = await engine.AssignAsync(job, [vendor]);

        Assert.Equal(AssignmentOutcome.Assigned, retried.Outcome);
        Assert.False(retried.IsReplay);
        Assert.Equal(2, gateway.CallCount);
        Assert.Single(publisher.Published);
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

internal sealed class StubVendorGateway : IVendorGateway
{
    private readonly Func<int, bool> _resultForAttempt;
    private int _callCount;

    private StubVendorGateway(Func<int, bool> resultForAttempt) => _resultForAttempt = resultForAttempt;

    public int CallCount => Volatile.Read(ref _callCount);

    public static StubVendorGateway AlwaysReserves() => new(_ => true);

    public static StubVendorGateway Sequence(params bool[] results)
        => new(attempt => results[Math.Min(attempt, results.Length - 1)]);

    public Task<bool> ReserveCapacityAsync(VendorPartner vendor, TranslationJob job, CancellationToken cancellationToken = default)
    {
        var attempt = Interlocked.Increment(ref _callCount) - 1;
        return Task.FromResult(_resultForAttempt(attempt));
    }
}

/// <summary>
/// Vendor gateway that parks inside the call until released, so a test can hold one request in the
/// critical section while another one runs.
/// </summary>
internal sealed class ControllableVendorGateway : IVendorGateway
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _callCount;

    public Task Entered => _entered.Task;

    public int CallCount => Volatile.Read(ref _callCount);

    public void Release() => _released.TrySetResult();

    public async Task<bool> ReserveCapacityAsync(VendorPartner vendor, TranslationJob job, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        _entered.TrySetResult();
        await _released.Task;
        return true;
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
