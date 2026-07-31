using System.Text.Json;
using Community_Service.Data;
using Community_Service.Services;
using Grpc.Core;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Community_Service.Events;

public sealed class RecordingStatusConsumer : BackgroundService, IAsyncDisposable
{
    private const string Exchange = "recording.events";
    private const string Queue = "community-service.recording-status";
    private const short VoiceChannelType = 2;

    // recording-service шлёт camelCase (recordingId), а System.Text.Json по умолчанию
    // сопоставляет имена с учётом регистра — без этого ни одно событие не разбиралось.
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

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
        // Ключи в формате websocket-gateway: recording.community.<serverId>.<status>.
        // Тот же exchange слушает и gateway, но по своему биндингу на подписки клиента.
        foreach (var suffix in new[] { "started", "assembling", "ready", "failed" })
        {
            await _channel.QueueBindAsync(Queue, Exchange, $"recording.community.*.{suffix}",
                cancellationToken: stoppingToken);
        }

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                var status = JsonSerializer.Deserialize<RecordingStatus>(delivery.Body.Span, PayloadOptions);
                if (status is null || string.IsNullOrWhiteSpace(status.RecordingId))
                    throw new InvalidOperationException("Recording status is missing recordingId");
                await HandleAsync(status, stoppingToken);
                await _channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
            }
            catch (Exception exception)
            {
                // Повторная доставка помогает только при временном сбое. Отказ, который сам
                // не исправится (битый payload, нереализованный RPC), иначе крутится в
                // бесконечном requeue-цикле и забивает брокер.
                var permanent = exception is JsonException or InvalidOperationException
                    || (exception is RpcException rpc && rpc.StatusCode is StatusCode.Unimplemented
                        or StatusCode.InvalidArgument
                        or StatusCode.PermissionDenied
                        or StatusCode.NotFound);
                _logger.LogError(exception,
                    "Failed to process recording status event (permanent: {Permanent})", permanent);
                await _channel.BasicNackAsync(delivery.DeliveryTag, multiple: false,
                    requeue: !permanent, stoppingToken);
            }
        };
        await _channel.BasicConsumeAsync(Queue, autoAck: false, consumer, stoppingToken);
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleAsync(RecordingStatus status, CancellationToken cancellationToken)
    {
        // Промежуточные статусы конвейера (transcribing, summarizing) карточку не
        // двигают: для читателя чата это всё ещё «идёт обработка».
        var phase = PhaseOf(status.Status);
        if (phase is null)
            return;

        using var scope = _services.CreateScope();
        var meetings = scope.ServiceProvider.GetRequiredService<MeetingRepository>();
        var channels = scope.ServiceProvider.GetRequiredService<ChannelRepository>();
        var meeting = await meetings.GetByRecordingIdAsync(status.RecordingId);

        if (meeting is not null)
        {
            // Карточку митинга на старте уже поставил MeetingGrpcService.StartMeeting —
            // повторный upsert до сохранения activity_message_id создал бы вторую.
            if (phase == "live")
                return;
            var meetingChannel = await channels.GetByIdAsync(meeting.ChannelId);
            if (meetingChannel is null)
                return;
            var meetingActivity = scope.ServiceProvider.GetRequiredService<IMeetingActivityClient>();
            var meetingMessageId = await meetingActivity.UpsertAsync(meeting, meetingChannel, phase);
            if (meetingMessageId is { } messageId)
                await meetings.SetActivityMessageIdAsync(meeting.Id, messageId);
            return;
        }

        await HandleChannelRecordingAsync(scope.ServiceProvider, channels, status, phase);
    }

    // Запись, начатая прямо в голосовом канале: своей сущности у неё нет, поэтому
    // карточка живёт в channel_recording_activity и публикуется в чат самого канала.
    private static async Task HandleChannelRecordingAsync(
        IServiceProvider services, ChannelRepository channels, RecordingStatus status, string phase)
    {
        if (!long.TryParse(status.ChannelId, out var channelId) ||
            !long.TryParse(status.ServerId, out var communityId))
            throw new InvalidOperationException("Recording status is missing channelId or serverId");

        var channel = await channels.GetByIdAsync(channelId);
        if (channel is null || channel.Type != VoiceChannelType)
            return;

        _ = long.TryParse(status.StartedBy, out var startedBy);
        var activities = services.GetRequiredService<RecordingActivityRepository>();
        var activity = await activities.AdvanceAsync(
            status.RecordingId, communityId, channelId, startedBy, phase);
        if (activity is null)
            return;

        var client = services.GetRequiredService<IRecordingActivityClient>();
        var messageId = await client.UpsertAsync(activity, channel, phase);
        if (messageId is { } id)
            await activities.MarkPublishedAsync(status.RecordingId, phase, id);
    }

    // Статус записи -> фаза карточки. Фазы общие с митингами, чтобы клиент читал
    // одно и то же поле status в system_payload.
    private static string? PhaseOf(string status) => status.ToLowerInvariant() switch
    {
        "collecting" => "live",
        "assembling" => "processing",
        "ready" => "ready",
        "failed" => "failed",
        _ => null
    };

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }

    private sealed class RecordingStatus
    {
        public string RecordingId { get; init; } = "";
        public string Status { get; init; } = "";
        // Снежинки едут строками — теми же полями пользуется и браузер.
        public string ServerId { get; init; } = "";
        public string ChannelId { get; init; } = "";
        public string StartedBy { get; init; } = "";
    }
}
