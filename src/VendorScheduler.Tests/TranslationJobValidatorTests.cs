using VendorScheduler.Core.Domain;
using VendorScheduler.Core.Services;

namespace VendorScheduler.Tests;

public sealed class TranslationJobValidatorTests
{
    private static readonly DateTime Now = new(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid JobId = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");

    private static TranslationJobValidation Validate(
        Guid? jobId = null,
        string? source = "en",
        string? target = "de",
        string? priority = "Normal",
        DateTime? dueAt = null)
        => TranslationJobValidator.Validate(
            jobId ?? JobId,
            source,
            target,
            priority,
            dueAt ?? Now.AddDays(7),
            Now);

    [Fact]
    public void Validate_WellFormedRequest_ProducesATypedJob()
    {
        var validation = Validate();

        Assert.True(validation.IsValid);
        Assert.Empty(validation.Errors);

        var job = Assert.IsType<TranslationJob>(validation.Job);
        Assert.Equal(JobId, job.JobId);
        Assert.Equal(JobPriority.Normal, job.Priority);
    }

    [Theory]
    [InlineData("low", JobPriority.Low)]
    [InlineData("NORMAL", JobPriority.Normal)]
    [InlineData("High", JobPriority.High)]
    public void Validate_AcceptsPriorityInAnyCasing(string priority, JobPriority expected)
    {
        var validation = Validate(priority: priority);

        Assert.True(validation.IsValid);
        Assert.Equal(expected, validation.Job!.Priority);
    }

    [Theory]
    [InlineData("urgent")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void Validate_UnknownPriority_IsRejected(string? priority)
    {
        var validation = Validate(priority: priority);

        Assert.False(validation.IsValid);
        Assert.Null(validation.Job);
        Assert.Contains(validation.Errors, e => e.Contains("priority"));
    }

    [Fact]
    public void Validate_NumericPriority_IsRejectedInsteadOfSilentlyMappingToAnOrdinal()
    {
        // Enum.TryParse would turn "1" into Normal, picking a priority nobody asked for.
        var validation = Validate(priority: "1");

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("priority"));
    }

    [Fact]
    public void Validate_EmptyJobId_IsRejected()
    {
        var validation = Validate(jobId: Guid.Empty);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("jobId"));
    }

    [Theory]
    [InlineData(null, "de")]
    [InlineData("", "de")]
    [InlineData("   ", "de")]
    [InlineData("en", null)]
    [InlineData("en", "")]
    public void Validate_MissingLanguage_IsRejected(string? source, string? target)
    {
        var validation = Validate(source: source, target: target);

        Assert.False(validation.IsValid);
        Assert.Null(validation.Job);
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData(" en ", "en")]
    public void Validate_SameSourceAndTarget_IsRejected(string source, string target)
    {
        // Nothing to translate, and no vendor would ever match the pair.
        var validation = Validate(source: source, target: target);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("must differ"));
    }

    [Fact]
    public void Validate_TrimsLanguagesBeforeBuildingTheJob()
    {
        var validation = Validate(source: "  en  ", target: " de ");

        Assert.True(validation.IsValid);
        Assert.Equal("en", validation.Job!.SourceLanguage);
        Assert.Equal("de", validation.Job.TargetLanguage);
    }

    [Fact]
    public void Validate_DueDateInThePast_IsRejected()
    {
        var validation = Validate(dueAt: Now.AddHours(-1));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("dueAtUtc"));
    }

    [Fact]
    public void Validate_DueDateWithoutAZone_IsTakenAsUtc()
    {
        // The field is named dueAtUtc; reinterpreting it through server local time would shift the
        // deadline, and with it the urgency the engine derives.
        var unspecified = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Unspecified);

        var validation = Validate(dueAt: unspecified);

        Assert.True(validation.IsValid);
        Assert.Equal(DateTimeKind.Utc, validation.Job!.DueAtUtc.Kind);
        Assert.Equal(unspecified.Ticks, validation.Job.DueAtUtc.Ticks);
    }

    [Fact]
    public void Validate_ReportsEveryProblemAtOnce()
    {
        var validation = TranslationJobValidator.Validate(
            Guid.Empty,
            sourceLanguage: "",
            targetLanguage: "",
            priority: "nope",
            dueAt: Now.AddDays(-1),
            nowUtc: Now);

        // One round trip, not five.
        Assert.True(validation.Errors.Count >= 4, $"expected several errors, got: {string.Join(" | ", validation.Errors)}");
    }
}
