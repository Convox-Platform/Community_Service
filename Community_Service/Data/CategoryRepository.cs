using Dapper;
using Community_Service.Events;
using Npgsql;

namespace Community_Service.Data
{
    public class CategoryRepository
    {
        private readonly NpgsqlDataSource _db;
        private readonly OutboxWriter _outbox;

        public CategoryRepository(NpgsqlDataSource db, OutboxWriter outbox)
        {
            _db = db;
            _outbox = outbox;
        }

        public async Task<CategoryEntity> CreateAsync(long communityId, string name, ulong actorUserId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var category = await conn.QuerySingleAsync<CategoryEntity>(
                @"INSERT INTO categories (community_id, name, position)
                  VALUES (@communityId, @name,
                          COALESCE((SELECT MAX(position) + 1 FROM categories WHERE community_id = @communityId), 0))
                  RETURNING *;",
                new { communityId, name }, tx);
            await _outbox.EnqueueAsync(conn, tx,
                CommunityEventFactory.CategoryCreated(category, actorUserId));
            await tx.CommitAsync();
            return category;
        }

        public async Task<CategoryEntity?> GetByIdAsync(long id)
        {
            await using var conn = await _db.OpenConnectionAsync();
            return await conn.QuerySingleOrDefaultAsync<CategoryEntity>(
                "SELECT * FROM categories WHERE id = @id;", new { id });
        }

        public async Task<IReadOnlyList<CategoryEntity>> ListByCommunityAsync(long communityId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            var rows = await conn.QueryAsync<CategoryEntity>(
                "SELECT * FROM categories WHERE community_id = @communityId ORDER BY position, id;",
                new { communityId });
            return rows.AsList();
        }

        public async Task<CategoryEntity> UpdateAsync(
            long id, string? name, int? position, ulong actorUserId, IEnumerable<string> changedFields)
        {
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var category = await conn.QuerySingleAsync<CategoryEntity>(
                @"UPDATE categories
                  SET name     = COALESCE(@name, name),
                      position = COALESCE(@position, position)
                  WHERE id = @id
                  RETURNING *;",
                new { id, name, position }, tx);
            await _outbox.EnqueueAsync(conn, tx,
                CommunityEventFactory.CategoryUpdated(category, actorUserId, changedFields));
            await tx.CommitAsync();
            return category;
        }

        public async Task DeleteAsync(long id, ulong actorUserId)
        {
            await using var conn = await _db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var category = await conn.QuerySingleAsync<CategoryEntity>(
                "SELECT * FROM categories WHERE id = @id FOR UPDATE;", new { id }, tx);
            await conn.ExecuteAsync("DELETE FROM categories WHERE id = @id;", new { id }, tx);
            await _outbox.EnqueueAsync(conn, tx,
                CommunityEventFactory.CategoryDeleted(category, actorUserId));
            await tx.CommitAsync();
        }
    }
}
