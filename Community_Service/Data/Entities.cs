namespace Community_Service.Data
{
    // Строки БД (Dapper). Имена столбцов snake_case маппятся на PascalCase
    // через DefaultTypeMap.MatchNamesWithUnderscores = true (см. Program).
    public class CommunityEntity
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string? Avatar { get; set; }
        public string Description { get; set; } = "";
        public long OwnerId { get; set; }
        public DateTime CreatedAt { get; set; }
        public int SortOrder { get; set; }
        // Заполняется агрегатным подзапросом в списках, иначе 0.
        public int MembersCount { get; set; }
    }

    // Членство пользователя в комьюнити (связь many-to-many «пользователь ↔ комьюнити»).
    public class MemberEntity
    {
        public long Id { get; set; }
        public long CommunityId { get; set; }
        public long UserId { get; set; }
        public DateTime JoinedAt { get; set; }
        public int SortOrder { get; set; }
    }

    public class CategoryEntity
    {
        public long Id { get; set; }
        public long CommunityId { get; set; }
        public string Name { get; set; } = "";
        public int Position { get; set; }
    }

    public class ChannelEntity
    {
        public long Id { get; set; }
        public long CommunityId { get; set; }
        public long? CategoryId { get; set; }
        public string Name { get; set; } = "";
        public short Type { get; set; }
        public string Description { get; set; } = "";
        public int Position { get; set; }
        public int? Bitrate { get; set; }
        public long? ActivityPublishChannelId { get; set; }
    }

    public class MeetingEntity
    {
        public long Id { get; set; }
        public long CommunityId { get; set; }
        public long ChannelId { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public DateTime StartAt { get; set; }
        public short Status { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public string? RecordingId { get; set; }
        public long? ActivityMessageId { get; set; }
    }

    public class InviteEntity
    {
        public string Code { get; set; } = "";
        public long CommunityId { get; set; }
        public long CreatorUserId { get; set; }
        public int? MaxUses { get; set; }
        public int UsesCount { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class InviteAttributionEntity
    {
        public long UserId { get; set; }
        public string InviteCode { get; set; } = "";
        public DateTime LastJoinedAt { get; set; }
        public bool IsCurrentMember { get; set; }
    }
}
