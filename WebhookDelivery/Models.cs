using System.Text.Json;

namespace WebhookDelivery;

public sealed record CreateEvent(string Type, JsonElement Payload);
public sealed record AcceptedEvent(Guid Id, string StatusUrl);
public sealed record WebhookEnvelope(Guid Id, string Type, DateTimeOffset OccurredAt, JsonElement Payload);
public sealed record DeliveryAttempt(int Number, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, int? StatusCode, string? Error);
public sealed record EventDetails(Guid Id, string Type, JsonElement Payload, string Status, int AttemptCount,
    DateTimeOffset CreatedAt, DateTimeOffset NextAttemptAt, IReadOnlyList<DeliveryAttempt> Attempts);
public sealed record DeliveryJob(Guid Id, string Type, string Payload, string Destination, DateTimeOffset CreatedAt,
    int Number, int CycleAttempt, Guid LeaseId);

public sealed class DeliveryOptions
{
    public string DestinationUrl { get; init; } = "";
    public int MaxAttempts { get; init; } = 3;
    public int RetryDelaySeconds { get; init; } = 2;
    public int RequestTimeoutSeconds { get; init; } = 10;
    public int LeaseSeconds { get; init; } = 30;
    public bool WorkerEnabled { get; init; } = true;

    public bool IsValid() => Uri.TryCreate(DestinationUrl, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo)
        && MaxAttempts is >= 1 and <= 10 && RetryDelaySeconds is >= 0 and <= 300
        && RequestTimeoutSeconds is >= 1 and <= 60 && LeaseSeconds > RequestTimeoutSeconds + 5 && LeaseSeconds <= 300;
}
