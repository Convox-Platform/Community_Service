using Dapper;
using Npgsql;

namespace Community_Service.Data
{
    public class ChannelRepository
    {
        private readonly NpgsqlDataSource _db;

        public ChannelRepository(NpgsqlDataSource db) => _db = db;

        public async Task<ChannelEntity> CreateAsync(long communityId, long? categoryId, string name, short type)
        {
            await using var conn = await _db.OpenConnectionAsync();
            return await conn.QuerySingleAsync<ChannelEntity>(
                @"INSERT INTO channels (community_id, category_id, name, type, position)
                  VALUES (@communityId, @categoryId, @name, @type,
                          COALESCE((SELECT MAX(position) + 1 FROM channels
                                    WHERE community_id = @communityId
                                      AND category_id IS NOT DISTINCT FROM @categoryId), 0))
                  RETURNING *;",
                new { communityId, categoryId, name, type });
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

        public async Task UpdateFieldsAsync(long id, string? name, string? description)
        {
            await using var conn = await _db.OpenConnectionAsync();
            await conn.ExecuteAsync(
                @"UPDATE channels
                  SET name        = COALESCE(@name, name),
                      description  = COALESCE(@description, description)
                  WHERE id = @id;",
                new { id, name, description });
        }

        // Перемещает канал в целевую категорию и располагает его относительно якоря.
        // anchorChannelId == null -> канал становится первым в целевой категории.
        // Позиции всех каналов целевой категории пересчитываются последовательно.
        public async Task MoveAsync(long channelId, long? targetCategoryId, long? anchorChannelId, bool below)
        {
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var channel = await conn.QuerySingleAsync<ChannelEntity>(
                "SELECT * FROM channels WHERE id = @channelId FOR UPDATE;", new { channelId }, tx);

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
                    "UPDATE channels SET position = @pos WHERE id = @id;",
                    new { pos = i, id = siblings[i].Id }, tx);
            }

            await tx.CommitAsync();
        }

        public async Task DeleteAsync(long id)
        {
            await using var conn = await _db.OpenConnectionAsync();
            await conn.ExecuteAsync("DELETE FROM channels WHERE id = @id;", new { id });
        }
    }
}
