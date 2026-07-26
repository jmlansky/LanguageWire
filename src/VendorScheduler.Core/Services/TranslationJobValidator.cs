using VendorScheduler.Core.Domain;

namespace VendorScheduler.Core.Services;

public sealed record TranslationJobValidation(IReadOnlyCollection<string> Errors, TranslationJob? Job)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Turns an untrusted request into a <see cref="TranslationJob"/> or into the full list of reasons it
/// cannot become one. Lives in Core because these are the job's invariants, not transport concerns;
/// the API layer only decides which status code to return.
/// </summary>
public static class TranslationJobValidator
{
    public static TranslationJobValidation Validate(
        Guid jobId,
        string? sourceLanguage,
        string? targetLanguage,
        string? priority,
        DateTime dueAt,
        DateTime nowUtc)
    {
        var errors = new List<string>();

        if (jobId == Guid.Empty)
        {
            errors.Add("jobId is required.");
        }

        var source = sourceLanguage?.Trim() ?? string.Empty;
        var target = targetLanguage?.Trim() ?? string.Empty;

        if (source.Length == 0)
        {
            errors.Add("sourceLanguage is required.");
        }

        if (target.Length == 0)
        {
            errors.Add("targetLanguage is required.");
        }

        if (source.Length > 0 && target.Length > 0 && string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("sourceLanguage and targetLanguage must differ.");
        }

        if (!TryParsePriority(priority, out var parsedPriority))
        {
            errors.Add($"priority '{priority}' is not valid. Expected one of: {string.Join(", ", Enum.GetNames<JobPriority>())}.");
        }

        var dueAtUtc = ToUtc(dueAt);
        if (dueAtUtc <= nowUtc)
        {
            errors.Add("dueAtUtc must be in the future.");
        }

        // Every problem is reported at once, so a caller fixes the request in one round trip.
        if (errors.Count > 0)
        {
            return new TranslationJobValidation(errors, null);
        }

        return new TranslationJobValidation([], new TranslationJob
        {
            JobId = jobId,
            SourceLanguage = source,
            TargetLanguage = target,
            Priority = parsedPriority,
            DueAtUtc = dueAtUtc
        });
    }

    /// <summary>
    /// Rejects digits explicitly: <c>Enum.TryParse</c> happily turns "1" into <c>Normal</c>, which
    /// would let a malformed request silently pick a priority nobody asked for.
    /// </summary>
    private static bool TryParsePriority(string? value, out JobPriority priority)
    {
        priority = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Any(char.IsDigit))
        {
            return false;
        }

        return Enum.TryParse(value.Trim(), ignoreCase: true, out priority) && Enum.IsDefined(priority);
    }

    /// <summary>
    /// The field is named dueAtUtc, so a timestamp that arrives without a zone is taken at its word
    /// rather than reinterpreted through the server's local time.
    /// </summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
