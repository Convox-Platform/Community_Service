using Dapper;
using Npgsql;

namespace Community_Service.Data;

internal readonly record struct MembershipInsertResult(MemberEntity Member, bool Inserted);

internal static class MembershipOrderStore
{
    public static async Task LockUserAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long storedUserId) =>
        await connection.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(@userId);",
            new { userId = storedUserId },
            transaction);

    public static async Task<MembershipInsertResult> AddAtTopAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long communityId,
        long storedUserId)
    {
        await LockUserAsync(connection, transaction, storedUserId);

        var existing = await connection.QuerySingleOrDefaultAsync<MemberEntity>(
            @"SELECT * FROM community_members
              WHERE community_id = @communityId AND user_id = @userId;",
            new { communityId, userId = storedUserId },
            transaction);
        if (existing is not null)
            return new MembershipInsertResult(existing, false);

        await connection.ExecuteAsync(
            @"UPDATE community_members
              SET sort_order = sort_order + 1
              WHERE user_id = @userId;",
            new { userId = storedUserId },
            transaction);

        var member = await connection.QuerySingleAsync<MemberEntity>(
            @"INSERT INTO community_members (community_id, user_id, sort_order)
              VALUES (@communityId, @userId, 0)
              RETURNING *;",
            new { communityId, userId = storedUserId },
            transaction);
        return new MembershipInsertResult(member, true);
    }

    public static async Task NormalizeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long storedUserId) =>
        await connection.ExecuteAsync(
            @"WITH ranked AS (
                  SELECT id,
                         ROW_NUMBER() OVER (ORDER BY sort_order, id) - 1 AS position
                  FROM community_members
                  WHERE user_id = @userId
              )
              UPDATE community_members AS member
              SET sort_order = ranked.position::INT
              FROM ranked
              WHERE ranked.id = member.id;",
            new { userId = storedUserId },
            transaction);
}
