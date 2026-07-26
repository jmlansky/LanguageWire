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

// One concrete directory instance plays two roles: the roster the engine reads, and the simulated
// vendor side whose capacity the fake gateway consumes. In the real system that second role belongs
// to a separate vendor management service.
builder.Services.AddSingleton<InMemoryVendorDirectory>();
builder.Services.AddSingleton<IVendorDirectory>(sp => sp.GetRequiredService<InMemoryVendorDirectory>());

builder.Services.AddSingleton<IAssignmentEventPublisher, ConsoleAssignmentEventPublisher>();

builder.Services.AddSingleton<InMemoryAssignmentMetrics>();
builder.Services.AddSingleton<IAssignmentMetrics>(sp => sp.GetRequiredService<InMemoryAssignmentMetrics>());

// The engine resolves IVendorGateway and gets the resilient decorator, unaware that retries exist.
// Swapping FakeVendorGateway for a real vendor client changes only the inner instance.
builder.Services.AddSingleton<IVendorGateway>(sp =>
    new ResilientVendorGateway(
        new FakeVendorGateway(sp.GetRequiredService<InMemoryVendorDirectory>()),
        new VendorResiliencePolicy(),
        logger: sp.GetRequiredService<ILogger<ResilientVendorGateway>>(),
        metrics: sp.GetRequiredService<IAssignmentMetrics>()));

builder.Services.AddScoped<IAssignmentEngine, AssignmentEngine>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Correlation id: honour the caller's if it sent one, mint one otherwise, put it on every log line
// of the request through a logging scope, and echo it back so the caller can quote it in a ticket.
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId))
    {
        correlationId = Guid.NewGuid().ToString("N");
    }

    context.Response.Headers[CorrelationIdHeader] = correlationId;

    var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
    var logger = loggerFactory.CreateLogger("VendorScheduler.Api.Request");

    using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
    {
        await next();
    }
});

app.MapPost("/api/assignments", async (
    AssignmentRequest request,
    IAssignmentEngine assignmentEngine,
    IVendorDirectory vendorDirectory,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    // The request is untrusted until it becomes a TranslationJob; the engine never sees a raw one.
    var validation = TranslationJobValidator.Validate(
        request.JobId,
        request.SourceLanguage,
        request.TargetLanguage,
        request.Priority,
        request.DueAtUtc,
        DateTime.UtcNow);

    if (validation.Job is not { } job)
    {
        return Results.BadRequest(new ValidationErrorResponse(request.JobId, validation.Errors));
    }

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
.Produces<ValidationErrorResponse>(StatusCodes.Status400BadRequest)
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

// Test support: in the real system vendor state is owned by a separate vendor management service.
// This endpoint stands in for it so failover and capacity-exhaustion scenarios can be staged from
// Swagger. It would not ship in production.
app.MapPut("/api/testing/vendors/{vendorId:guid}", (
    Guid vendorId,
    VendorStateRequest request,
    InMemoryVendorDirectory vendorDirectory) =>
{
    if (!vendorDirectory.TrySetState(vendorId, request.CurrentLoad, request.MaxCapacity))
    {
        return Results.NotFound(new NotFoundResponse(vendorId, "Unknown vendor"));
    }

    return Results.NoContent();
})
.WithName("SetVendorState")
.WithTags("Testing")
.WithDescription("Test support only: overwrites a vendor's load/capacity to stage assignment scenarios.")
.Produces(StatusCodes.Status204NoContent)
.Produces<NotFoundResponse>(StatusCodes.Status404NotFound);

app.MapGet("/metrics", (InMemoryAssignmentMetrics metrics) => Results.Ok(metrics.Snapshot()))
.WithName("GetMetrics")
.WithTags("Operations")
.WithDescription("Process-local counters. In production these would be OpenTelemetry instruments scraped by the monitoring stack; this snapshot exists to make them observable here.")
.Produces<MetricsSnapshot>(StatusCodes.Status200OK);

app.MapGet("/health", () => Results.Ok(new HealthResponse("healthy")))
.WithName("GetHealth")
.WithTags("Operations")
.Produces<HealthResponse>(StatusCodes.Status200OK);

app.Run();

/// <summary>
/// Maps an assignment outcome to an HTTP status so callers can act on the response without parsing
/// the body: 409 means "retry later", 422 means "this request can never be fulfilled as-is", and
/// 503 means "capacity or availability problem, retrying is worthwhile".
/// </summary>
static IResult ToHttpResult(AssignmentResult result) => result.Outcome switch
{
    AssignmentOutcome.Assigned => Results.Ok(result),
    AssignmentOutcome.AlreadyInProgress => Results.Json(result, statusCode: StatusCodes.Status409Conflict),
    AssignmentOutcome.NoMatchingVendor => Results.Json(result, statusCode: StatusCodes.Status422UnprocessableEntity),
    AssignmentOutcome.NoCapacityAvailable => Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable),
    AssignmentOutcome.VendorUnavailable => Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable),
    _ => Results.Json(result, statusCode: StatusCodes.Status500InternalServerError)
};

public partial class Program
{
    public const string CorrelationIdHeader = "X-Correlation-Id";
}

public sealed record AssignmentRequest(
    Guid JobId,
    string SourceLanguage,
    string TargetLanguage,
    string Priority,
    DateTime DueAtUtc);

public sealed record VendorStateRequest(int CurrentLoad, int MaxCapacity);

public sealed record ValidationErrorResponse(Guid JobId, IReadOnlyCollection<string> Errors);

public sealed record NotFoundResponse(Guid Id, string Reason);

public sealed record HealthResponse(string Status);
