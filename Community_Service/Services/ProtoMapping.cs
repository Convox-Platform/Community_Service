using Community_Service.Data;
using Community_Service.Presence.Grpc;
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

        public static CommunityInvite ToProto(this InviteEntity e)
        {
            var invite = new CommunityInvite
            {
                Code = e.Code,
                CommunityId = (ulong)e.CommunityId,
                CreatorUserId = UInt64Storage.ToUInt64(e.CreatorUserId),
                UsesCount = (uint)e.UsesCount,
                CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(e.CreatedAt, DateTimeKind.Utc)),
                IsValid = e.MaxUses is null || e.UsesCount < e.MaxUses
            };
            if (e.MaxUses is { } maxUses)
                invite.MaxUses = (uint)maxUses;
            return invite;
        }

        public static InviteAttribution ToProto(this InviteAttributionEntity e) => new()
        {
            UserId = UInt64Storage.ToUInt64(e.UserId),
            InviteCode = e.InviteCode,
            LastJoinedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(e.LastJoinedAt, DateTimeKind.Utc)),
            IsCurrentMember = e.IsCurrentMember
        };

        public static CommunityMember ToProto(this MemberEntity e, PresenceSnapshot presence) => new()
        {
            UserId = UInt64Storage.ToUInt64(e.UserId),
            JoinedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(e.JoinedAt, DateTimeKind.Utc)),
            Presence = presence
        };
    }
}
