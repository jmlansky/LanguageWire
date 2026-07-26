namespace VendorScheduler.Core.Domain;

public sealed class VendorPartner
{
    public required Guid VendorId { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyCollection<string> SupportedPairs { get; init; }
    public required int MaxCapacity { get; init; }
    public required int CurrentLoad { get; init; }
    public required decimal CostScore { get; init; }
    public required decimal QualityScore { get; init; }

    public bool CanHandle(string source, string target)
    {
        var pair = $"{source}->{target}";
        return SupportedPairs.Contains(pair, StringComparer.OrdinalIgnoreCase);
    }
}
