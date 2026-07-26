using VendorScheduler.Core.Domain;
using VendorScheduler.Core.Services;

namespace VendorScheduler.Tests;

public sealed class AssignmentEngineTests
{
    [Fact]
    public async Task AssignAsync_WithCapableVendor_AssignsAndPublishesOnce()
    {
        var gateway = ScriptedVendorGateway.AlwaysReserves();
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
        var gateway = ScriptedVendorGateway.AlwaysReserves();
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
    public async Task AssignAsync_WhenBestVendorRejects_FallsBackToTheNextRankedVendor()
    {
        var gateway = ScriptedVendorGateway.Returning(
            VendorReservation.Rejected(VendorRejectionReason.NoCapacity),
            VendorReservation.Reserved());
        var store = new FakeIdempotencyStore();
        var publisher = new RecordingEventPublisher();
        var engine = new AssignmentEngine(gateway, store, publisher);

        // Ranking is least loaded first, so VendorA is tried before VendorB.
        var best = TestData.Vendor("VendorA", currentLoad: 10);
        var second = TestData.Vendor("VendorB", currentLoad: 50);

        var result = await engine.AssignAsync(TestData.Job(), [best, second]);

        Assert.Equal(AssignmentOutcome.Assigned, result.Outcome);
        Assert.Equal(second.VendorId, result.VendorId);
        Assert.Equal(2, gateway.CallCount);
        Assert.Single(publisher.Published);
    }

    [Fact]
    public async Task AssignAsync_WhenEveryVendorRejects_ReportsNoCapacityRatherThanAnOutage()
    {
        var gateway = ScriptedVendorGateway.Always(
            VendorReservation.Rejected(VendorRejectionReason.NoCapacity));
        var store = new FakeIdempotencyStore();
        var publisher = new RecordingEventPublisher();
        var engine = new AssignmentEngine(gateway, store, publisher);

        var result = await engine.AssignAsync(
            TestData.Job(),
            [TestData.Vendor("VendorA", currentLoad: 10), TestData.Vendor("VendorB", currentLoad: 50)]);

        // A business problem (not enough contracted capacity), not a technical one.
        Assert.Equal(AssignmentOutcome.NoCapacityAvailable, result.Outcome);
        Assert.Equal(2, gateway.CallCount);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task AssignAsync_WhenAVendorCannotBeReached_ReportsVendorUnavailable()
    {
        // The resilience layer has already exhausted its retries by the time the engine sees this.
        var gateway = ScriptedVendorGateway.Returning(
            VendorReservation.Rejected(VendorRejectionReason.NoCapacity),
            VendorReservation.TransientFailure("vendor down"));
        var store = new FakeIdempotencyStore();
        var publisher = new RecordingEventPublisher();
        var engine = new AssignmentEngine(gateway, store, publisher);

        var result = await engine.AssignAsync(
            TestData.Job(),
            [TestData.Vendor("VendorA", currentLoad: 10), TestData.Vendor("VendorB", currentLoad: 50)]);

        // One vendor was merely full, but another never answered: that is an outage, and it wins.
        Assert.Equal(AssignmentOutcome.VendorUnavailable, result.Outcome);
        Assert.Empty(publisher.Published);

        // A technical failure must stay retryable.
        Assert.Null(await store.TryGetCompletedAsync(result.IdempotencyKey));
    }

    [Fact]
    public async Task AssignAsync_RepeatedAfterCompletion_ReplaysStoredResultWithoutReassigning()
    {
        var gateway = ScriptedVendorGateway.AlwaysReserves();
        var store = new FakeIdempotencyStore();
        var publisher = new RecordingEventPublisher();
        var engine = new AssignmentEngine(gateway, store, publisher);

        var job = TestData.Job();
        var vendor = TestData.Vendor("VendorA");

        var first = await engine.AssignAsync(job, [vendor]);
        var replay = await engine.AssignAsync(job, [vendor]);

        // The replay is the same logical assignment, flagged so the caller can tell them apart.
        Assert.Equal(AssignmentOutcome.Assigned, replay.Outcome);
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

        var gateway = ScriptedVendorGateway.AlwaysReserves();
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
        Assert.Single(results.Where(r => r.Success).Select(r => r.VendorId).Distinct());
    }

    [Fact]
    public async Task AssignAsync_AfterAFailedAttempt_CanBeRetriedSuccessfully()
    {
        var gateway = ScriptedVendorGateway.Returning(
            VendorReservation.TransientFailure("vendor down"),
            VendorReservation.Reserved());
        var store = new FakeIdempotencyStore();
        var publisher = new RecordingEventPublisher();
        var engine = new AssignmentEngine(gateway, store, publisher);

        var job = TestData.Job();
        var vendor = TestData.Vendor("VendorA");

        var failed = await engine.AssignAsync(job, [vendor]);
        Assert.Equal(AssignmentOutcome.VendorUnavailable, failed.Outcome);
        Assert.Empty(publisher.Published);

        // The failure did not consume the idempotency key, so the retry is a real attempt.
        var retried = await engine.AssignAsync(job, [vendor]);

        Assert.Equal(AssignmentOutcome.Assigned, retried.Outcome);
        Assert.False(retried.IsReplay);
        Assert.Equal(2, gateway.CallCount);
        Assert.Single(publisher.Published);
    }
}
