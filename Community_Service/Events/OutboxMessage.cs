using Community_Service.Events.Contracts;

namespace Community_Service.Events;

public sealed record OutboxMessage(EventEnvelope Envelope, IReadOnlyCollection<string> RoutingKeys);

public static class RoutingKeys
{
    public static string Community(long communityId, string suffix) =>
        $"community.community.{communityId}.{suffix}";

    public static string User(ulong userId, string suffix) =>
        $"community.user.{userId}.{suffix}";
}
