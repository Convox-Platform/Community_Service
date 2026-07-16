using RabbitMQ.Client;

namespace Community_Service.Events;

public sealed class RabbitMqOutboxPublisher : BackgroundService, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly OutboxStore _store;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqOutboxPublisher> _logger;
    private IConnection? _connection;
    private IChannel? _channel;
    private DateTime _nextCleanupUtc;

    public RabbitMqOutboxPublisher(
        OutboxStore store,
        RabbitMqOptions options,
        ILogger<RabbitMqOutboxPublisher> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
        _nextCleanupUtc = DateTime.UtcNow;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deliveries = await _store.ClaimAsync(100, Lease, stoppingToken);
                if (deliveries.Count == 0)
                {
                    await CleanupIfDueAsync(stoppingToken);
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }

                await EnsureChannelAsync(stoppingToken);
                foreach (var delivery in deliveries)
                    await PublishAsync(delivery, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "RabbitMQ outbox loop failed; retrying");
                await DisposeRabbitMqAsync();
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task PublishAsync(OutboxDelivery delivery, CancellationToken cancellationToken)
    {
        try
        {
            var properties = new BasicProperties
            {
                ContentType = "application/x-protobuf",
                Type = delivery.EventType,
                MessageId = delivery.EventId.ToString("D"),
                CorrelationId = delivery.CorrelationId.ToString("D"),
                Persistent = true,
                Timestamp = new AmqpTimestamp(new DateTimeOffset(delivery.OccurredAt).ToUnixTimeSeconds())
            };

            await _channel!.BasicPublishAsync(
                _options.ExchangeName,
                delivery.RoutingKey,
                mandatory: false,
                properties,
                delivery.Payload,
                cancellationToken);

            await _store.MarkPublishedAsync(delivery.Id, cancellationToken);
            _logger.LogDebug("Published outbox event {EventId} to {RoutingKey}",
                delivery.EventId, delivery.RoutingKey);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "Failed to publish outbox event {EventId} to {RoutingKey} on attempt {Attempt}",
                delivery.EventId, delivery.RoutingKey, delivery.AttemptCount);
            await _store.MarkFailedAsync(delivery.Id, delivery.AttemptCount, exception, cancellationToken);
            await DisposeRabbitMqAsync();
        }
    }

    private async Task EnsureChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
            return;

        await DisposeRabbitMqAsync();
        var factory = new ConnectionFactory
        {
            Uri = _options.AmqpUrl,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };
        _connection = await factory.CreateConnectionAsync(cancellationToken);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        await _channel.ExchangeDeclareAsync(
            _options.ExchangeName,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);
        _logger.LogInformation("Connected RabbitMQ outbox publisher to exchange {Exchange}",
            _options.ExchangeName);
    }

    private async Task CleanupIfDueAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow < _nextCleanupUtc)
            return;

        await _store.CleanupAsync(Retention, cancellationToken);
        _nextCleanupUtc = DateTime.UtcNow.AddHours(1);
    }

    private async Task DisposeRabbitMqAsync()
    {
        if (_channel is not null)
        {
            try { await _channel.DisposeAsync(); } catch { }
            _channel = null;
        }
        if (_connection is not null)
        {
            try { await _connection.DisposeAsync(); } catch { }
            _connection = null;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await DisposeRabbitMqAsync();
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await DisposeRabbitMqAsync();
        GC.SuppressFinalize(this);
    }
}
