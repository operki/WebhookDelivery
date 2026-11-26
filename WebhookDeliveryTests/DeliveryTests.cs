using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NUnit.Framework;
using WebhookDelivery;

namespace WebhookDeliveryTests;

public sealed class DeliveryTests
{
    private readonly string database = "webhook_test_" + Guid.NewGuid().ToString("N");
    private string adminConnection = "";
    private string connectionString = "";
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient api = null!;
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = null!;
    private DeliveryStore Store => factory.Services.GetRequiredService<DeliveryStore>();
    private static DeliveryOptions Settings => new() { MaxAttempts = 3, RetryDelaySeconds = 0, RequestTimeoutSeconds = 1, LeaseSeconds = 10 };

    [OneTimeSetUp]
    public async Task CreateDatabase()
    {
        adminConnection = Environment.GetEnvironmentVariable("TEST_DATABASE_CONNECTION")
            ?? "Host=localhost;Port=5433;Database=webhooks;Username=webhooks;Password=local-development-only";
        await using var connection = new NpgsqlConnection(adminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {database}", connection);
        await command.ExecuteNonQueryAsync();
        connectionString = new NpgsqlConnectionStringBuilder(adminConnection) { Database = database }.ConnectionString;
    }

    [SetUp]
    public async Task StartApplication()
    {
        send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        factory = CreateFactory();
        api = factory.CreateClient();
        await using var command = factory.Services.GetRequiredService<NpgsqlDataSource>()
            .CreateCommand("TRUNCATE delivery_attempts, events");
        await command.ExecuteNonQueryAsync();
    }

    [TearDown]
    public async Task StopApplication()
    {
        api?.Dispose();
        if (factory != null)
            await factory.DisposeAsync();
    }

    [OneTimeTearDown]
    public async Task DropDatabase()
    {
        if (connectionString.Length == 0)
            return;
        await using var connection = new NpgsqlConnection(adminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task ConcurrentIdempotentRequestsCreateOneEvent()
    {
        var key = Guid.NewGuid().ToString();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            using var response = await PostEvent(key);
            return (response.StatusCode, Event: (await response.Content.ReadFromJsonAsync<AcceptedEvent>())!);
        }));

        Assert.That(results.Count(result => result.StatusCode == HttpStatusCode.Accepted), Is.EqualTo(1));
        Assert.That(results.Select(result => result.Event.Id).Distinct().Count(), Is.EqualTo(1));
        Assert.That((await Store.GetAsync(results[0].Event.Id))!.AttemptCount, Is.Zero);
    }

    [Test]
    public async Task IdempotencyComparesJsonContentRatherThanPropertyOrder()
    {
        var key = Guid.NewGuid().ToString();
        using var first = await PostEvent(key, "{\"type\":\"order.created\",\"payload\":{\"a\":1,\"b\":2}}");
        using var second = await PostEvent(key, "{\"payload\":{\"b\":2,\"a\":1},\"type\":\"order.created\"}");

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await second.Content.ReadFromJsonAsync<AcceptedEvent>(), Is.EqualTo(await first.Content.ReadFromJsonAsync<AcceptedEvent>()));
    }

    [Test]
    public async Task ReusingAKeyForDifferentContentReturnsConflict()
    {
        var key = Guid.NewGuid().ToString();
        using var first = await PostEvent(key);
        using var second = await PostEvent(key, "{\"type\":\"order.created\",\"payload\":{\"id\":99}}");

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [TestCase(null, "{\"type\":\"order.created\",\"payload\":{}}")]
    [TestCase("key", "{\"type\":\"bad type\",\"payload\":{}}")]
    [TestCase("key", "{\"type\":\"order.created\\n\",\"payload\":{}}")]
    [TestCase("key", "{\"type\":\"order.created\",\"payload\":[]}")]
    [TestCase("key", "{\"type\":\"order.created\"}")]
    public async Task InvalidEventsAreRejected(string? key, string body)
    {
        using var response = await PostEvent(key, body);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task TransientFailuresAreRetriedWithAStableEventId()
    {
        var ids = new List<Guid>();
        var attemptHeaders = new List<string>();
        send = async (request, token) =>
        {
            var envelope = (await request.Content!.ReadFromJsonAsync<WebhookEnvelope>(token))!;
            ids.Add(envelope.Id);
            attemptHeaders.Add(request.Headers.GetValues("Webhook-Attempt").Single());
            Assert.That(request.Headers.GetValues("Webhook-Id").Single(), Is.EqualTo(envelope.Id.ToString()));
            return new HttpResponseMessage(ids.Count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        };
        var id = await CreateEvent();

        await Process();
        Assert.That((await Store.GetAsync(id))!.Status, Is.EqualTo("Pending"));
        await Process();
        var item = (await Store.GetAsync(id))!;

        Assert.That(item.Status, Is.EqualTo("Delivered"));
        Assert.That(ids, Is.EqualTo(new[] { id, id }));
        Assert.That(attemptHeaders, Is.EqualTo(new[] { "1", "2" }));
        Assert.That(item.Attempts.Select(attempt => attempt.StatusCode), Is.EqualTo(new[] { 503, 200 }));
        Assert.That(item.Attempts.All(attempt => attempt.CompletedAt.HasValue), Is.True);
    }

    [TestCase(400)]
    [TestCase(401)]
    [TestCase(404)]
    [TestCase(501)]
    [TestCase(302)]
    public async Task PermanentFailuresStopAfterOneAttempt(int code)
    {
        send = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code));
        var id = await CreateEvent();
        await Process();

        var item = (await Store.GetAsync(id))!;
        Assert.That(item.Status, Is.EqualTo("Failed"));
        Assert.That(item.AttemptCount, Is.EqualTo(1));
        Assert.That(await Process(), Is.False);
    }

    [Test]
    public async Task ExhaustedEventsCanBeRetriedWithoutLosingHistory()
    {
        send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var id = await CreateEvent();
        for (var attempt = 0; attempt < 3; attempt++)
            await Process();
        Assert.That((await Store.GetAsync(id))!.Status, Is.EqualTo("Failed"));

        using var response = await api.PostAsync($"/events/{id}/retry", null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        await Process();

        var item = (await Store.GetAsync(id))!;
        Assert.That(item.Status, Is.EqualTo("Delivered"));
        Assert.That(item.AttemptCount, Is.EqualTo(4));
        Assert.That(item.Attempts.Select(attempt => attempt.Number), Is.EqualTo(new[] { 1, 2, 3, 4 }));
    }

    [Test]
    public async Task PendingAndDeliveredEventsCannotBeManuallyRetried()
    {
        var id = await CreateEvent();
        using var pending = await api.PostAsync($"/events/{id}/retry", null);
        Assert.That(pending.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        await Process();
        using var delivered = await api.PostAsync($"/events/{id}/retry", null);
        Assert.That(delivered.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        using var missing = await api.PostAsync($"/events/{Guid.NewGuid()}/retry", null);
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task RetryAfterSchedulesDeliveryWithoutBlockingTheWorker()
    {
        send = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
            return Task.FromResult(response);
        };
        var id = await CreateEvent();
        await Process();

        var item = (await Store.GetAsync(id))!;
        Assert.That(item.Status, Is.EqualTo("Pending"));
        Assert.That(item.NextAttemptAt, Is.GreaterThan(DateTimeOffset.UtcNow.AddSeconds(50)));
        Assert.That(await Process(), Is.False);
    }

    [Test]
    public async Task TransportFailuresAreRecordedAndRetried()
    {
        send = (_, _) => throw new HttpRequestException("Connection reset.");
        var id = await CreateEvent();
        await Process();

        var item = (await Store.GetAsync(id))!;
        Assert.That(item.Status, Is.EqualTo("Pending"));
        Assert.That(item.Attempts.Single().Error, Is.EqualTo("Transport failure."));
    }

    [Test]
    public async Task RequestTimeoutsAreRecordedAndRetried()
    {
        send = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var id = await CreateEvent();
        await Process();

        var item = (await Store.GetAsync(id))!;
        Assert.That(item.Status, Is.EqualTo("Pending"));
        Assert.That(item.Attempts.Single().Error, Is.EqualTo("Request timed out."));
    }

    [Test]
    public async Task ShutdownCancellationLeavesTheLeaseForRecovery()
    {
        using var cancellation = new CancellationTokenSource();
        send = async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var id = await CreateEvent();

        await Assert.ThatAsync(async () => await factory.Services.GetRequiredService<DeliveryProcessor>()
            .ProcessNextAsync(cancellation.Token), Throws.InstanceOf<OperationCanceledException>());

        var item = (await Store.GetAsync(id))!;
        Assert.That(item.Status, Is.EqualTo("Delivering"));
        Assert.That(item.Attempts.Single().CompletedAt, Is.Null);
    }

    [Test]
    public async Task CompetingWorkersClaimEachEventOnce()
    {
        for (var index = 0; index < 8; index++)
            await CreateEvent();
        var jobs = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Store.ClaimAsync(Settings, CancellationToken.None)));

        Assert.That(jobs.Count(job => job != null), Is.EqualTo(8));
        Assert.That(jobs.OfType<DeliveryJob>().Select(job => job.Id).Distinct().Count(), Is.EqualTo(8));
    }

    [Test]
    public async Task ExpiredLeasesRecoverAndStaleWorkersCannotCompleteThem()
    {
        var id = await CreateEvent();
        var first = (await Store.ClaimAsync(Settings, CancellationToken.None))!;
        await ExpireLease(id);
        var second = (await Store.ClaimAsync(Settings, CancellationToken.None))!;
        await Store.CompleteAsync(first, "Delivered", 200, null, TimeSpan.Zero, CancellationToken.None);

        var item = (await Store.GetAsync(id))!;
        Assert.That(second.Number, Is.EqualTo(2));
        Assert.That(item.Status, Is.EqualTo("Delivering"));
        Assert.That(item.Attempts[0].Error, Is.EqualTo("Delivery lease expired."));
        Assert.That(item.Attempts[1].CompletedAt, Is.Null);
    }

    [Test]
    public async Task ExpiryOnTheFinalAttemptFailsTheEvent()
    {
        var id = await CreateEvent();
        var settings = new DeliveryOptions { MaxAttempts = 1, LeaseSeconds = 10 };
        await Store.ClaimAsync(settings, CancellationToken.None);
        await ExpireLease(id);

        Assert.That(await Store.ClaimAsync(settings, CancellationToken.None), Is.Null);
        Assert.That((await Store.GetAsync(id))!.Status, Is.EqualTo("Failed"));
    }

    [Test]
    public async Task EventsSurviveAnApplicationRestart()
    {
        var id = await CreateEvent();
        api.Dispose();
        await factory.DisposeAsync();
        factory = CreateFactory();
        api = factory.CreateClient();

        await Process();

        using var response = await api.GetAsync($"/events/{id}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await response.Content.ReadFromJsonAsync<EventDetails>())!.Status, Is.EqualTo("Delivered"));
    }

    [Test]
    public async Task DeliversOverRealHttpToALocalReceiver()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var receiver = builder.Build();
        var received = new List<WebhookEnvelope>();
        receiver.MapPost("/webhooks", async (HttpRequest request) =>
        {
            received.Add((await request.ReadFromJsonAsync<WebhookEnvelope>())!);
            return Results.StatusCode(received.Count == 1 ? 503 : 200);
        });
        await receiver.StartAsync();
        var created = await Store.CreateAsync(Guid.NewGuid().ToString(), "order.created", "{\"id\":42}",
            receiver.Urls.Single() + "/webhooks", CancellationToken.None);
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var processor = new DeliveryProcessor(Store, httpClient, Options.Create(Settings), NullLogger<DeliveryProcessor>.Instance);

        await processor.ProcessNextAsync(CancellationToken.None);
        await processor.ProcessNextAsync(CancellationToken.None);

        Assert.That(received.Select(item => item.Id), Is.EqualTo(new[] { created.Id, created.Id }));
        Assert.That(received[0].Payload.GetProperty("id").GetInt32(), Is.EqualTo(42));
        Assert.That((await Store.GetAsync(created.Id))!.Status, Is.EqualTo("Delivered"));
    }

    private WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:Postgres", connectionString);
        builder.UseSetting("Delivery:DestinationUrl", "https://receiver.test/webhooks");
        builder.UseSetting("Delivery:WorkerEnabled", "false");
        builder.UseSetting("Delivery:RetryDelaySeconds", "0");
        builder.UseSetting("Delivery:RequestTimeoutSeconds", "1");
        builder.UseSetting("Delivery:LeaseSeconds", "10");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureTestServices(services => services.AddHttpClient<DeliveryProcessor>()
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler((request, token) => send(request, token))));
    });

    private async Task<HttpResponseMessage> PostEvent(string? key,
        string body = "{\"type\":\"order.created\",\"payload\":{\"id\":42}}")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/events")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (key != null)
            request.Headers.Add("Idempotency-Key", key);
        return await api.SendAsync(request);
    }

    private async Task<Guid> CreateEvent()
    {
        using var response = await PostEvent(Guid.NewGuid().ToString());
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        return (await response.Content.ReadFromJsonAsync<AcceptedEvent>())!.Id;
    }

    private Task<bool> Process() => factory.Services.GetRequiredService<DeliveryProcessor>().ProcessNextAsync(CancellationToken.None);

    private async Task ExpireLease(Guid id)
    {
        await using var command = factory.Services.GetRequiredService<NpgsqlDataSource>()
            .CreateCommand("UPDATE events SET lease_expires_at = now() - interval '1 second' WHERE id = @id");
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => callback(request, cancellationToken);
    }
}
