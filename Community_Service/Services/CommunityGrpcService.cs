using Community_Service.Auth;
using Community_Service.Data;
using Community_Service.Permissions;
using Grpc.Core;

namespace Community_Service.Services
{
    public class CommunityGrpcService : CommunityService.CommunityServiceBase
    {
        private readonly CommunityRepository _communities;
        private readonly MemberRepository _members;
        private readonly CategoryRepository _categories;
        private readonly ChannelRepository _channels;
        private readonly IPermissionGuard _guard;

        public CommunityGrpcService(
            CommunityRepository communities,
            MemberRepository members,
            CategoryRepository categories,
            ChannelRepository channels,
            IPermissionGuard guard)
        {
            _communities = communities;
            _members = members;
            _categories = categories;
            _channels = channels;
            _guard = guard;
        }

        public override async Task<ListCommunitiesResponse> ListCommunities(
            ListCommunitiesRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communities = await _communities.ListByUserAsync(userId);

            var response = new ListCommunitiesResponse();
            response.Communities.AddRange(communities.Select(c => c.ToProto()));
            return response;
        }

        public override Task<JoinCommunityResponse> JoinCommunity(
            JoinCommunityRequest request, ServerCallContext context)
        {
            // Вступление по коду-приглашению появится вместе с сущностью инвайтов.
            throw new RpcException(new Status(StatusCode.Unimplemented, "Invites are not implemented yet"));
        }

        public override async Task<LeaveCommunityResponse> LeaveCommunity(
            LeaveCommunityRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;

            var ownerId = await _communities.GetOwnerIdAsync(communityId)
                ?? throw new RpcException(new Status(StatusCode.NotFound, "Community not found"));

            // Владелец не может просто выйти — иначе комьюнити останется без владельца.
            if (ownerId == userId)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Owner cannot leave the community"));

            if (!await _members.RemoveAsync(communityId, userId))
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Not a member of the community"));

            return new LeaveCommunityResponse();
        }

        public override async Task<CreateCommunityResponse> CreateCommunity(
            CreateCommunityRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();

            if (string.IsNullOrWhiteSpace(request.Name))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Name is required"));

            // template_id пока игнорируется: всегда создаётся дефолтная структура.
            var avatar = request.HasAvatar ? request.Avatar : null;

            var community = await _communities.CreateAsync(
                request.Name, avatar, string.Empty, userId, request.MemberIds);

            return new CreateCommunityResponse { Community = community.ToProto() };
        }

        public override async Task<ListChannelsResponse> ListChannels(
            ListChannelsRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;

            await _guard.EnsureMemberAsync(userId, communityId);

            var categories = await _categories.ListByCommunityAsync(communityId);
            var channels = await _channels.ListByCommunityAsync(communityId);

            var response = new ListChannelsResponse();
            response.Categories.AddRange(categories.Select(c => c.ToProto()));
            response.Channels.AddRange(channels.Select(c => c.ToProto()));
            return response;
        }
    }
}
