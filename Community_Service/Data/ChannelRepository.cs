using Community_Service.Events;
using Community_Service.Ids;
using Dapper;
using Npgsql;

namespace Community_Service.Data
{
    public class ChannelRepository
    {
        private readonly NpgsqlDataSource _db;
        private readonly OutboxWriter _outbox;
        private readonly SnowflakeIdGenerator _ids;

        public ChannelRepository(
            NpgsqlDataSource db,
            OutboxWriter outbox,
            SnowflakeIdGenerator ids)
        {
            _db = db;
            _outbox = outbox;
            _ids = ids;
        }

        public async Task<ChannelEntity> CreateAsync(
            long communityId, long? categoryId, string name, short type, ulong actorUserId)
        {
            var channelId = _ids.NextId();
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var channel = await conn.QuerySingleAsync<ChannelEntity>(
                @"INSERT INTO channels (id, community_id, category_id, name, type, position)
                  VALUES (@channelId, @communityId, @categoryId, @name, @type,
                          COALESCE((SELECT MAX(position) + 1 FROM channels
                                    WHERE community_id = @communityId
                                      AND category_id IS NOT DISTINCT FROM @categoryId), 0))
                  RETURNING *;",
                new { channelId, communityId, categoryId, name, type }, tx);
            await _outbox.EnqueueAsync(conn, tx,
                CommunityEventFactory.ChannelCreated(channel, actorUserId));
            await tx.CommitAsync();
            return channel;
        }

        public async Task<ChannelEntity?> GetByIdAsync(long id)
        {
            await using var conn = await _db.OpenConnectionAsync();
            return await conn.QuerySingleOrDefaultAsync<ChannelEntity>(
                "SELECT * FROM channels WHERE id = @id;", new { id });
        }

        public async Task<IReadOnlyList<ChannelEntity>> ListByCommunityAsync(long communityId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            var rows = await conn.QueryAsync<ChannelEntity>(
                @"SELECT * FROM channels WHERE community_id = @communityId
                  ORDER BY category_id NULLS FIRST, position, id;",
                new { communityId });
            return rows.AsList();
        }

        public async Task<ChannelEntity> UpdateAsync(
            long channelId,
            string? name,
            string? description,
            bool move,
            long? targetCategoryId,
            long? anchorChannelId,
            bool below,
            ulong actorUserId,
            IEnumerable<string> changedFields)
        {
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var channel = await conn.QuerySingleAsync<ChannelEntity>(
                "SELECT * FROM channels WHERE id = @channelId FOR UPDATE;", new { channelId }, tx);

            await conn.ExecuteAsync(
                @"UPDATE channels
                  SET name = COALESCE(@name, name),
                      description = COALESCE(@description, description)
                  WHERE id = @channelId;",
                new { channelId, name, description }, tx);

            if (move)
            {
                await conn.ExecuteAsync(
                    "UPDATE channels SET category_id = @targetCategoryId WHERE id = @channelId;",
                    new { targetCategoryId, channelId }, tx);

                var siblings = (await conn.QueryAsync<ChannelEntity>(
                    @"SELECT * FROM channels
                      WHERE community_id = @communityId
                        AND category_id IS NOT DISTINCT FROM @targetCategoryId
                        AND id <> @channelId
                      ORDER BY position, id;",
                    new { channel.CommunityId, targetCategoryId, channelId }, tx)).AsList();

                var insertAt = 0;
                if (anchorChannelId is { } anchorId)
                {
                    var anchorIndex = siblings.FindIndex(c => c.Id == anchorId);
                    insertAt = anchorIndex < 0 ? siblings.Count : (below ? anchorIndex + 1 : anchorIndex);
                }

                channel.CategoryId = targetCategoryId;
                siblings.Insert(Math.Clamp(insertAt, 0, siblings.Count), channel);
                for (var i = 0; i < siblings.Count; i++)
                {
                    await conn.ExecuteAsync(
                        "UPDATE channels SET position = @position WHERE id = @id;",
                        new { position = i, id = siblings[i].Id }, tx);
                }
            }

            var updated = await conn.QuerySingleAsync<ChannelEntity>(
                "SELECT * FROM channels WHERE id = @channelId;", new { channelId }, tx);
            await _outbox.EnqueueAsync(conn, tx,
                CommunityEventFactory.ChannelUpdated(updated, actorUserId, changedFields));
            await tx.CommitAsync();
            return updated;
        }

        public async Task DeleteAsync(long id, ulong actorUserId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var channel = await conn.QuerySingleAsync<ChannelEntity>(
                "SELECT * FROM channels WHERE id = @id FOR UPDATE;", new { id }, tx);
            await conn.ExecuteAsync("DELETE FROM channels WHERE id = @id;", new { id }, tx);
            await _outbox.EnqueueAsync(conn, tx,
                CommunityEventFactory.ChannelDeleted(channel, actorUserId));
            await tx.CommitAsync();
        }
    }
}
