using Community_Service.Auth;
using Community_Service.Data;
using Community_Service.Permissions;
using Grpc.Core;

namespace Community_Service.Services
{
    public class MeetingGrpcService : MeetingService.MeetingServiceBase
    {
        private readonly MeetingRepository _meetings;
        private readonly ChannelRepository _channels;
        private readonly IPermissionGuard _guard;
        private readonly IMeetingRecordingClient _recordings;
        private readonly IMeetingActivityClient _activity;
        private readonly ILogger<MeetingGrpcService> _logger;

        public MeetingGrpcService(
            MeetingRepository meetings,
            ChannelRepository channels,
            IPermissionGuard guard,
            IMeetingRecordingClient recordings,
            IMeetingActivityClient activity,
            ILogger<MeetingGrpcService> logger)
        {
            _meetings = meetings;
            _channels = channels;
            _guard = guard;
            _recordings = recordings;
            _activity = activity;
            _logger = logger;
        }

        public override async Task<CreateMeetingResponse> CreateMeeting(
            CreateMeetingRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureCanManageMeetingsAsync(userId, communityId);

            if (string.IsNullOrWhiteSpace(request.Name))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Name is required"));
            if (request.StartAt is null)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "start_at is required"));

            var channel = await EnsureVoiceChannelAsync((long)request.ChannelId, communityId);

            var meeting = await _meetings.CreateAsync(
                communityId, (long)request.ChannelId, request.Name, request.Description,
                request.StartAt.ToDateTime(), userId);
            await PublishActivityAsync(meeting, channel, "scheduled");

            return new CreateMeetingResponse { Meeting = meeting.ToProto() };
        }

        public override async Task<EditMeetingResponse> EditMeeting(
            EditMeetingRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureCanManageMeetingsAsync(userId, communityId);

            await LoadMeetingAsync((long)request.MeetingId, communityId);

            var changedFields = new List<string>();
            if (request.HasName) changedFields.Add("name");
            if (request.HasDescription) changedFields.Add("description");
            if (request.StartAt is not null) changedFields.Add("start_at");

            var meeting = await _meetings.UpdateAsync(
                (long)request.MeetingId,
                request.HasName ? request.Name : null,
                request.HasDescription ? request.Description : null,
                request.StartAt?.ToDateTime(),
                userId,
                changedFields);

            return new EditMeetingResponse { Meeting = meeting.ToProto() };
        }

        public override async Task<DeleteMeetingResponse> DeleteMeeting(
            DeleteMeetingRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureCanManageMeetingsAsync(userId, communityId);

            var meeting = await LoadMeetingAsync((long)request.MeetingId, communityId);
            await _meetings.DeleteAsync(meeting.Id, userId);
            return new DeleteMeetingResponse();
        }

        public override async Task<StartMeetingResponse> StartMeeting(
            StartMeetingRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureCanManageMeetingsAsync(userId, communityId);

            var meeting = await LoadMeetingAsync((long)request.MeetingId, communityId);
            if (meeting.Status != MeetingRepository.Scheduled)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Meeting is not scheduled"));

            string recordingId;
            try
            {
                recordingId = await _recordings.StartAsync(
                    meeting.Id, meeting.CommunityId, meeting.ChannelId, userId, meeting.Name);
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new RpcException(new Status(StatusCode.Unavailable,
                    $"Unable to start meeting recording: {exception.Message}"));
            }

            var started = await _meetings.StartAsync(meeting.Id, recordingId, userId);
            await PublishActivityAsync(started, await EnsureVoiceChannelAsync(started.ChannelId, communityId), "live");
            return new StartMeetingResponse { Meeting = started.ToProto() };
        }

        public override async Task<EndMeetingResponse> EndMeeting(
            EndMeetingRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureCanManageMeetingsAsync(userId, communityId);

            var meeting = await LoadMeetingAsync((long)request.MeetingId, communityId);
            if (meeting.Status == MeetingRepository.Ended)
                return new EndMeetingResponse { Meeting = meeting.ToProto() };
            if (meeting.Status != MeetingRepository.Live || string.IsNullOrWhiteSpace(meeting.RecordingId))
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Meeting is not live"));

            try
            {
                await _recordings.StopAsync(meeting.CommunityId, meeting.ChannelId, meeting.RecordingId);
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new RpcException(new Status(StatusCode.Unavailable,
                    $"Unable to stop meeting recording: {exception.Message}"));
            }

            var ended = await _meetings.EndAsync(meeting.Id, userId);
            await PublishActivityAsync(ended, await EnsureVoiceChannelAsync(ended.ChannelId, communityId), "processing");
            return new EndMeetingResponse { Meeting = ended.ToProto() };
        }

        public override async Task<CancelMeetingResponse> CancelMeeting(
            CancelMeetingRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureCanManageMeetingsAsync(userId, communityId);

            var meeting = await LoadMeetingAsync((long)request.MeetingId, communityId);
            if (meeting.Status != MeetingRepository.Scheduled)
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    "Only scheduled meetings can be cancelled"));
            var cancelled = await _meetings.CancelAsync(meeting.Id, userId);
            await PublishActivityAsync(cancelled, await EnsureVoiceChannelAsync(cancelled.ChannelId, communityId), "cancelled");
            return new CancelMeetingResponse { Meeting = cancelled.ToProto() };
        }

        public override async Task<ListMeetingsResponse> ListMeetings(
            ListMeetingsRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureMemberAsync(userId, communityId);

            var meetings = await _meetings.ListByCommunityAsync(communityId);

            var response = new ListMeetingsResponse();
            response.Meetings.AddRange(meetings.Select(m => m.ToProto()));
            return response;
        }

        private async Task<ChannelEntity> EnsureVoiceChannelAsync(long channelId, long communityId)
        {
            var channel = await _channels.GetByIdAsync(channelId);
            if (channel is null || channel.CommunityId != communityId)
                throw new RpcException(new Status(StatusCode.NotFound, "Channel not found"));
            if (channel.Type != (short)ChannelType.Voice)
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    "Meetings can only be created in voice channels"));
            return channel;
        }

        // Карточка активности — уведомление, а не часть транзакции митинга: состояние
        // уже сохранено, и падение message-service не должно отменять сам переход.
        private async Task PublishActivityAsync(MeetingEntity meeting, ChannelEntity voiceChannel, string phase)
        {
            try
            {
                var messageId = await _activity.UpsertAsync(meeting, voiceChannel, phase);
                if (messageId is not { } id)
                    return;
                meeting.ActivityMessageId = id;
                await _meetings.SetActivityMessageIdAsync(meeting.Id, id);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception,
                    "Failed to publish meeting activity for meeting {MeetingId} in phase {Phase}",
                    meeting.Id, phase);
            }
        }

        private async Task<MeetingEntity> LoadMeetingAsync(long meetingId, long communityId)
        {
            var meeting = await _meetings.GetByIdAsync(meetingId);
            if (meeting is null || meeting.CommunityId != communityId)
                throw new RpcException(new Status(StatusCode.NotFound, "Meeting not found"));
            return meeting;
        }
    }
}
