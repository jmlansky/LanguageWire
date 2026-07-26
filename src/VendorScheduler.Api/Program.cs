using System.Text.Json.Serialization;
using VendorScheduler.Core.Contracts;
using VendorScheduler.Core.Domain;
using VendorScheduler.Core.Services;
using VendorScheduler.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Outcomes travel as names ("NoMatchingVendor"), not ordinals, so the contract stays readable and
// does not silently change meaning if the enum is ever reordered.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
builder.Services.AddSingleton<IVendorGateway, FakeVendorGateway>();
builder.Services.AddSingleton<IVendorDirectory, InMemoryVendorDirectory>();
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
    IVendorDirectory vendorDirectory,
    HttpContext httpContext,
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

    var vendors = await vendorDirectory.GetVendorsAsync(cancellationToken);
    var result = await assignmentEngine.AssignAsync(job, vendors, cancellationToken);

    if (result.IsReplay)
    {
        httpContext.Response.Headers.Append("Idempotent-Replay", "true");
    }

    return ToHttpResult(result);
})
.WithName("CreateAssignment")
.WithTags("Assignments")
.Produces<AssignmentResult>(StatusCodes.Status200OK)
.Produces<AssignmentResult>(StatusCodes.Status409Conflict)
.Produces<AssignmentResult>(StatusCodes.Status422UnprocessableEntity)
.Produces<AssignmentResult>(StatusCodes.Status503ServiceUnavailable);

app.MapGet("/api/assignments/{jobId:guid}", async (
    Guid jobId,
    IIdempotencyStore idempotencyStore,
    CancellationToken cancellationToken) =>
{
    var stored = await idempotencyStore.TryGetCompletedAsync(AssignmentKeys.For(jobId), cancellationToken);
    if (stored is null)
    {
        return Results.NotFound(new NotFoundResponse(jobId, "No completed assignment recorded for this job"));
    }

    return Results.Ok(stored);
})
.WithName("GetAssignment")
.WithTags("Assignments")
.Produces<AssignmentResult>(StatusCodes.Status200OK)
.Produces<NotFoundResponse>(StatusCodes.Status404NotFound);

app.MapGet("/api/vendors", async (
    IVendorDirectory vendorDirectory,
    CancellationToken cancellationToken) => Results.Ok(await vendorDirectory.GetVendorsAsync(cancellationToken)))
.WithName("GetVendors")
.WithTags("Vendors")
.Produces<IReadOnlyCollection<VendorPartner>>(StatusCodes.Status200OK);

app.MapGet("/health", () => Results.Ok(new HealthResponse("healthy")))
.WithName("GetHealth")
.WithTags("Operations")
.Produces<HealthResponse>(StatusCodes.Status200OK);

app.Run();

/// <summary>
/// Maps an assignment outcome to an HTTP status so callers can act on the response without parsing
/// the body: 409 means "retry later", 422 means "this request can never be fulfilled as-is", and
/// 503 means "the vendor side failed, retrying is worthwhile".
/// </summary>
static IResult ToHttpResult(AssignmentResult result) => result.Outcome switch
{
    AssignmentOutcome.Assigned => Results.Ok(result),
    AssignmentOutcome.AlreadyInProgress => Results.Json(result, statusCode: StatusCodes.Status409Conflict),
    AssignmentOutcome.NoMatchingVendor => Results.Json(result, statusCode: StatusCodes.Status422UnprocessableEntity),
    AssignmentOutcome.VendorReservationFailed => Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable),
    _ => Results.Json(result, statusCode: StatusCodes.Status500InternalServerError)
};

public sealed record AssignmentRequest(
    Guid JobId,
    string SourceLanguage,
    string TargetLanguage,
    string Priority,
    DateTime DueAtUtc);

public sealed record NotFoundResponse(Guid JobId, string Reason);

public sealed record HealthResponse(string Status);
