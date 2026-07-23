using Community_Service.Data;
using Community_Service.Permissions.Grpc;
using Grpc.Core;
using Microsoft.AspNetCore.Http;

namespace Community_Service.Permissions
{
    // Ключи пермишенов. Должны совпадать с сидами permission-service.
    public static class PermissionKeys
    {
        public const string ReadChannel = "channel.read";
        public const string ManageChannels = "channel.manage";
        public const string EditChannel = "channel.edit";
        public const string ManageMeetings = "meeting.manage";
        public const string CreateInvite = "community.invite";
        public const string CommunityAdmin = "community.admin";
    }

    public sealed record ChannelLayoutAccess(
        IReadOnlySet<long> ReadableChannelIds,
        IReadOnlySet<long> ReadableCategoryIds);

    public interface IPermissionGuard
    {
        // Проверяет, что пользователь состоит в комьюнити. Иначе — RpcException.
        Task EnsureMemberAsync(ulong userId, long communityId);
        Task EnsureCanManageChannelsAsync(ulong userId, long communityId);
        Task EnsureCanEditChannelAsync(ulong userId, long communityId);
        Task EnsureCanManageMeetingsAsync(ulong userId, long communityId);
        Task EnsureCanCreateInviteAsync(ulong userId, long communityId);
        Task EnsureCommunityAdminAsync(ulong userId, long communityId);
        Task<ChannelLayoutAccess> ResolveChannelLayoutAccessAsync(
            ulong userId,
            long communityId,
            IReadOnlyCollection<CategoryEntity> categories,
            IReadOnlyCollection<ChannelEntity> channels);
        Task EnsureCanReadChannelAsync(ulong userId, long communityId, ChannelEntity channel);
    }

    public class PermissionGuard : IPermissionGuard
    {
        private readonly CommunityRepository _communities;
        private readonly MemberRepository _members;
        private readonly PermissionService.PermissionServiceClient _permissions;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public PermissionGuard(
            CommunityRepository communities,
            MemberRepository members,
            PermissionService.PermissionServiceClient permissions,
            IHttpContextAccessor httpContextAccessor)
        {
            _communities = communities;
            _members = members;
            _permissions = permissions;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task EnsureMemberAsync(ulong userId, long communityId)
        {
            if (await _communities.GetOwnerIdAsync(communityId) is null)
                throw new RpcException(new Status(StatusCode.NotFound, "Community not found"));

            if (!await _members.ExistsAsync(communityId, userId))
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Not a member of the community"));
        }

        public Task EnsureCanManageChannelsAsync(ulong userId, long communityId) =>
            EnsurePermissionAsync(userId, communityId, PermissionKeys.ManageChannels);

        public Task EnsureCanEditChannelAsync(ulong userId, long communityId) =>
            EnsureAnyPermissionAsync(
                userId, communityId, PermissionKeys.EditChannel, PermissionKeys.ManageChannels);

        public Task EnsureCanManageMeetingsAsync(ulong userId, long communityId) =>
            EnsurePermissionAsync(userId, communityId, PermissionKeys.ManageMeetings);

        public Task EnsureCanCreateInviteAsync(ulong userId, long communityId) =>
            EnsurePermissionAsync(userId, communityId, PermissionKeys.CreateInvite);

        public Task EnsureCommunityAdminAsync(ulong userId, long communityId) =>
            EnsurePermissionAsync(userId, communityId, PermissionKeys.CommunityAdmin);

        public async Task<ChannelLayoutAccess> ResolveChannelLayoutAccessAsync(
            ulong userId,
            long communityId,
            IReadOnlyCollection<CategoryEntity> categories,
            IReadOnlyCollection<ChannelEntity> channels)
        {
            var channelRequest = new AdminHasChannelAccessRequest
            {
                UserId = userId,
                ServerId = (ulong)communityId,
                Permission = PermissionKeys.ReadChannel
            };
            // Keep the legacy field populated so rolling deployments with an older
            // permission-service fail back to channel-only resolution instead of hiding everything.
            channelRequest.ChannelId.AddRange(channels.Select(channel => (ulong)channel.Id));
            channelRequest.ChannelContexts.AddRange(channels.Select(channel =>
                new ChannelAccessContext
                {
                    ChannelId = (ulong)channel.Id,
                    CategoryId = channel.CategoryId is { } categoryId ? (ulong)categoryId : 0
                }));

            var categoryRequest = new AdminHasCategoryAccessRequest
            {
                UserId = userId,
                ServerId = (ulong)communityId,
                Permission = PermissionKeys.ReadChannel
            };
            categoryRequest.CategoryId.AddRange(categories.Select(category => (ulong)category.Id));

            var authorization = ForwardAuthorization();
            var channelAccessTask = channels.Count == 0
                ? Task.FromResult(new AdminHasChannelAccessResponse())
                : _permissions.AdminHasChannelAccessAsync(
                    channelRequest, authorization).ResponseAsync;
            var categoryAccessTask = categories.Count == 0
                ? Task.FromResult(new AdminHasCategoryAccessResponse())
                : _permissions.AdminHasCategoryAccessAsync(
                    categoryRequest, authorization).ResponseAsync;

            await Task.WhenAll(channelAccessTask, categoryAccessTask);
            var readableChannelIds = (await channelAccessTask).Accesses
                .Where(access => access.Status > 0)
                .Select(access => (long)access.ChannelId)
                .ToHashSet();
            var readableCategoryIds = (await categoryAccessTask).Accesses
                .Where(access => access.Status > 0)
                .Select(access => (long)access.ChannelId)
                .ToHashSet();

            return new ChannelLayoutAccess(readableChannelIds, readableCategoryIds);
        }

        public async Task EnsureCanReadChannelAsync(
            ulong userId,
            long communityId,
            ChannelEntity channel)
        {
            var access = await ResolveChannelLayoutAccessAsync(
                userId,
                communityId,
                Array.Empty<CategoryEntity>(),
                new[] { channel });
            if (!access.ReadableChannelIds.Contains(channel.Id))
                throw new RpcException(new Status(
                    StatusCode.PermissionDenied,
                    $"Missing permission: {PermissionKeys.ReadChannel}"));
        }

        private Task EnsurePermissionAsync(ulong userId, long communityId, string key) =>
            EnsureAnyPermissionAsync(userId, communityId, key);

        private async Task EnsureAnyPermissionAsync(
            ulong userId, long communityId, params string[] keys)
        {
            var ownerId = await _communities.GetOwnerIdAsync(communityId)
                ?? throw new RpcException(new Status(StatusCode.NotFound, "Community not found"));

            if (!await _members.ExistsAsync(communityId, userId))
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Not a member of the community"));

            // Владелец имеет полный доступ и не зависит от ролей в permission-service.
            if (ownerId == userId)
                return;

            var response = await _permissions.AdminListUserPermissionsAsync(
                new AdminListUserPermissionsRequest
                {
                    UserId = userId,
                    ServerId = (ulong)communityId
                },
                ForwardAuthorization());

            var granted = response.Permissions.Any(
                p => keys.Contains(p.Key, StringComparer.Ordinal) && p.Status > 0);
            if (!granted)
                throw new RpcException(new Status(
                    StatusCode.PermissionDenied,
                    $"Missing permission: {string.Join(" or ", keys)}"));
        }

        private Metadata ForwardAuthorization()
        {
            var value = _httpContextAccessor.HttpContext?.Request.Headers.Authorization
                .ToString();
            if (string.IsNullOrWhiteSpace(value))
                throw new RpcException(new Status(
                    StatusCode.Unauthenticated,
                    "Authorization metadata is required"));

            return new Metadata { { "authorization", value } };
        }
    }
}
