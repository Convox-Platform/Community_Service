using System.Text.Json;
using Community_Service.Data;
using Community_Service.Messages.Grpc;
using Grpc.Core;

namespace Community_Service.Services;

public interface IRecordingActivityClient
{
    Task<long?> UpsertAsync(RecordingActivityEntity activity, ChannelEntity channel, string phase);
}

/// <summary>
/// Системная карточка записи голосового канала. В отличие от митингов, у канала
/// нет отдельного «канала публикации активности»: карточка едет в собственный чат
/// голосового канала (chat_id == id канала), где её и ждут участники разговора.
/// </summary>
public sealed class RecordingActivityClient : IRecordingActivityClient
{
    private readonly MessageService.MessageServiceClient _client;
    private readonly string _token;

    public RecordingActivityClient(MessageService.MessageServiceClient client, IConfiguration configuration)
    {
        _client = client;
        _token = configuration["MEETING_SERVICE_TOKEN"]
            ?? throw new InvalidOperationException("MEETING_SERVICE_TOKEN not found");
    }

    public async Task<long?> UpsertAsync(RecordingActivityEntity activity, ChannelEntity channel, string phase)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = "recording",
            recordingId = activity.RecordingId,
            voiceChannelId = activity.ChannelId.ToString(),
            startedBy = activity.StartedBy == 0 ? null : activity.StartedBy.ToString(),
            status = phase,
            actions = phase == "ready" ? new[] { "recap" } : Array.Empty<string>()
        });
        var content = phase switch
        {
            "live" => $"Идёт запись в голосовом канале «{channel.Name}».",
            "processing" => $"Запись в «{channel.Name}» остановлена, идёт обработка.",
            "ready" => $"Запись в «{channel.Name}» готова.",
            "failed" => $"Не удалось обработать запись в «{channel.Name}».",
            _ => $"Статус записи в «{channel.Name}» обновлён."
        };
        var response = await _client.UpsertSystemMessageAsync(
            new UpsertSystemMessageRequest
            {
                ChatId = activity.ChannelId,
                CommunityId = activity.CommunityId,
                MessageId = activity.ActivityMessageId ?? 0,
                Content = content,
                SystemPayload = payload
            }, new Metadata { { "x-meeting-service-token", _token } });
        return response.Id;
    }
}
