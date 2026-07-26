namespace VendorScheduler.Core.Domain;

public sealed class TranslationJob
{
    public required Guid JobId { get; init; }
    public required string SourceLanguage { get; init; }
    public required string TargetLanguage { get; init; }
    public required JobPriority Priority { get; init; }
    public required DateTime DueAtUtc { get; init; }
}
