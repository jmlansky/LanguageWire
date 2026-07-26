using System.Collections.Concurrent;
using VendorScheduler.Core.Contracts;
using VendorScheduler.Core.Domain;

namespace VendorScheduler.Tests;

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

/// <summary>
/// Records what the code under test asked to wait for, without waiting. Makes the backoff schedule
/// assertable and keeps the suite free of real delays.
/// </summary>
internal sealed class RecordingDelay
{
    private readonly ConcurrentQueue<TimeSpan> _delays = new();

    public IReadOnlyList<TimeSpan> Delays => _delays.ToArray();

    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _delays.Enqueue(delay);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Vendor gateway that answers from a script, so a test can dictate the exact sequence of outcomes.
/// </summary>
internal sealed class ScriptedVendorGateway : IVendorGateway
{
    private readonly VendorReservation[] _script;
    private readonly bool _repeatLast;
    private int _callCount;

    private ScriptedVendorGateway(VendorReservation[] script, bool repeatLast)
    {
        _script = script;
        _repeatLast = repeatLast;
    }

    public int CallCount => Volatile.Read(ref _callCount);

    public static ScriptedVendorGateway Returning(params VendorReservation[] script) => new(script, repeatLast: false);

    public static ScriptedVendorGateway Always(VendorReservation reservation) => new([reservation], repeatLast: true);

    public static ScriptedVendorGateway AlwaysReserves() => Always(VendorReservation.Reserved());

    public Task<VendorReservation> ReserveCapacityAsync(
        VendorPartner vendor,
        TranslationJob job,
        CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref _callCount) - 1;
        if (index >= _script.Length && !_repeatLast)
        {
            throw new InvalidOperationException($"Gateway called {index + 1} times but only {_script.Length} answers were scripted");
        }

        return Task.FromResult(_script[Math.Min(index, _script.Length - 1)]);
    }
}

/// <summary>Never answers, so the per-attempt timeout is what ends the call.</summary>
internal sealed class HangingVendorGateway : IVendorGateway
{
    public async Task<VendorReservation> ReserveCapacityAsync(
        VendorPartner vendor,
        TranslationJob job,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return VendorReservation.Reserved();
    }
}

/// <summary>Throws like a broken HTTP client would, instead of returning a classified outcome.</summary>
internal sealed class ThrowingVendorGateway(int throwsBeforeSucceeding) : IVendorGateway
{
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public Task<VendorReservation> ReserveCapacityAsync(
        VendorPartner vendor,
        TranslationJob job,
        CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref _callCount) - 1;
        if (index < throwsBeforeSucceeding)
        {
            throw new HttpRequestExceptionStub();
        }

        return Task.FromResult(VendorReservation.Reserved());
    }
}

internal sealed class HttpRequestExceptionStub() : Exception("simulated network failure");

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

    public async Task<VendorReservation> ReserveCapacityAsync(
        VendorPartner vendor,
        TranslationJob job,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        _entered.TrySetResult();
        await _released.Task;
        return VendorReservation.Reserved();
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
