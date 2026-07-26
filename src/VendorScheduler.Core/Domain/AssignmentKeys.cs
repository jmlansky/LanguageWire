namespace VendorScheduler.Core.Domain;

/// <summary>
/// Single source of truth for the idempotency key format, so the engine that writes a key and any
/// caller that looks one up cannot drift apart.
/// </summary>
public static class AssignmentKeys
{
    public static string For(Guid jobId) => $"assign:{jobId}";
}
