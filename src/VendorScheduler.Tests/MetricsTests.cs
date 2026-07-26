using VendorScheduler.Core.Domain;
using VendorScheduler.Core.Services;
using VendorScheduler.Infrastructure;

namespace VendorScheduler.Tests;

public sealed class InMemoryAssignmentMetricsTests
{
    [Fact]
    public void Snapshot_WithNoTraffic_ReportsZeroWithoutDividingByZero()
    {
        var snapshot = new InMemoryAssignmentMetrics().Snapshot();

        Assert.Equal(0, snapshot.AssignmentsTotal);
        Assert.Equal(0d, snapshot.SuccessRate);
    }

    [Fact]
    public void Snapshot_BreaksFailuresDownByReason()
    {
        var metrics = new InMemoryAssignmentMetrics();

        metrics.AssignmentCompleted(AssignmentOutcome.Assigned, isReplay: false);
        metrics.AssignmentCompleted(AssignmentOutcome.Assigned, isReplay: false);
        metrics.AssignmentCompleted(AssignmentOutcome.NoCapacityAvailable, isReplay: false);
        metrics.AssignmentCompleted(AssignmentOutcome.VendorUnavailable, isReplay: false);

        var snapshot = metrics.Snapshot();

        Assert.Equal(4, snapshot.AssignmentsTotal);
        Assert.Equal(2, snapshot.AssignmentsSucceeded);
        Assert.Equal(0.5d, snapshot.SuccessRate);

        // Separate counters are what let an alert distinguish "buy capacity" from "page someone".
        Assert.Equal(1, snapshot.AssignmentsByOutcome["NoCapacityAvailable"]);
        Assert.Equal(1, snapshot.AssignmentsByOutcome["VendorUnavailable"]);
    }

    [Fact]
    public void Snapshot_CountsReplaysSeparatelyFromFirstExecutions()
    {
        var metrics = new InMemoryAssignmentMetrics();

        metrics.AssignmentCompleted(AssignmentOutcome.Assigned, isReplay: false);
        metrics.AssignmentCompleted(AssignmentOutcome.Assigned, isReplay: true);

        var snapshot = metrics.Snapshot();

        // A replay rate that suddenly climbs means clients are retrying more than they should.
        Assert.Equal(2, snapshot.AssignmentsTotal);
        Assert.Equal(1, snapshot.AssignmentsReplayed);
    }

    [Fact]
    public async Task Metrics_UnderConcurrency_DoNotLoseCounts()
    {
        var metrics = new InMemoryAssignmentMetrics();

        var work = Enumerable.Range(0, 200)
            .Select(i => Task.Run(() =>
            {
                metrics.AssignmentCompleted(
                    i % 2 == 0 ? AssignmentOutcome.Assigned : AssignmentOutcome.NoCapacityAvailable,
                    isReplay: false);
                metrics.VendorRetryScheduled();
            }))
            .ToArray();

        await Task.WhenAll(work);

        var snapshot = metrics.Snapshot();
        Assert.Equal(200, snapshot.AssignmentsTotal);
        Assert.Equal(100, snapshot.AssignmentsSucceeded);
        Assert.Equal(200, snapshot.VendorRetriesTotal);
    }
}

public sealed class MetricsIntegrationTests
{
    [Fact]
    public async Task Engine_RecordsOneAssignmentPerRequestIncludingReplays()
    {
        var metrics = new InMemoryAssignmentMetrics();
        var engine = new AssignmentEngine(
            ScriptedVendorGateway.AlwaysReserves(),
            new FakeIdempotencyStore(),
            new RecordingEventPublisher(),
            metrics: metrics);

        var job = TestData.Job();
        var vendor = TestData.Vendor("VendorA");

        await engine.AssignAsync(job, [vendor]);
        await engine.AssignAsync(job, [vendor]);

        var snapshot = metrics.Snapshot();
        Assert.Equal(2, snapshot.AssignmentsTotal);
        Assert.Equal(1, snapshot.AssignmentsReplayed);
    }

    [Fact]
    public async Task ResilienceLayer_CountsEveryAttemptAndEveryRetry()
    {
        var metrics = new InMemoryAssignmentMetrics();
        var clock = new RecordingDelay();
        var gateway = new ResilientVendorGateway(
            ScriptedVendorGateway.Returning(
                VendorReservation.TransientFailure("boom"),
                VendorReservation.TransientFailure("boom"),
                VendorReservation.Reserved()),
            new VendorResiliencePolicy { MaxAttempts = 3 },
            clock.WaitAsync,
            () => 0d,
            metrics: metrics);

        await gateway.ReserveCapacityAsync(TestData.Vendor("VendorA"), TestData.Job());

        var snapshot = metrics.Snapshot();
        Assert.Equal(2, snapshot.VendorAttemptsByStatus["TransientFailure"]);
        Assert.Equal(1, snapshot.VendorAttemptsByStatus["Reserved"]);
        Assert.Equal(2, snapshot.VendorRetriesTotal);
    }
}
