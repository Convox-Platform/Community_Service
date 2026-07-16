using Dapper;
using Community_Service.Events;
using Npgsql;

namespace Community_Service.Data
{
    public class CommunityRepository
    {
        private readonly NpgsqlDataSource _db;
        private readonly OutboxWriter _outbox;

        public CommunityRepository(NpgsqlDataSource db, OutboxWriter outbox)
        {
            _db = db;
            _outbox = outbox;
        }

        // Создаёт комьюнити вместе с владельцем-участником, переданными участниками
        // и дефолтной структурой (1 категория + 1 текстовый канал) в одной транзакции.
        public async Task<CommunityEntity> CreateAsync(
            string name, string? avatar, string description,
            ulong ownerId, IEnumerable<ulong> memberIds)
        {
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var community = await conn.QuerySingleAsync<CommunityEntity>(
                @"INSERT INTO communities (name, avatar, description, owner_id)
                  VALUES (@name, @avatar, @description, @ownerId)
                  RETURNING *;",
                new { name, avatar, description, ownerId }, tx);

            var members = new HashSet<ulong>(memberIds) { ownerId };
            foreach (var uid in members)
            {
                await conn.ExecuteAsync(
                    @"INSERT INTO community_members (community_id, user_id)
                      VALUES (@cid, @uid) ON CONFLICT DO NOTHING;",
                    new { cid = community.Id, uid }, tx);
            }

            var categoryId = await conn.QuerySingleAsync<long>(
                @"INSERT INTO categories (community_id, name, position)
                  VALUES (@cid, @name, 0) RETURNING id;",
                new { cid = community.Id, name = "Общее" }, tx);

            await conn.ExecuteAsync(
                @"INSERT INTO channels (community_id, category_id, name, type, position)
                  VALUES (@cid, @catId, @name, @type, 0);",
                new { cid = community.Id, catId = categoryId, name = "общий", type = (short)ChannelType.Text }, tx);

            community.MembersCount = members.Count;
            await _outbox.EnqueueAsync(conn, tx,
                CommunityEventFactory.CommunityCreated(community, ownerId));
            await tx.CommitAsync();

            return community;
        }

        public async Task<CommunityEntity?> GetByIdAsync(long id)
        {
            await using var conn = await _db.OpenConnectionAsync();
            return await conn.QuerySingleOrDefaultAsync<CommunityEntity>(
                @"SELECT c.*, (SELECT COUNT(*) FROM community_members m WHERE m.community_id = c.id) AS members_count
                  FROM communities c WHERE c.id = @id;",
                new { id });
        }

        public async Task<IReadOnlyList<CommunityEntity>> ListByUserAsync(ulong userId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            var rows = await conn.QueryAsync<CommunityEntity>(
                @"SELECT c.*, (SELECT COUNT(*) FROM community_members m WHERE m.community_id = c.id) AS members_count
                  FROM communities c
                  JOIN community_members cm ON cm.community_id = c.id
                  WHERE cm.user_id = @userId
                  ORDER BY c.created_at;",
                new { userId });
            return rows.AsList();
        }

        public async Task<ulong?> GetOwnerIdAsync(long communityId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            return await conn.QuerySingleOrDefaultAsync<ulong?>(
                "SELECT owner_id FROM communities WHERE id = @communityId;",
                new { communityId });
        }

    }
}
