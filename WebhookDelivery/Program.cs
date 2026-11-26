using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Npgsql;
using WebhookDelivery;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 65_536);
builder.Services.AddProblemDetails();
builder.Services.AddOptions<DeliveryOptions>().BindConfiguration("Delivery")
    .Validate(options => options.IsValid(), "Invalid delivery settings. See README for allowed values.").ValidateOnStart();
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.")));
builder.Services.AddSingleton<DeliveryStore>();
builder.Services.AddHttpClient<DeliveryProcessor>((services, client) =>
    client.Timeout = TimeSpan.FromSeconds(services.GetRequiredService<IOptions<DeliveryOptions>>().Value.RequestTimeoutSeconds))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
if (builder.Configuration.GetValue("Delivery:WorkerEnabled", true))
    builder.Services.AddHostedService<DeliveryWorker>();

var app = builder.Build();
app.UseExceptionHandler();
await app.Services.GetRequiredService<DeliveryStore>().InitializeAsync();

app.MapGet("/health", async (NpgsqlDataSource dataSource, CancellationToken token) =>
{
    await using var command = dataSource.CreateCommand("SELECT 1");
    await command.ExecuteScalarAsync(token);
    return Results.Ok(new { status = "Healthy" });
});

app.MapPost("/events", async (CreateEvent input, HttpRequest request, DeliveryStore store,
    IOptions<DeliveryOptions> options, CancellationToken token) =>
{
    var key = request.Headers["Idempotency-Key"].ToString();
    if (key.Length is < 1 or > 128 || key.Any(char.IsControl) || string.IsNullOrWhiteSpace(key))
        return Results.Problem("Idempotency-Key must contain 1 to 128 printable characters.", statusCode: 400);
    if (input.Type == null || !Regex.IsMatch(input.Type, @"\A[A-Za-z0-9._-]{1,100}\z") || input.Payload.ValueKind != JsonValueKind.Object)
        return Results.Problem("Type must contain 1 to 100 letters, digits, dots, underscores or hyphens; payload must be a JSON object.", statusCode: 400);

    var result = await store.CreateAsync(key, input.Type, input.Payload.GetRawText(), options.Value.DestinationUrl, token);
    if (!result.Matches)
        return Results.Problem("This idempotency key was already used for a different event.", statusCode: 409);
    var accepted = new AcceptedEvent(result.Id, $"/events/{result.Id}");
    return result.Created ? Results.Accepted(accepted.StatusUrl, accepted) : Results.Ok(accepted);
});

app.MapGet("/events/{id:guid}", async (Guid id, DeliveryStore store, CancellationToken token) =>
    await store.GetAsync(id, token) is { } item ? Results.Ok(item) : Results.NotFound());

app.MapPost("/events/{id:guid}/retry", async (Guid id, DeliveryStore store, CancellationToken token) =>
{
    if (await store.RetryAsync(id, token))
        return Results.Accepted($"/events/{id}", new AcceptedEvent(id, $"/events/{id}"));
    return await store.GetAsync(id, token) == null ? Results.NotFound()
        : Results.Problem("Only failed events can be retried.", statusCode: 409);
});

app.Run();

public partial class Program;
