using Dapper;
using Community_Service.Events.Contracts;
using Google.Protobuf;
using Npgsql;

namespace Community_Service.Events;

public sealed class OutboxWriter
{
    public async Task EnqueueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        var envelope = message.Envelope;
        var eventId = Guid.Parse(envelope.EventId);
        var correlationId = Guid.Parse(envelope.CorrelationId);

        await connection.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO outbox_events
                (event_id, correlation_id, event_type, payload, occurred_at)
              VALUES (@eventId, @correlationId, @eventType, @payload, @occurredAt);",
            new
            {
                eventId,
                correlationId,
                eventType = EventEnvelope.Descriptor.FullName,
                payload = envelope.ToByteArray(),
                occurredAt = envelope.OccurredAt.ToDateTime()
            }, transaction, cancellationToken: cancellationToken));

        foreach (var routingKey in message.RoutingKeys.Distinct(StringComparer.Ordinal))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO outbox_destinations (event_id, routing_key)
                  VALUES (@eventId, @routingKey);",
                new { eventId, routingKey }, transaction, cancellationToken: cancellationToken));
        }
    }
}
