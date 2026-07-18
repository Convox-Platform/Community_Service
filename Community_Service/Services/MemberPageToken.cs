using System.Globalization;
using Grpc.Core;

namespace Community_Service.Services;

internal enum MemberPagePhase
{
    Online,
    Offline
}

internal readonly record struct MemberPageToken(MemberPagePhase Phase, ulong Cursor)
{
    public static MemberPageToken First => new(MemberPagePhase.Online, 0);

    public static MemberPageToken Parse(string value)
    {
        if (string.IsNullOrEmpty(value))
            return First;

        if (value.Length < 3 || value[1] != ':' ||
            !ulong.TryParse(value.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var cursor))
        {
            throw InvalidToken();
        }

        return value[0] switch
        {
            'o' when cursor > 0 => new MemberPageToken(MemberPagePhase.Online, cursor),
            'f' when cursor <= long.MaxValue => new MemberPageToken(MemberPagePhase.Offline, cursor),
            _ => throw InvalidToken()
        };
    }

    public override string ToString() =>
        $"{(Phase == MemberPagePhase.Online ? 'o' : 'f')}:{Cursor.ToString(CultureInfo.InvariantCulture)}";

    public static string AfterFullOnlinePage(
        int eligibleOnlineCount,
        int returnedOnlineCount,
        ulong totalMembers,
        ulong onlineCount,
        ulong lastUserId)
    {
        if (eligibleOnlineCount > returnedOnlineCount)
            return new MemberPageToken(MemberPagePhase.Online, lastUserId).ToString();

        return totalMembers > onlineCount
            ? new MemberPageToken(MemberPagePhase.Offline, 0).ToString()
            : string.Empty;
    }

    private static RpcException InvalidToken() =>
        new(new Status(StatusCode.InvalidArgument, "Invalid page_token"));
}
