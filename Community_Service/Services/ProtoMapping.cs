using Community_Service.Data;
using Google.Protobuf.WellKnownTypes;

namespace Community_Service.Services
{
    // Маппинг сущностей БД в proto-сообщения.
    public static class ProtoMapping
    {
        public static Community ToProto(this CommunityEntity e) => new()
        {
            Id = (ulong)e.Id,
            Name = e.Name,
            Avatar = e.Avatar ?? string.Empty,
            Description = e.Description,
            MembersCount = (uint)e.MembersCount
        };

        public static Category ToProto(this CategoryEntity e) => new()
        {
            Id = (ulong)e.Id,
            CommunityId = (ulong)e.CommunityId,
            Name = e.Name,
            Position = e.Position
        };

        public static Channel ToProto(this ChannelEntity e) => new()
        {
            Id = (ulong)e.Id,
            CommunityId = (ulong)e.CommunityId,
            CategoryId = (ulong)(e.CategoryId ?? 0),
            Name = e.Name,
            Type = (ChannelType)e.Type,
            Description = e.Description,
            Position = e.Position
        };

        public static Meeting ToProto(this MeetingEntity e) => new()
        {
            Id = (ulong)e.Id,
            CommunityId = (ulong)e.CommunityId,
            ChannelId = (ulong)e.ChannelId,
            Name = e.Name,
            Description = e.Description,
            StartAt = Timestamp.FromDateTime(DateTime.SpecifyKind(e.StartAt, DateTimeKind.Utc))
        };
    }
}
