using Community_Service.Events;
using Community_Service.Invites;
using Dapper;
using Npgsql;

namespace Community_Service.Data;

public enum InviteAcceptanceStatus
{
    Accepted,
    AlreadyMember,
    NotFound,
    LimitReached
}

public sealed record InviteAcceptanceResult(
    InviteAcceptanceStatus Status,
    CommunityEntity? Community = null);

public sealed class InviteRepository
{
    private const int CodeGenerationAttempts = 5;

    private readonly NpgsqlDataSource _db;
    private readonly OutboxWriter _outbox;
    private readonly IInviteCodeGenerator _codes;

    public InviteRepository(
        NpgsqlDataSource db,
        OutboxWriter outbox,
        IInviteCodeGenerator codes)
    {
        _db = db;
        _outbox = outbox;
        _codes = codes;
    }

    public async Task<InviteEntity> CreateAsync(
        long communityId, ulong creatorUserId, int? maxUses)
    {
        var storedCreatorUserId = UInt64Storage.ToInt64(creatorUserId);
        await using var conn = await _db.OpenConnectionAsync();

        for (var attempt = 0; attempt < CodeGenerationAttempts; attempt++)
        {
            var code = _codes.Generate();
            var invite = await conn.QuerySingleOrDefaultAsync<InviteEntity>(
                @"WITH reserved AS (
                      INSERT INTO community_invite_codes (code)
                      VALUES (@code)
                      ON CONFLICT (code) DO NOTHING
                      RETURNING code
                  )
                  INSERT INTO community_invites
                    (code, community_id, creator_user_id, max_uses)
                  SELECT code, @communityId, @creatorUserId, @maxUses FROM reserved
                  RETURNING *;",
                new { code, communityId, creatorUserId = storedCreatorUserId, maxUses });

            if (invite is not null)
                return invite;
        }

        throw new InvalidOperationException("Could not generate a unique invite code.");
    }

    public async Task<IReadOnlyList<InviteEntity>> ListAsync(long communityId)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<InviteEntity>(
            @"SELECT * FROM community_invites
              WHERE community_id = @communityId
              ORDER BY created_at DESC, code;",
            new { communityId });
        return rows.AsList();
    }

    public async Task<bool> DeleteAsync(long communityId, string code)
    {
        await using var conn = await _db.OpenConnectionAsync();
        return await conn.ExecuteAsync(
            @"DELETE FROM community_invites
              WHERE community_id = @communityId AND code = @code;",
            new { communityId, code }) > 0;
    }

    public async Task<IReadOnlyList<InviteAttributionEntity>> ListAttributionsAsync(long communityId)
    {
        await using var conn = await _db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<InviteAttributionEntity>(
            @"WITH ranked AS (
                  SELECT iu.*,
                         ROW_NUMBER() OVER (
                             PARTITION BY iu.user_id
                             ORDER BY iu.last_joined_at DESC, iu.invite_code) AS row_number
                  FROM community_invite_users iu
                  WHERE iu.community_id = @communityId
              )
              SELECT r.user_id, r.invite_code, r.last_joined_at,
                     EXISTS (
                         SELECT 1 FROM community_members m
                         WHERE m.community_id = r.community_id AND m.user_id = r.user_id
                     ) AS is_current_member
              FROM ranked r
              WHERE r.row_number = 1
              ORDER BY r.last_joined_at DESC, r.user_id;",
            new { communityId });
        return rows.AsList();
    }

    public async Task<InviteAcceptanceResult> AcceptAsync(string code, ulong userId)
    {
        var storedUserId = UInt64Storage.ToInt64(userId);
        await using var conn = await _db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var invite = await conn.QuerySingleOrDefaultAsync<InviteEntity>(
            "SELECT * FROM community_invites WHERE code = @code FOR UPDATE;",
            new { code }, tx);
        if (invite is null)
            return new InviteAcceptanceResult(InviteAcceptanceStatus.NotFound);

        var existingMember = await conn.QuerySingleOrDefaultAsync<MemberEntity>(
            @"SELECT * FROM community_members
              WHERE community_id = @communityId AND user_id = @userId;",
            new { invite.CommunityId, userId = storedUserId }, tx);
        if (existingMember is not null)
        {
            var existingCommunity = await LoadCommunityAsync(
                conn, tx, invite.CommunityId, storedUserId);
            await tx.CommitAsync();
            return new InviteAcceptanceResult(
                InviteAcceptanceStatus.AlreadyMember, existingCommunity);
        }

        var wasCounted = await conn.ExecuteScalarAsync<bool>(
            @"SELECT EXISTS(
                  SELECT 1 FROM community_invite_users
                  WHERE invite_code = @code AND user_id = @userId);",
            new { code, userId = storedUserId }, tx);

        if (!wasCounted && invite.MaxUses is { } maxUses && invite.UsesCount >= maxUses)
            return new InviteAcceptanceResult(InviteAcceptanceStatus.LimitReached);

        var membership = await MembershipOrderStore.AddAtTopAsync(
            conn, tx, invite.CommunityId, storedUserId);
        var member = membership.Member;

        // A concurrent accept through another code may have inserted the membership first.
        if (!membership.Inserted)
        {
            var concurrentCommunity = await LoadCommunityAsync(
                conn, tx, invite.CommunityId, storedUserId);
            await tx.CommitAsync();
            return new InviteAcceptanceResult(
                InviteAcceptanceStatus.AlreadyMember, concurrentCommunity);
        }

        if (wasCounted)
        {
            await conn.ExecuteAsync(
                @"UPDATE community_invite_users
                  SET last_joined_at = now()
                  WHERE invite_code = @code AND user_id = @userId;",
                new { code, userId = storedUserId }, tx);
        }
        else
        {
            await conn.ExecuteAsync(
                @"INSERT INTO community_invite_users
                    (community_id, invite_code, user_id)
                  VALUES (@communityId, @code, @userId);",
                new { invite.CommunityId, code, userId = storedUserId }, tx);
            await conn.ExecuteAsync(
                @"UPDATE community_invites
                  SET uses_count = uses_count + 1
                  WHERE code = @code;",
                new { code }, tx);
        }

        await _outbox.EnqueueAsync(conn, tx,
            CommunityEventFactory.MemberJoined(member, userId, code));
        var community = await LoadCommunityAsync(
            conn, tx, invite.CommunityId, storedUserId);
        await tx.CommitAsync();
        return new InviteAcceptanceResult(InviteAcceptanceStatus.Accepted, community);
    }

    private static Task<CommunityEntity> LoadCommunityAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long communityId,
        long storedUserId) =>
        conn.QuerySingleAsync<CommunityEntity>(
            @"SELECT c.*, cm.sort_order,
                     (SELECT COUNT(*) FROM community_members m WHERE m.community_id = c.id) AS members_count
              FROM communities c
              JOIN community_members cm
                ON cm.community_id = c.id AND cm.user_id = @userId
              WHERE c.id = @communityId;",
            new { communityId, userId = storedUserId }, tx);
}
