using Community_Service.Voice.Grpc;
using Grpc.Core;

namespace Community_Service.Services;

public interface IMeetingRecordingClient
{
    Task<string> StartAsync(long meetingId, long communityId, long channelId, ulong actorUserId);
    Task StopAsync(long communityId, long channelId, string recordingId);
}

public sealed class MeetingRecordingClient : IMeetingRecordingClient
{
    private readonly MeetingRecordingService.MeetingRecordingServiceClient _client;
    private readonly string _token;

    public MeetingRecordingClient(
        MeetingRecordingService.MeetingRecordingServiceClient client,
        IConfiguration configuration)
    {
        _client = client;
        _token = configuration["MEETING_SERVICE_TOKEN"]
            ?? throw new InvalidOperationException("MEETING_SERVICE_TOKEN not found");
    }

    public async Task<string> StartAsync(long meetingId, long communityId, long channelId, ulong actorUserId)
    {
        var response = await _client.StartMeetingRecordingAsync(
            new StartMeetingRecordingRequest
            {
                CommunityId = (ulong)communityId,
                ChannelId = (ulong)channelId,
                StartedBy = actorUserId,
                IdempotencyKey = $"meeting:{meetingId}"
            }, Headers());
        if (string.IsNullOrWhiteSpace(response.RecordingId))
            throw new RpcException(new Status(StatusCode.Unavailable, "Voice service returned an empty recording ID"));
        return response.RecordingId;
    }

    public async Task StopAsync(long communityId, long channelId, string recordingId)
    {
        await _client.StopMeetingRecordingAsync(
            new StopMeetingRecordingRequest
            {
                CommunityId = (ulong)communityId,
                ChannelId = (ulong)channelId,
                RecordingId = recordingId
            }, Headers());
    }

    private Metadata Headers() => new() { { "x-meeting-service-token", _token } };
}
