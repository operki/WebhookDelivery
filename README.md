# WebhookDelivery

[![CI](https://github.com/operki/WebhookDelivery/actions/workflows/ci.yml/badge.svg)](https://github.com/operki/WebhookDelivery/actions/workflows/ci.yml)

A small .NET 10 service that accepts events, stores them in PostgreSQL, and delivers them to a configured webhook receiver. Includes idempotent ingestion, durable retries, delivery history, lease recovery, and a sample receiver.

**Status:** `0.1.0-alpha.1`. This is a runnable backend example with a deliberately small API and one runtime package dependency, Npgsql.

## Run the demo

Install Docker with Compose, then run:

```sh
docker compose up --build -d
```

The API is at `http://localhost:8080`, and the sample receiver is at `http://localhost:8081`. The receiver returns 503 on the first request for each event, then succeeds. PostgreSQL data lives in a named Docker volume. Published ports bind to loopback only.

Create an event using curl (use `curl.exe` in Windows PowerShell):

```sh
curl -i http://localhost:8080/events \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: order-42-created' \
  -d '{"type":"order.created","payload":{"orderId":42,"total":19.90}}'
```

The response is `202 Accepted`, with an event ID, a status URL, and a `Location` header. Repeating the same request returns `200 OK` with the same ID. Reusing its key for different content returns `409 Conflict`.

```sh
curl http://localhost:8080/events/EVENT_ID
curl http://localhost:8081/received
```

After the retry, the event is `Delivered` with two recorded attempts (503 and 200). To demonstrate final failure, temporarily stop the receiver, submit a new event with a new key, and wait for the three attempts. Start the receiver again and retry the failed event:

```sh
docker compose stop receiver
# Submit a new event and wait for status Failed.
docker compose start receiver
curl -X POST http://localhost:8080/events/EVENT_ID/retry
```

Use `docker compose down` to stop the demo while retaining data. `docker compose down -v` also removes the demo database.

## API

| Method | Path | Behavior |
| --- | --- | --- |
| POST | `/events` | Accept `{ "type": "order.created", "payload": { ... } }` with an `Idempotency-Key` header. Returns 202 for a new event, 200 for a matching replay, or 409 for conflicting content. |
| GET | `/events/{id}` | Return event content, state, attempt count, next attempt time, and ordered delivery history; 404 if absent. |
| POST | `/events/{id}/retry` | Requeue a failed event with a fresh attempt budget. Preserve its ID and complete history. Returns 202, 409 for any other state, or 404 if absent. |
| GET | `/health` | Check the PostgreSQL connection. |

Keys contain 1–128 printable characters and are global to this service. Event types contain 1–100 ASCII letters, digits, dots, underscores, or hyphens. The payload must be a JSON object. The HTTP request-body limit is 64 KiB. The database compares JSON content, so object property order does not affect idempotency. Keys are retained with their events; there is no expiry or deletion endpoint.

The destination is configured by the operator, rather than accepted from public requests. Each event stores the destination at ingestion, so changing configuration affects only new events.

## Delivery behavior

The receiver gets a JSON envelope:

```json
{
  "id": "e763ed14-f885-48d0-90c5-dac826e5e6db",
  "type": "order.created",
  "occurredAt": "2026-01-01T12:00:00+00:00",
  "payload": { "orderId": 42 }
}
```

`occurredAt` is the time the service accepted the event. `Webhook-Id` repeats the stable event ID in a header; `Webhook-Attempt` is the lifetime attempt number. A receiver should deduplicate effects by event ID.

- Any 2xx response completes delivery.
- Transport failures, request timeouts, and HTTP 408, 429, 500, 502, 503, and 504 are retried within the attempt budget.
- Other responses, including redirects, fail immediately. The HTTP client does not follow redirects.
- Retry delays double from the configured base, capped at five minutes. A valid `Retry-After` delta or HTTP date overrides the delay, capped at one day. Delays are stored in PostgreSQL; a worker can process other events while waiting.
- Exhausting the budget sets `Failed`. Manual retry resets the per-cycle budget but preserves lifetime numbering and history.
- Responses are acknowledged at their headers; response bodies are neither consumed nor stored.
- Shutting down during delivery leaves a lease for recovery. Request timeout is shorter than the lease, so normal timeouts finish before lease expiry.

## Persistence and concurrency

The event row is also the durable queue entry. There is no separate broker and no gap between saving the event and scheduling delivery. A PostgreSQL unique constraint enforces idempotency, including concurrent requests.

Workers claim due events using a short transaction and `FOR UPDATE SKIP LOCKED`. Claiming also records the attempt before sending HTTP. The transaction ends before the network call. Completion updates are conditional on the lease ID, preventing an old worker from overwriting a recovered attempt.

Expired leases are recovered by the next claim operation. Interrupted attempts are marked in the history. If the final attempt was interrupted, the event fails instead of receiving an unbounded recovery budget. Multiple API instances can run workers against the same database. Each instance sends one delivery at a time and polls once per second when idle.

Delivery is **at least once**. A receiver may apply an effect before the sender crashes or loses its response, so a later attempt can repeat it. Lease expiry during a long process pause can also allow overlapping requests. Exactly-once effects require receiver-side deduplication. There is no ordering guarantee between different events.

The initial schema is embedded in the application and created idempotently on startup. Schema evolution and rolling migrations are outside this alpha.

## Configuration

Use standard .NET configuration or environment variables:

| Variable | Default | Meaning |
| --- | --- | --- |
| `ConnectionStrings__Postgres` | Required | PostgreSQL connection string. |
| `Delivery__DestinationUrl` | Required | Absolute HTTP(S) receiver URL, without user information. |
| `Delivery__MaxAttempts` | `3` | Attempts per cycle, including the first; range 1–10. |
| `Delivery__RetryDelaySeconds` | `2` | Initial delay; range 0–300. |
| `Delivery__RequestTimeoutSeconds` | `10` | HTTP request timeout; range 1–60. |
| `Delivery__LeaseSeconds` | `30` | Lease duration, at least six seconds longer than the request timeout; maximum 300. |
| `Delivery__WorkerEnabled` | `true` | Disable for an ingestion-only instance. |

Compose includes a local-development database password. Supply your own configuration outside the demo. The sample receiver keeps its receipt list in memory and is not a durable consumer.

## Build and test

Install the .NET 10 SDK and start the test database:

```sh
docker compose up -d postgres
dotnet restore WebhookDelivery.slnx
dotnet build WebhookDelivery.slnx --configuration Release --no-restore
dotnet test WebhookDelivery.slnx --configuration Release --no-build
```

Tests use real PostgreSQL, create a uniquely named database, and remove only that database afterward. The default connection matches Compose on port 5433. Override it with `TEST_DATABASE_CONNECTION`; its database user needs `CREATEDB` permission. Database access is required; integration tests are not silently skipped.

Coverage includes concurrent idempotency, conflicting content, transient and permanent failures, retry exhaustion, manual retry history, scheduling, timeouts, competing claims, expired leases, stale completion, application restart, and delivery over real HTTP to a local test receiver. Other HTTP cases use an in-memory handler. CI runs the tests against PostgreSQL and builds both container images.

## Scope

This alpha has no authentication, webhook signatures, tenant isolation, retention jobs, UI, or broker. It is intended for local demonstration and a trusted network. Add authentication and access controls before exposing ingestion or retry endpoints to other callers. Business operations in the producer's own database are not atomically committed with this service; producers still need an outbox if that atomicity is required.

## License

MIT. See [LICENSE](LICENSE).
