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

        public override async Task<PreviewCommunityResponse> PreviewCommunity(
            PreviewCommunityRequest request, ServerCallContext context)
        {
            if (request.CommunityId == 0 || request.CommunityId > long.MaxValue)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "community_id is required"));

            var communityId = (long)request.CommunityId;
            var community = await _communities.GetByIdAsync(communityId)
                ?? throw new RpcException(new Status(StatusCode.NotFound, "Community not found"));
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
            var totalMembers = (ulong)await totalMembersTask;
            var onlineCount = (await summaryTask).Summary?.OnlineCount ?? 0;
            return new PreviewCommunityResponse
            {
                Community = community.ToProto(),
                OnlineCount = Math.Min(onlineCount, totalMembers),
                TotalMembers = totalMembers
            };
        }

        public override async Task<SetCommunityPositionResponse> SetCommunityPosition(
            SetCommunityPositionRequest request, ServerCallContext context)
        {
            if (request.CommunityId == 0 || request.CommunityId > long.MaxValue)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "community_id is required"));
            if (request.Position > int.MaxValue)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "position is out of range"));

            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureMemberAsync(userId, communityId);

            var result = await _members.SetPositionAsync(
                communityId,
                userId,
                (int)request.Position);
            return result.Status switch
            {
                SetMemberPositionStatus.Success => new SetCommunityPositionResponse
                {
                    Position = (uint)result.Position
                },
                SetMemberPositionStatus.OutOfRange =>
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "position is out of range")),
                SetMemberPositionStatus.NotMember =>
                    throw new RpcException(new Status(StatusCode.PermissionDenied, "Not a member of the community")),
                _ => throw new InvalidOperationException("Unknown community position result")
            };
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
            ValidateCommunityId(request.CommunityId);
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;

            return await _members.LeaveAsync(communityId, userId) switch
            {
                LeaveCommunityStatus.Left => new LeaveCommunityResponse(),
                LeaveCommunityStatus.CommunityNotFound =>
                    throw new RpcException(new Status(StatusCode.NotFound, "Community not found")),
                LeaveCommunityStatus.OwnerCannotLeave =>
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "Owner cannot leave the community")),
                LeaveCommunityStatus.NotMember =>
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "Not a member of the community")),
                _ => throw new InvalidOperationException("Unknown community leave result")
            };
        }

        public override async Task<DeleteCommunityResponse> DeleteCommunity(
            DeleteCommunityRequest request, ServerCallContext context)
        {
            ValidateCommunityId(request.CommunityId);
            var result = await _communities.DeleteAsync(
                (long)request.CommunityId,
                context.GetUserId());

            return result switch
            {
                DeleteCommunityStatus.Deleted => new DeleteCommunityResponse(),
                DeleteCommunityStatus.NotFound =>
                    throw new RpcException(new Status(StatusCode.NotFound, "Community not found")),
                DeleteCommunityStatus.NotOwner =>
                    throw new RpcException(new Status(StatusCode.PermissionDenied, "Only the owner can delete the community")),
                _ => throw new InvalidOperationException("Unknown community deletion result")
            };
        }

        public override async Task<TransferCommunityOwnershipResponse> TransferCommunityOwnership(
            TransferCommunityOwnershipRequest request, ServerCallContext context)
        {
            ValidateCommunityId(request.CommunityId);
            if (request.NewOwnerUserId == 0)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "new_owner_user_id is required"));

            var result = await _communities.TransferOwnershipAsync(
                (long)request.CommunityId,
                context.GetUserId(),
                request.NewOwnerUserId);

            return result.Status switch
            {
                TransferCommunityOwnershipStatus.Transferred =>
                    new TransferCommunityOwnershipResponse
                    {
                        PreviousOwnerUserId = result.PreviousOwnerUserId,
                        NewOwnerUserId = result.NewOwnerUserId
                    },
                TransferCommunityOwnershipStatus.NotFound =>
                    throw new RpcException(new Status(StatusCode.NotFound, "Community not found")),
                TransferCommunityOwnershipStatus.NotOwner =>
                    throw new RpcException(new Status(StatusCode.PermissionDenied, "Only the owner can transfer the community")),
                TransferCommunityOwnershipStatus.NewOwnerNotMember =>
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "The new owner must be a community member")),
                TransferCommunityOwnershipStatus.AlreadyOwner =>
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "The user is already the community owner")),
                _ => throw new InvalidOperationException("Unknown community ownership transfer result")
            };
        }

        public override async Task<EditCommunityResponse> EditCommunity(
            EditCommunityRequest request, ServerCallContext context)
        {
            ValidateCommunityId(request.CommunityId);
            if (!request.HasName && !request.HasAvatar && !request.HasDescription)
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument, "At least one field to update is required"));
            if (request.HasName && string.IsNullOrWhiteSpace(request.Name))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Name is required"));

            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureCommunityAdminAsync(userId, communityId);

            var changedFields = new List<string>();
            if (request.HasName) changedFields.Add("name");
            if (request.HasAvatar) changedFields.Add("avatar");
            if (request.HasDescription) changedFields.Add("description");

            var updated = await _communities.UpdateAsync(
                communityId,
                request.HasName ? request.Name : null,
                request.HasAvatar,
                request.HasAvatar ? request.Avatar : null,
                request.HasDescription ? request.Description : null,
                userId,
                changedFields);
            return new EditCommunityResponse { Community = updated.ToProto() };
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

        public override async Task<RemoveCommunityMemberResponse> RemoveCommunityMember(
            RemoveCommunityMemberRequest request, ServerCallContext context)
        {
            if (request.CommunityId == 0 || request.CommunityId > long.MaxValue)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "community_id is required"));
            if (request.UserId == 0)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "user_id is required"));

            var communityId = (long)request.CommunityId;
            var actorUserId = context.GetUserId();
            await _guard.EnsureCommunityAdminAsync(actorUserId, communityId);

            var ownerId = await _communities.GetOwnerIdAsync(communityId)
                ?? throw new RpcException(new Status(StatusCode.NotFound, "Community not found"));
            if (request.UserId == ownerId)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "The community owner cannot be removed"));
            if (request.UserId == actorUserId)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Use LeaveCommunity to remove yourself"));
            if (!await _members.RemoveAsync(communityId, request.UserId))
                throw new RpcException(new Status(StatusCode.NotFound, "Community member not found"));

            return new RemoveCommunityMemberResponse();
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

        private static void ValidateCommunityId(ulong communityId)
        {
            if (communityId == 0 || communityId > long.MaxValue)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "community_id is required"));
        }

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

            var categoriesTask = _categories.ListByCommunityAsync(communityId);
            var channelsTask = _channels.ListByCommunityAsync(communityId);
            await Task.WhenAll(categoriesTask, channelsTask);

            var categories = await categoriesTask;
            var channels = await channelsTask;
            var access = await _guard.ResolveChannelLayoutAccessAsync(
                userId, communityId, categories, channels);
            var visibleChannels = channels
                .Where(channel => access.ReadableChannelIds.Contains(channel.Id))
                .ToArray();
            var visibleCategoryIds = access.ReadableCategoryIds.ToHashSet();
            visibleCategoryIds.UnionWith(visibleChannels
                .Where(channel => channel.CategoryId.HasValue)
                .Select(channel => channel.CategoryId!.Value));

            var response = new ListChannelsResponse();
            response.Categories.AddRange(categories
                .Where(category => visibleCategoryIds.Contains(category.Id))
                .Select(category => category.ToProto()));
            response.Channels.AddRange(visibleChannels.Select(channel => channel.ToProto()));
            return response;
        }
    }
}
