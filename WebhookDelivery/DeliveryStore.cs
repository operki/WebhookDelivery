using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace WebhookDelivery;

public sealed class DeliveryStore(NpgsqlDataSource dataSource)
{
    public async Task InitializeAsync(CancellationToken token = default)
    {
        using var stream = typeof(DeliveryStore).Assembly.GetManifestResourceStream("WebhookDelivery.Schema.sql")!;
        using var reader = new StreamReader(stream);
        await using var command = dataSource.CreateCommand(await reader.ReadToEndAsync(token));
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<(Guid Id, bool Created, bool Matches)> CreateAsync(
        string key, string type, string payload, string destination, CancellationToken token)
    {
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO events (id, idempotency_key, type, payload, destination)
            VALUES (@id, @key, @type, @payload, @destination)
            ON CONFLICT (idempotency_key) DO NOTHING RETURNING id;
            """;
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
        command.Parameters.AddWithValue("destination", destination);
        if (await command.ExecuteScalarAsync(token) is Guid id)
            return (id, true, true);

        command.CommandText = "SELECT id, type = @type AND payload = @payload FROM events WHERE idempotency_key = @key";
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        return (reader.GetGuid(0), false, reader.GetBoolean(1));
    }

    public async Task<EventDetails?> GetAsync(Guid id, CancellationToken token = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, payload::text, status, attempt_count, created_at, next_attempt_at FROM events WHERE id = @id;
            SELECT number, started_at, completed_at, status_code, error FROM delivery_attempts WHERE event_id = @id ORDER BY number;
            """;
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            return null;
        var type = reader.GetString(0);
        var payload = JsonSerializer.Deserialize<JsonElement>(reader.GetString(1));
        var status = reader.GetString(2);
        var count = reader.GetInt32(3);
        var created = reader.GetFieldValue<DateTime>(4);
        var next = reader.GetFieldValue<DateTime>(5);
        var attempts = new List<DeliveryAttempt>();
        await reader.NextResultAsync(token);
        while (await reader.ReadAsync(token))
        {
            attempts.Add(new DeliveryAttempt(reader.GetInt32(0), reader.GetFieldValue<DateTime>(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTime>(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return new EventDetails(id, type, payload, status, count, created, next, attempts);
    }

    public async Task<bool> RetryAsync(Guid id, CancellationToken token)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE events SET status = 'Pending', cycle_attempt_count = 0, next_attempt_at = now()
            WHERE id = @id AND status = 'Failed';
            """);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }

    public async Task<DeliveryJob?> ClaimAsync(DeliveryOptions options, CancellationToken token)
    {
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH expired AS MATERIALIZED (
                SELECT id FROM events WHERE status = 'Delivering' AND lease_expires_at <= now()
                FOR UPDATE SKIP LOCKED
            ), interrupted AS (
                UPDATE delivery_attempts a SET completed_at = now(), error = 'Delivery lease expired.'
                FROM expired WHERE a.event_id = expired.id AND a.completed_at IS NULL
            )
            UPDATE events e SET status = CASE WHEN cycle_attempt_count >= @max THEN 'Failed' ELSE 'Pending' END,
                lease_id = NULL, lease_expires_at = NULL
            FROM expired WHERE e.id = expired.id;
            """;
        command.Parameters.AddWithValue("max", options.MaxAttempts);
        await command.ExecuteNonQueryAsync(token);

        var leaseId = Guid.NewGuid();
        command.Parameters.Clear();
        command.CommandText = """
            WITH candidate AS (
                SELECT id FROM events WHERE status = 'Pending' AND next_attempt_at <= now()
                ORDER BY next_attempt_at, created_at FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE events e SET status = 'Delivering', attempt_count = attempt_count + 1,
                cycle_attempt_count = cycle_attempt_count + 1, lease_id = @lease, lease_expires_at = now() + @duration
            FROM candidate WHERE e.id = candidate.id
            RETURNING e.id, e.type, e.payload::text, e.destination, e.created_at, e.attempt_count, e.cycle_attempt_count;
            """;
        command.Parameters.AddWithValue("lease", leaseId);
        command.Parameters.AddWithValue("duration", NpgsqlDbType.Interval, TimeSpan.FromSeconds(options.LeaseSeconds));
        DeliveryJob? job;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            job = await reader.ReadAsync(token)
                ? new DeliveryJob(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetFieldValue<DateTime>(4), reader.GetInt32(5), reader.GetInt32(6), leaseId)
                : null;
        }
        if (job != null)
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO delivery_attempts (id, event_id, number) VALUES (@lease, @event, @number)";
            command.Parameters.AddWithValue("lease", leaseId);
            command.Parameters.AddWithValue("event", job.Id);
            command.Parameters.AddWithValue("number", job.Number);
            await command.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
        return job;
    }

    public async Task CompleteAsync(DeliveryJob job, string status, int? statusCode, string? error,
        TimeSpan delay, CancellationToken token)
    {
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE events SET status = @status, next_attempt_at = now() + @delay, lease_id = NULL, lease_expires_at = NULL
            WHERE id = @event AND lease_id = @lease;
            """;
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("delay", NpgsqlDbType.Interval, delay);
        command.Parameters.AddWithValue("event", job.Id);
        command.Parameters.AddWithValue("lease", job.LeaseId);
        if (await command.ExecuteNonQueryAsync(token) == 1)
        {
            command.CommandText = """
                UPDATE delivery_attempts SET completed_at = now(), status_code = @code, error = @error
                WHERE id = @lease AND completed_at IS NULL;
                """;
            command.Parameters.AddWithValue("code", NpgsqlDbType.Integer, (object?)statusCode ?? DBNull.Value);
            command.Parameters.AddWithValue("error", NpgsqlDbType.Text, (object?)error ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }
}
