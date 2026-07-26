using VendorScheduler.Core.Domain;
using VendorScheduler.Core.Services;
using VendorScheduler.Infrastructure;

namespace VendorScheduler.Tests;

public sealed class UrgencyPolicyTests
{
    private static readonly DateTime Now = new(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Evaluate_HighPriority_IsUrgentRegardlessOfDueDate()
    {
        var job = TestData.Job(priority: JobPriority.High, dueAtUtc: Now.AddDays(30));

        Assert.Equal(JobUrgency.Urgent, UrgencyPolicy.Evaluate(job, Now));
    }

    [Fact]
    public void Evaluate_NormalPriorityDueWithinTheWindow_EscalatesToUrgent()
    {
        var job = TestData.Job(priority: JobPriority.Normal, dueAtUtc: Now.AddHours(6));

        Assert.Equal(JobUrgency.Urgent, UrgencyPolicy.Evaluate(job, Now));
    }

    [Fact]
    public void Evaluate_LowPriorityAboutToExpire_EscalatesToUrgent()
    {
        // The deadline dominates: declared priority cannot keep an expiring job in the slow lane.
        var job = TestData.Job(priority: JobPriority.Low, dueAtUtc: Now.AddHours(2));

        Assert.Equal(JobUrgency.Urgent, UrgencyPolicy.Evaluate(job, Now));
    }

    [Fact]
    public void Evaluate_NormalPriorityWithComfortableMargin_StaysNormal()
    {
        var job = TestData.Job(priority: JobPriority.Normal, dueAtUtc: Now.AddDays(7));

        Assert.Equal(JobUrgency.Normal, UrgencyPolicy.Evaluate(job, Now));
    }

    [Fact]
    public void Evaluate_SameJobCloserToItsDeadline_EscalatesOnItsOwn()
    {
        // Urgency is derived at evaluation time, never stored: a retried job escalates by itself.
        var job = TestData.Job(priority: JobPriority.Normal, dueAtUtc: Now.AddDays(7));

        Assert.Equal(JobUrgency.Normal, UrgencyPolicy.Evaluate(job, Now));
        Assert.Equal(JobUrgency.Urgent, UrgencyPolicy.Evaluate(job, Now.AddDays(6).AddHours(2)));
    }
}

public sealed class RankingTests
{
    private static readonly DateTime Now = new(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task AssignAsync_NormalJob_PrefersTheLeastLoadedVendor()
    {
        var gateway = ScriptedVendorGateway.AlwaysReserves();
        var engine = BuildEngine(gateway);

        var lightlyLoaded = TestData.Vendor("Light", currentLoad: 10, qualityScore: 0.70m);
        var premium = TestData.Vendor("Premium", currentLoad: 80, qualityScore: 0.99m);

        var job = TestData.Job(priority: JobPriority.Normal, dueAtUtc: Now.AddDays(7));
        var result = await engine.AssignAsync(job, [premium, lightlyLoaded]);

        Assert.Equal(lightlyLoaded.VendorId, result.VendorId);
    }

    [Fact]
    public async Task AssignAsync_UrgentJob_PrefersTheBestQualityVendorEvenIfBusier()
    {
        var gateway = ScriptedVendorGateway.AlwaysReserves();
        var engine = BuildEngine(gateway);

        var lightlyLoaded = TestData.Vendor("Light", currentLoad: 10, qualityScore: 0.70m);
        var premium = TestData.Vendor("Premium", currentLoad: 80, qualityScore: 0.99m);

        var job = TestData.Job(priority: JobPriority.High, dueAtUtc: Now.AddDays(7));
        var result = await engine.AssignAsync(job, [premium, lightlyLoaded]);

        Assert.Equal(premium.VendorId, result.VendorId);
    }

    [Fact]
    public async Task AssignAsync_UrgentJobWhosePremiumVendorIsFull_FallsBackByQualityOrder()
    {
        var gateway = ScriptedVendorGateway.Returning(
            VendorReservation.Rejected(VendorRejectionReason.NoCapacity),
            VendorReservation.Reserved());
        var engine = BuildEngine(gateway);

        var second = TestData.Vendor("Second", qualityScore: 0.85m);
        var premium = TestData.Vendor("Premium", qualityScore: 0.99m);

        var job = TestData.Job(priority: JobPriority.High);
        var result = await engine.AssignAsync(job, [second, premium]);

        // Premium was tried first (and rejected); the fallback is the next best quality.
        Assert.Equal(second.VendorId, result.VendorId);
        Assert.Equal(2, gateway.CallCount);
    }

    private static AssignmentEngine BuildEngine(ScriptedVendorGateway gateway)
        => new(gateway, new FakeIdempotencyStore(), new RecordingEventPublisher(), () => Now);
}

public sealed class InMemoryVendorDirectoryTests
{
    private static readonly Guid VendorA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task TryReserveSlot_ConsumesCapacityUntilTheVendorIsFull()
    {
        var directory = new InMemoryVendorDirectory();
        Assert.True(directory.TrySetState(VendorA, currentLoad: 0, maxCapacity: 2));

        Assert.True(directory.TryReserveSlot(VendorA));
        Assert.True(directory.TryReserveSlot(VendorA));
        Assert.False(directory.TryReserveSlot(VendorA));

        var vendor = (await directory.GetVendorsAsync()).Single(v => v.VendorId == VendorA);
        Assert.Equal(2, vendor.CurrentLoad);
    }

    [Fact]
    public async Task TrySetState_OverwritesLoadAndCapacity_AndSnapshotsReflectIt()
    {
        var directory = new InMemoryVendorDirectory();

        Assert.True(directory.TrySetState(VendorA, currentLoad: 99, maxCapacity: 100));

        var vendor = (await directory.GetVendorsAsync()).Single(v => v.VendorId == VendorA);
        Assert.Equal(99, vendor.CurrentLoad);
        Assert.Equal(100, vendor.MaxCapacity);
    }

    [Fact]
    public void TrySetState_UnknownVendor_ReturnsFalse()
    {
        var directory = new InMemoryVendorDirectory();

        Assert.False(directory.TrySetState(Guid.NewGuid(), currentLoad: 0, maxCapacity: 10));
    }

    [Fact]
    public async Task TryReserveSlot_UnderConcurrency_NeverOversellsCapacity()
    {
        var directory = new InMemoryVendorDirectory();
        Assert.True(directory.TrySetState(VendorA, currentLoad: 0, maxCapacity: 10));

        var attempts = Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() => directory.TryReserveSlot(VendorA)))
            .ToArray();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(10, results.Count(r => r));

        var vendor = (await directory.GetVendorsAsync()).Single(v => v.VendorId == VendorA);
        Assert.Equal(10, vendor.CurrentLoad);
    }
}
