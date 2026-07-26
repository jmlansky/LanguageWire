using System.Collections.Concurrent;
using VendorScheduler.Core.Contracts;
using VendorScheduler.Core.Domain;

namespace VendorScheduler.Infrastructure;

public sealed record MetricsSnapshot(
    long AssignmentsTotal,
    long AssignmentsSucceeded,
    long AssignmentsReplayed,
    double SuccessRate,
    long VendorRetriesTotal,
    IReadOnlyDictionary<string, long> AssignmentsByOutcome,
    IReadOnlyDictionary<string, long> VendorAttemptsByStatus);

/// <summary>
/// Process-local counters exposed through <c>GET /metrics</c>.
/// </summary>
/// <remarks>
/// Deliberately simple: counters live in memory, are per-instance and reset with the process. In
/// production these would be OpenTelemetry instruments scraped by the monitoring stack rather than
/// summed here — the point of this adapter is that the domain already reports the right events, so
/// swapping it changes no code outside this class.
/// </remarks>
public sealed class InMemoryAssignmentMetrics : IAssignmentMetrics
{
    private readonly ConcurrentDictionary<AssignmentOutcome, long> _byOutcome = new();
    private readonly ConcurrentDictionary<VendorReservationStatus, long> _byAttemptStatus = new();
    private long _assignmentsTotal;
    private long _assignmentsReplayed;
    private long _retriesTotal;

    public void AssignmentCompleted(AssignmentOutcome outcome, bool isReplay)
    {
        Interlocked.Increment(ref _assignmentsTotal);
        _byOutcome.AddOrUpdate(outcome, 1, (_, current) => current + 1);

        if (isReplay)
        {
            Interlocked.Increment(ref _assignmentsReplayed);
        }
    }

    public void VendorAttempt(VendorReservationStatus status)
        => _byAttemptStatus.AddOrUpdate(status, 1, (_, current) => current + 1);

    public void VendorRetryScheduled() => Interlocked.Increment(ref _retriesTotal);

    public MetricsSnapshot Snapshot()
    {
        var total = Interlocked.Read(ref _assignmentsTotal);
        _byOutcome.TryGetValue(AssignmentOutcome.Assigned, out var succeeded);

        return new MetricsSnapshot(
            AssignmentsTotal: total,
            AssignmentsSucceeded: succeeded,
            AssignmentsReplayed: Interlocked.Read(ref _assignmentsReplayed),
            SuccessRate: total == 0 ? 0d : Math.Round(succeeded / (double)total, 4),
            VendorRetriesTotal: Interlocked.Read(ref _retriesTotal),
            AssignmentsByOutcome: _byOutcome.ToDictionary(e => e.Key.ToString(), e => e.Value),
            VendorAttemptsByStatus: _byAttemptStatus.ToDictionary(e => e.Key.ToString(), e => e.Value));
    }
}
