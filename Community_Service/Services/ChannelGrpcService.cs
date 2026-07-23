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

            var bitrate = ValidateBitrate(request.Type, request.HasBitrate, request.Bitrate);
            var activityPublishChannelId = await ResolveActivityPublishChannelAsync(
                request.Type, request.HasActivityPublishChannelId,
                request.ActivityPublishChannelId, communityId);

            var categoryId = await ResolveCategoryAsync(request.CategoryId, communityId);

            var channel = await _channels.CreateAsync(
                communityId, categoryId, request.Name, (short)request.Type, bitrate,
                activityPublishChannelId, userId);

            return new CreateChannelResponse { Channel = channel.ToProto() };
        }

        public override async Task<GetChannelInfoResponse> GetChannelInfo(
            GetChannelInfoRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureMemberAsync(userId, communityId);

            var channel = await LoadChannelAsync((long)request.ChannelId, communityId);
            await _guard.EnsureCanReadChannelAsync(userId, communityId, channel);
            return new GetChannelInfoResponse { Channel = channel.ToProto() };
        }

        public override async Task<EditChannelResponse> EditChannel(
            EditChannelRequest request, ServerCallContext context)
        {
            var userId = context.GetUserId();
            var communityId = (long)request.CommunityId;
            await _guard.EnsureCanEditChannelAsync(userId, communityId);

            var channelId = (long)request.ChannelId;
            var current = await LoadChannelAsync(channelId, communityId);
            var bitrate = ValidateBitrate(
                (ChannelType)current.Type, request.HasBitrate, request.Bitrate);
            var activityPublishChannelId = await ResolveActivityPublishChannelAsync(
                (ChannelType)current.Type, request.HasActivityPublishChannelId,
                request.ActivityPublishChannelId, communityId);

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
            if (request.HasBitrate) changedFields.Add("bitrate");
            if (request.HasActivityPublishChannelId) changedFields.Add("activity_publish_channel_id");

            var updated = await _channels.UpdateAsync(
                channelId,
                request.HasName ? request.Name : null,
                request.HasDescription ? request.Description : null,
                bitrate,
                request.HasActivityPublishChannelId,
                activityPublishChannelId,
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

        private static int? ValidateBitrate(ChannelType type, bool hasBitrate, uint bitrate)
        {
            if (!hasBitrate)
                return null;
            if (type != ChannelType.Voice)
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument, "Bitrate can only be set for voice channels"));
            if (bitrate is 0 or > int.MaxValue)
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument, "Bitrate must be between 1 and 2147483647 bit/s"));
            return (int)bitrate;
        }

        private async Task<long?> ResolveActivityPublishChannelAsync(
            ChannelType type,
            bool hasActivityPublishChannelId,
            ulong activityPublishChannelId,
            long communityId)
        {
            if (!hasActivityPublishChannelId || activityPublishChannelId == 0)
                return null;

            if (type != ChannelType.Voice)
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    "Activity publish channel can only be set for voice channels"));

            var channel = await LoadChannelAsync((long)activityPublishChannelId, communityId);
            if (channel.Type != (short)ChannelType.Text)
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    "Activity publish channel must be a text channel"));

            return channel.Id;
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
