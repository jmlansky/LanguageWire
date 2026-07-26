namespace VendorScheduler.Core.Domain;

public enum JobUrgency
{
    Normal,
    Urgent
}

/// <summary>
/// Urgency is derived at evaluation time from priority and the remaining margin to the due date —
/// never stored. A job that was "normal" on Monday and is retried the day before its deadline
/// escalates on its own, with no queue or scheduler re-prioritising it.
/// </summary>
public static class UrgencyPolicy
{
    /// <summary>Jobs due within this window are urgent regardless of their declared priority.</summary>
    public static readonly TimeSpan UrgentDueWindow = TimeSpan.FromHours(24);

    public static JobUrgency Evaluate(TranslationJob job, DateTime nowUtc)
    {
        if (job.Priority == JobPriority.High)
        {
            return JobUrgency.Urgent;
        }

        if (job.DueAtUtc - nowUtc <= UrgentDueWindow)
        {
            return JobUrgency.Urgent;
        }

        return JobUrgency.Normal;
    }
}
