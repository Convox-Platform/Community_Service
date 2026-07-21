using Dapper;
using Community_Service.Events;
using Npgsql;

namespace Community_Service.Data
{
    public enum SetMemberPositionStatus
    {
        Success,
        NotMember,
        OutOfRange
    }

    public enum LeaveCommunityStatus
    {
        Left,
        CommunityNotFound,
        OwnerCannotLeave,
        NotMember
    }

    public readonly record struct SetMemberPositionResult(
        SetMemberPositionStatus Status,
        int Position = 0);

    // Членство пользователей в комьюнити (таблица community_members).
    public class MemberRepository
    {
        private readonly NpgsqlDataSource _db;
        private readonly OutboxWriter _outbox;

        public MemberRepository(NpgsqlDataSource db, OutboxWriter outbox)
        {
            _db = db;
            _outbox = outbox;
        }

        // Добавляет участника (идемпотентно) и возвращает его запись.
        public async Task<MemberEntity> AddAsync(long communityId, ulong userId)
        {
            var storedUserId = UInt64Storage.ToInt64(userId);
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var result = await MembershipOrderStore.AddAtTopAsync(
                conn, tx, communityId, storedUserId);
            await tx.CommitAsync();
            return result.Member;
        }

        public async Task<bool> RemoveAsync(long communityId, ulong userId)
        {
            var storedUserId = UInt64Storage.ToInt64(userId);
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await MembershipOrderStore.LockUserAsync(conn, tx, storedUserId);
            var member = await conn.QuerySingleOrDefaultAsync<MemberEntity>(
                @"SELECT * FROM community_members
                  WHERE community_id = @communityId AND user_id = @userId
                  FOR UPDATE;",
                new { communityId, userId = storedUserId }, tx);
            if (member is null)
                return false;

            await conn.ExecuteAsync(
                @"DELETE FROM community_members
                  WHERE community_id = @communityId AND user_id = @userId;",
                new { communityId, userId = storedUserId }, tx);
            await MembershipOrderStore.NormalizeAsync(conn, tx, storedUserId);
            await _outbox.EnqueueAsync(conn, tx,
                CommunityEventFactory.MemberLeft(member, userId));
            await tx.CommitAsync();
            return true;
        }

        public async Task<LeaveCommunityStatus> LeaveAsync(long communityId, ulong userId)
        {
            var storedUserId = UInt64Storage.ToInt64(userId);
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var ownerId = await conn.QuerySingleOrDefaultAsync<long?>(
                @"SELECT owner_id FROM communities
                  WHERE id = @communityId
                  FOR SHARE;",
                new { communityId },
                tx);
            if (ownerId is null)
                return LeaveCommunityStatus.CommunityNotFound;
            if (ownerId.Value == storedUserId)
                return LeaveCommunityStatus.OwnerCannotLeave;

            await MembershipOrderStore.LockUserAsync(conn, tx, storedUserId);
            var member = await conn.QuerySingleOrDefaultAsync<MemberEntity>(
                @"SELECT * FROM community_members
                  WHERE community_id = @communityId AND user_id = @userId
                  FOR UPDATE;",
                new { communityId, userId = storedUserId },
                tx);
            if (member is null)
                return LeaveCommunityStatus.NotMember;

            await conn.ExecuteAsync(
                "DELETE FROM community_members WHERE id = @id;",
                new { member.Id },
                tx);
            await MembershipOrderStore.NormalizeAsync(conn, tx, storedUserId);
            await _outbox.EnqueueAsync(
                conn,
                tx,
                CommunityEventFactory.MemberLeft(member, userId));
            await tx.CommitAsync();
            return LeaveCommunityStatus.Left;
        }

        public async Task<SetMemberPositionResult> SetPositionAsync(
            long communityId,
            ulong userId,
            int position)
        {
            var storedUserId = UInt64Storage.ToInt64(userId);
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await MembershipOrderStore.LockUserAsync(conn, tx, storedUserId);

            var memberships = (await conn.QueryAsync<MemberEntity>(
                @"SELECT * FROM community_members
                  WHERE user_id = @userId
                  ORDER BY sort_order, id
                  FOR UPDATE;",
                new { userId = storedUserId },
                tx)).AsList();
            var currentIndex = memberships.FindIndex(member => member.CommunityId == communityId);
            if (currentIndex < 0)
                return new SetMemberPositionResult(SetMemberPositionStatus.NotMember);
            if (position < 0 || position >= memberships.Count)
                return new SetMemberPositionResult(SetMemberPositionStatus.OutOfRange);

            var moved = memberships[currentIndex];
            memberships.RemoveAt(currentIndex);
            memberships.Insert(position, moved);

            for (var index = 0; index < memberships.Count; index++)
            {
                if (memberships[index].SortOrder == index)
                    continue;

                await conn.ExecuteAsync(
                    "UPDATE community_members SET sort_order = @position WHERE id = @id;",
                    new { position = index, memberships[index].Id },
                    tx);
            }

            await tx.CommitAsync();
            return new SetMemberPositionResult(SetMemberPositionStatus.Success, position);
        }

        public async Task<bool> ExistsAsync(long communityId, ulong userId)
        {
            var storedUserId = UInt64Storage.ToInt64(userId);
            await using var conn = await _db.OpenConnectionAsync();
            return await conn.ExecuteScalarAsync<bool>(
                @"SELECT EXISTS(SELECT 1 FROM community_members
                  WHERE community_id = @communityId AND user_id = @userId);",
                new { communityId, userId = storedUserId });
        }

        public async Task<MemberEntity?> GetAsync(long communityId, ulong userId)
        {
            var storedUserId = UInt64Storage.ToInt64(userId);
            await using var conn = await _db.OpenConnectionAsync();
            return await conn.QuerySingleOrDefaultAsync<MemberEntity>(
                @"SELECT * FROM community_members
                  WHERE community_id = @communityId AND user_id = @userId;",
                new { communityId, userId = storedUserId });
        }

        public async Task<IReadOnlyList<MemberEntity>> ListByCommunityAsync(long communityId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            var rows = await conn.QueryAsync<MemberEntity>(
                "SELECT * FROM community_members WHERE community_id = @communityId ORDER BY joined_at, id;",
                new { communityId });
            return rows.AsList();
        }

        public async Task<IReadOnlyList<MemberEntity>> ListByUserIdsAsync(
            long communityId,
            IReadOnlyCollection<ulong> userIds)
        {
            if (userIds.Count == 0)
                return [];

            var storedUserIds = userIds.Select(UInt64Storage.ToInt64).ToArray();
            await using var conn = await _db.OpenConnectionAsync();
            var rows = await conn.QueryAsync<MemberEntity>(
                @"SELECT * FROM community_members
                  WHERE community_id = @communityId AND user_id = ANY(@userIds);",
                new { communityId, userIds = storedUserIds });
            return rows.AsList();
        }

        public async Task<IReadOnlyList<MemberEntity>> ListPageAfterIdAsync(
            long communityId,
            long afterId,
            int limit)
        {
            await using var conn = await _db.OpenConnectionAsync();
            var rows = await conn.QueryAsync<MemberEntity>(
                @"SELECT * FROM community_members
                  WHERE community_id = @communityId AND id > @afterId
                  ORDER BY id
                  LIMIT @limit;",
                new { communityId, afterId, limit });
            return rows.AsList();
        }

        public async Task<int> CountByCommunityAsync(long communityId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            return await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM community_members WHERE community_id = @communityId;",
                new { communityId });
        }
    }
}
