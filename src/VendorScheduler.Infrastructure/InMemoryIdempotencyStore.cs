using System.Collections.Concurrent;
using VendorScheduler.Core.Contracts;
using VendorScheduler.Core.Domain;

namespace VendorScheduler.Infrastructure;

public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AssignmentResult> _completed = new(StringComparer.Ordinal);

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

        var added = _inFlight.TryAdd(idempotencyKey, 0);
        return Task.FromResult(added);
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
