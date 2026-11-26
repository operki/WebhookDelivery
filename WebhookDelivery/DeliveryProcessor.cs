using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace WebhookDelivery;

public sealed class DeliveryProcessor(DeliveryStore store, HttpClient httpClient, IOptions<DeliveryOptions> settings,
    ILogger<DeliveryProcessor> logger)
{
    public async Task<bool> ProcessNextAsync(CancellationToken token)
    {
        var options = settings.Value;
        var job = await store.ClaimAsync(options, token);
        if (job == null)
            return false;

        int? code = null;
        string? error = null;
        var retryable = true;
        var delay = TimeSpan.FromSeconds(Math.Min(options.RetryDelaySeconds * Math.Pow(2, job.CycleAttempt - 1), 300));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, job.Destination)
            {
                Content = JsonContent.Create(new WebhookEnvelope(job.Id, job.Type, job.CreatedAt,
                    JsonSerializer.Deserialize<JsonElement>(job.Payload)))
            };
            request.Headers.Add("Webhook-Id", job.Id.ToString());
            request.Headers.Add("Webhook-Attempt", job.Number.ToString(CultureInfo.InvariantCulture));
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            code = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                error = $"HTTP {code}.";
                retryable = code is 408 or 429 or 500 or 502 or 503 or 504;
                var retryAfter = response.Headers.RetryAfter;
                var suggested = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : delay);
                delay = suggested < TimeSpan.Zero ? TimeSpan.Zero : suggested > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : suggested;
            }
        }
        catch (HttpRequestException)
        {
            error = "Transport failure.";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            error = "Request timed out.";
        }

        var status = error == null ? "Delivered" : retryable && job.CycleAttempt < options.MaxAttempts ? "Pending" : "Failed";
        await store.CompleteAsync(job, status, code, error, delay, token);
        logger.LogInformation("Event {EventId}, attempt {Attempt}: {Status}", job.Id, job.Number, status);
        return true;
    }
}

public sealed class DeliveryWorker(IServiceScopeFactory scopes, ILogger<DeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<DeliveryProcessor>().ProcessNextAsync(stoppingToken))
                    continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Delivery worker iteration failed.");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
