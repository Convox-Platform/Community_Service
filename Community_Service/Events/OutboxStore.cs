using Dapper;
using Npgsql;

namespace Community_Service.Events;

public sealed class OutboxStore
{
    private readonly NpgsqlDataSource _db;

    public OutboxStore(NpgsqlDataSource db) => _db = db;

    public async Task<IReadOnlyList<OutboxDelivery>> ClaimAsync(
        int batchSize, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var deliveries = (await connection.QueryAsync<OutboxDelivery>(new CommandDefinition(
            @"WITH candidates AS (
                  SELECT id
                  FROM outbox_destinations
                  WHERE published_at IS NULL
                    AND next_attempt_at <= now()
                    AND (lease_until IS NULL OR lease_until < now())
                  ORDER BY id
                  FOR UPDATE SKIP LOCKED
                  LIMIT @batchSize
              ), claimed AS (
                  UPDATE outbox_destinations d
                  SET lease_until = now() + @lease,
                      attempt_count = attempt_count + 1
                  FROM candidates c
                  WHERE d.id = c.id
                  RETURNING d.id, d.event_id, d.routing_key, d.attempt_count
              )
              SELECT c.id, c.event_id, c.routing_key, c.attempt_count,
                     e.correlation_id, e.event_type, e.payload, e.occurred_at
              FROM claimed c
              JOIN outbox_events e ON e.event_id = c.event_id
              ORDER BY c.id;",
            new { batchSize, lease }, transaction, cancellationToken: cancellationToken))).AsList();

        await transaction.CommitAsync(cancellationToken);
        return deliveries;
    }

    public async Task MarkPublishedAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            @"UPDATE outbox_destinations
              SET published_at = now(), lease_until = NULL, last_error = NULL
              WHERE id = @id;",
            new { id }, cancellationToken: cancellationToken));
    }

    public async Task MarkFailedAsync(
        long id, int attemptCount, Exception exception, CancellationToken cancellationToken)
    {
        var exponent = Math.Min(Math.Max(attemptCount - 1, 0), 6);
        var delaySeconds = Math.Min(60, 1 << exponent) + Random.Shared.NextDouble();
        var error = exception.ToString();
        if (error.Length > 4000)
            error = error[..4000];

        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            @"UPDATE outbox_destinations
              SET lease_until = NULL,
                  next_attempt_at = now() + @delay,
                  last_error = @error
              WHERE id = @id;",
            new { id, delay = TimeSpan.FromSeconds(delaySeconds), error },
            cancellationToken: cancellationToken));
    }

    public async Task CleanupAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            @"DELETE FROM outbox_events e
              WHERE e.occurred_at < now() - @retention
                AND NOT EXISTS (
                    SELECT 1 FROM outbox_destinations d
                    WHERE d.event_id = e.event_id AND d.published_at IS NULL
                );",
            new { retention }, cancellationToken: cancellationToken));
    }
}

public sealed class OutboxDelivery
{
    public long Id { get; init; }
    public Guid EventId { get; init; }
    public Guid CorrelationId { get; init; }
    public string RoutingKey { get; init; } = "";
    public string EventType { get; init; } = "";
    public byte[] Payload { get; init; } = [];
    public DateTime OccurredAt { get; init; }
    public int AttemptCount { get; init; }
}
