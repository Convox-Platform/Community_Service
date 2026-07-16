using Community_Service.Auth;
using Community_Service.Data;
using Community_Service.Permissions;
using Grpc.Core;

namespace Community_Service.Services
{
    public class ChannelGrpcService : ChannelService.ChannelServiceBase
    {
        private readonly ChannelRepository _channels;
        private readonly CategoryRepository _categories;
        private readonly IPermissionGuard _guard;

        public ChannelGrpcService(
            ChannelRepository channels,
            CategoryRepository categories,
            IPermissionGuard guard)
        {
            _channels = channels;
            _categories = categories;
            _guard = guard;
        }

        public override async Task<CreateChannelResponse> CreateChannel(
            CreateChannelRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureCanManageChannelsAsync(userId, communityId);

            if (string.IsNullOrWhiteSpace(request.Name))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Name is required"));

            var categoryId = await ResolveCategoryAsync(request.CategoryId, communityId);

            var channel = await _channels.CreateAsync(
                communityId, categoryId, request.Name, (short)request.Type, userId);

            return new CreateChannelResponse { Channel = channel.ToProto() };
        }

        public override async Task<GetChannelInfoResponse> GetChannelInfo(
            GetChannelInfoRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureMemberAsync(userId, communityId);

            var channel = await LoadChannelAsync((long)request.ChannelId, communityId);
            return new GetChannelInfoResponse { Channel = channel.ToProto() };
        }

        public override async Task<EditChannelResponse> EditChannel(
            EditChannelRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureCanManageChannelsAsync(userId, communityId);

            var channelId = (long)request.ChannelId;
            var current = await LoadChannelAsync(channelId, communityId);

            // Перемещение выполняется, если задана категория и/или якорь.
            var move = request.HasCategoryId || request.Anchor is not null;
            long? targetCategory = current.CategoryId;
            long? anchorId = null;
            var below = false;
            if (move)
            {
                targetCategory = request.HasCategoryId
                    ? await ResolveCategoryAsync(request.CategoryId, communityId)
                    : current.CategoryId;

                if (request.Anchor is { } anchor)
                {
                    var anchorChannel = await LoadChannelAsync((long)anchor.ChannelId, communityId);
                    anchorId = anchorChannel.Id;
                    below = anchor.Position == AnchorPosition.Below;
                }

            }

            var changedFields = new List<string>();
            if (request.HasName) changedFields.Add("name");
            if (request.HasDescription) changedFields.Add("description");
            if (request.HasCategoryId) changedFields.Add("category_id");
            if (move) changedFields.Add("position");

            var updated = await _channels.UpdateAsync(
                channelId,
                request.HasName ? request.Name : null,
                request.HasDescription ? request.Description : null,
                move,
                targetCategory,
                anchorId,
                below,
                userId,
                changedFields);
            return new EditChannelResponse { Channel = updated.ToProto() };
        }

        public override async Task<DeleteChannelResponse> DeleteChannel(
            DeleteChannelRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureCanManageChannelsAsync(userId, communityId);

            var channel = await LoadChannelAsync((long)request.ChannelId, communityId);
            await _channels.DeleteAsync(channel.Id, userId);
            return new DeleteChannelResponse();
        }

        private async Task<ChannelEntity> LoadChannelAsync(long channelId, long communityId)
        {
            var channel = await _channels.GetByIdAsync(channelId);
            if (channel is null || channel.CommunityId != communityId)
                throw new RpcException(new Status(StatusCode.NotFound, "Channel not found"));
            return channel;
        }

        // 0 → канал вне категории (NULL). Иначе категория обязана принадлежать этому же комьюнити.
        private async Task<long?> ResolveCategoryAsync(ulong categoryId, long communityId)
        {
            if (categoryId == 0)
                return null;

            var category = await _categories.GetByIdAsync((long)categoryId);
            if (category is null || category.CommunityId != communityId)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Category not found in this community"));
            return category.Id;
        }
    }
}
