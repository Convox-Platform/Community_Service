using Community_Service.Auth;
using Community_Service.Data;
using Community_Service.Invites;
using Community_Service.Permissions;
using PresenceGrpc = Community_Service.Presence.Grpc;
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
        private readonly PresenceGrpc.PresenceService.PresenceServiceClient _presence;

        private const uint DefaultMembersLimit = 50;
        private const uint MaxMembersLimit = 200;
        private const int OfflineScanBatchSize = 500;

        public CommunityGrpcService(
            CommunityRepository communities,
            MemberRepository members,
            CategoryRepository categories,
            ChannelRepository channels,
            InviteRepository invites,
            IPermissionGuard guard,
            PresenceGrpc.PresenceService.PresenceServiceClient presence)
        {
            _communities = communities;
            _members = members;
            _categories = categories;
            _channels = channels;
            _invites = invites;
            _guard = guard;
            _presence = presence;
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

        public override async Task<GetMembersResponse> GetMembers(
            GetMembersRequest request, ServerCallContext context)
        {
            if (request.CommunityId == 0 || request.CommunityId > long.MaxValue)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "community_id is required"));

            var limit = request.Limit == 0 ? DefaultMembersLimit : request.Limit;
            if (limit > MaxMembersLimit)
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    $"limit must not exceed {MaxMembersLimit}"));

            var token = MemberPageToken.Parse(request.PageToken);
            var communityId = (long)request.CommunityId;
            await _guard.EnsureMemberAsync(context.GetUserId(), communityId);

            var authorization = ForwardAuthorization(context);
            var totalMembersTask = _members.CountByCommunityAsync(communityId);
            var summaryTask = _presence.GetCommunityPresenceSummaryAsync(
                new PresenceGrpc.GetCommunityPresenceSummaryRequest
                {
                    CommunityId = request.CommunityId
                },
                authorization,
                cancellationToken: context.CancellationToken).ResponseAsync;

            await Task.WhenAll(totalMembersTask, summaryTask);
            var response = new GetMembersResponse
            {
                TotalMembers = (ulong)await totalMembersTask,
                OnlineCount = (await summaryTask).Summary?.OnlineCount ?? 0
            };

            var returnedUserIds = new HashSet<ulong>();
            var remaining = (int)limit;
            var offlineCursor = 0L;

            if (token.Phase == MemberPagePhase.Online)
            {
                var onlinePresences = await ListAllCommunityOnlineAsync(
                    request.CommunityId,
                    authorization,
                    context.CancellationToken);
                var onlineRows = await _members.ListByUserIdsAsync(
                    communityId,
                    onlinePresences.Keys.ToArray());

                var orderedOnlineRows = onlineRows
                    .OrderBy(row => UInt64Storage.ToUInt64(row.UserId))
                    .ToArray();
                response.OnlineCount = (ulong)orderedOnlineRows.Length;

                var eligibleOnlineRows = orderedOnlineRows
                    .Where(row => UInt64Storage.ToUInt64(row.UserId) > token.Cursor)
                    .ToArray();
                foreach (var row in eligibleOnlineRows.Take(remaining))
                {
                    var userId = UInt64Storage.ToUInt64(row.UserId);
                    response.OnlineMembers.Add(row.ToProto(onlinePresences[userId]));
                    returnedUserIds.Add(userId);
                    remaining--;
                }

                if (remaining == 0)
                {
                    var lastUserId = response.OnlineMembers[^1].UserId;
                    response.NextPageToken = MemberPageToken.AfterFullOnlinePage(
                        eligibleOnlineRows.Length,
                        response.OnlineMembers.Count,
                        response.TotalMembers,
                        response.OnlineCount,
                        lastUserId);
                    return response;
                }
            }
            else
            {
                offlineCursor = checked((long)token.Cursor);
            }

            response.NextPageToken = await AppendOfflineMembersAsync(
                communityId,
                offlineCursor,
                remaining,
                returnedUserIds,
                response,
                authorization,
                context.CancellationToken);
            return response;
        }

        private async Task<Dictionary<ulong, PresenceGrpc.PresenceSnapshot>> ListAllCommunityOnlineAsync(
            ulong communityId,
            Metadata authorization,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<ulong, PresenceGrpc.PresenceSnapshot>();
            var visitedCursors = new HashSet<ulong>();
            var cursor = 0UL;

            do
            {
                var page = await _presence.ListCommunityOnlineAsync(
                    new PresenceGrpc.ListCommunityOnlineRequest
                    {
                        CommunityId = communityId,
                        Cursor = cursor,
                        Limit = MaxMembersLimit
                    },
                    authorization,
                    cancellationToken: cancellationToken);

                foreach (var presence in page.Presences)
                {
                    if (IsOnline(presence.State))
                        result[presence.UserId] = presence;
                }

                cursor = page.NextCursor;
                if (cursor != 0 && !visitedCursors.Add(cursor))
                    throw new RpcException(new Status(StatusCode.Internal, "Presence pagination cursor repeated"));
            } while (cursor != 0);

            return result;
        }

        private async Task<string> AppendOfflineMembersAsync(
            long communityId,
            long afterId,
            int limit,
            HashSet<ulong> returnedUserIds,
            GetMembersResponse response,
            Metadata authorization,
            CancellationToken cancellationToken)
        {
            var remaining = limit;
            while (remaining > 0)
            {
                var candidates = await _members.ListPageAfterIdAsync(
                    communityId,
                    afterId,
                    OfflineScanBatchSize);
                if (candidates.Count == 0)
                    return string.Empty;

                var presenceRequest = new PresenceGrpc.BatchGetPresenceRequest();
                presenceRequest.UserIds.AddRange(
                    candidates.Select(row => UInt64Storage.ToUInt64(row.UserId)));
                var presencePage = await _presence.BatchGetPresenceAsync(
                    presenceRequest,
                    authorization,
                    cancellationToken: cancellationToken);
                var presences = presencePage.Presences
                    .GroupBy(item => item.UserId)
                    .ToDictionary(group => group.Key, group => group.Last());

                for (var index = 0; index < candidates.Count; index++)
                {
                    var candidate = candidates[index];
                    afterId = candidate.Id;
                    var userId = UInt64Storage.ToUInt64(candidate.UserId);
                    var presence = presences.GetValueOrDefault(userId) ?? new PresenceGrpc.PresenceSnapshot
                    {
                        UserId = userId,
                        State = PresenceGrpc.PresenceState.Offline
                    };

                    if (IsOnline(presence.State) || !returnedUserIds.Add(userId))
                        continue;

                    response.OfflineMembers.Add(candidate.ToProto(presence));
                    remaining--;
                    if (remaining == 0)
                    {
                        var mayHaveMore = index < candidates.Count - 1 ||
                                          candidates.Count == OfflineScanBatchSize;
                        return mayHaveMore
                            ? new MemberPageToken(MemberPagePhase.Offline, (ulong)afterId).ToString()
                            : string.Empty;
                    }
                }

                if (candidates.Count < OfflineScanBatchSize)
                    return string.Empty;
            }

            return string.Empty;
        }

        private static bool IsOnline(PresenceGrpc.PresenceState state) =>
            state is PresenceGrpc.PresenceState.Online or
                PresenceGrpc.PresenceState.Idle or
                PresenceGrpc.PresenceState.DoNotDisturb;

        private static Metadata ForwardAuthorization(ServerCallContext context)
        {
            var value = context.RequestHeaders.FirstOrDefault(header =>
                !header.IsBinary &&
                string.Equals(header.Key, "authorization", StringComparison.OrdinalIgnoreCase))?.Value;
            if (string.IsNullOrWhiteSpace(value))
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Authorization metadata is required"));

            return new Metadata { { "authorization", value } };
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
