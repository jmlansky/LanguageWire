using VendorScheduler.Core.Contracts;
using VendorScheduler.Core.Domain;

namespace VendorScheduler.Infrastructure;

public sealed class FakeVendorGateway : IVendorGateway
{
    private readonly int _maxRetries;
    private readonly TimeSpan _baseBackoff;
    private readonly TimeSpan _maxBackoff;
    private readonly TimeSpan _attemptTimeout;
    private readonly Func<VendorPartner, TranslationJob, CancellationToken, Task<bool>> _reserveCall;

    public FakeVendorGateway(
        int maxRetries = 3,
        TimeSpan? baseBackoff = null,
        TimeSpan? maxBackoff = null,
        TimeSpan? attemptTimeout = null,
        Func<VendorPartner, TranslationJob, CancellationToken, Task<bool>>? reserveCall = null)
    {
        _maxRetries = Math.Max(0, maxRetries);
        _baseBackoff = baseBackoff ?? TimeSpan.FromMilliseconds(100);
        _maxBackoff = maxBackoff ?? TimeSpan.FromMilliseconds(1000);
        _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(2);
        _reserveCall = reserveCall ?? ReserveOnceDefaultAsync;
    }

    public Task<bool> ReserveCapacityAsync(VendorPartner vendor, TranslationJob job, CancellationToken cancellationToken = default)
    {
        return ReserveWithRetryAsync(vendor, job, cancellationToken);
    }

    private async Task<bool> ReserveWithRetryAsync(VendorPartner vendor, TranslationJob job, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(_attemptTimeout);
                return await _reserveCall(vendor, job, attemptCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == _maxRetries)
                {
                    return false;
                }
            }
            catch
            {
                if (attempt == _maxRetries)
                {
                    return false;
                }
            }

            var delay = CalculateDelay(attempt);
            await Task.Delay(delay, cancellationToken);
        }

        return false;
    }

    private TimeSpan CalculateDelay(int attempt)
    {
        var exponent = Math.Min(attempt, 6);
        var next = _baseBackoff.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(next, _maxBackoff.TotalMilliseconds);
        var jitter = Random.Shared.Next(0, 25);
        return TimeSpan.FromMilliseconds(capped + jitter);
    }

    private static Task<bool> ReserveOnceDefaultAsync(VendorPartner vendor, TranslationJob job, CancellationToken cancellationToken)
    {
        if (vendor.CurrentLoad >= vendor.MaxCapacity)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }
}
