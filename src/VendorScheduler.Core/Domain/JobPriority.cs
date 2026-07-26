namespace VendorScheduler.Core.Domain;

/// <summary>
/// Closed set of job priorities. Typed rather than the starter's free-form string, so an invalid
/// value is rejected at the API boundary instead of silently flowing through the engine.
/// </summary>
public enum JobPriority
{
    Low,
    Normal,
    High
}
