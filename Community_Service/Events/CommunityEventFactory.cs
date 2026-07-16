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
                Community = new CommunitySnapshot
                {
                    Community = community.ToProto(),
                    OwnerUserId = community.OwnerId,
                    CreatedAt = ToTimestamp(community.CreatedAt)
                }
            },
            RoutingKeys.Community(community.Id, "created"));

    public static OutboxMessage MemberLeft(MemberEntity member, ulong actorUserId) =>
        Build(member.CommunityId, actorUserId,
            new MemberLeft
            {
                Member = new MemberSnapshot
                {
                    Id = (ulong)member.Id,
                    CommunityId = (ulong)member.CommunityId,
                    UserId = member.UserId,
                    JoinedAt = ToTimestamp(member.JoinedAt)
                }
            },
            RoutingKeys.Community(member.CommunityId, "member.left"),
            RoutingKeys.User(member.UserId, "membership.left"));

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
            case MemberLeft value: envelope.MemberLeft = value; break;
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
}
