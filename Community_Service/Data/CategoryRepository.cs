using Dapper;
using Npgsql;

namespace Community_Service.Data
{
    public class CategoryRepository
    {
        private readonly NpgsqlDataSource _db;

        public CategoryRepository(NpgsqlDataSource db) => _db = db;

        public async Task<CategoryEntity> CreateAsync(long communityId, string name)
        {
            await using var conn = await _db.OpenConnectionAsync();
            return await conn.QuerySingleAsync<CategoryEntity>(
                @"INSERT INTO categories (community_id, name, position)
                  VALUES (@communityId, @name,
                          COALESCE((SELECT MAX(position) + 1 FROM categories WHERE community_id = @communityId), 0))
                  RETURNING *;",
                new { communityId, name });
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

        public async Task<CategoryEntity> UpdateAsync(long id, string? name, int? position)
        {
            await using var conn = await _db.OpenConnectionAsync();
            return await conn.QuerySingleAsync<CategoryEntity>(
                @"UPDATE categories
                  SET name     = COALESCE(@name, name),
                      position = COALESCE(@position, position)
                  WHERE id = @id
                  RETURNING *;",
                new { id, name, position });
        }

        public async Task DeleteAsync(long id)
        {
            await using var conn = await _db.OpenConnectionAsync();
            await conn.ExecuteAsync("DELETE FROM categories WHERE id = @id;", new { id });
        }
    }
}
