using Dapper;
using Community_Service.Events;
using Community_Service.Ids;
using Npgsql;

namespace Community_Service.Data
{
    public class CommunityRepository
    {
        private readonly NpgsqlDataSource _db;
        private readonly OutboxWriter _outbox;
        private readonly SnowflakeIdGenerator _ids;

        public CommunityRepository(
            NpgsqlDataSource db,
            OutboxWriter outbox,
            SnowflakeIdGenerator ids)
        {
            _db = db;
            _outbox = outbox;
            _ids = ids;
        }

        // Создаёт комьюнити вместе с владельцем-участником, переданными участниками
        // и дефолтной структурой (1 категория + 1 текстовый канал) в одной транзакции.
        public async Task<CommunityEntity> CreateAsync(
            string name, string? avatar, string description,
            ulong ownerId, IEnumerable<ulong> memberIds)
        {
            var communityId = _ids.NextId();
            var channelId = _ids.NextId();
            var storedOwnerId = UInt64Storage.ToInt64(ownerId);
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var community = await conn.QuerySingleAsync<CommunityEntity>(
                @"INSERT INTO communities (id, name, avatar, description, owner_id)
                  VALUES (@communityId, @name, @avatar, @description, @ownerId)
                  RETURNING *;",
                new { communityId, name, avatar, description, ownerId = storedOwnerId }, tx);

            var members = new HashSet<ulong>(memberIds) { ownerId };
            var insertedMembers = new List<MemberEntity>(members.Count);
            foreach (var uid in members)
            {
                var storedUserId = UInt64Storage.ToInt64(uid);
                var member = await conn.QuerySingleOrDefaultAsync<MemberEntity>(
                    @"INSERT INTO community_members (community_id, user_id)
                      VALUES (@cid, @uid)
                      ON CONFLICT DO NOTHING
                      RETURNING *;",
                    new { cid = community.Id, uid = storedUserId }, tx);
                if (member is not null)
                    insertedMembers.Add(member);
            }

            var categoryId = await conn.QuerySingleAsync<long>(
                @"INSERT INTO categories (community_id, name, position)
                  VALUES (@cid, @name, 0) RETURNING id;",
                new { cid = community.Id, name = "Общее" }, tx);

            await conn.ExecuteAsync(
                @"INSERT INTO channels (id, community_id, category_id, name, type, position)
                  VALUES (@channelId, @cid, @catId, @name, @type, 0);",
                new { channelId, cid = community.Id, catId = categoryId, name = "общий", type = (short)ChannelType.Text }, tx);

            community.MembersCount = members.Count;
            await _outbox.EnqueueAsync(conn, tx,
                CommunityEventFactory.CommunityCreated(community, ownerId));
            foreach (var member in insertedMembers)
            {
                await _outbox.EnqueueAsync(conn, tx,
                    CommunityEventFactory.MemberJoined(member, ownerId, string.Empty));
            }
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
            var storedUserId = UInt64Storage.ToInt64(userId);
            await using var conn = await _db.OpenConnectionAsync();
            var rows = await conn.QueryAsync<CommunityEntity>(
                @"SELECT c.*, (SELECT COUNT(*) FROM community_members m WHERE m.community_id = c.id) AS members_count
                  FROM communities c
                  JOIN community_members cm ON cm.community_id = c.id
                  WHERE cm.user_id = @userId
                  ORDER BY c.created_at;",
                new { userId = storedUserId });
            return rows.AsList();
        }

        public async Task<ulong?> GetOwnerIdAsync(long communityId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            var ownerId = await conn.QuerySingleOrDefaultAsync<long?>(
                "SELECT owner_id FROM communities WHERE id = @communityId;",
                new { communityId });
            return ownerId is { } value ? UInt64Storage.ToUInt64(value) : null;
        }

    }
}
