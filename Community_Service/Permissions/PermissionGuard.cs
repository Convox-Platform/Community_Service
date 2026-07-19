using Community_Service.Data;
using Community_Service.Permissions.Grpc;
using Grpc.Core;

namespace Community_Service.Permissions
{
    // Ключи пермишенов. Должны совпадать с сидами permission-service.
    public static class PermissionKeys
    {
        public const string ManageChannels = "channel.manage";
        public const string EditChannel = "channel.edit";
        public const string ManageMeetings = "meeting.manage";
        public const string CreateInvite = "community.invite";
        public const string CommunityAdmin = "community.admin";
    }

    public interface IPermissionGuard
    {
        // Проверяет, что пользователь состоит в комьюнити. Иначе — RpcException.
        Task EnsureMemberAsync(ulong userId, long communityId);
        Task EnsureCanManageChannelsAsync(ulong userId, long communityId);
        Task EnsureCanEditChannelAsync(ulong userId, long communityId);
        Task EnsureCanManageMeetingsAsync(ulong userId, long communityId);
        Task EnsureCanCreateInviteAsync(ulong userId, long communityId);
        Task EnsureCommunityAdminAsync(ulong userId, long communityId);
    }

    public class PermissionGuard : IPermissionGuard
    {
        private readonly CommunityRepository _communities;
        private readonly MemberRepository _members;
        private readonly PermissionService.PermissionServiceClient _permissions;

        public PermissionGuard(
            CommunityRepository communities,
            MemberRepository members,
            PermissionService.PermissionServiceClient permissions)
        {
            _communities = communities;
            _members = members;
            _permissions = permissions;
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
                });

            var granted = response.Permissions.Any(
                p => keys.Contains(p.Key, StringComparer.Ordinal) && p.Status > 0);
            if (!granted)
                throw new RpcException(new Status(
                    StatusCode.PermissionDenied,
                    $"Missing permission: {string.Join(" or ", keys)}"));
        }
    }
}
