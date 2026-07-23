using System.Text.Json;
using Community_Service.Data;
using Community_Service.Services;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Community_Service.Events;

public sealed class RecordingStatusConsumer : BackgroundService, IAsyncDisposable
{
    private const string Exchange = "recording.events";
    private const string Queue = "community-service.recording-status";
    private readonly IServiceProvider _services;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RecordingStatusConsumer> _logger;
    private IConnection? _connection;
    private IChannel? _channel;

    public RecordingStatusConsumer(IServiceProvider services, RabbitMqOptions options,
        ILogger<RecordingStatusConsumer> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory { Uri = _options.AmqpUrl };
        _connection = await factory.CreateConnectionAsync(stoppingToken);
        _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await _channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true,
            autoDelete: false, arguments: null, cancellationToken: stoppingToken);
        await _channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: null, cancellationToken: stoppingToken);
        await _channel.QueueBindAsync(Queue, Exchange, "recording.ready", cancellationToken: stoppingToken);
        await _channel.QueueBindAsync(Queue, Exchange, "recording.failed", cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                var status = JsonSerializer.Deserialize<RecordingStatus>(delivery.Body.Span);
                if (status is null || string.IsNullOrWhiteSpace(status.RecordingId))
                    throw new InvalidOperationException("Recording status is missing recordingId");
                await HandleAsync(status, stoppingToken);
                await _channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to process recording status event");
                await _channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, stoppingToken);
            }
        };
        await _channel.BasicConsumeAsync(Queue, autoAck: false, consumer, stoppingToken);
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleAsync(RecordingStatus status, CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var meetings = scope.ServiceProvider.GetRequiredService<MeetingRepository>();
        var channels = scope.ServiceProvider.GetRequiredService<ChannelRepository>();
        var activity = scope.ServiceProvider.GetRequiredService<IMeetingActivityClient>();
        var meeting = await meetings.GetByRecordingIdAsync(status.RecordingId);
        if (meeting is null)
            return;
        var channel = await channels.GetByIdAsync(meeting.ChannelId);
        if (channel is null)
            return;
        var messageId = await activity.UpsertAsync(meeting, channel,
            string.Equals(status.Status, "ready", StringComparison.OrdinalIgnoreCase) ? "ready" : "failed");
        if (messageId is { } id)
            await meetings.SetActivityMessageIdAsync(meeting.Id, id);
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }

    private sealed class RecordingStatus
    {
        public string RecordingId { get; init; } = "";
        public string Status { get; init; } = "";
    }
}
