using VendorScheduler.Core.Domain;
using VendorScheduler.Core.Services;

namespace VendorScheduler.Tests;

public sealed class ResilientVendorGatewayTests
{
    private static readonly VendorResiliencePolicy Policy = new()
    {
        MaxAttempts = 3,
        BaseBackoff = TimeSpan.FromMilliseconds(100),
        MaxBackoff = TimeSpan.FromMilliseconds(1000),
        JitterFactor = 0.2
    };

    [Fact]
    public async Task ReserveCapacityAsync_WhenVendorReservesImmediately_DoesNotRetryOrWait()
    {
        var inner = ScriptedVendorGateway.Returning(VendorReservation.Reserved());
        var clock = new RecordingDelay();
        var gateway = Build(inner, clock);

        var reservation = await gateway.ReserveCapacityAsync(TestData.Vendor("VendorA"), TestData.Job());

        Assert.Equal(VendorReservationStatus.Reserved, reservation.Status);
        Assert.Equal(1, inner.CallCount);
        Assert.Empty(clock.Delays);
    }

    [Fact]
    public async Task ReserveCapacityAsync_WhenVendorRejects_DoesNotRetry()
    {
        // A rejection is the vendor's final answer: insisting only wastes time and calls.
        var inner = ScriptedVendorGateway.Returning(
            VendorReservation.Rejected(VendorRejectionReason.NoCapacity),
            VendorReservation.Reserved());
        var clock = new RecordingDelay();
        var gateway = Build(inner, clock);

        var reservation = await gateway.ReserveCapacityAsync(TestData.Vendor("VendorA"), TestData.Job());

        Assert.Equal(VendorReservationStatus.Rejected, reservation.Status);
        Assert.Equal(VendorRejectionReason.NoCapacity, reservation.RejectionReason);
        Assert.Equal(1, inner.CallCount);
        Assert.Empty(clock.Delays);
    }

    [Fact]
    public async Task ReserveCapacityAsync_WhenTransientFailureThenSuccess_RetriesAndSucceeds()
    {
        var inner = ScriptedVendorGateway.Returning(
            VendorReservation.TransientFailure("connection reset"),
            VendorReservation.Reserved());
        var clock = new RecordingDelay();
        var gateway = Build(inner, clock);

        var reservation = await gateway.ReserveCapacityAsync(TestData.Vendor("VendorA"), TestData.Job());

        Assert.Equal(VendorReservationStatus.Reserved, reservation.Status);
        Assert.Equal(2, inner.CallCount);
        Assert.Single(clock.Delays);
    }

    [Fact]
    public async Task ReserveCapacityAsync_WhenAlwaysFailing_StopsAtMaxAttemptsAndReportsTerminalFailure()
    {
        var inner = ScriptedVendorGateway.Always(VendorReservation.TransientFailure("vendor down"));
        var clock = new RecordingDelay();
        var gateway = Build(inner, clock);

        var reservation = await gateway.ReserveCapacityAsync(TestData.Vendor("VendorA"), TestData.Job());

        Assert.Equal(VendorReservationStatus.TransientFailure, reservation.Status);
        Assert.Equal(Policy.MaxAttempts, inner.CallCount);

        // One wait between attempts, never after the last one.
        Assert.Equal(Policy.MaxAttempts - 1, clock.Delays.Count);
    }

    [Fact]
    public async Task ReserveCapacityAsync_BacksOffExponentially_WithoutExceedingTheCeiling()
    {
        var policy = new VendorResiliencePolicy
        {
            MaxAttempts = 5,
            BaseBackoff = TimeSpan.FromMilliseconds(100),
            MaxBackoff = TimeSpan.FromMilliseconds(300),
            JitterFactor = 0
        };
        var inner = ScriptedVendorGateway.Always(VendorReservation.TransientFailure("vendor down"));
        var clock = new RecordingDelay();
        var gateway = new ResilientVendorGateway(inner, policy, clock.WaitAsync, () => 0d);

        await gateway.ReserveCapacityAsync(TestData.Vendor("VendorA"), TestData.Job());

        // 100, 200, then capped at 300 instead of growing to 400 and 800.
        var scheduleMs = clock.Delays.Select(d => d.TotalMilliseconds).ToArray();
        Assert.Equal([100d, 200d, 300d, 300d], scheduleMs);
    }

    [Fact]
    public async Task ReserveCapacityAsync_AddsJitterWithinTheConfiguredFraction()
    {
        var policy = new VendorResiliencePolicy
        {
            MaxAttempts = 2,
            BaseBackoff = TimeSpan.FromMilliseconds(100),
            MaxBackoff = TimeSpan.FromMilliseconds(1000),
            JitterFactor = 0.2
        };
        var inner = ScriptedVendorGateway.Always(VendorReservation.TransientFailure("vendor down"));
        var clock = new RecordingDelay();

        // Maximum jitter: 100ms + 20% = 120ms. Proportional, so it keeps working as the delay grows.
        var gateway = new ResilientVendorGateway(inner, policy, clock.WaitAsync, () => 1d);

        await gateway.ReserveCapacityAsync(TestData.Vendor("VendorA"), TestData.Job());

        Assert.Equal(120d, Assert.Single(clock.Delays).TotalMilliseconds);
    }

    [Fact]
    public async Task ReserveCapacityAsync_WhenAnAttemptTimesOut_ReportsUncertainRatherThanFailure()
    {
        // The vendor may have reserved anyway, so the outcome is uncertain, not a clean failure.
        var policy = new VendorResiliencePolicy
        {
            MaxAttempts = 1,
            AttemptTimeout = TimeSpan.FromMilliseconds(50)
        };
        var inner = new HangingVendorGateway();
        var clock = new RecordingDelay();
        var gateway = new ResilientVendorGateway(inner, policy, clock.WaitAsync, () => 0d);

        var reservation = await gateway.ReserveCapacityAsync(TestData.Vendor("VendorA"), TestData.Job());

        Assert.Equal(VendorReservationStatus.Uncertain, reservation.Status);
    }

    [Fact]
    public async Task ReserveCapacityAsync_WhenGatewayThrows_TreatsItAsTransientAndRetries()
    {
        var inner = new ThrowingVendorGateway(throwsBeforeSucceeding: 1);
        var clock = new RecordingDelay();
        var gateway = Build(inner, clock);

        var reservation = await gateway.ReserveCapacityAsync(TestData.Vendor("VendorA"), TestData.Job());

        Assert.Equal(VendorReservationStatus.Reserved, reservation.Status);
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task ReserveCapacityAsync_WhenCallerCancels_PropagatesInsteadOfSwallowing()
    {
        // A caller walking away is not a vendor failure and must not be retried.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var inner = new HangingVendorGateway();
        var gateway = Build(inner, new RecordingDelay());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gateway.ReserveCapacityAsync(TestData.Vendor("VendorA"), TestData.Job(), cts.Token));
    }

    private static ResilientVendorGateway Build(Core.Contracts.IVendorGateway inner, RecordingDelay clock)
        => new(inner, Policy, clock.WaitAsync, () => 0d);
}
