using System.Text.Json;
using Community_Service.Data;
using Community_Service.Messages.Grpc;
using Grpc.Core;

namespace Community_Service.Services;

public interface IMeetingActivityClient
{
    Task<long?> UpsertAsync(MeetingEntity meeting, ChannelEntity channel, string phase);
}

public sealed class MeetingActivityClient : IMeetingActivityClient
{
    private readonly MessageService.MessageServiceClient _client;
    private readonly string _token;

    public MeetingActivityClient(MessageService.MessageServiceClient client, IConfiguration configuration)
    {
        _client = client;
        _token = configuration["MEETING_SERVICE_TOKEN"]
            ?? throw new InvalidOperationException("MEETING_SERVICE_TOKEN not found");
    }

    public async Task<long?> UpsertAsync(MeetingEntity meeting, ChannelEntity channel, string phase)
    {
        if (channel.ActivityPublishChannelId is not { } activityChannelId)
            return null;

        var payload = JsonSerializer.Serialize(new
        {
            type = "meeting",
            meetingId = meeting.Id.ToString(),
            voiceChannelId = meeting.ChannelId.ToString(),
            recordingId = meeting.RecordingId,
            status = phase,
            actions = phase == "ready" ? new[] { "recap" } : Array.Empty<string>(),
            scheduledAt = meeting.StartAt,
            startedAt = meeting.StartedAt,
            endedAt = meeting.EndedAt
        });
        var content = phase switch
        {
            "scheduled" => $"Встреча «{meeting.Name}» запланирована на {meeting.StartAt:u}.",
            "live" => $"Встреча «{meeting.Name}» началась. Идёт запись.",
            "processing" => $"Встреча «{meeting.Name}» завершена. Recap обрабатывается.",
            "ready" => $"Recap встречи «{meeting.Name}» готов.",
            "failed" => $"Не удалось подготовить recap встречи «{meeting.Name}».",
            "cancelled" => $"Встреча «{meeting.Name}» отменена.",
            _ => $"Статус встречи «{meeting.Name}» обновлён."
        };
        var response = await _client.UpsertSystemMessageAsync(
            new UpsertSystemMessageRequest
            {
                ChatId = activityChannelId,
                CommunityId = meeting.CommunityId,
                MessageId = meeting.ActivityMessageId ?? 0,
                Content = content,
                SystemPayload = payload
            }, new Metadata { { "x-meeting-service-token", _token } });
        return response.Id;
    }
}
