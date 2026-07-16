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

        public MeetingGrpcService(
            MeetingRepository meetings,
            ChannelRepository channels,
            IPermissionGuard guard)
        {
            _meetings = meetings;
            _channels = channels;
            _guard = guard;
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

            await EnsureChannelAsync((long)request.ChannelId, communityId);

            var meeting = await _meetings.CreateAsync(
                communityId, (long)request.ChannelId, request.Name, request.Description,
                request.StartAt.ToDateTime(), userId);

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

        private async Task EnsureChannelAsync(long channelId, long communityId)
        {
            var channel = await _channels.GetByIdAsync(channelId);
            if (channel is null || channel.CommunityId != communityId)
                throw new RpcException(new Status(StatusCode.NotFound, "Channel not found"));
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
