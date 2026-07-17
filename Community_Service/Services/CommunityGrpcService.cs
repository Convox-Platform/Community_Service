using Community_Service.Auth;
using Community_Service.Data;
using Community_Service.Invites;
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
        private readonly InviteRepository _invites;
        private readonly IPermissionGuard _guard;

        public CommunityGrpcService(
            CommunityRepository communities,
            MemberRepository members,
            CategoryRepository categories,
            ChannelRepository channels,
            InviteRepository invites,
            IPermissionGuard guard)
        {
            _communities = communities;
            _members = members;
            _categories = categories;
            _channels = channels;
            _invites = invites;
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

        public override async Task<CreateInviteResponse> CreateInvite(
            CreateInviteRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            if (request.HasMaxUses && (request.MaxUses == 0 || request.MaxUses > int.MaxValue))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "max_uses must be between 1 and 2147483647"));

            await _guard.EnsureCanCreateInviteAsync(userId, communityId);
            var invite = await _invites.CreateAsync(
                communityId, userId, request.HasMaxUses ? (int)request.MaxUses : null);
            return new CreateInviteResponse { Invite = invite.ToProto() };
        }

        public override async Task<ListInvitesResponse> ListInvites(
            ListInvitesRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureCommunityAdminAsync(userId, communityId);

            var response = new ListInvitesResponse();
            response.Invites.AddRange((await _invites.ListAsync(communityId)).Select(i => i.ToProto()));
            return response;
        }

        public override async Task<DeleteInviteResponse> DeleteInvite(
            DeleteInviteRequest request, ServerCallContext context)
        {
            if (!InviteCodeGenerator.IsValid(request.Code))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid invite code"));

            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureCommunityAdminAsync(userId, communityId);
            if (!await _invites.DeleteAsync(communityId, request.Code))
                throw new RpcException(new Status(StatusCode.NotFound, "Invite not found"));
            return new DeleteInviteResponse();
        }

        public override async Task<AcceptInviteResponse> AcceptInvite(
            AcceptInviteRequest request, ServerCallContext context)
        {
            if (!InviteCodeGenerator.IsValid(request.Code))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid invite code"));

            var result = await _invites.AcceptAsync(request.Code, context.GetUserId());
            return result.Status switch
            {
                InviteAcceptanceStatus.Accepted or InviteAcceptanceStatus.AlreadyMember =>
                    new AcceptInviteResponse { Community = result.Community!.ToProto() },
                InviteAcceptanceStatus.NotFound =>
                    throw new RpcException(new Status(StatusCode.NotFound, "Invite not found")),
                InviteAcceptanceStatus.LimitReached =>
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "Invite usage limit reached")),
                _ => throw new InvalidOperationException("Unknown invite acceptance status")
            };
        }

        public override async Task<ListInviteAttributionsResponse> ListInviteAttributions(
            ListInviteAttributionsRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureCommunityAdminAsync(userId, communityId);

            var response = new ListInviteAttributionsResponse();
            response.Attributions.AddRange(
                (await _invites.ListAttributionsAsync(communityId)).Select(a => a.ToProto()));
            return response;
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
