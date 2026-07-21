using Community_Service.Data;
using Community_Service.Events.Contracts;
using Community_Service.Services;
using Google.Protobuf.WellKnownTypes;

namespace Community_Service.Events;

public static class CommunityEventFactory
{
    private const uint SchemaVersion = 1;

    public static OutboxMessage CommunityCreated(CommunityEntity community, ulong actorUserId) =>
        Build(community.Id, actorUserId,
            new CommunityCreated
            {
                Community = ToSnapshot(community)
            },
            RoutingKeys.Community(community.Id, "created"));

    public static OutboxMessage CommunityUpdated(
        CommunityEntity community, ulong actorUserId, IEnumerable<string> changedFields)
    {
        var payload = new CommunityUpdated { Community = community.ToProto() };
        payload.ChangedFields.AddRange(changedFields);
        return Build(community.Id, actorUserId, payload,
            RoutingKeys.Community(community.Id, "updated"));
    }

    public static OutboxMessage CommunityDeleted(
        CommunityEntity community,
        ulong actorUserId,
        IReadOnlyCollection<ulong> memberUserIds)
    {
        var payload = new CommunityDeleted { Community = ToSnapshot(community) };
        payload.MemberUserIds.AddRange(memberUserIds);
        var routingKeys = new List<string>(memberUserIds.Count + 1)
        {
            RoutingKeys.Community(community.Id, "deleted")
        };
        routingKeys.AddRange(memberUserIds.Select(userId =>
            RoutingKeys.User(userId, "community.deleted")));
        return Build(community.Id, actorUserId, payload, routingKeys.ToArray());
    }

    public static OutboxMessage CommunityOwnershipTransferred(
        CommunityEntity community,
        ulong actorUserId,
        ulong previousOwnerUserId,
        ulong newOwnerUserId) =>
        Build(community.Id, actorUserId,
            new CommunityOwnershipTransferred
            {
                Community = ToSnapshot(community),
                PreviousOwnerUserId = previousOwnerUserId,
                NewOwnerUserId = newOwnerUserId
            },
            RoutingKeys.Community(community.Id, "ownership.transferred"),
            RoutingKeys.User(previousOwnerUserId, "community.ownership.transferred"),
            RoutingKeys.User(newOwnerUserId, "community.ownership.transferred"));

    public static OutboxMessage MemberLeft(MemberEntity member, ulong actorUserId) =>
        Build(member.CommunityId, actorUserId,
            new MemberLeft
            {
                Member = new MemberSnapshot
                {
                    Id = (ulong)member.Id,
                    CommunityId = (ulong)member.CommunityId,
                    UserId = UInt64Storage.ToUInt64(member.UserId),
                    JoinedAt = ToTimestamp(member.JoinedAt)
                }
            },
            RoutingKeys.Community(member.CommunityId, "member.left"),
            RoutingKeys.User(UInt64Storage.ToUInt64(member.UserId), "membership.left"));

    public static OutboxMessage MemberJoined(
        MemberEntity member, ulong actorUserId, string inviteCode) =>
        Build(member.CommunityId, actorUserId,
            new MemberJoined
            {
                Member = new MemberSnapshot
                {
                    Id = (ulong)member.Id,
                    CommunityId = (ulong)member.CommunityId,
                    UserId = UInt64Storage.ToUInt64(member.UserId),
                    JoinedAt = ToTimestamp(member.JoinedAt)
                },
                InviteCode = inviteCode
            },
            RoutingKeys.Community(member.CommunityId, "member.joined"),
            RoutingKeys.User(UInt64Storage.ToUInt64(member.UserId), "membership.joined"));

    public static OutboxMessage ChannelCreated(ChannelEntity channel, ulong actorUserId) =>
        Build(channel.CommunityId, actorUserId,
            new ChannelCreated { Channel = channel.ToProto() },
            RoutingKeys.Community(channel.CommunityId, "channel.created"));

    public static OutboxMessage ChannelUpdated(
        ChannelEntity channel, ulong actorUserId, IEnumerable<string> changedFields)
    {
        var payload = new ChannelUpdated { Channel = channel.ToProto() };
        payload.ChangedFields.AddRange(changedFields);
        return Build(channel.CommunityId, actorUserId, payload,
            RoutingKeys.Community(channel.CommunityId, "channel.updated"));
    }

    public static OutboxMessage ChannelDeleted(ChannelEntity channel, ulong actorUserId) =>
        Build(channel.CommunityId, actorUserId,
            new ChannelDeleted { Channel = channel.ToProto() },
            RoutingKeys.Community(channel.CommunityId, "channel.deleted"));

    public static OutboxMessage CategoryCreated(CategoryEntity category, ulong actorUserId) =>
        Build(category.CommunityId, actorUserId,
            new CategoryCreated { Category = category.ToProto() },
            RoutingKeys.Community(category.CommunityId, "category.created"));

    public static OutboxMessage CategoryUpdated(
        CategoryEntity category, ulong actorUserId, IEnumerable<string> changedFields)
    {
        var payload = new CategoryUpdated { Category = category.ToProto() };
        payload.ChangedFields.AddRange(changedFields);
        return Build(category.CommunityId, actorUserId, payload,
            RoutingKeys.Community(category.CommunityId, "category.updated"));
    }

    public static OutboxMessage CategoryDeleted(CategoryEntity category, ulong actorUserId) =>
        Build(category.CommunityId, actorUserId,
            new CategoryDeleted { Category = category.ToProto() },
            RoutingKeys.Community(category.CommunityId, "category.deleted"));

    public static OutboxMessage MeetingCreated(MeetingEntity meeting, ulong actorUserId) =>
        Build(meeting.CommunityId, actorUserId,
            new MeetingCreated { Meeting = meeting.ToProto() },
            RoutingKeys.Community(meeting.CommunityId, "meeting.created"));

    public static OutboxMessage MeetingUpdated(
        MeetingEntity meeting, ulong actorUserId, IEnumerable<string> changedFields)
    {
        var payload = new MeetingUpdated { Meeting = meeting.ToProto() };
        payload.ChangedFields.AddRange(changedFields);
        return Build(meeting.CommunityId, actorUserId, payload,
            RoutingKeys.Community(meeting.CommunityId, "meeting.updated"));
    }

    public static OutboxMessage MeetingDeleted(MeetingEntity meeting, ulong actorUserId) =>
        Build(meeting.CommunityId, actorUserId,
            new MeetingDeleted { Meeting = meeting.ToProto() },
            RoutingKeys.Community(meeting.CommunityId, "meeting.deleted"));

    private static OutboxMessage Build(
        long communityId, ulong actorUserId, Google.Protobuf.IMessage payload,
        params string[] routingKeys)
    {
        var eventId = Guid.NewGuid();
        var envelope = new EventEnvelope
        {
            EventId = eventId.ToString("D"),
            CorrelationId = Guid.NewGuid().ToString("D"),
            OccurredAt = Timestamp.FromDateTime(DateTime.UtcNow),
            ActorUserId = actorUserId,
            CommunityId = (ulong)communityId,
            SchemaVersion = SchemaVersion
        };

        switch (payload)
        {
            case CommunityCreated value: envelope.CommunityCreated = value; break;
            case CommunityUpdated value: envelope.CommunityUpdated = value; break;
            case CommunityDeleted value: envelope.CommunityDeleted = value; break;
            case CommunityOwnershipTransferred value: envelope.CommunityOwnershipTransferred = value; break;
            case MemberLeft value: envelope.MemberLeft = value; break;
            case MemberJoined value: envelope.MemberJoined = value; break;
            case ChannelCreated value: envelope.ChannelCreated = value; break;
            case ChannelUpdated value: envelope.ChannelUpdated = value; break;
            case ChannelDeleted value: envelope.ChannelDeleted = value; break;
            case CategoryCreated value: envelope.CategoryCreated = value; break;
            case CategoryUpdated value: envelope.CategoryUpdated = value; break;
            case CategoryDeleted value: envelope.CategoryDeleted = value; break;
            case MeetingCreated value: envelope.MeetingCreated = value; break;
            case MeetingUpdated value: envelope.MeetingUpdated = value; break;
            case MeetingDeleted value: envelope.MeetingDeleted = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(payload));
        }

        return new OutboxMessage(envelope, routingKeys);
    }

    private static Timestamp ToTimestamp(DateTime value) =>
        Timestamp.FromDateTime(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static CommunitySnapshot ToSnapshot(CommunityEntity community) => new()
    {
        Community = community.ToProto(),
        OwnerUserId = UInt64Storage.ToUInt64(community.OwnerId),
        CreatedAt = ToTimestamp(community.CreatedAt)
    };
}
