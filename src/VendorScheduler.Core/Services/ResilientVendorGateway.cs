using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VendorScheduler.Core.Contracts;
using VendorScheduler.Core.Domain;

namespace VendorScheduler.Core.Services;

/// <summary>
/// Applies the retry policy around a vendor gateway. Sits between the engine and the gateway so the
/// engine never deals with attempts and the gateway never deals with policy.
/// </summary>
/// <remarks>
/// Waiting and randomness are injected rather than called directly, so tests can assert the exact
/// backoff schedule without ever sleeping.
/// </remarks>
public sealed class ResilientVendorGateway : IVendorGateway
{
    private readonly IVendorGateway _inner;
    private readonly VendorResiliencePolicy _policy;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _jitterSource;
    private readonly ILogger _logger;
    private readonly IAssignmentMetrics _metrics;

    public ResilientVendorGateway(
        IVendorGateway inner,
        VendorResiliencePolicy? policy = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<double>? jitterSource = null,
        ILogger<ResilientVendorGateway>? logger = null,
        IAssignmentMetrics? metrics = null)
    {
        _inner = inner;
        _policy = policy ?? new VendorResiliencePolicy();
        _delay = delay ?? Task.Delay;
        _jitterSource = jitterSource ?? Random.Shared.NextDouble;
        _logger = logger ?? NullLogger<ResilientVendorGateway>.Instance;
        _metrics = metrics ?? NoOpAssignmentMetrics.Instance;
    }

    public async Task<VendorReservation> ReserveCapacityAsync(
        VendorPartner vendor,
        TranslationJob job,
        CancellationToken cancellationToken = default)
    {
        var attempts = Math.Max(1, _policy.MaxAttempts);
        var lastOutcome = VendorReservation.TransientFailure("No attempt was made");

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            lastOutcome = await AttemptAsync(vendor, job, cancellationToken);
            _metrics.VendorAttempt(lastOutcome.Status);

            // Reserved is done; Rejected is the vendor's final answer, so neither is retried.
            if (!lastOutcome.IsRetryable)
            {
                return lastOutcome;
            }

            var isLastAttempt = attempt == attempts - 1;
            if (isLastAttempt)
            {
                _logger.LogError(
                    "Vendor {VendorName} unreachable for job {JobId} after {Attempts} attempt(s): {Status} ({Detail})",
                    vendor.Name, job.JobId, attempts, lastOutcome.Status, lastOutcome.Detail);
                return lastOutcome;
            }

            var backoff = CalculateBackoff(attempt);
            _metrics.VendorRetryScheduled();
            _logger.LogWarning(
                "Vendor {VendorName} attempt {Attempt}/{MaxAttempts} for job {JobId} failed: {Status} ({Detail}). Retrying in {BackoffMs}ms",
                vendor.Name, attempt + 1, attempts, job.JobId, lastOutcome.Status, lastOutcome.Detail, backoff.TotalMilliseconds);

            await _delay(backoff, cancellationToken);
        }

        return lastOutcome;
    }

    private async Task<VendorReservation> AttemptAsync(
        VendorPartner vendor,
        TranslationJob job,
        CancellationToken cancellationToken)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(_policy.AttemptTimeout);

        try
        {
            return await _inner.ReserveCapacityAsync(vendor, job, attemptCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up, not the vendor. Not our failure to classify.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Timed out: the vendor may still have reserved, so this is uncertain, not a clean failure.
            return VendorReservation.Uncertain($"Attempt exceeded {_policy.AttemptTimeout.TotalMilliseconds:0}ms");
        }
        catch (Exception ex)
        {
            return VendorReservation.TransientFailure($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Exponential growth, capped by <see cref="VendorResiliencePolicy.MaxBackoff"/>, plus a spread
    /// proportional to the delay itself.
    /// </summary>
    private TimeSpan CalculateBackoff(int attempt)
    {
        var exponential = _policy.BaseBackoff.TotalMilliseconds * Math.Pow(2, attempt);
        var capped = Math.Min(exponential, _policy.MaxBackoff.TotalMilliseconds);
        var jitter = capped * _policy.JitterFactor * _jitterSource();
        return TimeSpan.FromMilliseconds(capped + jitter);
    }
}
