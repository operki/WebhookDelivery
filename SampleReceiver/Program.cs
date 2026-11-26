using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var attempts = new ConcurrentDictionary<Guid, int>();
var delivered = new ConcurrentDictionary<Guid, byte>();
var failures = builder.Configuration.GetValue("FailuresBeforeSuccess", 1);

app.MapPost("/webhooks", (HttpRequest request) =>
{
    if (!Guid.TryParse(request.Headers["Webhook-Id"], out var id))
        return Results.BadRequest();
    var number = attempts.AddOrUpdate(id, 1, (_, count) => count + 1);
    if (number <= failures)
        return Results.StatusCode(503);
    delivered.TryAdd(id, 0);
    return Results.Ok(new { id });
});
app.MapGet("/received", () => attempts.Select(pair => new
{
    id = pair.Key, attempts = pair.Value, delivered = delivered.ContainsKey(pair.Key)
}));

app.Run();
