using Dapper;
using Community_Service.Events;
using Npgsql;

namespace Community_Service.Data
{
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
            return await conn.QuerySingleAsync<MemberEntity>(
                @"INSERT INTO community_members (community_id, user_id)
                  VALUES (@communityId, @userId)
                  ON CONFLICT (community_id, user_id) DO UPDATE SET user_id = EXCLUDED.user_id
                  RETURNING *;",
                new { communityId, userId = storedUserId });
        }

        public async Task<bool> RemoveAsync(long communityId, ulong userId)
        {
            var storedUserId = UInt64Storage.ToInt64(userId);
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
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
            await _outbox.EnqueueAsync(conn, tx,
                CommunityEventFactory.MemberLeft(member, userId));
            await tx.CommitAsync();
            return true;
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

        public async Task<int> CountByCommunityAsync(long communityId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            return await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM community_members WHERE community_id = @communityId;",
                new { communityId });
        }
    }
}
