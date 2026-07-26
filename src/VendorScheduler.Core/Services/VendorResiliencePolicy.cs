namespace VendorScheduler.Core.Services;

public sealed class VendorResiliencePolicy
{
    /// <summary>Total attempts, not extra retries: 1 means "call once, never retry".</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Budget for a single attempt. Exceeding it is treated as an uncertain outcome.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Ceiling for the backoff, so exponential growth stays bounded.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Fraction of the delay used as random spread, so clients that failed together do not retry in
    /// lockstep. Proportional rather than a fixed number of milliseconds, which would be negligible
    /// once the backoff grows.
    /// </summary>
    public double JitterFactor { get; init; } = 0.2;
}
