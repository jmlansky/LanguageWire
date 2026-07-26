using VendorScheduler.Core.Contracts;
using VendorScheduler.Core.Domain;
using VendorScheduler.Core.Services;
using VendorScheduler.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
builder.Services.AddSingleton<IVendorGateway, FakeVendorGateway>();
builder.Services.AddSingleton<IAssignmentEventPublisher, ConsoleAssignmentEventPublisher>();
builder.Services.AddScoped<IAssignmentEngine, AssignmentEngine>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapPost("/api/assignments", async (
    AssignmentRequest request,
    IAssignmentEngine assignmentEngine,
    CancellationToken cancellationToken) =>
{
    var job = new TranslationJob
    {
        JobId = request.JobId,
        SourceLanguage = request.SourceLanguage,
        TargetLanguage = request.TargetLanguage,
        Priority = request.Priority,
        DueAtUtc = request.DueAtUtc
    };

    var vendors = new List<VendorPartner>
    {
        new()
        {
            VendorId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "VendorA",
            SupportedPairs = new[] { "en->de", "en->fr" },
            MaxCapacity = 100,
            CurrentLoad = 35,
            CostScore = 0.75m,
            QualityScore = 0.90m
        },
        new()
        {
            VendorId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Name = "VendorB",
            SupportedPairs = new[] { "en->de", "de->en" },
            MaxCapacity = 120,
            CurrentLoad = 50,
            CostScore = 0.70m,
            QualityScore = 0.85m
        }
    };

    var result = await assignmentEngine.AssignAsync(job, vendors, cancellationToken);
    return Results.Ok(result);
});

app.Run();

public sealed record AssignmentRequest(
    Guid JobId,
    string SourceLanguage,
    string TargetLanguage,
    string Priority,
    DateTime DueAtUtc);
